using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LXMusicAccessible.Tests;

[Collection("WPF accessibility semantics")]
public sealed class ManualSourceSwitchWindowTests
{
    private static readonly MusicInfo Target = new()
    {
        Id = "netease-1",
        Source = "netease",
        Title = "目标歌曲",
        Artist = "目标歌手"
    };

    [Fact]
    public async Task Window_and_playable_options_have_accessible_names_and_initial_focus_is_first_option()
    {
        var option = CreateOption();
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([option]));
        var announcer = new RecordingAnnouncer();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(workflow, announcer);
            try
            {
                window.Show();
                Pump(window.Dispatcher);

                Assert.Equal("换源播放", window.Title);
                Assert.Equal("换源选项", new ListBoxAutomationPeer(window.SourceOptionsList).GetName());
                Assert.Equal("目标歌曲 - 目标歌手", window.TargetSongText.Text);
                var first = Assert.IsType<ListBoxItem>(
                    window.SourceOptionsList.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Equal(option.DisplayLabel, AutomationProperties.GetName(first));
                Assert.Same(first, Keyboard.FocusedElement);
                Assert.Contains("\u6b63\u5728\u67e5\u8be2\u53ef\u64ad\u653e\u7684\u6362\u6e90\u9009\u9879", announcer.Messages);
                Assert.Contains("找到 1 个可播放的换源选项", announcer.Messages);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Repeated_enter_on_same_failed_option_announces_each_attempt_with_unique_activity()
    {
        var option = CreateOption();
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([option]));
        var playbackRequests = new ConcurrentQueue<ManualSourcePlaybackRequest>();
        var announcer = new RecordingAnnouncer();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(workflow, announcer, _ =>
            {
                playbackRequests.Enqueue(new([], Target, option, PlaybackQueueOrigin.Static));
                return Task.FromResult(FailedPlayback());
            });
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                var item = Assert.IsType<ListBoxItem>(
                    window.SourceOptionsList.ItemContainerGenerator.ContainerFromIndex(0));

                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);
                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);

