using System.Net;
using System.Net.Http;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>Focused audit coverage for playback-order selection and stale completion cancellation.</summary>
public sealed class PlaybackOrderStateAuditTests
{
    [Theory]
    [InlineData(PlayMode.Sequential, "a", "b", "c", null)]
    [InlineData(PlayMode.Loop, "a", "b", "c", "a")]
    [InlineData(PlayMode.Shuffle, "c", "a", "c", "a")]
    [InlineData(PlayMode.SingleLoop, "a", "b", "b", "b")]
    public async Task Every_mode_selects_expected_manual_and_natural_targets(
        PlayMode mode,
        string previousTarget,
        string nextTarget,
        string naturalTarget,
        string? secondNaturalTarget)
    {
        await using var fixture = new PlayerFixture(random: new SequenceRandom([2, 0, 2, 0]));
        var songs = new[] { LocalSong("a"), LocalSong("b"), LocalSong("c") };
        fixture.Player.Mode = mode;
        await fixture.Player.PlayAsync(new PlaybackRequest(songs, songs[1]));

        Assert.Equal(previousTarget, (await fixture.Player.PreviousAsync()).Music?.Id);
        Assert.Equal(nextTarget, (await fixture.Player.NextAsync()).Music?.Id);

        fixture.Audio.RaiseCompleted();
        await fixture.Background.WaitAsync(PlayerFixture.PlaySlot);
        Assert.Equal(naturalTarget, fixture.Player.CurrentMusic?.Id);

        fixture.Audio.RaiseCompleted();
        await fixture.Background.WaitAsync(PlayerFixture.PlaySlot);
        if (secondNaturalTarget is null)
        {
            Assert.Equal(PlaybackResultStatus.QueueCompleted, (await fixture.Player.NextAsync()).Status);
            Assert.False(fixture.Player.IsPlaying);
        }
        else
        {
            Assert.Equal(secondNaturalTarget, fixture.Player.CurrentMusic?.Id);
        }
    }

    [Fact]
    public async Task Zero_item_queue_reports_no_change_in_both_directions()
    {
        await using var fixture = new PlayerFixture();

        Assert.Equal(PlaybackResultStatus.NoChange, (await fixture.Player.NextAsync()).Status);
        Assert.Equal(PlaybackResultStatus.NoChange, (await fixture.Player.PreviousAsync()).Status);
        Assert.Null(fixture.Player.CurrentMusic);
    }

    [Theory]
    [InlineData(PlayMode.Sequential, false)]
    [InlineData(PlayMode.Loop, true)]
    [InlineData(PlayMode.Shuffle, true)]
    [InlineData(PlayMode.SingleLoop, true)]
    public async Task One_item_queue_applies_every_mode_on_natural_completion(
        PlayMode mode,
        bool shouldReplay)
    {
        await using var fixture = new PlayerFixture();
        var song = LocalSong("one");
        fixture.Player.Mode = mode;
        await fixture.Player.PlayAsync(new PlaybackRequest([song], song));

        fixture.Audio.RaiseCompleted();
        await fixture.Background.WaitAsync(PlayerFixture.PlaySlot);

        Assert.Equal("one", fixture.Player.CurrentMusic?.Id);
        Assert.Equal(shouldReplay ? ["one", "one"] : ["one"], fixture.Audio.LoadedUrls);
        Assert.Equal(shouldReplay, fixture.Player.IsPlaying);
    }

    [Theory]
    [InlineData(PlayMode.Sequential)]
    [InlineData(PlayMode.Loop)]
    [InlineData(PlayMode.Shuffle)]
    [InlineData(PlayMode.SingleLoop)]
    public async Task Replay_restarts_playing_and_resumes_paused_in_every_mode(PlayMode mode)
    {
        await using var fixture = new PlayerFixture();
        var songs = new[] { LocalSong("a"), LocalSong("b") };
        fixture.Player.Mode = mode;
        await fixture.Player.PlayAsync(new PlaybackRequest(songs, songs[0]));

        var restarted = await fixture.Player.ReplayAsync();
        Assert.Equal(PlaybackResultStatus.Playing, restarted.Status);
        Assert.Equal("a", restarted.Music?.Id);
        Assert.True(fixture.Player.IsPlaying);
        Assert.Equal(["a", "a"], fixture.Audio.LoadedUrls);

        fixture.Player.Pause();
        Assert.True(fixture.Player.IsPaused);
        var resumed = await fixture.Player.ReplayAsync();
        Assert.Equal(PlaybackResultStatus.Playing, resumed.Status);
        Assert.Equal("a", resumed.Music?.Id);
        Assert.True(fixture.Player.IsPlaying);
        Assert.Equal(["a", "a"], fixture.Audio.LoadedUrls);
    }

