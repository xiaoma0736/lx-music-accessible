using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class QualityProbeWorkflowTests
{
    [Fact]
    public async Task Probe_lists_only_source_declared_qualities_in_fidelity_order()
    {
        var music = Song();
        var source = new FakeCandidates(new[]
        {
            new SourceQualityCandidate("qq", "QQ", 2, "flac"),
            new SourceQualityCandidate("qq", "QQ", 2, "320k"),
            new SourceQualityCandidate("migu", "咪咕", 1, "flac24bit"),
        });
        var workflow = Create(source);

        var result = await workflow.ProbeAsync(music);

        Assert.Equal(["flac24bit", "flac", "320k"], result.Options.Select(option => option.Quality).ToArray());
        Assert.All(result.Options, option =>
        {
            Assert.Equal(QualityAvailability.Playable, option.Status);
            Assert.Null(option.ActualQuality);
            Assert.Null(option.DownloadQuality);
        });
        Assert.Equal("咪咕", Assert.Single(result.Options, option => option.Quality == "flac24bit").InterfaceName);
        Assert.Equal("QQ", Assert.Single(result.Options, option => option.Quality == "flac").InterfaceName);
        Assert.Equal("音源声明音质已加载", result.Message);
    }

    [Fact]
    public async Task Probe_reports_when_the_song_platform_has_no_declared_qualities()
    {
        var workflow = Create(new FakeCandidates([]));

        var result = await workflow.ProbeAsync(Song());

        Assert.Empty(result.Options);
        Assert.Equal("当前音源没有声明可用音质", result.Message);
    }

    [Fact]
    public async Task Probe_does_not_resolve_media_urls()
    {
        var source = new FakeCandidates([new SourceQualityCandidate("qq", "QQ", 1, "320k")]);
        var workflow = Create(source);

        await workflow.ProbeAsync(Song());

        Assert.Equal(0, source.ResolveCalls);
    }

    [Fact]
    public async Task Cached_result_is_reused_until_invalidated()
    {
        var source = new FakeCandidates([new SourceQualityCandidate("qq", "QQ", 1, "320k")]);
        var workflow = Create(source);
        var music = Song();

        await workflow.ProbeAsync(music);
        await workflow.ProbeAsync(music);
        Assert.Equal(1, source.CandidateCalls);

        var cached = workflow.GetCached(music);
        Assert.Single(cached.Options);

        workflow.Invalidate(music);
        Assert.Empty(workflow.GetCached(music).Options);
        await workflow.ProbeAsync(music);
        Assert.Equal(2, source.CandidateCalls);
    }

    [Fact]
    public async Task Invalidate_all_drops_results_for_every_song()
    {
        var source = new FakeCandidates([new SourceQualityCandidate("qq", "QQ", 1, "320k")]);
        var workflow = Create(source);
        await workflow.ProbeAsync(Song("one"));
        await workflow.ProbeAsync(Song("two"));

        workflow.InvalidateAll();

        Assert.Empty(workflow.GetCached(Song("one")).Options);
        Assert.Empty(workflow.GetCached(Song("two")).Options);
    }

    private static QualityProbeWorkflow Create(FakeCandidates source) => new(source);

    private static MusicInfo Song(string id = "song") => new()
    {
        Id = id,
        Source = "qq",
        Title = "Song",
        Artist = "Artist",
    };

    private sealed class FakeCandidates(IReadOnlyList<SourceQualityCandidate> candidates) : IQualityCandidateSource
    {
        private int _candidateCalls; public int CandidateCalls => _candidateCalls;
        private int _resolveCalls; public int ResolveCalls => _resolveCalls;

        public Task<IReadOnlyList<SourceQualityCandidate>> GetCandidatesAsync(
            MusicInfo music,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _candidateCalls);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(candidates);
        }

        public Task<string?> ResolveAsync(
            MusicInfo music,
            SourceQualityCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resolveCalls);
            throw new InvalidOperationException("Declaration-only quality probing must not resolve media URLs.");
        }
    }
}
