using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class PlaybackHotkeyStatusRegressionTests
{
    [Fact]
    public async Task Pause_then_play_resumes_the_same_stream_and_position()
    {
        var audio = new PositionAudio();
        var player = CreatePlayer(audio);
        var song = new MusicInfo { Id = "local-1", Title = "Song", Artist = "Artist", Source = "Local", PlayUrl = Path.Combine(Path.GetTempPath(), "missing.mp3") };

        await player.PlayAsync(new PlaybackRequest(new[] { song }, song, Origin: PlaybackQueueOrigin.Search));
        Assert.True(player.IsPlaying);
        Assert.Equal(1, audio.LoadCalls);

        audio.Position = TimeSpan.FromSeconds(42);
        player.Pause();
        Assert.True(player.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(42), audio.Position);

        var resumed = await player.ReplayAsync();
        Assert.Equal(PlaybackResultStatus.Playing, resumed.Status);
        Assert.True(player.IsPlaying);
        Assert.Equal(1, audio.LoadCalls);
        Assert.Equal(2, audio.PlayCalls);
        Assert.Equal(TimeSpan.FromSeconds(42), audio.Position);
    }

    [Fact]
    public void Plain_f10_is_volume_up_and_does_not_reach_menu_processing()
    {
        WinFormsTestRunner.Run(() =>
        {
            var audio = new CountingAudio();
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = new RecordingAnnouncer(),
                BrowseSource = new InteractionBrowseSource(),
                Playback = new RecordingPlayback(),
                Audio = audio,
                TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-hotkey-" + Guid.NewGuid().ToString("N"))
            });

            var method = typeof(Form).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var arguments = new object?[] { new Message(), Keys.F10 };
            var handled = (bool)method!.Invoke(shell, arguments)!;
            Assert.True(handled);
            Assert.Equal(1, audio.VolumeChanges);
        });
    }

    [Fact]
    public async Task Playing_song_updates_window_title_and_status_bar()
    {
        var source = new InteractionBrowseSource();
        var playback = new RecordingPlayback();
        var announcer = new RecordingAnnouncer();
        await WinFormsSta.RunAsync(async () =>
        {
            using var shell = CreateShell(source, playback);
            shell.Show();
            Application.DoEvents();
            FillSearch(shell);
            await WaitFor(() => playback.Requests.Count == 1);

            Assert.StartsWith("Artist - Song", shell.Text, StringComparison.Ordinal);
            Assert.Contains("Song", shell.PlaybackInfoForTests.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Playing_song_falls_back_to_song_quality_in_status_bar()
    {
        var playback = new RecordingPlayback();
        var audio = new CountingAudio();
        await WinFormsSta.RunAsync(async () =>
        {
            using var shell = CreateShell(new InteractionBrowseSource(), playback, audio);
            shell.Show();
            Application.DoEvents();
            FillSearch(shell, quality: 320);
            await WaitFor(() => playback.Requests.Count == 1);

            Assert.Contains("音质：极高", shell.PlaybackInfoForTests.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Song_details_include_quality_and_current_playback_quality()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new InteractionBrowseSource(), new RecordingPlayback());
            var song = new MusicInfo
            {
                Id = "song-quality",
                Title = "Song",
                Artist = "Artist",
                Quality = 320
            };
            var method = typeof(WinFormsShell).GetMethod(
                "BuildSongDetailLines",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            var lines = Assert.IsType<string[]>(method!.Invoke(shell, new object?[] { song }));

            Assert.Contains("音质：极高", lines);
            Assert.DoesNotContain(lines, line => line.StartsWith("当前播放音质：", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Lyrics_display_is_visible_to_screen_reader_automation()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var form = new LyricsForm(new RecordingAnnouncer(), null!, _ => false);
            var text = form.Controls.OfType<RichTextBox>().Single();
            Assert.Equal("歌词", text.AccessibleName);
            Assert.NotEqual(AccessibleRole.None, text.AccessibilityObject.Role);
            Assert.False((text.AccessibilityObject.State & AccessibleStates.Invisible) != 0);
        });
    }

    /// <summary>
    /// The viewer used to prefix every line with its position, so a screen reader said "2. 第一句" and the
    /// listener had to count lines to know where the song was. The text now is the lyric itself, and the
    /// line being sung is carried by the selection, which is what UIA reports as the current value. The
    /// flow runs through the real workflow so the seam between LyricsWorkflow and the window is covered.
    /// </summary>
    [Fact]
    public void Lyrics_viewer_shows_line_text_without_sequence_numbers()
    {
        WinFormsSta.RunAsync(async () =>
        {
            var lyrics = new LyricsWorkflow(
                NullLogger<LyricsWorkflow>.Instance,
                new FixedLyricService(),
                new RecordingPlayback(),
                new CountingAudio(),
                new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance),
                new RecordingAnnouncer(),
                startPositionTracking: false);
            using var form = new LyricsForm(new RecordingAnnouncer(), lyrics, _ => false);
            form.Show();
            Application.DoEvents();

            Assert.True(await lyrics.LoadAsync(new MusicInfo
            {
                Id = "song-1",
                Source = "test",
                Title = "Song",
                Artist = "Artist"
            }));
            await WaitFor(() => lyrics.Current.Lines.Count == 2);

            var text = form.Controls.OfType<RichTextBox>().Single();
            Assert.Equal("First line\nSecond line\nTranslation", text.Text.Replace("\r\n", "\n"));
            Assert.All(text.Lines, line => Assert.False(
                char.IsDigit(line.TrimStart().FirstOrDefault()),
                "The viewer added a sequence number: " + line));
            Assert.Equal(AccessibleRole.Document, text.AccessibilityObject.Role);
            Assert.Equal("First line", text.SelectedText);

            lyrics.Seek(TimeSpan.FromSeconds(1));
            await WaitFor(() => text.SelectedText.StartsWith("Second line", StringComparison.Ordinal));
            Assert.Equal("Second line\nTranslation", text.SelectedText.Replace("\r\n", "\n"));
        });
    }

    /// <summary>
    /// Pausing, stopping and turning the volume up or down must not speak. The music stopping or changing
    /// loudness is the feedback, and one utterance per key press competes with both the music and the
    /// screen reader. The status text and the live region still change, while the commands that have no
    /// audible equivalent at all, such as the play mode, keep speaking.
    /// </summary>
    [Fact]
    public void Pause_stop_and_volume_mirror_stay_silent_when_their_scopes_are_off()
    {
        WinFormsSta.RunAsync(async () =>
        {
            var speech = new RecordingSpeech();
            var announcer = new WinFormsAccessibleAnnouncer(
                new ITtsService[] { speech }, NullLogger<WinFormsAccessibleAnnouncer>.Instance);
            var playback = new PlayingThenStopped();
            var audio = new CountingAudio();
            announcer.SpeechScope = new AccessibilitySettings
            {
                AnnouncePlaybackState = false,
                AnnounceVolume = false
            };
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = announcer,
                BrowseSource = new InteractionBrowseSource(),
                Playback = playback,
                Audio = audio,
                TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-speech-" + Guid.NewGuid().ToString("N"))
            });
            shell.Show();
            Application.DoEvents();

            PressCmdKey(shell, Keys.F5);
            await WaitFor(() => shell.LiveRegionForTests.Text == "已暂停");
            Assert.Equal(1, playback.PauseCalls);
            Assert.Empty(speech.Spoken);

            PressCmdKey(shell, Keys.F6);
            await WaitFor(() => shell.LiveRegionForTests.Text == "已停止播放");
            Assert.Equal(1, playback.StopCalls);
            Assert.Empty(speech.Spoken);

            PressCmdKey(shell, Keys.F9);
            await WaitFor(() => shell.LiveRegionForTests.Text.StartsWith("音量", StringComparison.Ordinal));
            Assert.Equal(1, audio.VolumeChanges);
            Assert.Empty(speech.Spoken);

            // A mode change can neither be seen nor heard, so it still has to be spoken.
            PressCmdKey(shell, Keys.F1);
            await WaitFor(() => speech.Spoken.Count == 1);
        });
    }

    /// <summary>
    /// A lyric line is spoken even when the optional voice announcements are off, because reading the line
    /// that is being sung is the whole point of the lyric reader. A routine playback state is not.
    /// </summary>
    [Fact]
    public void Always_speech_still_reaches_the_backend_when_announcements_are_off()
    {
        WinFormsTestRunner.Run(() =>
        {
            var speech = new RecordingSpeech();
            var announcer = new WinFormsAccessibleAnnouncer(
                new ITtsService[] { speech }, NullLogger<WinFormsAccessibleAnnouncer>.Instance);
            using var live = new Label();
            announcer.Attach(live);
            announcer.SpeechEnabled = false;

            announcer.Announce(new AccessibleAnnouncement(
                "第一句", AnnouncementKind.Status, "lyrics", AnnouncementSpeech.Always));
            Assert.Equal(new[] { "第一句" }, speech.Spoken);

            announcer.Announce(new AccessibleAnnouncement("已暂停", AnnouncementKind.Status, "playback-state"));
            Assert.Single(speech.Spoken);
            Assert.Equal("已暂停", live.Text);
        });
    }

    /// <summary>
    /// 语音播报 is the master switch for every routine announcement, so a play-mode change has to obey it:
    /// 逐句歌词 is the one channel with its own 歌词播报 switch and the one exception. With speech off the
    /// mode still reaches the visible announcement line, so the state is not lost, only not spoken.
    /// </summary>
    [Fact]
    public void Play_mode_is_spoken_while_announcements_are_on_and_stays_silent_when_they_are_off()
    {
        WinFormsTestRunner.Run(() =>
        {
            var speech = new RecordingSpeech();
            var announcer = new WinFormsAccessibleAnnouncer(
                new ITtsService[] { speech }, NullLogger<WinFormsAccessibleAnnouncer>.Instance);
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = announcer,
                BrowseSource = new InteractionBrowseSource(),
                Playback = new RecordingPlayback(),
                TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-playmode-" + Guid.NewGuid().ToString("N"))
            });
            shell.Show();
            Application.DoEvents();

            announcer.SpeechEnabled = true;
            PressCmdKey(shell, Keys.F1);
            Assert.Equal(new[] { "列表循环" }, speech.Spoken);

            announcer.SpeechEnabled = false;
            PressCmdKey(shell, Keys.F1);
            Assert.Single(speech.Spoken);
            Assert.Equal("随机播放", shell.LiveRegionForTests.Text);
        });
    }

    /// <summary>
    /// The announcement mirror is the only channel left for a status message when speech is turned off, so it
    /// has to be a rendered line: a 1x1 label is never painted, which hides it from a review cursor, from
    /// magnification and from anyone reading the window with their eyes.
    /// </summary>
    [Fact]
    public void Announcement_mirror_is_a_visible_line_above_the_status_bar()
    {
        WinFormsTestRunner.Run(() =>
        {
            var announcer = new WinFormsAccessibleAnnouncer(
                Array.Empty<ITtsService>(), NullLogger<WinFormsAccessibleAnnouncer>.Instance);
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = announcer,
                BrowseSource = new InteractionBrowseSource(),
                Playback = new RecordingPlayback(),
                TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-mirror-" + Guid.NewGuid().ToString("N"))
            });
            shell.Show();
            Application.DoEvents();

            var live = shell.LiveRegionForTests;
            var status = Assert.IsType<StatusBar>(shell.Controls.OfType<StatusBar>().Single());
            Assert.True(live.Visible, "the announcement line is not rendered");
            Assert.True(live.Height >= 20, "the announcement line is too thin to read: " + live.Height);
            Assert.True(live.Width > 300, "the announcement line is too narrow to read: " + live.Width);
            Assert.True(live.Bottom <= status.Top, "the announcement line overlaps the status bar");
            Assert.True(status.Bottom >= shell.ClientSize.Height - 2, "the status bar is not the bottom edge");

            announcer.Announce(new AccessibleAnnouncement("播报区域自检", AnnouncementKind.Status, "audit"));
            Assert.Equal("播报区域自检", live.Text);
            Assert.Equal(live.Text, live.AccessibilityObject.Name);
        });
    }

    /// <summary>
    /// Starting the application a second time has to bring the window back the same way the tray menu does.
    /// Hidden-to-tray minimises the shell and removes its taskbar button, so simply flipping Visible left a
    /// keyboard-only user with a window that existed but could not be reached or read.
    /// </summary>
    [Fact]
    public void Second_launch_restores_a_window_that_was_hidden_to_the_tray()
    {
        WinFormsTestRunner.Run(() =>
        {
            var announcer = new RecordingAnnouncer();
            using var integration = new WinFormsDesktopIntegration(announcer);
            using var desktop = new DesktopLifecycleWorkflow(
                integration, new ApplicationShutdownRequest(), new RecordingPlayback(), announcer);
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = announcer,
                BrowseSource = new InteractionBrowseSource(),
                Playback = new RecordingPlayback(),
                Desktop = desktop,
                TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-second-" + Guid.NewGuid().ToString("N"))
            });
            desktop.Attach(shell);
            shell.Show();
            Application.DoEvents();

            _ = desktop.ExecuteAsync(DesktopCommand.Escape);
            Application.DoEvents();
            Assert.False(shell.Visible);

            shell.RestoreForSecondLaunch();
            Application.DoEvents();

            Assert.True(shell.Visible);
            Assert.True(shell.ShowInTaskbar);
            Assert.Equal(FormWindowState.Normal, shell.WindowState);
        });
    }

    /// <summary>
    /// 上一首/下一首 used to speak 已切换歌曲 without naming anything. The title of the song that started is
    /// the one fact a listener cannot perceive except by hearing the whole song, and a screen reader does not
    /// re-read a window caption, so the same sentence as a fresh playback has to be spoken.
    /// </summary>
    [Fact]
    public void Advancing_the_queue_announces_the_song_that_started()
    {
        WinFormsSta.RunAsync(async () =>
        {
            var speech = new RecordingSpeech();
            var announcer = new WinFormsAccessibleAnnouncer(
                new ITtsService[] { speech }, NullLogger<WinFormsAccessibleAnnouncer>.Instance);
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = announcer,
                BrowseSource = new InteractionBrowseSource(),
                Playback = new SteppingPlayback(new MusicInfo
                {
                    Id = "next-1",
                    Title = "Next Song",
                    Artist = "Artist",
                    Source = "netease"
                }),
                TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-step-" + Guid.NewGuid().ToString("N"))
            });
            shell.Show();
            Application.DoEvents();

            PressCmdKey(shell, Keys.F4);
            await WaitFor(() => speech.Spoken.Count == 1);

            Assert.Equal("正在播放：Artist，Next Song", speech.Spoken[0]);
            Assert.StartsWith("Artist - Next Song", shell.Text, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// 设置→歌词→自动滚动歌词 has to be honoured: with it off the viewer must not pull the caret back to the
    /// line that is being sung, otherwise a reader cannot look ahead or behind while the song plays. The
    /// current line is still available through 自动朗读当前歌词, and switching the setting back on resumes
    /// following.
    /// </summary>
    [Fact]
    public void Lyrics_viewer_keeps_the_reading_position_when_auto_scroll_is_off()
    {
        WinFormsSta.RunAsync(async () =>
        {
            var lyrics = new LyricsWorkflow(
                NullLogger<LyricsWorkflow>.Instance,
                new FixedLyricService(),
                new RecordingPlayback(),
                new CountingAudio(),
                new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance),
                new RecordingAnnouncer(),
                startPositionTracking: false);
            using var form = new LyricsForm(new RecordingAnnouncer(), lyrics, _ => false);
            form.Show();
            Application.DoEvents();

            Assert.True(await lyrics.LoadAsync(new MusicInfo
            {
                Id = "song-1",
                Source = "test",
                Title = "Song",
                Artist = "Artist"
            }));
            await WaitFor(() => lyrics.Current.Lines.Count == 2);

            var text = form.Controls.OfType<RichTextBox>().Single();
            Assert.Equal("First line", text.SelectedText);

            lyrics.ApplySettings(autoAnnounce: false, autoScroll: false, offsets: null);
            await WaitFor(() => !lyrics.Current.AutoScroll);

            var parked = text.TextLength;
            text.Select(parked, 0);
            lyrics.Seek(TimeSpan.Zero);
            await WaitFor(() => lyrics.Current.CurrentIndex == 0);
            Application.DoEvents();

            Assert.Equal(parked, text.SelectionStart);
            Assert.Equal(0, text.SelectionLength);

            lyrics.ApplySettings(autoAnnounce: false, autoScroll: true, offsets: null);
            await WaitFor(() => text.SelectedText == "First line");
        });
    }

    /// <summary>
    /// Sends a command key through the shell window the way the keyboard does. The playback commands are
    /// handled by the shell and still fall through to the base implementation, so the assertions below are
    /// about the visible effect rather than about the return value.
    /// </summary>
    private static void PressCmdKey(Form form, Keys key)
    {
        var method = typeof(Form).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var arguments = new object?[] { new Message(), key };
        method!.Invoke(form, arguments);
        Application.DoEvents();
    }

    private sealed class RecordingSpeech : ITtsService
    {
        public List<string> Spoken { get; } = new();
        public bool IsAvailable => true;
        public string ActiveBackendName => "test";
        public Task SpeakAsync(string text) { lock (Spoken) Spoken.Add(text); return Task.CompletedTask; }
        public Task SpeakQueuedAsync(string text) => SpeakAsync(text);
        public void Stop() { }
        public void SetRate(double rate) { }
        public void SetVoice(string voiceId) { }
        public List<VoiceInfo> GetAvailableVoices() => new();
    }

    private sealed class FixedLyricService : ILyricService
    {
        public Task<LyricResult?> GetLyricAsync(MusicInfo music, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<LyricResult?>(new LyricResult
            {
                SourceId = "test",
                Lyric = new Lyric
                {
                    Lines = new List<LyricLine>
                    {
                        new() { Time = TimeSpan.Zero, Text = "First line" },
                        new() { Time = TimeSpan.FromSeconds(1), Text = "Second line", Translation = "Translation" }
                    }
                }
            });
        }
    }

    private sealed class PlayingThenStopped : IPlaybackCoordinator
    {
        public int PauseCalls { get; private set; }
        public int StopCalls { get; private set; }
        public bool IsPlaying => PauseCalls == 0 && StopCalls == 0;
        public bool IsPaused => PauseCalls > 0 && StopCalls == 0;
        public MusicInfo? CurrentMusic => null;
        public PlayMode Mode { get; set; }
        public string DefaultQuality { get; set; } = "128k";
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted { add { } remove { } }
        public Task<PlaybackResult> PlayAsync(PlaybackRequest request) => NoChange();
        public Task<PlaybackResult> ReplayAsync() => NoChange();
        public Task<PlaybackResult> NextAsync() => NoChange();
        public Task<PlaybackResult> PreviousAsync() => NoChange();
        public Task ResumeAsync() => Task.CompletedTask;
        public void Pause() => PauseCalls++;
        public void Stop() => StopCalls++;
        private static Task<PlaybackResult> NoChange() =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, null, PlaybackQueueOrigin.Search, "test"));
    }

    /// <summary>Playback that moves to a fixed next song, the way the queue does for 上一首/下一首.</summary>
    private sealed class SteppingPlayback : IPlaybackCoordinator
    {
        private readonly MusicInfo _next;
        public SteppingPlayback(MusicInfo next) => _next = next;
        public bool IsPlaying => true;
        public bool IsPaused => false;
        public MusicInfo? CurrentMusic => _next;
        public PlayMode Mode { get; set; }
        public string DefaultQuality { get; set; } = "128k";
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted { add { } remove { } }
        public Task<PlaybackResult> PlayAsync(PlaybackRequest request) =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, request.Selection, request.Origin, "playing"));
        public Task<PlaybackResult> ReplayAsync() => Step();
        public Task<PlaybackResult> NextAsync() => Step();
        public Task<PlaybackResult> PreviousAsync() => Step();
        public Task ResumeAsync() => Task.CompletedTask;
        public void Pause() { }
        public void Stop() { }
        private Task<PlaybackResult> Step() =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, _next, PlaybackQueueOrigin.Search, "playing"));
    }

    [Fact]
    public void Hidden_hotkey_window_dispatches_registered_hotkey_ids()
    {
        WinFormsTestRunner.Run(() =>
        {
            int? received = null;
            using var window = new GlobalHotkeyMessageWindow();
            window.HotkeyReceived += (_, id) => received = id;
            Assert.True(PostMessage(window.Handle, 0x0312, new IntPtr(97), IntPtr.Zero));
            Application.DoEvents();
            Assert.Equal(97, received);
        });
    }

    private static MusicPlayerService CreatePlayer(PositionAudio audio) => new(
        NullLogger<MusicPlayerService>.Instance,
        audio,
        new FixedSourceAdapter(),
        new RecordingAnnouncer(),
        new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance),
        new System.Net.Http.HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan },
        Path.Combine(Path.GetTempPath(), "lx-playback-resume-" + Guid.NewGuid().ToString("N")));

    private static WinFormsShell CreateShell(
        InteractionBrowseSource source,
        RecordingPlayback playback,
        IAudioPlaybackControl? audio = null) => new(
        new WinFormsShellServices
        {
            Announcer = new RecordingAnnouncer(),
            BrowseSource = source,
            Playback = playback,
            Audio = audio,
            TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-status-" + Guid.NewGuid().ToString("N"))
        });

    private static void FillSearch(Form shell, int quality = 0)
    {
        var results = AllListBoxes(shell).First();
        results.Items.Add(new ObjectBox<MusicInfo>("Artist - Song", new MusicInfo { Id = "song-1", Title = "Song", Artist = "Artist", Quality = quality }));
        results.SelectedIndex = 0;
        var handler = typeof(ListBox).GetMethod("OnKeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        handler.Invoke(results, new object[] { new KeyEventArgs(Keys.Enter) });
    }

    private static System.Collections.Generic.IEnumerable<ListBox> AllListBoxes(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is ListBox list) yield return list;
            foreach (var nested in AllListBoxes(child)) yield return nested;
        }
    }

    private static void WaitForUi(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (condition()) return;
            Thread.Sleep(10);
        }
        Assert.True(condition(), "condition was not reached");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "condition was not reached");
    }

    private static class WinFormsSta
    {
        public static Task RunAsync(Func<Task> test)
        {
            Exception? failure = null;
            var thread = new Thread(async () =>
            {
                try { await test(); }
                catch (Exception exception) { failure = exception; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure is not null) throw failure;
            return Task.CompletedTask;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    private sealed class FixedSourceAdapter : IPlaybackSourceAdapter
    {
        public IReadOnlyList<string> GetAlternativeSourceIds(string currentSource) => Array.Empty<string>();
        public Task<string?> ResolveAsync(MusicInfo music, string quality, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
        public Task<string?> ResolveExactAsync(MusicInfo music, string sourceId, string quality, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<MusicInfo>> FindAlternativesAsync(MusicInfo music, string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MusicInfo>>(Array.Empty<MusicInfo>());
    }

    private sealed class PositionAudio : IAudioPlayer
    {
        public int LoadCalls { get; private set; }
        public int PlayCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public AudioPlayerState State { get; set; } = AudioPlayerState.Stopped;
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.FromSeconds(180);
        public double Volume { get; set; } = 0.8;
        public void Dispose() { }
        public int CurrentOutputDeviceIndex => -1;
        public bool SwitchOutputDevice(int index) => false;
#pragma warning disable 67
        public event EventHandler<AudioPlayerState>? StateChanged;
        public event EventHandler? PlaybackCompleted;
#pragma warning restore 67

        public Task LoadAsync(string url, CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            Position = TimeSpan.Zero;
            State = AudioPlayerState.Stopped;
            return Task.CompletedTask;
        }

        public Task PlayAsync()
        {
            PlayCalls++;
            State = AudioPlayerState.Playing;
            return Task.CompletedTask;
        }

        public void Pause()
        {
            PauseCalls++;
            State = AudioPlayerState.Paused;
        }

        public void Stop()
        {
            Position = TimeSpan.Zero;
            State = AudioPlayerState.Stopped;
        }

        public string? GetActualQuality() => "128k";
        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => Array.Empty<OutputDeviceInfo>();
        public void SeekTo(TimeSpan position) => Position = position;
    }

    private sealed class CountingAudio : IAudioPlaybackControl
    {
        public int VolumeChanges { get; private set; }
        public double Volume { get; private set; } = 0.5;
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public string? ActualQuality => null;

        public void SetVolume(double volume) { Volume = volume; VolumeChanges++; }
        public void SyncVolumeFromDevice() { }
        public void SetPosition(TimeSpan position) { }
        public void SeekTo(TimeSpan position) { }
        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => Array.Empty<OutputDeviceInfo>();
        public int CurrentOutputDeviceIndex => -1;
        public string CurrentOutputDeviceName => "系统默认";
        public string? SwitchToNextOutputDevice() => null;
        public string? SwitchToPreviousOutputDevice() => null;
        public string? SwitchOutputDevice(int index) => null;
    }
}