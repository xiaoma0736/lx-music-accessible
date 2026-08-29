using System.IO;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class DesktopLifecycleWorkflowTests
{
    [Fact]
    public async Task Public_commands_apply_close_policy_restore_focus_and_announce_uia_state()
    {
        var fixture = new Fixture();

        var hidden = await fixture.Workflow.ExecuteAsync(DesktopCommand.Escape);
        var duplicateHide = await fixture.Workflow.ExecuteAsync(DesktopCommand.Escape);
        var restored = await fixture.Workflow.ExecuteAsync(DesktopCommand.Restore);
        var duplicateRestore = await fixture.Workflow.ExecuteAsync(DesktopCommand.Restore);
        var altF4Hidden = await fixture.Workflow.ExecuteAsync(DesktopCommand.AltF4, hideOnAltF4: true);

        Assert.Equal(DesktopLifecycleState.Hidden, hidden.State);
        Assert.False(duplicateHide.Applied);
        Assert.Equal(DesktopFocusTarget.PreviousControl, restored.FocusTarget);
        Assert.False(duplicateRestore.Applied);
        Assert.Equal(DesktopLifecycleState.Hidden, altF4Hidden.State);
        Assert.Equal(2, fixture.Desktop.HideCount);
        Assert.Equal(1, fixture.Desktop.RestoreCount);
        Assert.Equal(0, fixture.Shutdown.RequestCount);
        Assert.Collection(
            fixture.Announcer.Announcements,
            item => AssertAnnouncement(item, "已隐藏到系统托盘，音乐继续播放"),
            item => AssertAnnouncement(item, "已恢复主窗口"),
            item => AssertAnnouncement(item, "已隐藏到系统托盘，音乐继续播放"));

        await fixture.Workflow.ExecuteAsync(DesktopCommand.Restore);
        var closed = await fixture.Workflow.ExecuteAsync(DesktopCommand.AltF4, hideOnAltF4: false);

        Assert.Equal(DesktopLifecycleState.Closed, closed.State);
        Assert.Equal(1, fixture.Shutdown.RequestCount);
        Assert.Equal(1, fixture.Desktop.DeactivateCount);
        Assert.Equal(1, fixture.Desktop.CompleteExitCount);
        Assert.True(fixture.Workflow.AllowWindowClose);
    }

    [Fact]
    public async Task Repeated_exit_and_resource_events_are_idempotent()
    {
        var shutdown = new RecordingShutdown(completeImmediately: false);
        var fixture = new Fixture(shutdown);

        var first = fixture.Workflow.ExecuteAsync(DesktopCommand.Exit);
        var second = fixture.Workflow.ExecuteAsync(DesktopCommand.Exit);

        Assert.Same(first, second);
        Assert.Equal(1, shutdown.RequestCount);
        Assert.Equal(1, fixture.Desktop.DeactivateCount);
        Assert.Equal(0, fixture.Desktop.CompleteExitCount);

        shutdown.Complete();
        Assert.Equal(DesktopLifecycleState.Closed, (await first).State);
        Assert.Equal(1, fixture.Desktop.CompleteExitCount);
        Assert.Same(first, fixture.Workflow.ExecuteAsync(DesktopCommand.Exit));

        fixture.Workflow.Dispose();
        fixture.Workflow.Dispose();
        fixture.Desktop.Dispose();
        fixture.Desktop.Dispose();
        Assert.Equal(1, fixture.Desktop.SubscriptionCount);
        Assert.Equal(1, fixture.Desktop.UnsubscriptionCount);
        Assert.Equal(1, fixture.Desktop.DisposeCount);
    }

    [Fact]
    public async Task Tray_commands_use_the_same_playback_module_state()
    {
        var fixture = new Fixture();
        fixture.Playback.Current = new MusicInfo { Id = "shared", Title = "共享歌曲" };
        fixture.Playback.Playing = true;

        fixture.Desktop.Raise(DesktopCommand.TogglePlayback);
        await WaitUntilAsync(() => fixture.Playback.PauseCount == 1);
        Assert.False(fixture.Playback.Playing);
        Assert.True(fixture.Playback.Paused);

        fixture.Desktop.Raise(DesktopCommand.TogglePlayback);
        await WaitUntilAsync(() => fixture.Playback.ResumeCount == 1);
        fixture.Desktop.Raise(DesktopCommand.Previous);
        fixture.Desktop.Raise(DesktopCommand.Next);
        await WaitUntilAsync(() => fixture.Playback.PreviousCount == 1 && fixture.Playback.NextCount == 1);

        Assert.Same(fixture.Playback, fixture.WorkflowPlayback);
        Assert.Contains(fixture.Announcer.Messages, message => message == "已暂停");
        Assert.Contains(fixture.Announcer.Messages, message => message == "继续播放");
        Assert.Contains(fixture.Announcer.Messages, message => message == "上一首");
        Assert.Contains(fixture.Announcer.Messages, message => message == "下一首");
    }

    [Fact]
    public async Task Host_replaces_the_real_desktop_seam_and_releases_it_once()
    {
        var desktop = new RecordingDesktopIntegration();

        await using (var app = await TestApplicationHost.StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDesktopIntegration>(_ => desktop));
        }))
        {
            var workflow = app.Services.GetRequiredService<DesktopLifecycleWorkflow>();
            Assert.Same(workflow, app.Services.GetRequiredService<DesktopLifecycleWorkflow>());
            Assert.Equal(1, desktop.SubscriptionCount);
        }

        Assert.Equal(1, desktop.UnsubscriptionCount);
        Assert.Equal(1, desktop.DisposeCount);
    }

    [Fact]
    public void Main_window_is_only_a_desktop_adapter_and_keeps_semantic_commands()
    {
        var root = FindRepositoryRoot();
        var mainCode = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "MainWindow.xaml.cs"));
        var mainXaml = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "MainWindow.xaml"));
        var appCode = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "App.xaml.cs"));
        var lifecycle = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "DesktopLifecycleWorkflow.cs"));

        Assert.DoesNotContain("System.Windows.Forms.NotifyIcon", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ContextMenuStrip", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("private void HideToTray(", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("private void ShowFromTray(", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_applicationShutdown.RequestAsync", mainCode, StringComparison.Ordinal);
        Assert.Contains("DesktopCommand.Escape", mainCode, StringComparison.Ordinal);
        Assert.Contains("DesktopCommand.AltF4", mainCode, StringComparison.Ordinal);
        Assert.Contains("DesktopCommand.Exit", mainCode, StringComparison.Ordinal);
        Assert.Contains("DesktopCommand.Restore", appCode, StringComparison.Ordinal);
        Assert.Contains("Header=\"隐藏/显示窗口(_H)\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"退出(_X)\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("IDesktopIntegration", lifecycle, StringComparison.Ordinal);
        Assert.Contains("DesktopFocusTarget.PreviousControl", lifecycle, StringComparison.Ordinal);
        Assert.Contains("toolWindows.HideVisibleWindows()", lifecycle, StringComparison.Ordinal);
        Assert.Contains("toolWindows.RestoreVisibleWindows(_toolSnapshot)", lifecycle, StringComparison.Ordinal);
    }

    private static void AssertAnnouncement(AccessibleAnnouncement announcement, string text)
    {
        Assert.Equal(text, announcement.Text);
        Assert.Equal(AnnouncementKind.Status, announcement.Kind);
        Assert.Equal("desktop-lifecycle", announcement.ActivityId);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 100 && !predicate(); attempt++)
            await Task.Delay(10);
        Assert.True(predicate());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "verify.ps1")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class Fixture
    {
        internal Fixture(RecordingShutdown? shutdown = null)
        {
            Desktop = new();
            Shutdown = shutdown ?? new();
            Playback = new();
            Announcer = new();
            WorkflowPlayback = Playback;
            Workflow = new(Desktop, Shutdown, WorkflowPlayback, Announcer);
        }

        internal RecordingDesktopIntegration Desktop { get; }
        internal RecordingShutdown Shutdown { get; }
        internal RecordingPlayback Playback { get; }
        internal IPlaybackCoordinator WorkflowPlayback { get; }
        internal RecordingAnnouncer Announcer { get; }
        internal DesktopLifecycleWorkflow Workflow { get; }
    }

    private sealed class RecordingDesktopIntegration : IDesktopIntegration
    {
        private EventHandler<DesktopCommandRequestedEventArgs>? _commandRequested;
        private bool _disposed;

        public event EventHandler<DesktopCommandRequestedEventArgs>? CommandRequested
        {
            add { _commandRequested += value; SubscriptionCount++; }
            remove { _commandRequested -= value; UnsubscriptionCount++; }
        }

        public bool IsWindowRestored { get; private set; } = true;
        public int HideCount { get; private set; }
        public int RestoreCount { get; private set; }
        public int DeactivateCount { get; private set; }
        public int CompleteExitCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int SubscriptionCount { get; private set; }
        public int UnsubscriptionCount { get; private set; }

        public void Attach(MainWindow window) { }

        public bool HideToTray()
        {
            HideCount++;
            IsWindowRestored = false;
            return true;
        }

        public DesktopFocusTarget RestoreWindow()
        {
            RestoreCount++;
            IsWindowRestored = true;
            return DesktopFocusTarget.PreviousControl;
        }

        public void DeactivateTray() => DeactivateCount++;
        public void CompleteExit() => CompleteExitCount++;
        public void Raise(DesktopCommand command) =>
            _commandRequested?.Invoke(this, new(command));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeCount++;
        }
    }

    private sealed class RecordingShutdown(bool completeImmediately = true) : IApplicationShutdown
    {
        private readonly TaskCompletionSource<ShutdownResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount { get; private set; }

        public Task<ShutdownResult> RequestAsync()
        {
            RequestCount++;
            if (completeImmediately) Complete();
            return _completion.Task;
        }

        public void Complete() =>
            _completion.TrySetResult(new(ShutdownStatus.Completed, "completed"));
    }

    private sealed class RecordingPlayback : IPlaybackCoordinator
    {
        public bool Playing { get; set; }
        public bool Paused { get; set; }
        public MusicInfo? Current { get; set; }
        public int PauseCount { get; private set; }
        public int ResumeCount { get; private set; }
        public int PreviousCount { get; private set; }
        public int NextCount { get; private set; }
        public bool IsPlaying => Playing;
        public bool IsPaused => Paused;
        public MusicInfo? CurrentMusic => Current;
        public PlayMode Mode { get; set; }
        public string DefaultQuality { get; set; } = "128k";
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted
        {
            add { }
            remove { }
        }

        public Task<PlaybackResult> PlayAsync(PlaybackRequest request) =>
            Task.FromResult(Result(request.Selection));

        public Task<PlaybackResult> ReplayAsync()
        {
            Playing = true;
            Paused = false;
            return Task.FromResult(Result(Current));
        }

        public void Pause()
        {
            PauseCount++;
            Playing = false;
            Paused = true;
        }

        public void Stop()
        {
            Playing = false;
            Paused = false;
        }

        public Task<PlaybackResult> NextAsync()
        {
            NextCount++;
            return Task.FromResult(Result(Current));
        }

        public Task<PlaybackResult> PreviousAsync()
        {
            PreviousCount++;
            return Task.FromResult(Result(Current));
        }

        public Task ResumeAsync()
        {
            ResumeCount++;
            Playing = true;
            Paused = false;
            return Task.CompletedTask;
        }

        private static PlaybackResult Result(MusicInfo? music) =>
            new(PlaybackResultStatus.Playing, music, PlaybackQueueOrigin.Static, "ok");
    }
}
