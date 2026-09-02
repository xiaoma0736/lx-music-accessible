using System.Collections.Concurrent;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ManualSourceExactPlaybackTests
{
    private static readonly MusicInfo Original = new()
    {
        Id = "netease-original",
        Source = "netease",
        Title = "同一首歌",
        Artist = "同一位歌手",
        Album = "同一张专辑",
        Duration = "03:00"
    };

    private static readonly MusicInfo Candidate = new()
    {
        Id = "qq-exact",
        Source = "qq",
        Title = Original.Title,
        Artist = Original.Artist,
        Album = Original.Album,
        Duration = Original.Duration
    };

    [Fact]
    public async Task Exact_playback_resolves_selected_source_fresh_and_preserves_queue_and_default_quality()
    {
        var audio = new ExactAudioPlayer();
        audio.QualitiesByUrl["selected-320"] = "320k";
        var sources = new ExactSourceAdapter { Url = "selected-320" };
        await using var player = CreatePlayer(audio, sources);
        player.DefaultQuality = "128k";
        var next = new MusicInfo { Id = "next", Source = "netease", Title = "下一首", Artist = "歌手" };
        var option = ManualSourceOption.Create(
            "qq-script", "QQ脚本", "qq", Candidate, "320k", "QQ脚本，320k");

        var result = await player.PlayExactAsync(
            new([Original, next], Original, option.Quality, PlaybackQueueOrigin.Search),
            option);

        Assert.Equal(PlaybackResultStatus.Playing, result.Status);
        Assert.Equal("320k", sources.LastQuality);
        Assert.Equal(["qq-script"], sources.ExactSourceIds.ToArray());
        Assert.Equal(["selected-320"], audio.LoadedUrls.ToArray());
        Assert.Equal("128k", player.DefaultQuality);
        Assert.Equal("qq-exact", player.CurrentMusic?.Id);
        Assert.Equal("next", (await player.NextAsync()).Music?.Id);
    }

    [Fact]
    public async Task Exact_playback_rejects_platform_mismatch_with_accessible_failure_message()
    {
        var audio = new ExactAudioPlayer();
        var sources = new ExactSourceAdapter { Url = "selected" };
        await using var player = CreatePlayer(audio, sources);
        var option = ManualSourceOption.Create(
            "qq-script", "QQ??", "netease", Candidate, "320k", "QQ???320k");

        var result = await player.PlayExactAsync(
            new([Original], Original, option.Quality, PlaybackQueueOrigin.Search),
            option);

        Assert.Equal(PlaybackResultStatus.Failed, result.Status);
        Assert.Equal("\u6362\u6e90\u5019\u9009\u4e0d\u662f\u4e25\u683c\u540c\u66f2", result.Message);
        Assert.Equal(PlaybackTrigger.UserInitiated, result.Trigger);
        Assert.Empty(audio.LoadedUrls);
    }

    [Fact]
    public async Task Exact_playback_failure_does_not_fallback_or_change_default_quality()
    {
        var audio = new ExactAudioPlayer();
        var sources = new ExactSourceAdapter();
        await using var player = CreatePlayer(audio, sources);
        player.DefaultQuality = "128k";
        var option = ManualSourceOption.Create(
            "qq-script", "QQ脚本", "qq", Candidate, "320k", "QQ脚本，320k");

        var result = await player.PlayExactAsync(
            new([Original], Original, option.Quality, PlaybackQueueOrigin.Search),
            option);

        Assert.Equal(PlaybackResultStatus.Failed, result.Status);
        Assert.Equal("320k", sources.LastQuality);
        Assert.Empty(audio.LoadedUrls);
        Assert.Empty(sources.ResolvedQualities);
        Assert.Empty(sources.FindAlternativeSourceIds);
        Assert.Equal("128k", player.DefaultQuality);
    }

    private static MusicPlayerService CreatePlayer(
        IAudioPlayer audio,
        IPlaybackSourceAdapter sources) => new(
            NullLogger<MusicPlayerService>.Instance,
            audio,
            sources,
            new ExactAnnouncer(),
            new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance));

    private sealed class ExactSourceAdapter : IPlaybackSourceAdapter
    {
        public ConcurrentQueue<string> ResolvedQualities { get; } = new();
        public ConcurrentQueue<string> FindAlternativeSourceIds { get; } = new();
        public ConcurrentQueue<string> ExactSourceIds { get; } = new();
        public string? LastQuality { get; private set; }
        public string? Url { get; init; }

        public IReadOnlyList<string> GetAlternativeSourceIds(string currentSource)
        {
            FindAlternativeSourceIds.Enqueue(currentSource);
            return ["fallback"];
        }

        public Task<string?> ResolveAsync(
            MusicInfo music,
            string quality,
            CancellationToken cancellationToken = default)
        {
            ResolvedQualities.Enqueue(quality);
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<MusicInfo>> FindAlternativesAsync(
            MusicInfo music,
            string sourceId,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<MusicInfo> result = [];
            return Task.FromResult(result);
        }

        public Task<string?> ResolveExactAsync(
            MusicInfo music,
            string sourceId,
            string quality,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastQuality = quality;
            ExactSourceIds.Enqueue(sourceId);
            return Task.FromResult(Url);
        }
    }

    private sealed class ExactAudioPlayer : IAudioPlayer
    {
        public ConcurrentQueue<string> LoadedUrls { get; } = new();
        public Dictionary<string, string> QualitiesByUrl { get; } = new(StringComparer.Ordinal);
        public double Volume { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
        public int CurrentOutputDeviceIndex => -1;
        public AudioPlayerState State { get; set; } = AudioPlayerState.Stopped;
        public event EventHandler<AudioPlayerState>? StateChanged { add { } remove { } }
        public event EventHandler? PlaybackCompleted { add { } remove { } }

        public Task LoadAsync(string url, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedUrls.Enqueue(url);
            State = AudioPlayerState.Loading;
            return Task.CompletedTask;
        }

        public Task PlayAsync()
        {
            State = AudioPlayerState.Playing;
            return Task.CompletedTask;
        }

        public void Pause() => State = AudioPlayerState.Paused;
        public void Stop() => State = AudioPlayerState.Stopped;
        public string? GetActualQuality() => QualitiesByUrl.GetValueOrDefault(LoadedUrls.LastOrDefault() ?? "");
        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => [];
        public bool SwitchOutputDevice(int index) => false;
        public void SeekTo(TimeSpan position) => Position = position;
        public void Dispose() { }
    }
}

internal sealed class ExactAnnouncer : IAccessibleAnnouncer
{
    public bool SpeechEnabled { get; set; }
    public void Announce(AccessibleAnnouncement announcement) { }
}
