using System.Collections.Concurrent;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ManualSourceSwitchWorkflowTests
{
    private static readonly MusicInfo Target = new()
    {
        Id = "netease-1",
        Source = "netease",
        Title = "同一首歌",
        Artist = "同一位歌手",
        Album = "同一张专辑",
        Duration = "03:00"
    };

    [Fact]
    public async Task Only_enabled_matching_builtin_and_script_interfaces_participate()
    {
        var gateway = new RecordingGateway
        {
            Interfaces =
            [
                ManualSourceInterface.BuiltIn("netease", "网易云音乐", Target.Source, ["128k", "320k"]),
                ManualSourceInterface.Script("script-a", "声明网易脚本", Target.Source, ["128k"]),
                ManualSourceInterface.Script("script-disabled", "禁用脚本", Target.Source, ["128k"], isEnabled: false),
                ManualSourceInterface.Script("script-qq", "QQ脚本", "qq", ["128k"])
            ]
        };
        gateway.AddCandidate("netease", Candidate("match-netease", "netease"));
        var workflow = CreateWorkflow(gateway, new RecordingExactPlayback());

        var result = await workflow.DiscoverAsync(Target);

        Assert.Equal(["netease", "script-a"], result.Options.Select(option => option.SourceId).Distinct());
        Assert.All(result.Options, option => Assert.Equal(Target.Source, option.Platform));
    }

    [Fact]
    public async Task Builtin_candidates_require_strict_same_song_and_do_not_use_near_matches()
    {
        var gateway = new RecordingGateway
        {
            Interfaces = [ManualSourceInterface.BuiltIn("qq", "QQ音乐", "qq", ["128k"])]
        };
        gateway.AddCandidate("qq", Candidate("near-title", "qq", title: "近似歌名"));
        gateway.AddCandidate("qq", Candidate("near-duration", "qq", duration: "04:00"));
        gateway.AddCandidate("qq", Candidate("exact", "qq"));
        var workflow = CreateWorkflow(gateway, new RecordingExactPlayback());

        var result = await workflow.DiscoverAsync(Target);

        var option = Assert.Single(result.Options);
        Assert.Equal("exact", option.Candidate.Id);
    }

    [Fact]
    public async Task Only_resolved_playable_options_are_returned_deduplicated_and_deterministically_ordered()
    {
        var gateway = new RecordingGateway
        {
            Interfaces =
            [
                ManualSourceInterface.BuiltIn("netease", "网易云音乐", Target.Source, ["320k", "128k"]),
                ManualSourceInterface.Script("script-a", "脚本A", Target.Source, ["128k", "320k"])
            ]
        };
        gateway.AddCandidate("netease", Candidate("duplicate", Target.Source));
        gateway.AddCandidate("netease", Candidate("duplicate", Target.Source));
        gateway.AddCandidate("script-a", Candidate("script", Target.Source));
        gateway.Resolve = (_, _, quality, _) => quality == "128k"
            ? Task.FromResult<string?>($"https://fixture.test/{quality}")
            : Task.FromResult<string?>(null);
        var workflow = CreateWorkflow(gateway, new RecordingExactPlayback());

        var result = await workflow.DiscoverAsync(Target);

        Assert.Equal(
            [("netease", "128k"), ("script-a", "128k")],
            result.Options.Select(option => (option.SourceId, option.Quality)));
        Assert.All(result.Options, option =>
            Assert.Contains(option.SourceName, option.DisplayLabel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Query_budget_cancels_pending_gateway_work_and_reports_timeout()
    {
        var gateway = new RecordingGateway
        {
            Interfaces = [ManualSourceInterface.BuiltIn("netease", "网易云音乐", Target.Source, ["128k"])],
            SearchDelay = TimeSpan.FromMilliseconds(250)
        };
        var workflow = CreateWorkflow(gateway, new RecordingExactPlayback(), budget: TimeSpan.FromMilliseconds(30));

        var result = await workflow.DiscoverAsync(Target);

        Assert.Equal(ManualSourceDiscoveryStatus.Timeout, result.Status);
        Assert.Empty(result.Options);
        Assert.True(gateway.SearchCancellationObserved);
    }

    [Fact]
    public async Task External_cancellation_cancels_the_active_query()
    {
        var gateway = new RecordingGateway
        {
            Interfaces = [ManualSourceInterface.BuiltIn("netease", "网易云音乐", Target.Source, ["128k"])],
            SearchDelay = TimeSpan.FromMilliseconds(250)
        };
        var workflow = CreateWorkflow(gateway, new RecordingExactPlayback());
        using var cancellation = new CancellationTokenSource();

        var query = workflow.DiscoverAsync(Target, cancellation.Token);
        await cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.True(gateway.SearchCancellationObserved);
    }

    [Fact]
    public async Task Enter_uses_the_selected_candidate_interface_quality_and_user_initiated_trigger()
    {
        var gateway = new RecordingGateway
        {
            Interfaces = [ManualSourceInterface.BuiltIn("qq", "QQ音乐", "qq", ["128k"])]
        };
        gateway.AddCandidate("qq", Candidate("qq-1", "qq"));
        var discovery = CreateWorkflow(gateway, new RecordingExactPlayback());
        var discovered = await discovery.DiscoverAsync(Target);
        var original = new MusicInfo { Id = "netease-1", Source = "netease", Title = "旧", Artist = "歌" };
        var queue = new List<MusicInfo> { original, Candidate("netease-2", "netease") };
        var playback = new RecordingExactPlayback();

        var workflow = CreateWorkflow(gateway, playback);
        var result = await workflow.PlayAsync(new(
            queue,
            Target,
            Assert.Single(discovered.Options),
            PlaybackQueueOrigin.Search));

        Assert.Equal(PlaybackResultStatus.Playing, result.Status);
        var request = Assert.IsType<ManualSourceExactPlaybackRequest>(playback.LastRequest);
        Assert.Equal(PlaybackTrigger.UserInitiated, request.Request.Trigger);
        Assert.Equal("128k", request.Request.Quality);
        Assert.Equal("qq-1", request.Option.Candidate.Id);
        Assert.Equal("qq", request.Option.SourceId);
        Assert.Equal(2, request.Request.Queue.Count);
        Assert.Equal("netease-1", request.Request.Selection.Id);
    }

    private static ManualSourceDiscoveryWorkflow CreateWorkflow(
        IManualSourceGateway gateway,
        IExactPlaybackCoordinator playback,
        TimeSpan? budget = null) =>
        new(gateway, playback, Microsoft.Extensions.Logging.Abstractions.NullLogger<ManualSourceDiscoveryWorkflow>.Instance, budget);

    private static MusicInfo Candidate(
        string id,
        string source,
        string? title = null,
        string? duration = null) => new()
    {
        Id = id,
        Source = source,
        Title = title ?? Target.Title,
        Artist = Target.Artist,
        Album = Target.Album,
        Duration = duration ?? Target.Duration
    };

    private sealed class RecordingGateway : IManualSourceGateway
    {
        public List<ManualSourceInterface> Interfaces { get; init; } = [];
        private readonly Dictionary<string, List<MusicInfo>> _candidates = new(StringComparer.OrdinalIgnoreCase);
        public Func<MusicInfo, string, string, CancellationToken, Task<string?>>? Resolve { get; set; }
        public TimeSpan SearchDelay { get; init; }
        public bool SearchCancellationObserved { get; private set; }

        public void AddCandidate(string sourceId, MusicInfo candidate)
        {
            if (!_candidates.TryGetValue(sourceId, out var list))
                _candidates[sourceId] = list = [];
            list.Add(candidate);
        }

        public IReadOnlyList<ManualSourceInterface> GetPlaybackInterfaces(MusicInfo target) => Interfaces;

        public async Task<IReadOnlyList<MusicInfo>> SearchCandidatesAsync(
            MusicInfo target,
            string sourceId,
            CancellationToken cancellationToken)
        {
            try
            {
                if (SearchDelay > TimeSpan.Zero)
                    await Task.Delay(SearchDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SearchCancellationObserved = true;
                throw;
            }

            return _candidates.GetValueOrDefault(sourceId, []);
        }

        public Task<string?> ResolveExactAsync(
            MusicInfo candidate,
            string sourceId,
            string quality,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Resolve?.Invoke(candidate, sourceId, quality, cancellationToken)
                ?? Task.FromResult<string?>("https://fixture.test/resolved");
        }
    }

    private sealed class RecordingExactPlayback : IExactPlaybackCoordinator
    {
        public object? LastRequest { get; private set; }

        public Task<PlaybackResult> PlayExactAsync(PlaybackRequest request, ManualSourceOption option)
        {
            LastRequest = new ManualSourceExactPlaybackRequest(request, option);
            return Task.FromResult(new PlaybackResult(
                PlaybackResultStatus.Playing,
                option.Candidate,
                request.Origin,
                "正在播放",
                request.Trigger));
        }
    }

    private sealed record ManualSourceExactPlaybackRequest(
        PlaybackRequest Request,
        ManualSourceOption Option);
}
