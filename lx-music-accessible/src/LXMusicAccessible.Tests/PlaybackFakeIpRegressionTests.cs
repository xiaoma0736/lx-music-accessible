using System.Diagnostics;
using System.Net;
using System.Net.Http;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class PlaybackFakeIpRegressionTests
{
    private static readonly Uri FastResolveUri = new("https://fast-source.test/value");
    private static readonly Uri SlowResolveUri = new("https://slow-source.test/value");
    private static readonly Uri AudioUri = new("https://audio-source.test/song.mp3");
    private const string FastScript = """
        /** @name Fast direct source @version 1.0.0 */
        lx.on(lx.EVENT_NAMES.request, function () {
          return fetch('https://fast-source.test/value').then(function (response) {
            return response.text();
          });
        });
        """;
    private const string SlowScript = """
        /** @name Slow fallback source @version 1.0.0 */
        lx.on(lx.EVENT_NAMES.request, function () {
          return fetch('https://slow-source.test/value').then(function (response) {
            return response.text();
          });
        });
        """;

    [Fact]
    public async Task Fake_ip_direct_source_reaches_audio_before_slow_fallback()
    {
        var handler = new RejectingHttpMessageHandler();
        handler.RegisterText(HttpMethod.Get, FastResolveUri, AudioUri.AbsoluteUri);
        handler.RegisterAsync(HttpMethod.Get, SlowResolveUri, async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(AudioUri.AbsoluteUri)
            };
        });
        var audio = new TimedAudioPlayer();
        await using var host = await TestApplicationHost.StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(new ScriptNetworkClient(
                handler,
                ResolveFixtureAddressAsync)));
            services.Replace(ServiceDescriptor.Singleton<IAudioPlayer>(audio));
        });
        host.HttpHandler.Register(HttpMethod.Get, AudioUri, AudioResponse);
        var dataService = host.Services.GetRequiredService<IDataService>();
        await dataService.SaveMusicSourcesAsync([
            TrustedSource("fast", FastScript, 10),
            TrustedSource("slow", SlowScript, 11)
        ]);

        await host.Services.GetRequiredService<MusicSourceService>().InitializeScriptSourcesAsync();
        var music = new MusicInfo
        {
            Id = "000I6w-test",
            Source = "qq",
            Title = "想说爱你不容易",
            Artist = "大双二双"
        };
        audio.StartMeasurement();
        var timer = Stopwatch.StartNew();

        var result = await host.Services.GetRequiredService<IPlaybackCoordinator>().PlayAsync(
            new PlaybackRequest([music], music, Origin: PlaybackQueueOrigin.Search));

        Assert.Equal(PlaybackResultStatus.Playing, result.Status);
        Assert.True(
            audio.LoadedAt < TimeSpan.FromMilliseconds(150),
            $"Direct playback reached audio after {audio.LoadedAt.TotalMilliseconds:F0} ms");
        Assert.Single(handler.Requests, request =>
            request == (HttpMethod.Get.Method, FastResolveUri.AbsoluteUri));
        Assert.DoesNotContain(handler.Requests, request =>
            request == (HttpMethod.Get.Method, SlowResolveUri.AbsoluteUri));
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(150));
    }

    private static Task<IPAddress[]> ResolveFixtureAddressAsync(
        string host,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var address = string.Equals(host, FastResolveUri.Host, StringComparison.OrdinalIgnoreCase)
            ? "198.18.68.192"
            : "93.184.216.34";
        return Task.FromResult(new[] { IPAddress.Parse(address) });
    }

    private static MusicSource TrustedSource(string id, string script, int priority) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        ScriptUrl = $"local://{id}",
        ScriptContent = script,
        IsEnabled = true,
        Priority = priority,
        Type = SourceType.Custom,
        TrustState = ScriptTrustState.UserApproved,
        ContentSha256 = ScriptMusicSource.ComputeSha256(script)
    };

    private static HttpResponseMessage AudioResponse()
    {
        var content = new ByteArrayContent(new byte[70_000]);
        content.Headers.ContentType = new("audio/mpeg");
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
    }

    private sealed class TimedAudioPlayer : IAudioPlayer
    {
        private readonly Stopwatch _clock = new();
        public TimeSpan LoadedAt { get; private set; } = Timeout.InfiniteTimeSpan;
        public double Volume { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.Zero;
        public int CurrentOutputDeviceIndex => -1;
        public AudioPlayerState State { get; set; }
        public event EventHandler<AudioPlayerState>? StateChanged { add { } remove { } }
        public event EventHandler? PlaybackCompleted { add { } remove { } }

        public void StartMeasurement()
        {
            LoadedAt = Timeout.InfiniteTimeSpan;
            _clock.Restart();
        }

        public Task LoadAsync(string url, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedAt = _clock.Elapsed;
            return Task.CompletedTask;
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
        public void Dispose() { }
    }

}