                Assert.Equal(2, playbackRequests.Count);
                var failures = PlaybackFailures(announcer);
                Assert.Equal(2, failures.Length);
                Assert.NotEqual(failures[0].ActivityId, failures[1].ActivityId);
                Assert.All(failures, announcement =>
                    Assert.Equal("换源播放失败", announcement.Text));
                Assert.Same(item, Keyboard.FocusedElement);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Different_failed_options_announce_each_attempt()
    {
        var first = CreateOption();
        var second = ManualSourceOption.Create(
            "script-b", "脚本B", Target.Source, Candidate("second-candidate"), "flac",
            "脚本B，flac，second-candidate，目标歌手");
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([first, second]));
        var playedOptions = new ConcurrentQueue<ManualSourceOption>();
        var announcer = new RecordingAnnouncer();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(workflow, announcer, request =>
            {
                playedOptions.Enqueue(request.Option);
                return Task.FromResult(FailedPlayback());
            });
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                var firstItem = Assert.IsType<ListBoxItem>(
                    window.SourceOptionsList.ItemContainerGenerator.ContainerFromIndex(0));

                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);
                RaiseKey(firstItem, Key.Down, preview: false);
                Pump(window.Dispatcher);
                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);

                Assert.Equal([first, second], playedOptions);
                var failures = PlaybackFailures(announcer);
                Assert.Equal(2, failures.Length);
                Assert.NotEqual(failures[0].ActivityId, failures[1].ActivityId);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Playback_exception_announces_failure_and_keeps_switcher_focus()
    {
        var option = CreateOption();
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([option]));
        var announcer = new RecordingAnnouncer();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(workflow, announcer, _ => throw new InvalidOperationException("network failed"));
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                var item = Assert.IsType<ListBoxItem>(
                    window.SourceOptionsList.ItemContainerGenerator.ContainerFromIndex(0));

                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);

                Assert.Equal("换源播放失败", window.StatusText.Text);
                Assert.Contains(PlaybackFailures(announcer), a => a.Text == "换源播放失败");
                Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(window.StatusText));
                Assert.Same(item, Keyboard.FocusedElement);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Cancelled_playback_announces_cancelled_status_when_window_is_open()
    {
        var option = CreateOption();
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([option]));
        var playbackRequests = new ConcurrentQueue<ManualSourcePlaybackRequest>();
        var announcer = new RecordingAnnouncer();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(workflow, announcer, _ =>
            {
                playbackRequests.Enqueue(new([], Target, option, PlaybackQueueOrigin.Static));
                return Task.FromCanceled<PlaybackResult>(new CancellationToken(canceled: true));
            });
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);

                Assert.Single(playbackRequests);
                Assert.True(window.IsVisible);
                Assert.Equal("换源播放已取消", window.StatusText.Text);
                Assert.Contains(announcer.Announcements, a =>
                    a.Kind == AnnouncementKind.Status
                    && a.Text == "换源播放已取消"
                    && a.ActivityId!.StartsWith("manual-source-playback:", StringComparison.Ordinal));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(window.StatusText));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Empty_result_enter_reports_missing_options_without_playback_request()
    {
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([]));
        var playbackRequests = new ConcurrentQueue<ManualSourcePlaybackRequest>();
        var announcer = new RecordingAnnouncer();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(workflow, announcer, _ =>
            {
                playbackRequests.Enqueue(new([], Target, CreateOption(), PlaybackQueueOrigin.Static));
                throw new InvalidOperationException("Empty result must not start playback.");
            });
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);

                Assert.Empty(playbackRequests);
                Assert.Equal("未找到可播放的换源选项", window.StatusText.Text);
                Assert.Contains(announcer.Announcements, a =>
                    a.Kind == AnnouncementKind.Error
                    && a.Text == "未找到可播放的换源选项"
                    && a.ActivityId!.StartsWith("manual-source-query:", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(ManualSourceDiscoveryStatus.Timeout, "\u6362\u6e90\u67e5\u8be2\u8d85\u65f6")]
    [InlineData(ManualSourceDiscoveryStatus.Failed, "\u6362\u6e90\u67e5\u8be2\u5931\u8d25")]
    public async Task Query_terminal_without_message_uses_fallback_status(ManualSourceDiscoveryStatus status, string expected)
    {
        var workflow = new RecordingWorkflow(new(status, [], string.Empty));
        var announcer = new RecordingAnnouncer();
        var expectedActivityId = $"manual-source-query:{Target.Source}:{Target.Id}";

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(workflow, announcer);
            try
            {
                window.Show();
                Pump(window.Dispatcher);

                Assert.Equal(expected, window.StatusText.Text);
                Assert.Contains(announcer.Announcements, a =>
                    a.Kind == AnnouncementKind.Error
                    && a.Text == expected
                    && a.ActivityId == expectedActivityId);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Repeated_empty_result_enter_announces_each_time_with_unique_activity()
    {
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([]));
        var playbackRequests = new ConcurrentQueue<ManualSourcePlaybackRequest>();
        var announcer = new RecordingAnnouncer();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(workflow, announcer, _ =>
            {
                playbackRequests.Enqueue(new([], Target, CreateOption(), PlaybackQueueOrigin.Static));
                throw new InvalidOperationException("Empty result must not start playback.");
            });
            try
            {
                window.Show();
                Pump(window.Dispatcher);

                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);
                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);

                Assert.Empty(playbackRequests);
                var announcements = announcer.Announcements
                    .Where(a => a.Kind == AnnouncementKind.Error
                        && a.Text == "\u672a\u627e\u5230\u53ef\u64ad\u653e\u7684\u6362\u6e90\u9009\u9879"
                        && a.ActivityId!.StartsWith($"manual-source-query:{Target.Source}:{Target.Id}:empty:", StringComparison.Ordinal))
                    .ToArray();
                Assert.Equal(2, announcements.Length);
                Assert.Equal(
                    [
                        $"manual-source-query:{Target.Source}:{Target.Id}:empty:1",
                        $"manual-source-query:{Target.Source}:{Target.Id}:empty:2"
                    ],
                    announcements.Select(a => a.ActivityId));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Switcher_exposes_dynamic_status_name_keyboard_instructions_and_source_list()
    {
        var option = CreateOption();
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([option]));
        var announcer = new RecordingAnnouncer();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(workflow, announcer, _ => Task.FromResult(FailedPlayback()));
            try
            {
                window.Show();
                Pump(window.Dispatcher);

                Assert.Equal(window.StatusText.Text, AutomationProperties.GetName(window.StatusText));
                var help = Assert.IsType<TextBlock>(FindLogicalDescendant(
                    window, "方向键选择，回车播放，Esc 关闭"));
                Assert.Equal("方向键选择，回车播放，Esc 关闭", help.Text);
                Assert.Equal("换源选项", new ListBoxAutomationPeer(window.SourceOptionsList).GetName());
                Assert.Equal("目标歌曲 - 目标歌手", AutomationProperties.GetName(window.TargetSongText));

                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);
                Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(window.StatusText));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Enter_plays_selected_option_with_queue_context_and_closes_after_success()
    {
        var option = CreateOption();
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([option]));
        var playbackRequests = new ConcurrentQueue<ManualSourcePlaybackRequest>();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(
                workflow,
                new RecordingAnnouncer(),
                request =>
                {
                    playbackRequests.Enqueue(request);
                    return Task.FromResult(new PlaybackResult(
                        PlaybackResultStatus.Playing,
                        request.Option.Candidate,
                        request.Origin,
                        "正在播放"));
                });
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                Assert.Equal(option, window.SourceOptionsList.SelectedItem);
                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);

                var request = Assert.Single(playbackRequests);
                Assert.Same(option, request.Option);
                Assert.Equal("netease-1", request.Target.Id);
                Assert.Equal(["netease-1", "netease-2"], request.Queue.Select(song => song.Id));

                Assert.False(window.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Empty_result_announces_the_exact_no_result_state()
    {
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([]));
        var announcer = new RecordingAnnouncer();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(workflow, announcer);
            try
            {
                window.Show();
                Pump(window.Dispatcher);

                Assert.Equal("未找到可播放的换源选项", window.StatusText.Text);
                Assert.Contains("未找到可播放的换源选项", announcer.Messages);
                Assert.Same(window.SourceOptionsList, Keyboard.FocusedElement);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Shared_song_menu_helper_excludes_local_and_navigation_items_only_for_switch_source()
    {
        Assert.True(MainWindow.CanManualSourceSwitch(Target));
        Assert.False(MainWindow.CanManualSourceSwitch(new() { Source = "Local", Id = "local-1" }));
        Assert.False(MainWindow.CanManualSourceSwitch(new() { Source = "netease", Id = MainWindow.PagePrevId }));
        Assert.False(MainWindow.CanManualSourceSwitch(new() { Source = "netease", Id = MainWindow.PageNextId }));
        Assert.False(MainWindow.CanManualSourceSwitch(new() { Source = "netease", IsAlbum = true }));
    }

    [Fact]
    public async Task Shared_song_context_menu_contains_manual_source_switch_entry()
    {
        await RunOnStaThreadAsync(async () =>
        {
            await using var host = await TestApplicationHost.StartAsync();
            var mainWindow = host.Services.GetRequiredService<MainWindow>();
            var menu = mainWindow.BuildSongContextMenu(new ListBox());

            Assert.Contains(
                MainWindow.ManualSourceSwitchMenuHeader,
                menu.Items.OfType<MenuItem>().Select(item => item.Header.ToString()));
        });
    }

    [Fact]
    public async Task Arrow_keys_change_selection_without_playing()
    {
        var first = CreateOption();
        var second = ManualSourceOption.Create(
            "script-b", "\u811a\u672cB", Target.Source, Candidate("second-candidate"), "flac",
            "\u811a\u672cB\uff0cflac\uff0csecond-candidate\uff0c\u76ee\u6807\u6b4c\u624b");
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([first, second]));
        var playbackRequests = new ConcurrentQueue<ManualSourcePlaybackRequest>();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(workflow, new RecordingAnnouncer(), _ =>
            {
                playbackRequests.Enqueue(new([], Target, first, PlaybackQueueOrigin.Static));
                return Task.FromResult(new PlaybackResult(
                    PlaybackResultStatus.Playing, Target, PlaybackQueueOrigin.Static, "\u6b63\u5728\u64ad\u653e"));
            });
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                var firstItem = Assert.IsType<ListBoxItem>(
                    window.SourceOptionsList.ItemContainerGenerator.ContainerFromIndex(0));

                RaiseKey(firstItem, Key.Down, preview: false);
                Pump(window.Dispatcher);

                Assert.Equal(second, window.SourceOptionsList.SelectedItem);
                Assert.Empty(playbackRequests);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Playback_failure_announces_without_fallback_and_escape_closes_without_playing()
    {
        var option = CreateOption();
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([option]));
        var playbackRequests = new ConcurrentQueue<ManualSourcePlaybackRequest>();
        var announcer = new RecordingAnnouncer();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(workflow, announcer, _ =>
            {
                playbackRequests.Enqueue(new([], Target, option, PlaybackQueueOrigin.Static));
                return Task.FromResult(new PlaybackResult(
                    PlaybackResultStatus.Failed,
                    null,
                    PlaybackQueueOrigin.Static,
                    "\u6362\u6e90\u64ad\u653e\u5931\u8d25",
                    PlaybackTrigger.UserInitiated));
            });
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);

                var request = Assert.Single(playbackRequests);
                Assert.Same(option, request.Option);
                Assert.Contains("\u6362\u6e90\u64ad\u653e\u5931\u8d25", announcer.Messages);

                RaiseKey(window, Key.Escape);
                Pump(window.Dispatcher);
                Assert.False(window.IsVisible);
                Assert.Single(playbackRequests);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(PlaybackQueueOrigin.Search)]
    [InlineData(PlaybackQueueOrigin.Category)]
    [InlineData(PlaybackQueueOrigin.Ranking)]
    public async Task Enter_passes_correct_playback_queue_origin_to_playback_request(
        PlaybackQueueOrigin origin)
    {
        var option = CreateOption();
        var workflow = new RecordingWorkflow(ManualSourceDiscoveryResult.Success([option]));
        var playbackRequests = new ConcurrentQueue<ManualSourcePlaybackRequest>();

        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(
                workflow,
                new RecordingAnnouncer(),
                request =>
                {
                    playbackRequests.Enqueue(request);
                    return Task.FromResult(new PlaybackResult(
                        PlaybackResultStatus.Playing,
                        request.Option.Candidate,
                        request.Origin,
                        "正在播放"));
                },
                origin);
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                RaiseKey(window, Key.Enter);
                Pump(window.Dispatcher);

                var request = Assert.Single(playbackRequests);
                Assert.Equal(origin, request.Origin);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Window_close_cancels_active_discovery_query()
    {
        var workflow = new CancelRecordingWorkflow();
        var announcer = new RecordingAnnouncer();

        await RunOnStaThreadAsync(async () =>
        {
            var window = CreateWindow(workflow, announcer);
            window.Show();
            Pump(window.Dispatcher);
            window.Close();
            Pump(window.Dispatcher);
            await workflow.WaitForCancelledAsync(TimeSpan.FromSeconds(5));
            Assert.True(workflow.WasCancelled);
        });
    }

    private static PlaybackResult FailedPlayback() => new(
        PlaybackResultStatus.Failed,
        null,
        PlaybackQueueOrigin.Static,
        "换源播放失败",
        PlaybackTrigger.UserInitiated);

    private static AccessibleAnnouncement[] PlaybackFailures(RecordingAnnouncer announcer) =>
        announcer.Announcements
            .Where(a => a.Kind == AnnouncementKind.Error
                && a.ActivityId!.StartsWith("manual-source-playback:", StringComparison.Ordinal))
            .ToArray();

    private static DependencyObject? FindLogicalDescendant(DependencyObject parent, string text)
    {
        if (parent is TextBlock block && block.Text == text)
            return parent;

        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            var match = FindLogicalDescendant(child, text);
            if (match is not null) return match;
        }

        return null;
    }

    private static ManualSourceSwitchWindow CreateWindow(
        IManualSourceDiscoveryWorkflow workflow,
        IAccessibleAnnouncer announcer,
        Func<ManualSourcePlaybackRequest, Task<PlaybackResult>>? playback = null,
        PlaybackQueueOrigin origin = PlaybackQueueOrigin.Static) =>
        new(
            Target,
            [Target, Candidate("netease-2")],
            workflow,
            announcer,
            playback ?? (_ => Task.FromResult(new PlaybackResult(
                PlaybackResultStatus.Playing,
                Target,
                origin,
                "正在播放"))),
            origin);

    private static ManualSourceOption CreateOption() => ManualSourceOption.Create(
        "script-a",
        "脚本A",
        Target.Source,
        Candidate("script-candidate"),
        "320k",
        "脚本A，320k 高品质，script-candidate，目标歌手");

    private static MusicInfo Candidate(string id) => new()
    {
        Id = id,
        Source = Target.Source,
        Title = Target.Title,
        Artist = Target.Artist,
        Album = Target.Album,
        Duration = Target.Duration
    };

    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void RaiseKey(UIElement element, Key key, bool preview = true)
    {
        var source = PresentationSource.FromVisual(element);
        Assert.NotNull(source);
        element.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent
        });
    }

    private static Task RunOnStaThreadAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(async () =>
        {
            Exception? failure = null;
            try { await action(); }
            catch (Exception exception) { failure = exception; }
            finally
            {
                Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
                if (failure is null) completion.SetResult();
                else completion.SetException(failure);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static Task RunOnStaThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            if (failure is null) completion.SetResult();
            else completion.SetException(failure);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class RecordingWorkflow(ManualSourceDiscoveryResult result)
        : IManualSourceDiscoveryWorkflow
    {
        public Task<ManualSourceDiscoveryResult> DiscoverAsync(
            MusicInfo target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public Task<PlaybackResult> PlayAsync(
            ManualSourcePlaybackRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose() { }
    }

    private sealed class CancelRecordingWorkflow : IManualSourceDiscoveryWorkflow
    {
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancelled { get; private set; }

        public async Task<ManualSourceDiscoveryResult> DiscoverAsync(
            MusicInfo target,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                _cancelled.TrySetResult();
                throw;
            }
            return ManualSourceDiscoveryResult.Success([]);
        }

        public async Task WaitForCancelledAsync(TimeSpan timeout) =>
            await _cancelled.Task.WaitAsync(timeout);

        public Task<PlaybackResult> PlayAsync(
            ManualSourcePlaybackRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose() { }
    }

    private sealed class RecordingAnnouncer : IAccessibleAnnouncer
    {
        public bool SpeechEnabled { get; set; }
        public List<AccessibleAnnouncement> Announcements { get; } = [];
        public List<string> Messages { get; } = [];

        public void Announce(AccessibleAnnouncement announcement)
        {
            Announcements.Add(announcement);
            Messages.Add(announcement.Text);
        }
    }
}
