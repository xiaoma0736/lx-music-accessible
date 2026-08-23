using System.IO;
using System.Net;
using System.Net.Http;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class MusicPlayerBackgroundTests
{
    [Fact]
    public async Task Rapid_play_cancels_old_generation_before_new_state_is_published()
    {
        var audio = new ControlledAudioPlayer(blockedUrl: "old");
        var app = await TestApplicationHost.StartAsync(services =>
            services.Replace(ServiceDescriptor.Singleton<IAudioPlayer>(_ => audio)));

        var player = app.Services.GetRequiredService<IMusicPlayerService>();
        var oldMusic = Song("old");
        var newMusic = Song("new");
        var oldPlay = player.PlayAsync("old", oldMusic);
        await audio.BlockedLoadStarted.Task;

        var newPlay = player.PlayAsync("new", newMusic);
        await Task.WhenAll(oldPlay, newPlay);

        Assert.Same(newMusic, player.CurrentMusic);
        Assert.True(player.IsPlaying);
        Assert.Equal(new[] { "old", "new" }, audio.LoadedUrls);
        Assert.Equal(0, app.Services.GetRequiredService<BackgroundWorkHost>().ActiveCount);

        await app.DisposeAsync();
        Assert.Equal(1, audio.DisposeCount);
    }

    [Fact]
    public async Task Cancelling_precache_releases_response_and_removes_owned_temp_file()
    {
        var audio = new ControlledAudioPlayer();
        await using var app = await TestApplicationHost.StartAsync(services =>
            services.Replace(ServiceDescriptor.Singleton<IAudioPlayer>(_ => audio)));
        var uri = new Uri("https://fixture.invalid/next.mp3");
        var content = new BlockingContent();
        app.HttpHandler.Register(HttpMethod.Get, uri, () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var current = Song("current");
        var next = Song("next");
        next.Source = "fixture";
        next.PlayUrl = uri.AbsoluteUri;
        var player = app.Services.GetRequiredService<IMusicPlayerService>();
        player.SetQueue([current, next], current);
        await player.PlayAsync("current", current);
        await content.ReadStarted.Task;

        var cacheDirectory = Path.Combine(app.DataDirectory, "cache");
        Assert.Single(Directory.EnumerateFiles(cacheDirectory, "lxmusic_pre_*.tmp"));

        player.SetQueue([current], current);
        await app.Services.GetRequiredService<BackgroundWorkHost>()
            .WaitAsync("music-player.precache");

        Assert.Empty(Directory.EnumerateFiles(cacheDirectory));
        Assert.Equal(1, content.DisposeCount);
    }

    private static MusicInfo Song(string id) => new()
    {
        Id = id,
        Source = "Local",
        Title = id,
        Artist = "fixture"
    };

    private sealed class ControlledAudioPlayer(string? blockedUrl = null) : IAudioPlayer
    {
        private int _disposed;

        public TaskCompletionSource BlockedLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> LoadedUrls { get; } = [];
        public int DisposeCount { get; private set; }
        public double Volume { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
        public int CurrentOutputDeviceIndex => -1;
        public AudioPlayerState State { get; set; }
        public event EventHandler<AudioPlayerState>? StateChanged { add { } remove { } }
        public event EventHandler? PlaybackCompleted { add { } remove { } }

        public async Task LoadAsync(
            string url,
            CancellationToken cancellationToken = default)
        {
            LoadedUrls.Add(url);
            if (!string.Equals(url, blockedUrl, StringComparison.Ordinal)) return;
            BlockedLoadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task PlayAsync()
        {
            State = AudioPlayerState.Playing;
            return Task.CompletedTask;
        }

        public void Pause() => State = AudioPlayerState.Paused;
        public void Stop() => State = AudioPlayerState.Stopped;
        public string? GetActualQuality() => null;
        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => [];
        public bool SwitchOutputDevice(int index) => false;
        public void SeekTo(TimeSpan position) => Position = position;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            DisposeCount++;
        }
    }

    private sealed class BlockingContent : HttpContent
    {
        private int _disposed;

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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
