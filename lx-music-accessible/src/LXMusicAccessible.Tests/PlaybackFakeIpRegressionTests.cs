using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
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

[Collection("Global network settings")]
public sealed class PlaybackFakeIpRegressionTests
{
    private static readonly Uri FastResolveUri = new("https://fast-source.test/value");
    private static readonly Uri SlowResolveUri = new("https://slow-source.test/value");
    private static readonly Uri AudioUri = new("https://audio-source.test/song.mp3");
    private static readonly Uri ProxyRedirectUri = new("http://proxy-redirect.invalid/start");
    private static readonly Uri ProxyCdnUri = new("http://proxy-cdn.invalid/value");
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
    private const string ProxyOnlyScript = """
        /** @name Proxy-only source @version 1.0.0 */
        lx.on(lx.EVENT_NAMES.request, function () {
          return fetch('http://proxy-only.invalid/value').then(function (response) {
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

    [Fact]
    public async Task System_proxy_only_profile_reaches_audio_instead_of_failing_all_candidates()
    {
        await using var proxy = new FixedResponseProxy(AudioUri.AbsoluteUri);
        using var proxyScope = new DefaultProxyScope(proxy.Uri);
        var audio = new TimedAudioPlayer();
        await using var host = await TestApplicationHost.StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(new ScriptNetworkClient(
                ResolveOnlyThroughProxyAsync)));
            services.Replace(ServiceDescriptor.Singleton<IAudioPlayer>(audio));
        });
        host.HttpHandler.Register(HttpMethod.Get, AudioUri, AudioResponse);
        var dataService = host.Services.GetRequiredService<IDataService>();
        await dataService.SaveMusicSourcesAsync([
            TrustedSource("proxy-a", ProxyOnlyScript, 10),
            TrustedSource("proxy-b", ProxyOnlyScript, 11)
        ]);
        await host.Services.GetRequiredService<MusicSourceService>().InitializeScriptSourcesAsync();
        var music = new MusicInfo
        {
            Id = "proxy-only-test",
            Source = "qq",
            Title = "想说爱你不容易",
            Artist = "大双二双"
        };
        audio.StartMeasurement();

        var result = await host.Services.GetRequiredService<IPlaybackCoordinator>().PlayAsync(
            new PlaybackRequest([music], music, Origin: PlaybackQueueOrigin.Search));

        Assert.True(
            result.Status == PlaybackResultStatus.Playing,
            $"All playback candidates failed: status={result.Status}; " +
            $"audioLoads={audio.LoadCount}; proxyRequests={proxy.RequestCount}");
        Assert.Equal(1, audio.LoadCount);
        Assert.True(proxy.RequestCount >= 1);
    }

    [Fact]
    public async Task System_proxy_does_not_forward_a_hostname_that_resolves_private()
    {
        await using var proxy = new FixedResponseProxy("blocked");
        using var proxyScope = new DefaultProxyScope(proxy.Uri);
        using var client = new ScriptNetworkClient((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new[] { IPAddress.Parse("10.0.0.8") });
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://private-target.test/value");

        var exception = await Assert.ThrowsAsync<ScriptNetworkException>(() =>
            client.SendAsync(request));

        Assert.Equal("network_target_blocked", exception.Code);
        Assert.Equal(0, proxy.RequestCount);
    }

    [Fact]
    public async Task System_proxy_does_not_forward_a_private_literal()
    {
        await using var proxy = new FixedResponseProxy("blocked");
        using var proxyScope = new DefaultProxyScope(proxy.Uri);
        using var client = new ScriptNetworkClient(ResolveOnlyThroughProxyAsync);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://10.0.0.8/value");

        var exception = await Assert.ThrowsAsync<ScriptNetworkException>(() =>
            client.SendAsync(request));

        Assert.Equal("network_target_blocked", exception.Code);
        Assert.Equal(0, proxy.RequestCount);
    }

    [Fact]
    public async Task System_proxy_supports_remote_dns_across_a_public_CDN_redirect()
    {
        await using var proxy = new FixedResponseProxy(
            AudioUri.AbsoluteUri,
            ProxyRedirectUri,
            ProxyCdnUri);
        using var proxyScope = new DefaultProxyScope(proxy.Uri);
        using var client = new ScriptNetworkClient(ResolveOnlyThroughProxyAsync);
        using var request = new HttpRequestMessage(HttpMethod.Get, ProxyRedirectUri);

        var response = await client.SendAsync(request);

        Assert.Equal(AudioUri.AbsoluteUri, Encoding.UTF8.GetString(response.Body));
        Assert.Equal(2, proxy.RequestCount);
    }

    private static Task<IPAddress[]> ResolveOnlyThroughProxyAsync(
        string host,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new SocketException((int)SocketError.HostNotFound);
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
        public int LoadCount { get; private set; }
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
            LoadCount++;
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

    private sealed class FixedResponseProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly byte[] _successResponse;
        private readonly byte[]? _redirectResponse;
        private readonly Uri? _redirectFrom;
        private readonly Task _server;
        private int _requestCount;

        public FixedResponseProxy(string body, Uri? redirectFrom = null, Uri? redirectTo = null)
        {
            _successResponse = CreateSuccessResponse(body);
            _redirectFrom = redirectFrom;
            _redirectResponse = redirectTo is null
                ? null
                : Encoding.ASCII.GetBytes(
                    "HTTP/1.1 302 Found\r\n" +
                    $"Location: {redirectTo.AbsoluteUri}\r\n" +
                    "Content-Length: 0\r\n" +
                    "Connection: close\r\n\r\n");
            _listener.Start();
            Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _server = ServeAsync(_cancellation.Token);
        }

        private static byte[] CreateSuccessResponse(string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            return Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {bytes.Length}\r\n" +
                "Connection: close\r\n\r\n")
                .Concat(bytes)
                .ToArray();
        }

        public Uri Uri { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    await RespondAsync(client, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected when the fixture is disposed.
            }
        }

        private async Task RespondAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                var used = 0;
                while (used < buffer.Length)
                {
                    var read = await stream.ReadAsync(
                        buffer.AsMemory(used, buffer.Length - used),
                        cancellationToken);
                    if (read == 0) return;
                    used += read;
                    if (Encoding.ASCII.GetString(buffer, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal))
                        break;
                }
                Interlocked.Increment(ref _requestCount);
                var requestText = Encoding.ASCII.GetString(buffer, 0, used);
                var response = _redirectFrom is not null
                    && _redirectResponse is not null
                    && requestText.StartsWith(
                        $"GET {_redirectFrom.AbsoluteUri} ",
                        StringComparison.Ordinal)
                        ? _redirectResponse
                        : _successResponse;
                await stream.WriteAsync(response, cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try { await _server; }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
                // AcceptTcpClientAsync may surface listener shutdown as a socket error.
            }
            _cancellation.Dispose();
        }
    }

    private sealed class DefaultProxyScope : IDisposable
    {
        private readonly IWebProxy _original = HttpClient.DefaultProxy;

        public DefaultProxyScope(Uri proxyUri)
        {
            HttpClient.DefaultProxy = new WebProxy(proxyUri) { BypassProxyOnLocal = false };
        }

        public void Dispose() => HttpClient.DefaultProxy = _original;
    }
}

[CollectionDefinition("Global network settings", DisableParallelization = true)]
public sealed class GlobalNetworkSettingsCollectionDefinition;
