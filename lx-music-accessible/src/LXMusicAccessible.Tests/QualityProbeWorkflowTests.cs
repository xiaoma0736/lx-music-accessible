using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class QualityProbeWorkflowTests
{
    [Fact]
    public async Task Probe_marks_validated_media_playable_and_caches_result()
    {
        var handler = new FakeQualityHttpHandler();
        handler.Bodies["320k"] = new byte[70_000];
        handler.Bodies["flac24bit"] = FlacBytes(bitsPerSample: 24, sampleRate: 48_000);
        using var httpClient = new HttpClient(handler);
        
        var source = new FakeCandidateSource([
            new("interface-a", "接口 A", 1, "320k"),
            new("interface-b", "接口 B", 2, "flac24bit")
        ]);
        var workflow = new QualityProbeWorkflow(source, httpClient);
        var song = Song();

        var flacActual = AudioQualityInspector.Identify(FlacBytes(bitsPerSample: 24, sampleRate: 48_000));
        Assert.Equal("flac24bit", flacActual);
        var first = await workflow.ProbeAsync(song);
        var second = await workflow.ProbeAsync(song);

        Assert.Equal(QualityAvailability.Playable, first.Options.Single(item => item.Quality == "320k").Status);
        Assert.True(first.Options.Single(item => item.Quality == "320k").Downloadable);
        Assert.Equal(QualityAvailability.Playable, first.Options.Single(item => item.Quality == "flac24bit").Status);
        Assert.Equal("接口 A", first.Options.Single(item => item.Quality == "320k").InterfaceName);
        Assert.Equal(first.Options.Single(item => item.Quality == "320k").Status, second.Options.Single(item => item.Quality == "320k").Status);
        Assert.Equal(2, source.ResolveCalls);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Probe_marks_failed_resolution_and_short_payload_unavailable()
    {
        var handler = new FakeQualityHttpHandler();
        handler.Bodies["320k"] = new byte[16];
        using var httpClient = new HttpClient(handler);
        
        var source = new FakeCandidateSource([
            new("unresolved", "不可解析接口", 1, "master"),
            new("short", "短数据接口", 2, "320k")
        ]);
        var workflow = new QualityProbeWorkflow(source, httpClient);

        var result = await workflow.ProbeAsync(Song());

        Assert.Equal(QualityAvailability.Unavailable, result.Options.Single(item => item.Quality == "master").Status);
        Assert.Equal(QualityAvailability.Unavailable, result.Options.Single(item => item.Quality == "320k").Status);
    }

    [Fact]
    public async Task Probe_rejects_a_lower_actual_flac_than_requested()
    {
        var handler = new FakeQualityHttpHandler();
        handler.Bodies["master"] = FlacBytes(bitsPerSample: 16, sampleRate: 44_100);
        using var httpClient = new HttpClient(handler);
        
        var source = new FakeCandidateSource([
            new("interface", "接口", 1, "master")
        ]);
        var workflow = new QualityProbeWorkflow(source, httpClient);

        var result = await workflow.ProbeAsync(Song());

        Assert.Equal(QualityAvailability.Unavailable, result.Options.Single(item => item.Quality == "master").Status);
    }

    [Fact]
    public async Task Probe_accepts_extended_source_quality_ids()
    {
        var handler = new FakeQualityHttpHandler();
        handler.Bodies["viper"] = new byte[70_000];
        using var httpClient = new HttpClient(handler);
        
        var source = new FakeCandidateSource([
            new("viper-interface", "蝰蛇接口", 1, "viper")
        ]);
        var workflow = new QualityProbeWorkflow(source, httpClient);

        var result = await workflow.ProbeAsync(Song());

        var option = Assert.Single(result.Options);
        Assert.Equal("viper", option.Quality);
        Assert.Equal("蝰蛇音质", option.Label);
        Assert.Equal(QualityAvailability.Playable, option.Status);
    }

    [Fact]
    public async Task Probe_reports_a_lossless_fallback_as_playable_but_not_downloadable()
    {
        var handler = new FakeQualityHttpHandler();
        handler.Bodies["hires"] = FlacBytes(bitsPerSample: 16, sampleRate: 44_100);
        using var httpClient = new HttpClient(handler);
        var source = new FakeCandidateSource(
            [new("interface", "接口", 1, "hires")],
            _ => Task.FromResult<string?>("https://quality.test/hires"));
        var workflow = new QualityProbeWorkflow(source, httpClient);

        var result = await workflow.ProbeAsync(Song());

        var option = Assert.Single(result.Options);
        Assert.Equal(QualityAvailability.Playable, option.Status);
        Assert.Equal("flac", option.ActualQuality);
        Assert.False(option.Downloadable);
    }

    [Fact]
    public async Task Probe_keeps_probing_when_one_candidate_throws()
    {
        var handler = new FakeQualityHttpHandler();
        handler.Bodies["320k"] = new byte[70_000];
        using var httpClient = new HttpClient(handler);
        var source = new FakeCandidateSource(
            [
                new("broken", "坏接口", 1, "320k"),
                new("healthy", "好接口", 2, "320k")
            ],
            candidate => candidate.InterfaceId == "broken"
                ? throw new HttpRequestException("resolver down")
                : Task.FromResult<string?>("https://quality.test/320k"));
        var workflow = new QualityProbeWorkflow(source, httpClient);

        var result = await workflow.ProbeAsync(Song());

        var option = Assert.Single(result.Options);
        Assert.Equal(QualityAvailability.Playable, option.Status);
        Assert.Equal("好接口", option.InterfaceName);
    }

    [Fact]
    public async Task Probe_failure_is_returned_once_and_retried_on_the_next_request()
    {
        var handler = new FakeQualityHttpHandler();
        handler.Bodies["320k"] = new byte[70_000];
        using var httpClient = new HttpClient(handler);
        var failing = true;
        var source = new FakeCandidateSource(
            [new("flaky", "不稳定接口", 1, "320k")],
            _ => failing
                ? throw new HttpRequestException("boom")
                : Task.FromResult<string?>("https://quality.test/320k"));
        var workflow = new QualityProbeWorkflow(source, httpClient);

        var first = await workflow.ProbeAsync(Song());
        Assert.Equal(QualityAvailability.Failed, Assert.Single(first.Options).Status);
        var callsAfterFailure = source.ResolveCalls;

        failing = false;
        var second = await workflow.ProbeAsync(Song());

        Assert.True(source.ResolveCalls > callsAfterFailure);
        Assert.Equal(QualityAvailability.Playable, Assert.Single(second.Options).Status);
    }

    [Fact]
    public async Task Probe_identifies_itself_with_a_user_agent()
    {
        var handler = new FakeQualityHttpHandler();
        handler.Bodies["320k"] = new byte[70_000];
        using var httpClient = new HttpClient(handler);
        var source = new FakeCandidateSource([new("interface", "接口", 1, "320k")]);
        var workflow = new QualityProbeWorkflow(source, httpClient);

        await workflow.ProbeAsync(Song());

        Assert.Contains(handler.UserAgents, value => !string.IsNullOrWhiteSpace(value));
    }

    [Fact]
    public async Task Probe_rejects_large_html_or_json_error_payloads()
    {
        var handler = new FakeQualityHttpHandler();
        var errorPage = Encoding.UTF8.GetBytes("<html><body>" + new string('x', 70_000) + "</body></html>");
        handler.Bodies["320k"] = errorPage;
        handler.ContentTypes["320k"] = "text/html";
        handler.Bodies["flac"] = errorPage;
        using var httpClient = new HttpClient(handler);
        var source = new FakeCandidateSource(
            [
                new("typed", "声明错误页接口", 1, "320k"),
                new("untyped", "无类型错误页接口", 2, "flac")
            ],
            candidate => Task.FromResult<string?>($"https://quality.test/{candidate.Quality}"));
        var workflow = new QualityProbeWorkflow(source, httpClient);

        var result = await workflow.ProbeAsync(Song());

        Assert.All(result.Options, option => Assert.Equal(QualityAvailability.Unavailable, option.Status));
    }

    [Fact]
    public void Inspector_recognizes_iso_base_media_and_adts_frames()
    {
        var container = new byte[64];
        "ftyp"u8.CopyTo(container.AsSpan(4));
        Assert.Equal("m4a", AudioQualityInspector.Identify(container));

        var adts = new byte[64];
        adts[0] = 0xFF;
        adts[1] = 0xF1;
        Assert.Equal("aac", AudioQualityInspector.Identify(adts));
    }

    [Fact]
    public async Task Probe_bounds_parallel_quality_groups()
    {
        var qualities = new[]
        {
            "jymaster", "master", "atmos_plus", "atmos", "dolby", "sky",
            "jyeffect", "hires", "flac24bit", "flac", "320k", "192k"
        };
        var handler = new FakeQualityHttpHandler();
        using var httpClient = new HttpClient(handler);
        var active = 0;
        var peak = 0;
        var source = new FakeCandidateSource(
            qualities.Select(quality => new SourceQualityCandidate("interface", "接口", 1, quality)).ToArray(),
            async _ =>
            {
                var current = Interlocked.Increment(ref active);
                UpdatePeak(ref peak, current);
                await Task.Delay(40);
                Interlocked.Decrement(ref active);
                return null;
            });
        var workflow = new QualityProbeWorkflow(source, httpClient);

        var result = await workflow.ProbeAsync(Song());

        Assert.Equal(qualities.Length, result.Options.Count);
        Assert.InRange(peak, 2, 4);
    }

    [Fact]
    public async Task Invalidating_all_forces_a_fresh_round_after_a_source_change()
    {
        var handler = new FakeQualityHttpHandler();
        handler.Bodies["320k"] = new byte[70_000];
        using var httpClient = new HttpClient(handler);
        var source = new FakeCandidateSource([new("interface", "接口", 1, "320k")]);
        var workflow = new QualityProbeWorkflow(source, httpClient);

        await workflow.ProbeAsync(Song());
        await workflow.ProbeAsync(Song());
        Assert.Equal(1, source.ResolveCalls);

        workflow.InvalidateAll();
        await workflow.ProbeAsync(Song());

        Assert.Equal(2, source.ResolveCalls);
    }

    private static void UpdatePeak(ref int peak, int value)
    {
        var current = Volatile.Read(ref peak);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref peak, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private static MusicInfo Song() => new()
    {
        Id = "song-id",
        Source = "qq",
        Title = "Quality song",
        Artist = "Fixture"
    };

    private static byte[] FlacBytes(int bitsPerSample, int sampleRate)
    {
        var bytes = new byte[80];
        "fLaC"u8.CopyTo(bytes);
        bytes[4] = 0;
        bytes[5] = 0;
        bytes[6] = 0;
        bytes[7] = 18;
        ulong packed = ((ulong)(uint)sampleRate << 44)
            | ((ulong)(uint)(bitsPerSample - 1) << 36)
            | (1UL << 35);
        for (var index = 18; index < 26; index++)
            bytes[index] = (byte)((packed >> (56 - ((index - 18) * 8))) & 0xFF);
        return bytes;
    }

    private sealed class FakeQualityHttpHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Bodies = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> UserAgents = new();
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            lock (UserAgents)
                UserAgents.Add(request.Headers.UserAgent.ToString());
            var quality = request.RequestUri!.AbsolutePath.TrimStart('/');
            Bodies.TryGetValue(quality, out var bytes);
            bytes ??= [];
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            if (ContentTypes.TryGetValue(quality, out var contentType))
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class FakeCandidateSource : IQualityCandidateSource
    {
        private readonly IReadOnlyList<SourceQualityCandidate> _candidates;
        private readonly Func<SourceQualityCandidate, Task<string?>>? _resolve;
        private int _resolveCalls;

        public FakeCandidateSource(IReadOnlyList<SourceQualityCandidate> candidates)
            : this(candidates, null)
        {
        }

        public FakeCandidateSource(
            IReadOnlyList<SourceQualityCandidate> candidates,
            Func<SourceQualityCandidate, Task<string?>>? resolve)
        {
            _candidates = candidates;
            _resolve = resolve;
        }

        public int ResolveCalls => Volatile.Read(ref _resolveCalls);

        public Task<IReadOnlyList<SourceQualityCandidate>> GetCandidatesAsync(
            MusicInfo music,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_candidates);

        public Task<string?> ResolveAsync(
            MusicInfo music,
            SourceQualityCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resolveCalls);
            if (_resolve is not null) return _resolve(candidate);
            return Task.FromResult<string?>(candidate.Quality switch
            {
                "320k" => $"https://quality.test/{candidate.Quality}",
                "flac24bit" => $"https://quality.test/{candidate.Quality}",
                "viper" => $"https://quality.test/{candidate.Quality}",
                _ => null
            });
        }
    }
}
