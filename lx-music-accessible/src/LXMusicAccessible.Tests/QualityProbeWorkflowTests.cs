using System;
using System.IO;
using System.Net;
using System.Net.Http;
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

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "quality-probe-test.log"), "start\n");
        var flacActual = AudioQualityInspector.Identify(FlacBytes(bitsPerSample: 24, sampleRate: 48_000));
        Assert.Equal("flac24bit", flacActual);
        var first = await workflow.ProbeAsync(song);
        var second = await workflow.ProbeAsync(song);

        Assert.Equal(QualityAvailability.Playable, first.Options.Single(item => item.Quality == "320k").Status);
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
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var quality = request.RequestUri!.AbsolutePath.TrimStart('/');
            Bodies.TryGetValue(quality, out var bytes);
            bytes ??= [];
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class FakeCandidateSource(IReadOnlyList<SourceQualityCandidate> candidates) : IQualityCandidateSource
    {
        private int _resolveCalls;
        public int ResolveCalls => _resolveCalls;

        public Task<IReadOnlyList<SourceQualityCandidate>> GetCandidatesAsync(
            MusicInfo music,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(candidates);

        public Task<string?> ResolveAsync(
            MusicInfo music,
            SourceQualityCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resolveCalls);
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
