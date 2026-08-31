using System.IO;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class LyricsWorkflowTests
{
    [Fact]
    public void Lrc_parser_preserves_fractional_timestamps_repeated_tags_and_translation()
    {
        var lyric = LyricService.ParseLrc(new LxLyricRaw
        {
            Lyric = "  [00:01.25][00:02.5000]第一句\n[01:03:750]第二句",
            Tlyric = "[00:01.250][00:02.5]First line\n[01:03.75]Second line"
        });

        Assert.NotNull(lyric);
        Assert.Equal(
            new[] { 1250d, 2500d, 63750d },
            lyric.Lines.Select(line => line.Time.TotalMilliseconds));
        Assert.Equal(new[] { "第一句", "第一句", "第二句" }, lyric.Lines.Select(line => line.Text));
        Assert.Equal(
            new[] { "First line", "First line", "Second line" },
            lyric.Lines.Select(line => line.Translation));
    }

    [Fact]
    public async Task Position_boundaries_and_positive_negative_offsets_are_mapped_by_the_workflow()
    {
        var music = Song("one");
        var source = new ImmediateLyricSource(Result(
            Line(1, "一"),
            Line(2, "二"),
            Line(3, "三")));
        await using var harness = CreateHarness(source);

        await harness.Workflow.LoadAsync(music);
        harness.Playback.SetState(PlayerState.Playing, music);

        harness.Audio.Position = TimeSpan.FromMilliseconds(999);
        harness.Workflow.SynchronizePlaybackPosition();
        Assert.Equal(-1, harness.Workflow.Current.CurrentIndex);

        harness.Audio.Position = TimeSpan.FromSeconds(2);
        harness.Workflow.SynchronizePlaybackPosition();
        Assert.Equal(1, harness.Workflow.Current.CurrentIndex);

        harness.Workflow.AdjustOffset(500);
        harness.Workflow.SynchronizePlaybackPosition();
        Assert.Equal(0, harness.Workflow.Current.CurrentIndex);

        harness.Workflow.AdjustOffset(-1000);
        harness.Workflow.SynchronizePlaybackPosition();
        Assert.Equal(1, harness.Workflow.Current.CurrentIndex);
    }

    [Fact]
    public async Task New_song_generation_prevents_an_old_request_from_overwriting_current_lyrics()
    {
        var source = new ControlledLyricSource(ignoreCancellation: true);
        await using var harness = CreateHarness(source);
        var oldSong = Song("old");
        var newSong = Song("new");

        var oldLoad = harness.Workflow.LoadAsync(oldSong);
        await source.WaitUntilRequestedAsync("old");
        var newLoad = harness.Workflow.LoadAsync(newSong);
        source.Complete("old", Result(Line(1, "旧歌词")));
        await source.WaitUntilRequestedAsync("new");
        source.Complete("new", Result(Line(1, "新歌词")));
        Assert.False(await oldLoad);
        Assert.True(await newLoad);

        Assert.Equal("new", harness.Workflow.Current.Music?.Id);
        Assert.Equal("新歌词", Assert.Single(harness.Workflow.Current.Lines).Text);
        Assert.DoesNotContain(
            harness.States,
            state => state.Music?.Id == "old" && state.Status == LyricsLoadStatus.Ready);
    }

    [Fact]
    public async Task Playback_subscription_handles_pause_seek_stop_cancellation_and_missing_lyrics()
    {
        var source = new ControlledLyricSource(ignoreCancellation: false);
        await using var harness = CreateHarness(source);
        var song = Song("playing");

        harness.Playback.Raise(PlayerState.Loading, song);
        Assert.Equal(LyricsLoadStatus.Empty, harness.Workflow.Current.Status);
        Assert.False(source.WasRequested("playing"));
        harness.Playback.Raise(PlayerState.Playing, song);
        await source.WaitUntilRequestedAsync("playing");
        source.Complete("playing", Result(Line(1, "一"), Line(2, "二")));
        await WaitUntilAsync(() => harness.Workflow.Current.Status == LyricsLoadStatus.Ready);

        harness.Playback.SetState(PlayerState.Playing, song);
        harness.Audio.Position = TimeSpan.FromSeconds(1);
        harness.Workflow.SynchronizePlaybackPosition();
        Assert.Equal(0, harness.Workflow.Current.CurrentIndex);

        harness.Playback.SetState(PlayerState.Paused, song);
        harness.Audio.Position = TimeSpan.FromSeconds(2);
        harness.Workflow.SynchronizePlaybackPosition();
        Assert.Equal(0, harness.Workflow.Current.CurrentIndex);

        harness.Workflow.Seek(TimeSpan.FromSeconds(2));
        Assert.Equal(1, harness.Workflow.Current.CurrentIndex);

        var cancelled = Song("cancelled");
        harness.Playback.Raise(PlayerState.Playing, cancelled);
        await source.WaitUntilRequestedAsync("cancelled");
        harness.Playback.Raise(PlayerState.Stopped, cancelled);
        await WaitUntilAsync(() => source.WasCancelled("cancelled"));
        Assert.Equal(LyricsLoadStatus.Empty, harness.Workflow.Current.Status);
        Assert.Equal("暂无歌词", harness.Workflow.Current.Message);

        var missing = Song("missing");
        var missingLoad = harness.Workflow.LoadAsync(missing);
        await source.WaitUntilRequestedAsync("missing");
        source.Complete("missing", null);
        await missingLoad;
        Assert.Equal(LyricsLoadStatus.Missing, harness.Workflow.Current.Status);
        Assert.Empty(harness.Workflow.Current.Lines);
    }

    [Fact]
    public async Task Announcement_failure_cannot_break_bilingual_display_state()
    {
        var music = Song("tts");
        var source = new ImmediateLyricSource(Result(new LyricLine
        {
            Time = TimeSpan.Zero,
            Text = "原文",
            Translation = "Translation"
        }));
        await using var harness = CreateHarness(source, new ThrowingAnnouncer());

        await harness.Workflow.LoadAsync(music);
        harness.Playback.SetState(PlayerState.Playing, music);
        harness.Workflow.SynchronizePlaybackPosition();
        harness.Workflow.AnnounceCurrentLine();

        var line = Assert.Single(harness.Workflow.Current.Lines);
        Assert.Equal("原文\nTranslation", line.DisplayText);
        Assert.Equal("原文，翻译：Translation", line.AccessibleText);
        Assert.Equal(0, harness.Workflow.Current.CurrentIndex);
    }

    [Fact]
    public void Lyrics_window_keeps_deterministic_accessible_semantics_with_copy_and_live_updates()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "LXMusicAccessible.App",
            "LyricsWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            root,
            "src",
            "LXMusicAccessible.App",
            "LyricsWindow.xaml.cs"));

        Assert.Contains("Title=\"歌词\"", xaml);
        Assert.Contains("x:Name=\"LyricsTextBox\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"歌词\"", xaml);
        Assert.Contains("IsReadOnly=\"True\"", xaml);
        Assert.Contains("IsReadOnlyCaretVisible=\"True\"", xaml);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml);
        Assert.Contains("AcceptsReturn=\"True\"", xaml);
        Assert.Contains("x:Name=\"CopyLyricsButton\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.DoesNotContain("AutomationProperties.HelpText=", xaml);
        Assert.Contains("_workflow.StateChanged += Workflow_StateChanged", code);
        Assert.Contains("_workflow.AnnounceCurrentLine()", code);
        Assert.Contains("Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)", code);
        Assert.DoesNotContain("catch {", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", code, StringComparison.Ordinal);
    }

    private static Harness CreateHarness(
        ILyricService source,
        IAccessibleAnnouncer? announcer = null)
    {
        var playback = new PlaybackHarness();
        var background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
        var workflow = new LyricsWorkflow(
            NullLogger<LyricsWorkflow>.Instance,
            source,
            playback,
            playback,
            background,
            announcer ?? new RecordingLyricsAnnouncer(),
            startPositionTracking: false);
        var states = new List<LyricsState>();
        workflow.StateChanged += (_, state) => states.Add(state);
        return new Harness(workflow, playback, playback, background, states);
    }

    private static MusicInfo Song(string id) => new()
    {
        Id = id,
        Source = "test",
        Title = id,
        Artist = "artist"
    };

    private static LyricLine Line(double seconds, string text) => new()
    {
        Time = TimeSpan.FromSeconds(seconds),
        Text = text
    };

    private static LyricResult Result(params LyricLine[] lines) => new()
    {
        Lyric = new Lyric { Lines = lines.ToList() },
        SourceId = "test"
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "verify.ps1"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class ImmediateLyricSource(LyricResult? result) : ILyricService
    {
        public Task<LyricResult?> GetLyricAsync(
            MusicInfo music,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class ControlledLyricSource(bool ignoreCancellation) : ILyricService
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Request> _requests = [];

        public Task<LyricResult?> GetLyricAsync(
            MusicInfo music,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<LyricResult?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new Request(completion);
            lock (_gate) _requests[music.Id] = request;
            if (!ignoreCancellation)
            {
                cancellationToken.Register(() =>
                {
                    request.Cancelled = true;
                    completion.TrySetCanceled(cancellationToken);
                });
            }
            return completion.Task;
        }

        public void Complete(string id, LyricResult? result)
        {
            Request request;
            lock (_gate) request = _requests[id];
            request.Completion.TrySetResult(result);
        }

        public bool WasCancelled(string id)
        {
            lock (_gate) return _requests.TryGetValue(id, out var request) && request.Cancelled;
        }

        public bool WasRequested(string id)
        {
            lock (_gate) return _requests.ContainsKey(id);
        }

        public async Task WaitUntilRequestedAsync(string id) =>
            await WaitUntilAsync(() =>
            {
                lock (_gate) return _requests.ContainsKey(id);
            });

        private sealed record Request(TaskCompletionSource<LyricResult?> Completion)
        {
            public bool Cancelled { get; set; }
        }
    }

    private sealed class PlaybackHarness : IPlaybackCoordinator, IAudioPlaybackControl
    {
        private PlayerState _state;

        public bool IsPlaying => _state == PlayerState.Playing;
        public bool IsPaused => _state == PlayerState.Paused;
        public MusicInfo? CurrentMusic { get; private set; }
        public PlayMode Mode { get; set; }
        public string DefaultQuality { get; set; } = "128k";
        public double Volume { get; private set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; }
        public string? ActualQuality => null;
        public int CurrentOutputDeviceIndex => -1;
        public string CurrentOutputDeviceName => "test";
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted
        {
            add { }
            remove { }
        }

        public void Raise(PlayerState state, MusicInfo? music)
        {
            var old = _state;
            _state = state;
            CurrentMusic = music;
            StateChanged?.Invoke(this, new PlayerStateChangedEventArgs
            {
                OldState = old,
                NewState = state,
                CurrentMusic = music
            });
        }

        public void SetState(PlayerState state, MusicInfo? music)
        {
            _state = state;
            CurrentMusic = music;
        }

        public Task<PlaybackResult> PlayAsync(PlaybackRequest request) => throw new NotSupportedException();
        public Task<PlaybackResult> ReplayAsync() => throw new NotSupportedException();
        public void Pause() => _state = PlayerState.Paused;
        public void Stop() => _state = PlayerState.Stopped;
        public Task<PlaybackResult> NextAsync() => throw new NotSupportedException();
        public Task<PlaybackResult> PreviousAsync() => throw new NotSupportedException();
        public Task ResumeAsync() { _state = PlayerState.Playing; return Task.CompletedTask; }
        public void SetVolume(double volume) => Volume = volume;
        public void SyncVolumeFromDevice() { }
        public void SetPosition(TimeSpan position) => Position = position;
        public void SeekTo(TimeSpan position) => Position = position;
        public IReadOnlyList<LXMusicAccessible.Audio.OutputDeviceInfo> GetOutputDevices() => [];
        public string? SwitchToNextOutputDevice() => null;
        public string? SwitchToPreviousOutputDevice() => null;
        public string? SwitchOutputDevice(int index) => null;
    }

    private sealed class ThrowingAnnouncer : IAccessibleAnnouncer
    {
        public bool SpeechEnabled { get; set; }
        public void Announce(AccessibleAnnouncement announcement) =>
            throw new InvalidOperationException("simulated TTS failure after UIA delivery");
    }

    private sealed class RecordingLyricsAnnouncer : IAccessibleAnnouncer
    {
        public bool SpeechEnabled { get; set; }
        public List<AccessibleAnnouncement> Announcements { get; } = [];
        public void Announce(AccessibleAnnouncement announcement) => Announcements.Add(announcement);
    }

    private sealed class Harness(
        LyricsWorkflow workflow,
        PlaybackHarness playback,
        PlaybackHarness audio,
        BackgroundWorkHost background,
        List<LyricsState> states) : IAsyncDisposable
    {
        public LyricsWorkflow Workflow { get; } = workflow;
        public PlaybackHarness Playback { get; } = playback;
        public PlaybackHarness Audio { get; } = audio;
        public IReadOnlyList<LyricsState> States { get; } = states;

        public async ValueTask DisposeAsync()
        {
            Workflow.Dispose();
            await background.StopAsync();
            await background.DisposeAsync();
        }
    }
}
