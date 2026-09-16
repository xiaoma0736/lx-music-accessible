using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Automation;
using System.Windows.Threading;
using LXMusicAccessible.App;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LXMusicAccessible.Tests;

[Collection("WPF accessibility semantics")]
public sealed class PlaybackRecoveryAndPlaybackInfoTests
{
    [Fact]
    public async Task Requested_quality_timeout_falls_back_to_lower_quality_without_changing_default()
    {
        var audio = new RecoveryAudioPlayer();
        audio.StartDelays["320k-song"] = TimeSpan.FromMilliseconds(5_100);
        audio.StartDelays["128k-song"] = TimeSpan.FromMilliseconds(10);
        audio.QualitiesByUrl["320k-song"] = "320k";
        audio.QualitiesByUrl["128k-song"] = "128k";
        var sources = new RecoverySourceAdapter();
        using var fixture = await PlaybackFixture.StartAsync(audio, sources);
        var music = NetworkSong("song");

        var result = await fixture.Player.PlayAsync(new(
            [music],
            music,
            "320k",
            PlaybackQueueOrigin.Search));

        Assert.Equal(PlaybackResultStatus.Playing, result.Status);
        Assert.Same(music, result.Music);
        Assert.Equal("128k", fixture.Control.ActualQuality);
        Assert.Equal("320k", fixture.Player.DefaultQuality);
        Assert.Equal(["320k", "128k"], sources.ResolvedQualities.ToArray());
        Assert.Equal(["320k-song", "128k-song"], audio.LoadedUrls.ToArray());
        Assert.Equal(PlaybackTrigger.UserInitiated, result.Trigger);
    }

    [Fact]
    public async Task Downgrade_failure_switches_to_the_first_playable_strict_match_and_announces_the_switch()
    {
        var audio = new RecoveryAudioPlayer();
        audio.FailuresByUrl.UnionWith(["320k-song", "128k-song"]);
        audio.QualitiesByUrl["good-b"] = "128k";
        var sources = new RecoverySourceAdapter();
        sources.AddAlternative("b", NetworkSong("song", "b"), "good-b");
        sources.AddAlternative("b", ApproximateSong("near-b", "b"), "near-b-url");
        using var fixture = await PlaybackFixture.StartAsync(audio, sources);
        var music = NetworkSong("song");

        var result = await fixture.Player.PlayAsync(new([music], music, "320k"));

        Assert.Equal(PlaybackResultStatus.Playing, result.Status);
        Assert.Equal("b", result.Music?.Source);
        Assert.Equal("song", result.Music?.Id);
        Assert.Equal("good-b", audio.LastLoadedUrl());
        Assert.DoesNotContain("near-b-url", audio.LoadedUrls);
        Assert.Equal(["320k", "128k", "128k"], sources.ResolvedQualities.ToArray());
        Assert.Equal(["song", "song", "song"], sources.MatchedCandidateIds.ToArray());
        Assert.Single(fixture.Announcer.Announcements, announcement =>
            announcement.Kind == AnnouncementKind.Busy
            && announcement.Text.Contains("正在切换音源接口", StringComparison.Ordinal));
    }

