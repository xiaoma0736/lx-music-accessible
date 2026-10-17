using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Forms;
using LXMusicAccessible.App;
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
    public async Task Every_declared_quality_is_returned_and_playability_is_preserved()
    {
        var gateway = new RecordingGateway
        {
            Interfaces =
            [
                ManualSourceInterface.BuiltIn("source-a", "音源A", "a", ["320k", "128k"]),
                ManualSourceInterface.BuiltIn("source-b", "音源B", "b", ["128k", "flac"])
            ]
        };
        gateway.AddCandidate("source-a", Candidate("a-1", "a"));
        gateway.AddCandidate("source-b", Candidate("b-1", "b"));
        gateway.Resolve = (_, _, quality, _) => Task.FromResult<string?>(
            quality == "320k" ? "https://fixture.test/a-320" : null);
        var workflow = CreateWorkflow(gateway, new RecordingExactPlayback());

        var result = await workflow.DiscoverAsync(Target);

        Assert.Equal(ManualSourceDiscoveryStatus.Success, result.Status);
        Assert.Equal(
            [
                ("source-a", "320k", true),
                ("source-a", "128k", false),
                ("source-b", "flac", false),
                ("source-b", "128k", false)
            ],
            result.Options.Select(option => (option.SourceId, option.Quality, option.IsPlayable)).ToArray());
        Assert.Equal("找到 1 个可播放音源，已隐藏 3 个不可用项", result.Message);
        Assert.Contains("不可用", Assert.Single(result.Options, option => option is { SourceId: "source-a", Quality: "128k" }).DisplayLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_qualities_of_a_source_start_before_any_resolution_finishes()
    {
        var started = new[]
        {
            new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var next = -1;
        var gateway = new RecordingGateway
        {
            Interfaces = [ManualSourceInterface.BuiltIn("source-a", "音源A", "a", ["flac", "320k", "128k"])]
        };
        gateway.AddCandidate("source-a", Candidate("a-1", "a"));
        gateway.Resolve = async (_, _, quality, _) =>
        {
            var index = Interlocked.Increment(ref next);
            started[index].TrySetResult(null);
            await Task.WhenAll(started.Select(item => item.Task));
            return quality == "flac" ? "https://fixture.test/flac" : null;
        };

        var workflow = CreateWorkflow(gateway, new RecordingExactPlayback());

        var result = await workflow.DiscoverAsync(Target);

        Assert.Equal(ManualSourceDiscoveryStatus.Success, result.Status);
        Assert.All(started, item => Assert.True(item.Task.IsCompleted));
        Assert.Equal(["flac", "320k", "128k"], result.Options.Select(option => option.Quality).ToArray());
    }

    [Fact]
    public async Task Discovery_bounds_concurrent_quality_resolutions()
    {
        var qualities = new[]
        {
            "jymaster", "master", "atmos_plus", "atmos", "dolby", "sky",
            "jyeffect", "hires", "flac24bit", "flac", "320k", "192k"
        };
        var gateway = new RecordingGateway
        {
            Interfaces = [ManualSourceInterface.BuiltIn("source-a", "音源A", "a", qualities)]
        };
        gateway.AddCandidate("source-a", Candidate("a-1", "a"));
        var active = 0;
        var peak = 0;
        gateway.Resolve = async (_, _, _, _) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdatePeak(ref peak, current);
            await Task.Delay(30);
            Interlocked.Decrement(ref active);
            return null;
        };
        var workflow = CreateWorkflow(gateway, new RecordingExactPlayback());

        var result = await workflow.DiscoverAsync(Target);

        Assert.Equal(ManualSourceDiscoveryStatus.Success, result.Status);
        Assert.Equal(qualities.Length, result.Options.Count);
        Assert.InRange(peak, 2, 8);
    }

    [Fact]
    public async Task Timeout_keeps_playable_options_found_before_the_budget_expires()
    {
        var gateway = new RecordingGateway
        {
            Interfaces = [ManualSourceInterface.BuiltIn("source-a", "音源A", "a", ["320k", "flac"])]
        };
        gateway.AddCandidate("source-a", Candidate("a-1", "a"));
        gateway.Resolve = async (_, _, quality, cancellationToken) =>
        {
            if (quality == "320k") return "https://fixture.test/320k";
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return null;
        };
        var workflow = CreateWorkflow(gateway, new RecordingExactPlayback(), budget: TimeSpan.FromMilliseconds(150));

        var result = await workflow.DiscoverAsync(Target);

        Assert.Equal(ManualSourceDiscoveryStatus.Success, result.Status);
        var option = Assert.Single(result.Options);
        Assert.Equal("320k", option.Quality);
        Assert.True(option.IsPlayable);
        Assert.Contains("超时", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_source_window_cancels_the_previous_search_when_restarted()
    {
        var announcer = new RecordingAnnouncer();
        var workflow = new BlockingManualWorkflow();
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var form = new ManualSourceSwitchForm(announcer, workflow, Target, _ => false);
            form.Show();
            Application.DoEvents();
            var search = Assert.IsType<Button>(Assert.Single(form.Controls.Find("搜索可用音源", true)));
            await WaitFor(() => workflow.Starts == 1);
            var first = workflow.TokenAt(0);

            search.PerformClick();
            await WaitFor(() => workflow.Starts == 2);

            Assert.True(first.IsCancellationRequested);
            Assert.False(workflow.TokenAt(1).IsCancellationRequested);

            form.Close();
            await WaitFor(() => workflow.TokenAt(1).IsCancellationRequested);
        });
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
    [Fact]
    public async Task Unavailable_option_is_never_sent_to_exact_playback()
    {
        var gateway = new RecordingGateway
        {
            Interfaces = [ManualSourceInterface.BuiltIn("source-a", "音源A", "a", ["flac"])]
        };
        gateway.AddCandidate("source-a", Candidate("a-1", "a"));
        var playback = new RecordingExactPlayback();
        var workflow = CreateWorkflow(gateway, playback);
        var option = ManualSourceOption.Unavailable(
            "source-a",
            "音源A",
            "a",
            Candidate("a-1", "a"),
            "flac",
            "解析失败");

        var result = await workflow.PlayAsync(new(
            [Target],
            Target,
            option,
            PlaybackQueueOrigin.Search));

        Assert.Equal(PlaybackResultStatus.Failed, result.Status);
        Assert.Equal("解析失败", result.Message);
        Assert.Null(playback.LastRequest);
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
            [
                ("netease", "320k", false),
                ("netease", "128k", true),
                ("script-a", "320k", false),
                ("script-a", "128k", true)
            ],
            result.Options.Select(option => (option.SourceId, option.Quality, option.IsPlayable)).ToArray());
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
        cancellation.Cancel();

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

    [Fact]
    public void Manual_source_window_hides_unavailable_candidates_and_blocks_exact_playback()
    {
        var announcer = new RecordingAnnouncer();
        var gateway = new RecordingGateway
        {
            Interfaces =
            [
                ManualSourceInterface.BuiltIn("source-a", "音源A", "a", ["flac"]),
                ManualSourceInterface.BuiltIn("source-b", "音源B", "a", ["320k"])
            ]
        };
        gateway.AddCandidate("source-a", Candidate("a-1", "a"));
        gateway.AddCandidate("source-b", Candidate("b-1", "a"));
        gateway.Resolve = (_, sourceId, quality, _) => Task.FromResult<string?>(
            sourceId == "source-b" && quality == "320k" ? "https://fixture.test/320k" : null);
        var exact = new RecordingExactPlayback();
        var workflow = CreateWorkflow(gateway, exact);
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var form = new ManualSourceSwitchForm(announcer, workflow, Target, _ => false);
            form.Show();
            Application.DoEvents();
            var list = Assert.IsType<ListBox>(Assert.Single(form.Controls.Find("可用音源", true)));
            await WaitFor(() => list.Items.Count > 0);
            var status = Assert.IsType<Label>(Assert.Single(form.Controls.Find("ManualSwitchStatus", true)));

            Assert.Single(list.Items);
            Assert.Equal("音源B，极高，同一首歌，同一位歌手，a", list.Items[0].ToString());
            Assert.Equal("找到 1 个可播放音源，已隐藏 1 个不可用项", status.Text);
            Assert.DoesNotContain(
                list.Items.Cast<object>().Select(item => item.ToString()),
                text => text!.Contains("不可用", StringComparison.Ordinal));

            list.SelectedIndex = 0;
            var method = typeof(ManualSourceSwitchForm).GetMethod("PlayAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            await (Task)method!.Invoke(form, Array.Empty<object?>())!;
            await Task.Delay(25);

            // The playable row plays exactly what the user selected.
            Assert.Single(exact.Requests);
            Assert.Equal("source-b", exact.Requests[0].Option.SourceId);
            Assert.Equal("320k", exact.Requests[0].Request.Quality);
        });
    }

    [Fact]
    public void Manual_source_window_reports_when_nothing_is_playable()
    {
        var option = ManualSourceOption.Unavailable(
            "source-a", "音源A", "a", Candidate("a-1", "a"), "flac", "解析失败");
        var announcer = new RecordingAnnouncer();
        var workflow = new StaticManualWorkflow(ManualSourceDiscoveryResult.Success([option]));
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var form = new ManualSourceSwitchForm(announcer, workflow, Target, _ => false);
            form.Show();
            Application.DoEvents();
            var list = Assert.IsType<ListBox>(Assert.Single(form.Controls.Find("可用音源", true)));
            var status = Assert.IsType<Label>(Assert.Single(form.Controls.Find("ManualSwitchStatus", true)));
            await WaitFor(() => status.Text == "没有找到可播放音源");

            Assert.Empty(list.Items);
        });
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Application.DoEvents();
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition(), "The awaited UI condition was not reached.");
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

    private sealed class StaticManualWorkflow(ManualSourceDiscoveryResult result) : IManualSourceDiscoveryWorkflow
    {
        public Task<ManualSourceDiscoveryResult> DiscoverAsync(MusicInfo target, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public Task<PlaybackResult> PlayAsync(ManualSourcePlaybackRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, request.Option.Candidate, request.Origin, "playing"));

        public void Dispose() { }
    }

    private sealed class BlockingManualWorkflow : IManualSourceDiscoveryWorkflow
    {
        private readonly List<CancellationToken> _tokens = new();
        private int _starts;

        public int Starts => Volatile.Read(ref _starts);

        public CancellationToken TokenAt(int index)
        {
            lock (_tokens)
                return _tokens[index];
        }

        public Task<ManualSourceDiscoveryResult> DiscoverAsync(
            MusicInfo target,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _starts);
            lock (_tokens)
                _tokens.Add(cancellationToken);
            var completion = new TaskCompletionSource<ManualSourceDiscoveryResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public Task<PlaybackResult> PlayAsync(
            ManualSourcePlaybackRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.Failed, null, request.Origin, "not used"));

        public void Dispose() { }
    }

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
        public List<ManualSourceExactPlaybackRequest> Requests { get; } = new();

        public Task<PlaybackResult> PlayExactAsync(PlaybackRequest request, ManualSourceOption option)
        {
            LastRequest = new ManualSourceExactPlaybackRequest(request, option);
            Requests.Add((ManualSourceExactPlaybackRequest)LastRequest);
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
