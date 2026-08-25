using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ScriptSecurityTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");

    [Theory]
    [InlineData("file:///windows/win.ini", "93.184.216.34")]
    [InlineData("http://user:secret@example.test/", "93.184.216.34")]
    [InlineData("http://example.test:22/", "93.184.216.34")]
    [InlineData("http://localhost/", "93.184.216.34")]
    [InlineData("http://example.test/", "127.0.0.1")]
    [InlineData("http://example.test/", "10.0.0.1")]
    [InlineData("http://example.test/", "172.16.0.1")]
    [InlineData("http://example.test/", "192.168.0.1")]
    [InlineData("http://example.test/", "169.254.169.254")]
    [InlineData("http://198.18.68.192/", "198.18.68.192")]
    [InlineData("http://[::ffff:198.18.68.192]/", "::ffff:198.18.68.192")]
    [InlineData("http://example.test/", "::1")]
    [InlineData("http://example.test/", "fe80::1")]
    [InlineData("http://example.test/", "fc00::1")]
    [InlineData("http://2130706433/", "127.0.0.1")]
    [InlineData("http://example.test/", "::ffff:127.0.0.1")]
    public async Task Non_public_script_targets_are_blocked_before_http(
        string url,
        string resolvedAddress)
    {
        var handler = new RejectingHttpMessageHandler();
        using var client = new ScriptNetworkClient(
            handler,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(resolvedAddress) }));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var exception = await Assert.ThrowsAsync<ScriptNetworkException>(() =>
            client.SendAsync(request));

        Assert.Equal("network_target_blocked", exception.Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    public async Task Public_IPv4_and_IPv6_dns_results_remain_usable(string resolvedAddress)
    {
        var uri = new Uri("https://public-address.test/value");
        var handler = new RejectingHttpMessageHandler();
        handler.RegisterText(HttpMethod.Get, uri, "ok");
        using var client = new ScriptNetworkClient(
            handler,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new[] { IPAddress.Parse(resolvedAddress) });
            });
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        var response = await client.SendAsync(request);

        Assert.Equal("ok", Encoding.UTF8.GetString(response.Body));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Direct_DNS_failure_is_reported_before_http()
    {
        var handler = new RejectingHttpMessageHandler();
        using var client = new ScriptNetworkClient(
            handler,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new SocketException((int)SocketError.HostNotFound);
            });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://missing.test/value");

        var exception = await Assert.ThrowsAsync<ScriptNetworkException>(() =>
            client.SendAsync(request));

        Assert.Equal("dns_resolution_failed", exception.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Redirect_between_public_CDN_hosts_remains_usable()
    {
        var start = new Uri("https://origin.test/start");
        var cdn = new Uri("https://cdn.test/audio");
        var handler = new RejectingHttpMessageHandler();
        handler.Register(HttpMethod.Get, start, () => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = cdn }
        });
        handler.RegisterText(HttpMethod.Get, cdn, "audio");
        using var client = CreatePublicClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, start);

        var response = await client.SendAsync(request);

        Assert.Equal("audio", Encoding.UTF8.GetString(response.Body));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Redirect_to_private_dns_result_is_blocked()
    {
        var start = new Uri("https://public.test/start");
        var handler = new RejectingHttpMessageHandler();
        handler.Register(HttpMethod.Get, start, () => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://private.test/secret") }
        });
        using var client = new ScriptNetworkClient(handler, (host, _) => Task.FromResult(
            new[] { host == "private.test" ? IPAddress.Parse("10.0.0.8") : PublicAddress }));
        using var request = new HttpRequestMessage(HttpMethod.Get, start);

        var exception = await Assert.ThrowsAsync<ScriptNetworkException>(() =>
            client.SendAsync(request));

        Assert.Equal("network_target_blocked", exception.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Dns_change_is_rechecked_on_every_redirect_hop()
    {
        var start = new Uri("https://changing.test/start");
        var next = new Uri("https://changing.test/next");
        var resolutions = 0;
        var handler = new RejectingHttpMessageHandler();
        handler.Register(HttpMethod.Get, start, () => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = next }
        });
        using var client = new ScriptNetworkClient(handler, (_, _) => Task.FromResult(
            new[] { ++resolutions == 1 ? PublicAddress : IPAddress.Loopback }));
        using var request = new HttpRequestMessage(HttpMethod.Get, start);

        var exception = await Assert.ThrowsAsync<ScriptNetworkException>(() =>
            client.SendAsync(request));

        Assert.Equal("network_target_blocked", exception.Code);
        Assert.Equal(2, resolutions);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Dns_rebinding_between_preflight_and_connection_is_blocked()
    {
        var resolutions = 0;
        using var client = new ScriptNetworkClient(
            (_, _) => Task.FromResult(
                new[] { ++resolutions == 1 ? PublicAddress : IPAddress.Loopback }),
            proxy: null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://rebind.test/value");

        var exception = await Assert.ThrowsAsync<ScriptNetworkException>(() =>
            client.SendAsync(request));

        Assert.Equal("network_target_blocked", exception.Code);
        Assert.Equal(2, resolutions);
    }

    [Fact]
    public async Task Oversized_http_response_is_rejected()
    {
        var uri = new Uri("https://public.test/large");
        var handler = new RejectingHttpMessageHandler();
        handler.Register(HttpMethod.Get, uri, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[ScriptNetworkClient.MaxResponseBytes + 1])
        });
        using var client = CreatePublicClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        var exception = await Assert.ThrowsAsync<ScriptNetworkException>(() =>
            client.SendAsync(request));

        Assert.Equal("response_limit_exceeded", exception.Code);
    }

    [Fact]
    public async Task Compressed_response_cannot_expand_past_the_limit()
    {
        var uri = new Uri("https://public.test/bomb");
        var compressed = CompressGzip(new byte[ScriptNetworkClient.MaxResponseBytes + 1]);
        var handler = new RejectingHttpMessageHandler();
        handler.Register(HttpMethod.Get, uri, () =>
        {
            var content = new ByteArrayContent(compressed);
            content.Headers.ContentEncoding.Add("gzip");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = CreatePublicClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        var exception = await Assert.ThrowsAsync<ScriptNetworkException>(() =>
            client.SendAsync(request));

        Assert.Equal("decompression_limit_exceeded", exception.Code);
    }

    [Fact]
    public async Task Cancellation_stops_a_waiting_script_request()
    {
        using var client = new ScriptNetworkClient(
            new WaitingHandler(),
            (_, _) => Task.FromResult(new[] { PublicAddress }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://public.test/wait");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendAsync(request, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Oversized_script_is_not_staged()
    {
        var handler = new RejectingHttpMessageHandler();
        using var network = CreatePublicClient(handler);
        using var source = new ScriptMusicSource(
            NullLogger<ScriptMusicSource>.Instance,
            network);

        var result = source.StageFromContent(
            new string('x', ScriptNetworkClient.MaxScriptBytes + 1),
            "large",
            "local",
            isLocal: true);

        Assert.Null(result);
        Assert.Equal("script_limit_exceeded", source.LastFailureCode);
    }

    [Fact]
    public async Task Infinite_loop_is_stopped_by_jint_limits()
    {
        using var network = CreatePublicClient(new RejectingHttpMessageHandler());
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance, network);
        var script = CreateScript("while (true) {}", "loop");
        var timer = Stopwatch.StartNew();

        var result = await engine.GetPlayUrlAsync(
            script,
            new MusicInfo { Id = "1", Title = "test" },
            "qq",
            "128k");

        Assert.Null(result);
        Assert.Contains(engine.LastError, new[]
        {
            "script_statement_limit_exceeded",
            "script_timeout"
        });
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task Jint_memory_growth_is_stopped_by_heap_limit()
    {
        using var network = CreatePublicClient(new RejectingHttpMessageHandler());
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance, network);
        var script = CreateScript(
            "var a = []; while (true) { a.push(('x').repeat(4096)); }",
            "memory");

        var result = await engine.GetPlayUrlAsync(
            script,
            new MusicInfo { Id = "1", Title = "test" },
            "qq",
            "128k");

        Assert.Null(result);
        Assert.Equal("script_memory_limit_exceeded", engine.LastError);
    }

    [Fact]
    public async Task Jint_http_bridge_cannot_reach_private_dns_results()
    {
        var handler = new RejectingHttpMessageHandler();
        using var network = new ScriptNetworkClient(
            handler,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }));
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance, network);
        var script = CreateScript(
            "return fetch('http://metadata.test/latest').then(function (r) { return r.text(); });",
            "private-http");

        var result = await engine.GetPlayUrlAsync(
            script,
            new MusicInfo { Id = "1", Title = "test" },
            "qq",
            "128k");

        Assert.Null(result);
        Assert.Equal("network_target_blocked", engine.LastError);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Jint_timeout_cancels_a_waiting_native_request()
    {
        using var network = new ScriptNetworkClient(
            new WaitingHandler(),
            (_, _) => Task.FromResult(new[] { PublicAddress }));
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance, network);
        var script = CreateScript(
            "return fetch('https://slow.test/value').then(function (r) { return r.text(); });",
            "timeout");
        var timer = Stopwatch.StartNew();

        var result = await engine.GetPlayUrlAsync(
            script,
            new MusicInfo { Id = "1", Title = "test" },
            "qq",
            "128k");

        Assert.Null(result);
        Assert.Equal("script_timeout", engine.LastError);
        Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(9));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task Jint_zlib_bridge_rejects_decompression_bombs()
    {
        var compressed = CompressZlib(new byte[ScriptNetworkClient.MaxResponseBytes + 1]);
        using var network = CreatePublicClient(new RejectingHttpMessageHandler());
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance, network);
        var script = CreateScript(
            $"return lx.utils.zlib.inflate(Buffer.from('{Convert.ToBase64String(compressed)}', 'base64')).then(function () {{ return 'unexpected'; }});",
            "zlib-bomb");

        var result = await engine.GetPlayUrlAsync(
            script,
            new MusicInfo { Id = "1", Title = "test" },
            "qq",
            "128k");

        Assert.Null(result);
        Assert.Equal("compression_output_limit_exceeded", engine.LastError);
    }

    [Fact]
    public async Task Normal_script_remains_usable_within_limits()
    {
        using var network = CreatePublicClient(new RejectingHttpMessageHandler());
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance, network);
        var script = CreateScript("return 'https://cdn.example.test/audio.mp3';", "normal");

        Assert.True(await engine.TestLoadAsync(script));
        var result = await engine.GetPlayUrlAsync(
            script,
            new MusicInfo { Id = "1", Title = "test" },
            "qq",
            "128k");

        Assert.Equal("https://cdn.example.test/audio.mp3", result);
        Assert.Null(engine.LastFailure);
    }

    private static ScriptNetworkClient CreatePublicClient(HttpMessageHandler handler)
        => new(handler, (_, _) => Task.FromResult(new[] { PublicAddress }));

    private static LxScriptInfo CreateScript(string handlerBody, string id)
        => new()
        {
            Id = id,
            Name = id,
            RawScript = $"lx.on(lx.EVENT_NAMES.request, function () {{ {handlerBody} }});"
        };

    private static byte[] CompressGzip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(input);
        return output.ToArray();
    }

    private static byte[] CompressZlib(byte[] input)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(input);
        return output.ToArray();
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }
}