    [Fact]
    public async Task Active_playback_immediately_uses_a_changed_mode()
    {
        await using var fixture = new PlayerFixture();
        var songs = new[] { LocalSong("a"), LocalSong("b") };
        await fixture.Player.PlayAsync(new PlaybackRequest(songs, songs[0]));

        fixture.Player.Mode = PlayMode.SingleLoop;
        fixture.Audio.RaiseCompleted();
        await fixture.Background.WaitAsync(PlayerFixture.PlaySlot);
        Assert.Equal("a", fixture.Player.CurrentMusic?.Id);

        fixture.Player.Mode = PlayMode.Loop;
        fixture.Audio.RaiseCompleted();
        await fixture.Background.WaitAsync(PlayerFixture.PlaySlot);
        Assert.Equal("b", fixture.Player.CurrentMusic?.Id);
        Assert.Equal(new[] { "a", "a", "b" }, fixture.Audio.LoadedUrls);
    }

    [Fact]
    public async Task Sequential_queue_end_stops_playback_and_ignores_stale_completion()
    {
        await using var fixture = new PlayerFixture();
        var songs = new[] { LocalSong("a"), LocalSong("b") };
        var completed = new List<PlaybackQueueOrigin>();
        fixture.Player.QueueCompleted += (_, args) => completed.Add(args.Origin);
        await fixture.Player.PlayAsync(new PlaybackRequest(
            songs,
            songs[0],
            Origin: PlaybackQueueOrigin.Search));

        fixture.Audio.RaiseCompleted();
        await fixture.Background.WaitAsync(PlayerFixture.PlaySlot);
        fixture.Audio.RaiseCompleted();
        await fixture.Background.WaitAsync(PlayerFixture.PlaySlot);

        Assert.Equal([PlaybackQueueOrigin.Search], completed);
        Assert.Equal("b", fixture.Player.CurrentMusic?.Id);
        Assert.Equal(new[] { "a", "b" }, fixture.Audio.LoadedUrls);
        Assert.False(fixture.Player.IsPlaying);
    }

    [Fact]
    public async Task Stop_cancels_a_stale_natural_completion_before_it_selects_the_next_track()
    {
        await using var fixture = new PlayerFixture();
        var songs = new[] { LocalSong("a"), LocalSong("b"), LocalSong("c") };
        await fixture.Player.PlayAsync(new PlaybackRequest(songs, songs[0]));

        fixture.Audio.RaiseCompleted();
        await fixture.Background.WaitAsync(PlayerFixture.PlaySlot);
        fixture.Player.Stop();
        fixture.Audio.RaiseCompleted();
        await fixture.Background.WaitAsync(PlayerFixture.PlaySlot);

        Assert.Equal("b", fixture.Player.CurrentMusic?.Id);
        Assert.Equal(new[] { "a", "b" }, fixture.Audio.LoadedUrls);
        Assert.False(fixture.Player.IsPlaying);
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

    private sealed class PlayerFixture : IAsyncDisposable
    {
        public const string PlaySlot = "music-player.play";

        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "LXMusicAccessible.PlaybackOrder-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _httpClient;
        private readonly MusicPlayerService _player;

        public PlayerFixture(Random? random = null)
        {
            Audio = new CompletedEventAudioPlayer();
            Announcer = new RecordingAnnouncer();
            Background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            Directory.CreateDirectory(_directory);
            _httpClient = new HttpClient(new RejectAllHandler());
            _player = new MusicPlayerService(
                NullLogger<MusicPlayerService>.Instance,
                Audio,
                new NullPlaybackSources(),
                Announcer,
                Background,
                _httpClient,
                Path.Combine(_directory, "cache"),
                random);
        }

        public CompletedEventAudioPlayer Audio { get; }
        public RecordingAnnouncer Announcer { get; }
        public BackgroundWorkHost Background { get; }
        public IPlaybackCoordinator Player => _player;

        public async ValueTask DisposeAsync()
        {
            await _player.DisposeAsync();
            await Background.DisposeAsync();
            _httpClient.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class SequenceRandom(IReadOnlyList<int> indexes) : Random
    {
        private int _position;

        public override int Next(int maxValue) => indexes[_position++ % indexes.Count] % maxValue;
    }

    private sealed class RejectAllHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected HTTP request in playback test.");
    }

    private sealed class NullPlaybackSources : IPlaybackSourceAdapter
    {
        public IReadOnlyList<string> GetAlternativeSourceIds(string currentSource) => [];
        public Task<PlaybackSourceResolution?> ResolveAsync(
            MusicInfo music,
            string quality,
            CancellationToken cancellationToken) => Task.FromResult<PlaybackSourceResolution?>(null);
        public Task<IReadOnlyList<MusicInfo>> FindAlternativesAsync(
            MusicInfo music,
            string sourceId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MusicInfo>>([]);
        public Task<PlaybackSourceResolution?> ResolveExactAsync(
            MusicInfo music,
            string sourceId,
            string quality,
            CancellationToken cancellationToken) => Task.FromResult<PlaybackSourceResolution?>(null);
    }

    private sealed class CompletedEventAudioPlayer : IAudioPlayer
    {
        public List<string> LoadedUrls { get; } = [];
        public double Volume { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
        public AudioPlayerState State { get; set; }
        public int CurrentOutputDeviceIndex => -1;
#pragma warning disable 67
        public event EventHandler<AudioPlayerState>? StateChanged;
        public event EventHandler? PlaybackCompleted;
#pragma warning restore 67

        public Task LoadAsync(string url, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedUrls.Add(url);
            return Task.CompletedTask;
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
}
