using System.IO;
using System.Net;
using System.Net.Http;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class MusicPlayerBackgroundTests
{
    [Fact]
    public async Task Sequential_previous_next_and_natural_end_are_observable_through_the_public_seam()
    {
        await using var fixture = new PlayerFixture();
        var songs = new[] { LocalSong("a"), LocalSong("b"), LocalSong("c") };
        var completed = new List<PlaybackQueueOrigin>();
        fixture.Player.QueueCompleted += (_, args) => completed.Add(args.Origin);

        await fixture.Player.PlayAsync(new(songs, songs[1], Origin: PlaybackQueueOrigin.Search));
        Assert.Equal("a", (await fixture.Player.PreviousAsync()).Music?.Id);
        Assert.Equal("b", (await fixture.Player.NextAsync()).Music?.Id);
        Assert.Equal("c", (await fixture.Player.NextAsync()).Music?.Id);

        fixture.Audio.RaiseCompleted();
        await fixture.Background.WaitAsync("music-player.play");

        Assert.Equal(new[] { PlaybackQueueOrigin.Search }, completed);
        Assert.Equal("c", fixture.Player.CurrentMusic?.Id);
    }

    [Fact]
    public async Task List_loop_wraps_both_directions()
    {
        await using var fixture = new PlayerFixture();
        var songs = new[] { LocalSong("a"), LocalSong("b") };
        fixture.Player.Mode = PlayMode.Loop;
        await fixture.Player.PlayAsync(new(songs, songs[1]));

        Assert.Equal("a", (await fixture.Player.NextAsync()).Music?.Id);
        Assert.Equal("b", (await fixture.Player.PreviousAsync()).Music?.Id);
    }

    [Fact]
    public async Task Single_loop_replays_current_song_on_natural_end()
    {
        await using var fixture = new PlayerFixture();
        var songs = new[] { LocalSong("a"), LocalSong("b") };
        fixture.Player.Mode = PlayMode.SingleLoop;
        await fixture.Player.PlayAsync(new(songs, songs[0]));

        fixture.Audio.RaiseCompleted();
        await fixture.Background.WaitAsync("music-player.play");

        Assert.Equal("a", fixture.Player.CurrentMusic?.Id);
        Assert.Equal(new[] { "a", "a" }, fixture.Audio.LoadedUrls);
    }

    [Fact]
    public async Task Shuffle_uses_the_controlled_random_adapter()
    {
        await using var fixture = new PlayerFixture(random: new FixedRandom(2));
        var songs = new[] { LocalSong("a"), LocalSong("b"), LocalSong("c") };
        fixture.Player.Mode = PlayMode.Shuffle;
        await fixture.Player.PlayAsync(new(songs, songs[0]));

        var result = await fixture.Player.NextAsync();

        Assert.Equal("c", result.Music?.Id);
    }

    [Fact]
    public async Task Alternative_sources_fail_in_order_then_the_first_playable_match_wins()
    {
        var sources = new ControlledPlaybackSources(["b", "c"]);
        var requested = NetworkSong("original", "a");
        requested.PlayUrl = "bad-a";
        sources.AddAlternative("b", NetworkSong("match-b", "b"), "bad-b");
        sources.AddAlternative("c", NetworkSong("match-c", "c"), "good-c");
        await using var fixture = new PlayerFixture(sources: sources);
        fixture.Audio.FailingUrls.UnionWith(["bad-a", "bad-b"]);

        var result = await fixture.Player.PlayAsync(new([requested], requested));

        Assert.Equal(PlaybackResultStatus.Playing, result.Status);
        Assert.Equal("c", result.Music?.Source);
        Assert.Equal(new[] { "bad-a", "bad-b", "good-c" }, fixture.Audio.LoadedUrls);
        Assert.Equal(new[] { "b", "c" }, sources.SearchedSources);
        Assert.Single(fixture.Announcer.Announcements, item =>
            item.Kind == AnnouncementKind.Success);
    }

    [Fact]
    public async Task All_sources_fail_at_the_fixed_limit_and_announce_one_understandable_error()
    {
        var sourceIds = Enumerable.Range(1, 7).Select(index => $"s{index}").ToArray();
        var sources = new ControlledPlaybackSources(sourceIds);
        var requested = NetworkSong("original", "a");
        foreach (var sourceId in sourceIds)
            sources.AddAlternative(sourceId, NetworkSong($"match-{sourceId}", sourceId), null);
        await using var fixture = new PlayerFixture(sources: sources);

        var result = await fixture.Player.PlayAsync(new([requested], requested));

        Assert.Equal(PlaybackResultStatus.Failed, result.Status);
        Assert.Equal(sourceIds.Take(5), sources.SearchedSources);
        var error = Assert.Single(fixture.Announcer.Announcements, item =>
            item.Kind == AnnouncementKind.Error);
        Assert.Contains("已尝试所有可用音源", error.Text);
    }

    [Fact]
    public async Task New_queue_cancels_old_generation_before_it_can_publish_or_advance()
    {
        await using var fixture = new PlayerFixture(blockedUrl: "old");
        var oldMusic = LocalSong("old");
        var newMusic = LocalSong("new");
        var oldPlay = fixture.Player.PlayAsync(new([oldMusic], oldMusic));
        await fixture.Audio.BlockedLoadStarted.Task;

        var newPlay = fixture.Player.PlayAsync(new([newMusic], newMusic));
        var results = await Task.WhenAll(oldPlay, newPlay);

        Assert.Equal(PlaybackResultStatus.Superseded, results[0].Status);
        Assert.Equal(PlaybackResultStatus.Playing, results[1].Status);
        Assert.Same(newMusic, fixture.Player.CurrentMusic);
        Assert.Equal(PlaybackResultStatus.QueueCompleted, (await fixture.Player.NextAsync()).Status);
        Assert.Equal(0, fixture.Background.ActiveCount);
    }

    [Fact]
    public async Task Replacing_queue_cancels_precache_and_removes_its_owned_temp_file()
    {
        var content = new BlockingContent();
        using var http = new HttpClient(new SingleResponseHandler(content));
        await using var fixture = new PlayerFixture(httpClient: http);
        var current = LocalSong("current");
        var next = NetworkSong("next", "fixture");
        next.PlayUrl = "https://fixture.invalid/next.mp3";
        await fixture.Player.PlayAsync(new([current, next], current));
        await content.ReadStarted.Task;
        Assert.Single(Directory.EnumerateFiles(fixture.CacheDirectory, "lxmusic_pre_*.tmp"));

        var replacement = LocalSong("replacement");
        await fixture.Player.PlayAsync(new([replacement], replacement));
        await fixture.Background.WaitAsync("music-player.precache");

        Assert.Empty(Directory.EnumerateFiles(fixture.CacheDirectory));
        Assert.Equal(1, content.DisposeCount);
    }

    private static MusicInfo LocalSong(string id) => new()
    {
        Id = id,
        Source = "Local",
        Title = id,
        Artist = "fixture",
        Duration = "03:00",
        PlayUrl = id
    };

    private static MusicInfo NetworkSong(string id, string source) => new()
    {
        Id = id,
        Source = source,
        Title = "同一首歌",
        Artist = "同一歌手",
        Album = "同一专辑",
        Duration = "03:00"
    };

    private sealed class PlayerFixture : IAsyncDisposable
    {
        private readonly string _directory =
            Directory.CreateTempSubdirectory("LXMusicAccessible.Player-").FullName;
        private readonly HttpClient _httpClient;
        private readonly MusicPlayerService _implementation;

        public PlayerFixture(
            ControlledPlaybackSources? sources = null,
            string? blockedUrl = null,
            HttpClient? httpClient = null,
            Random? random = null)
        {
            Audio = new ControlledAudioPlayer(blockedUrl);
            Sources = sources ?? new ControlledPlaybackSources([]);
            Announcer = new RecordingAnnouncer();
            Background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            CacheDirectory = Path.Combine(_directory, "cache");
            _httpClient = httpClient ?? new HttpClient(new RejectAllHandler());
            _implementation = new MusicPlayerService(
                NullLogger<MusicPlayerService>.Instance,
                Audio,
                Sources,
                Announcer,
                Background,
                _httpClient,
                CacheDirectory,
                random);
        }

        public ControlledAudioPlayer Audio { get; }
        public ControlledPlaybackSources Sources { get; }
        public RecordingAnnouncer Announcer { get; }
        public BackgroundWorkHost Background { get; }
        public IPlaybackCoordinator Player => _implementation;
        public string CacheDirectory { get; }

        public async ValueTask DisposeAsync()
        {
            await _implementation.DisposeAsync();
            await Background.DisposeAsync();
            _httpClient.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ControlledPlaybackSources(
        IReadOnlyList<string> sourceIds) : IPlaybackSourceAdapter
    {
        private readonly Dictionary<string, (MusicInfo Music, string? Url)> _alternatives =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> SearchedSources { get; } = [];

        public void AddAlternative(string sourceId, MusicInfo music, string? url) =>
            _alternatives[sourceId] = (music, url);

        public IReadOnlyList<string> GetAlternativeSourceIds(string currentSource) => sourceIds;

        public Task<string?> ResolveAsync(
            MusicInfo music,
            string quality,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = _alternatives.Values.FirstOrDefault(entry =>
                ReferenceEquals(entry.Music, music));
            return Task.FromResult(match.Music is null ? music.PlayUrl : match.Url);
        }

        public Task<IReadOnlyList<MusicInfo>> FindAlternativesAsync(
            MusicInfo music,
            string sourceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchedSources.Add(sourceId);
            IReadOnlyList<MusicInfo> result = _alternatives.TryGetValue(sourceId, out var entry)
                ? [entry.Music]
                : [];
            return Task.FromResult(result);
        }
    }

    private sealed class ControlledAudioPlayer(string? blockedUrl = null) : IAudioPlayer
    {
        public TaskCompletionSource BlockedLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> LoadedUrls { get; } = [];
        public HashSet<string> FailingUrls { get; } = new(StringComparer.Ordinal);
        public double Volume { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
        public int CurrentOutputDeviceIndex => -1;
        public AudioPlayerState State { get; set; }
        public event EventHandler<AudioPlayerState>? StateChanged { add { } remove { } }
        public event EventHandler? PlaybackCompleted;

        public async Task LoadAsync(string url, CancellationToken cancellationToken = default)
        {
            LoadedUrls.Add(url);
            if (FailingUrls.Contains(url)) throw new InvalidOperationException("fixture failure");
            if (!string.Equals(url, blockedUrl, StringComparison.Ordinal)) return;
            BlockedLoadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task PlayAsync()
        {
            State = AudioPlayerState.Playing;
            return Task.CompletedTask;
        }

        public void RaiseCompleted() => PlaybackCompleted?.Invoke(this, EventArgs.Empty);
        public void Pause() => State = AudioPlayerState.Paused;
        public void Stop() => State = AudioPlayerState.Stopped;
        public string? GetActualQuality() => null;
        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => [];
        public bool SwitchOutputDevice(int index) => false;
        public void SeekTo(TimeSpan position) => Position = position;
        public void Dispose() { }
    }

    private sealed class FixedRandom(int index) : Random
    {
        public override int Next(int maxValue) => index % maxValue;
    }

    private sealed class RejectAllHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected HTTP request in playback test.");
    }

    private sealed class SingleResponseHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class BlockingContent : HttpContent
    {
        private int _disposed;
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 1;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
