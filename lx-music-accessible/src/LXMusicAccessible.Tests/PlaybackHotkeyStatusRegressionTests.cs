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
    public void Plain_f10_is_handled_as_volume_down_and_does_not_reach_menu_processing()
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
    public void Lyrics_display_is_hidden_from_screen_reader_automation()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var form = new LyricsForm(new RecordingAnnouncer(), null!, _ => false);
            var text = form.Controls.OfType<TextBox>().Single();
            Assert.Equal(AccessibleRole.None, text.AccessibleRole);
        });
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

    private static WinFormsShell CreateShell(InteractionBrowseSource source, RecordingPlayback playback) => new(
        new WinFormsShellServices
        {
            Announcer = new RecordingAnnouncer(),
            BrowseSource = source,
            Playback = playback,
            TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-status-" + Guid.NewGuid().ToString("N"))
        });

    private static void FillSearch(Form shell)
    {
        var results = AllListBoxes(shell).First();
        results.Items.Add(new ObjectBox<MusicInfo>("Artist - Song", new MusicInfo { Id = "song-1", Title = "Song", Artist = "Artist" }));
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