    [Fact]
    public async Task All_recovery_candidates_fail_without_loading_an_approximate_song()
    {
        var audio = new RecoveryAudioPlayer();
        audio.FailuresByUrl.UnionWith(["320k-song", "128k-song"]);
        var sources = new RecoverySourceAdapter();
        sources.AddAlternative("b", ApproximateSong("near-b", "b"), "near-b-url");
        using var fixture = await PlaybackFixture.StartAsync(audio, sources);
        var music = NetworkSong("song");

        var result = await fixture.Player.PlayAsync(new([music], music, "320k"));

        Assert.Equal(PlaybackResultStatus.Failed, result.Status);
        Assert.Equal(PlaybackTrigger.UserInitiated, result.Trigger);
        Assert.Equal(["320k-song", "128k-song"], audio.LoadedUrls.ToArray());
        Assert.DoesNotContain("near-b-url", audio.LoadedUrls);
        Assert.Single(fixture.Announcer.Announcements, announcement =>
            announcement.Kind == AnnouncementKind.Error
            && announcement.Text.Contains("已尝试所有可用音源", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Automatic_playback_failure_is_marked_as_auto_continuation()
    {
        var audio = new RecoveryAudioPlayer();
        audio.FailuresByUrl.UnionWith(["320k-song", "128k-song"]);
        var sources = new RecoverySourceAdapter();
        using var fixture = await PlaybackFixture.StartAsync(audio, sources);
        var music = NetworkSong("song");

        var result = await fixture.Player.PlayAsync(new(
            [music],
            music,
            "320k",
            Trigger: PlaybackTrigger.AutoContinuation));

        Assert.Equal(PlaybackResultStatus.Failed, result.Status);
        Assert.Equal(PlaybackTrigger.AutoContinuation, result.Trigger);
        Assert.Equal(["320k-song", "128k-song"], audio.LoadedUrls.ToArray());
    }

    [Fact]
    public async Task Main_window_tracks_playing_title_actual_quality_duration_position_and_restores_on_stop()
    {
        var audio = new RecoveryAudioPlayer
        {
            Position = TimeSpan.FromMinutes(1.5),
            FixedDuration = TimeSpan.FromMinutes(3),
            DefaultActualQuality = "128k"
        };
        var sources = new RecoverySourceAdapter();
        using var fixture = await PlaybackFixture.StartAsync(audio, sources);
        var music = NetworkSong("song");
        var window = await fixture.CreateMainWindowAsync();
        try
        {
            var result = await fixture.Player.PlayAsync(new([music], music, "320k"));
            Assert.Equal(PlaybackResultStatus.Playing, result.Status);
            var expectedTitle = $"{music.Title} - {music.Artist} - \u843d\u96ea\u97f3\u4e50 \u65e0\u969c\u788d\u7248";
            var actualTitle = string.Empty;
            PumpUntil(window.Dispatcher, () =>
            {
                actualTitle = window.Title;
                return actualTitle == expectedTitle;
            });
            Assert.Equal(expectedTitle, actualTitle);
            Assert.Equal(music.Title, OnWindow(window.Dispatcher, () => window.CurrentTitle.Text));
            Assert.Equal(music.Artist, OnWindow(window.Dispatcher, () => window.CurrentArtist.Text));
            var infoText = OnWindow(window.Dispatcher, () =>
                Assert.IsType<System.Windows.Controls.TextBlock>(
                    FindField(window, "PlaybackInfoText")));
            var expectedInfo =
                "128k \u6807\u51c6\uff0c\u603b\u65f6\u957f 03:00\uff0c\u5f53\u524d\u4f4d\u7f6e 01:30";
            Assert.Equal(expectedInfo, OnWindow(window.Dispatcher, () => infoText.Text));
            Assert.Equal("\u64ad\u653e\u4fe1\u606f", OnWindow(
                window.Dispatcher,
                () => AutomationProperties.GetName((System.Windows.Controls.Primitives.StatusBarItem)infoText.Parent)));

            fixture.Player.Stop();
            PumpUntil(window.Dispatcher, () => window.Title == "\u843d\u96ea\u97f3\u4e50 \u65e0\u969c\u788d\u7248");
            Assert.Equal("\u672a\u64ad\u653e", OnWindow(window.Dispatcher, () => infoText.Text));
        }
        finally
        {
            await window.Dispatcher.InvokeAsync(window.Hide);
        }
    }

    private static T OnWindow<T>(Dispatcher dispatcher, Func<T> value) => dispatcher.Invoke(value);

    private static MusicInfo NetworkSong(string id, string? source = null) => new()
    {
        Id = id,
        Source = source ?? "preferred",
        Title = "同一首歌",
        Artist = "同一歌手",
        Album = "同一专辑",
        Duration = "03:00"
    };

    private static MusicInfo ApproximateSong(string id, string source) => new()
    {
        Id = id,
        Source = source,
        Title = "同一首歌",
        Artist = "不同歌手",
        Album = "同一专辑",
        Duration = "03:00"
    };

    private static object FindField(object owner, string name)
    {
        var value = new[]
        {
            owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic),
            owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)
        }.FirstOrDefault(field => field is not null)?.GetValue(owner);
        Assert.NotNull(value);
        return value!;
    }

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            dispatcher);
        var poll = new DispatcherTimer(
            TimeSpan.FromMilliseconds(10),
            DispatcherPriority.Background,
            (_, _) => { if (dispatcher.Invoke(condition)) frame.Continue = false; },
            dispatcher);
        timeout.Start();
        poll.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        poll.Stop();
        Assert.True(dispatcher.Invoke(condition));
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private sealed class PlaybackFixture : IAsyncDisposable
    {
        private PlaybackFixture(
            TestApplicationHost host,
            RecoveryAudioPlayer audio,
            RecoverySourceAdapter sources)
        {
            Host = host;
            Audio = audio;
            Sources = sources;
            Player = host.Services.GetRequiredService<IPlaybackCoordinator>();
            Control = host.Services.GetRequiredService<IAudioPlaybackControl>();
            Announcer = host.Announcer;
        }

        public TestApplicationHost Host { get; }
        public IPlaybackCoordinator Player { get; }
        public IAudioPlaybackControl Control { get; }
        public RecoveryAudioPlayer Audio { get; }
        public RecoverySourceAdapter Sources { get; }
        public RecordingAnnouncer Announcer { get; }
        private Thread? WindowThread { get; set; }
        private Dispatcher? WindowDispatcher { get; set; }

        public async Task<MainWindow> CreateMainWindowAsync()
        {
            var completion = new TaskCompletionSource<MainWindow>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    WindowDispatcher = dispatcher;
                    var window = Host.Services.GetRequiredService<MainWindow>();
                    window.Show();
                    completion.SetResult(window);
                    Dispatcher.Run();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            WindowThread = thread;
            thread.Start();
            return await completion.Task;
        }

        public static async Task<PlaybackFixture> StartAsync(
            RecoveryAudioPlayer audio,
            RecoverySourceAdapter sources)
        {
            var host = await TestApplicationHost.StartAsync(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IAudioPlayer>(audio));
                services.Replace(ServiceDescriptor.Singleton<IPlaybackSourceAdapter>(sources));
            });
            return new PlaybackFixture(host, audio, sources);
        }

        public async ValueTask DisposeAsync()
        {
            WindowDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            if (WindowThread is not null)
                await Task.Run(() => WindowThread.Join(TimeSpan.FromSeconds(5)));
            await Host.DisposeAsync();
        }
    }

    private sealed class RecoverySourceAdapter : IPlaybackSourceAdapter
    {
        private readonly Dictionary<string, List<(MusicInfo Music, string? Url)>> _alternatives =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _urls = new(StringComparer.Ordinal);

        public ConcurrentQueue<string> ResolvedQualities { get; } = new();
        public ConcurrentQueue<string> MatchedCandidateIds { get; } = new();

        public void AddAlternative(string sourceId, MusicInfo music, string? url)
        {
            if (!_alternatives.TryGetValue(sourceId, out var candidates))
                _alternatives[sourceId] = candidates = [];
            candidates.Add((music, url));
            _urls[$"{music.Source}|{music.Id}"] = url ?? string.Empty;
        }

        public IReadOnlyList<string> GetAlternativeSourceIds(string currentSource) =>
            _alternatives.Keys
                .Where(sourceId => !string.Equals(sourceId, currentSource, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        public Task<string?> ResolveAsync(
            MusicInfo music,
            string quality,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedQualities.Enqueue(quality);
            MatchedCandidateIds.Enqueue(music.Id);
            var customUrl = _urls.GetValueOrDefault($"{music.Source}|{music.Id}");
            return Task.FromResult<string?>(
                string.IsNullOrWhiteSpace(customUrl) ? $"{quality}-{music.Id}" : customUrl);
        }

        public Task<IReadOnlyList<MusicInfo>> FindAlternativesAsync(
            MusicInfo music,
            string sourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<MusicInfo> result = _alternatives.TryGetValue(sourceId, out var candidates)
                ? candidates.Select(candidate => candidate.Music).ToArray()
                : [];
            return Task.FromResult(result);
        }

        public Task<string?> ResolveExactAsync(
            MusicInfo music,
            string sourceId,
            string quality,
            CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class RecoveryAudioPlayer : IAudioPlayer
    {
        public ConcurrentQueue<string> LoadedUrls { get; } = new();
        public HashSet<string> FailuresByUrl { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TimeSpan> StartDelays { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> QualitiesByUrl { get; } = new(StringComparer.Ordinal);
        public string? DefaultActualQuality { get; init; }
        public TimeSpan Position { get; set; }
        public TimeSpan FixedDuration { get; init; }
        public double Volume { get; set; }
        public AudioPlayerState State { get; set; } = AudioPlayerState.Stopped;
        public TimeSpan Duration => FixedDuration;
        public int CurrentOutputDeviceIndex => -1;
        public event EventHandler<AudioPlayerState>? StateChanged { add { } remove { } }
        public event EventHandler? PlaybackCompleted { add { } remove { } }

        public Task LoadAsync(string url, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedUrls.Enqueue(url);
            if (FailuresByUrl.Contains(url))
                throw new InvalidOperationException("fixture playback failure");
            return Task.CompletedTask;
        }

        public string LastLoadedUrl() => LoadedUrls.LastOrDefault() ?? string.Empty;

        public async Task PlayAsync()
        {
            var url = LastLoadedUrl();
            var delay = StartDelays.GetValueOrDefault(url);
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            if (string.Equals(LastLoadedUrl(), url, StringComparison.Ordinal))
                State = AudioPlayerState.Playing;
        }

        public void Pause() => State = AudioPlayerState.Paused;
        public void Stop() => State = AudioPlayerState.Stopped;
        public string? GetActualQuality() =>
            QualitiesByUrl.GetValueOrDefault(LastLoadedUrl()) ?? DefaultActualQuality;
        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => [];
        public bool SwitchOutputDevice(int index) => false;
        public void SeekTo(TimeSpan position) => Position = position;
        public void Dispose() { }
    }
}