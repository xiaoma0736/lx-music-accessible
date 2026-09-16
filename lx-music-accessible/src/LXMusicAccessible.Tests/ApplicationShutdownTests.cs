using System.IO;
using LXMusicAccessible.App;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ApplicationShutdownTests
{
    [Fact]
    public async Task Concurrent_requests_capture_quiesce_persist_and_release_exactly_once_in_order()
    {
        var events = new List<string>();
        var persistGate = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var participant = new RecordingParticipant(events, persistGate.Task);
        var feedback = new RecordingFeedback();
        var coordinator = CreateCoordinator(
            participant,
            events,
            feedback,
            new RecordingLogger<ApplicationShutdownCoordinator>(),
            TimeSpan.FromSeconds(2));

        var first = coordinator.RequestAsync();
        var second = coordinator.RequestAsync();
        var third = coordinator.RequestAsync();

        Assert.Same(first, second);
        Assert.Same(first, third);
        await participant.PersistStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "capture-block", "background-stop", "persist" }, events);
        Assert.DoesNotContain("host-stop", events);

        persistGate.SetResult(null);
        var result = await first;

        Assert.Equal(ShutdownStatus.Completed, result.Status);
        Assert.Equal(
            new[]
            {
                "capture-block", "background-stop", "persist", "host-stop", "host-dispose"
            },
            events);
        Assert.Equal(1, participant.CaptureCount);
        Assert.Equal(1, participant.PersistCount);
        Assert.Empty(feedback.Results);
    }

    [Fact]
    public async Task Persistence_failure_is_observable_sanitized_and_still_releases_once()
    {
        const string bait = "SECRET-cookie-token";
        var events = new List<string>();
        var participant = new RecordingParticipant(
            events,
            Task.FromException(new InvalidOperationException(bait)));
        var feedback = new RecordingFeedback();
        var logger = new RecordingLogger<ApplicationShutdownCoordinator>();
        var coordinator = CreateCoordinator(
            participant,
            events,
            feedback,
            logger,
            TimeSpan.FromSeconds(2));

        var result = await coordinator.RequestAsync();

        Assert.Equal(new ShutdownResult(ShutdownStatus.Failed, "persistence"), result);
        Assert.Equal(result, Assert.Single(feedback.Results));
        Assert.Equal(1, events.Count(item => item == "host-stop"));
        Assert.Equal(1, events.Count(item => item == "host-dispose"));
        Assert.DoesNotContain(bait, string.Join(Environment.NewLine, logger.Messages));
        Assert.Contains("InvalidOperationException", string.Join(Environment.NewLine, logger.Messages));
    }

    [Fact]
    public async Task Persistence_timeout_is_observable_cancels_work_and_does_not_escape()
    {
        var events = new List<string>();
        var participant = new RecordingParticipant(
            events,
            Task.Delay(Timeout.InfiniteTimeSpan));
        participant.UseCancellation = true;
        var feedback = new RecordingFeedback();
        var coordinator = CreateCoordinator(
            participant,
            events,
            feedback,
            new RecordingLogger<ApplicationShutdownCoordinator>(),
            TimeSpan.FromMilliseconds(30));

        var result = await coordinator.RequestAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new ShutdownResult(ShutdownStatus.TimedOut, "persistence"), result);
        Assert.Equal(result, Assert.Single(feedback.Results));
        Assert.Equal(1, events.Count(item => item == "host-stop"));
        Assert.Equal(1, events.Count(item => item == "host-dispose"));
    }

    [Fact]
    public async Task Persisted_settings_hotkeys_playback_and_sources_are_readable_after_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LXMusicAccessible.Shutdown-" + Guid.NewGuid().ToString("N"));
        try
        {
            var initializer = new DatabaseInitializer(
                NullLogger<DatabaseInitializer>.Instance,
                directory);
            Assert.True((await initializer.InitializeAsync()).Succeeded);

            var firstRun = new DataService(NullLogger<DataService>.Instance, directory);
            var settings = new UserSettings
            {
                DefaultVolume = 0.42,
                CustomHotkeys = new()
                {
                    new Hotkey { ActionId = "play_pause", Name = "播放暂停" }
                },
                Playback = new PlaybackSessionState
                {
                    CurrentMusic = new MusicInfo
                    {
                        Id = "restart-song",
                        Source = "restart-source",
                        Title = "Restart title"
                    },
                    PositionSeconds = 73.5,
                    WasPlaying = true
                }
            };
            var sources = new List<MusicSource>
            {
                new()
                {
                    Id = "restart-source",
                    Name = "Restart source",
                    Version = "1",
                    Type = SourceType.Custom,
                    IsEnabled = true,
                    IsDefault = true
                }
            };

            var participant = new SqliteShutdownParticipant(firstRun, settings, sources);
            var coordinator = new ApplicationShutdownCoordinator(
                () => participant,
                _ => Task.CompletedTask,
                _ => Task.CompletedTask,
                new RecordingFeedback(),
                new RecordingLogger<ApplicationShutdownCoordinator>(),
                TimeSpan.FromSeconds(2));
            Assert.True((await coordinator.RequestAsync()).Succeeded);
            SqliteConnection.ClearAllPools();

            var restarted = new DataService(NullLogger<DataService>.Instance, directory);
            var restoredSettings = Assert.IsType<UserSettings>(await restarted.GetSettingsAsync());
            var restoredSource = Assert.Single(await restarted.GetMusicSourcesAsync(),
                source => source.Id == "restart-source");

            Assert.Equal(0.42, restoredSettings.DefaultVolume);
            Assert.Equal("play_pause", Assert.Single(restoredSettings.CustomHotkeys).ActionId);
            Assert.Equal("restart-song", restoredSettings.Playback.CurrentMusic?.Id);
            Assert.Equal(73.5, restoredSettings.Playback.PositionSeconds);
            Assert.True(restoredSettings.Playback.WasPlaying);
            Assert.True(restoredSource.IsDefault);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Every_window_exit_entry_routes_to_close_and_on_exit_is_synchronous_fallback()
    {
        var root = FindRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(
            root,
            "src",
            "LXMusicAccessible.App",
            "MainWindow.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(
            root,
            "src",
            "LXMusicAccessible.App",
            "App.xaml.cs"));
        var desktop = File.ReadAllText(Path.Combine(
            root,
            "src",
            "LXMusicAccessible.App",
            "DesktopLifecycleWorkflow.cs"));

        Assert.Contains("DesktopCommand.Exit", window);
        Assert.Contains("DesktopCommand.AltF4", window);
        Assert.Contains("protected override void OnClosing(CancelEventArgs e)", window);
        Assert.DoesNotContain("_applicationShutdown", window);
        Assert.Contains("await _shutdown.RequestAsync();", desktop);
        Assert.Contains("_desktop.CompleteExit();", desktop);
        Assert.DoesNotContain("SaveSettingsOnExit", window);
        Assert.Contains("protected override void OnExit(ExitEventArgs e)", app);
        Assert.DoesNotContain("protected override async void OnExit", app);
    }

    private static ApplicationShutdownCoordinator CreateCoordinator(
        RecordingParticipant participant,
        List<string> events,
        RecordingFeedback feedback,
        RecordingLogger<ApplicationShutdownCoordinator> logger,
        TimeSpan timeout) =>
        new(
            () => participant,
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                events.Add("background-stop");
                return Task.CompletedTask;
            },
            cancellationToken =>
            {
                events.Add("host-stop");
                events.Add("host-dispose");
                return Task.CompletedTask;
            },
            feedback,
            logger,
            timeout);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "verify.ps1")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class RecordingParticipant(List<string> events, Task persistence)
        : IApplicationShutdownParticipant
    {
        public int CaptureCount { get; private set; }
        public int PersistCount { get; private set; }
        public bool UseCancellation { get; set; }
        public TaskCompletionSource<object?> PersistStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ApplicationShutdownSnapshot CaptureAndBlock()
        {
            CaptureCount++;
            events.Add("capture-block");
            return new(new SettingsEditState(0, new UserSettings()));
        }

        public async Task PersistAsync(
            ApplicationShutdownSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            PersistCount++;
            events.Add("persist");
            PersistStarted.TrySetResult(null);
            if (UseCancellation)
                await persistence.WaitAsync(cancellationToken);
            else
                await persistence;
        }
    }

    private sealed class RecordingFeedback : IShutdownFeedback
    {
        public List<ShutdownResult> Results { get; } = new();
        public void Report(ShutdownResult result) => Results.Add(result);
    }

    private sealed class SqliteShutdownParticipant(
        IDataService data,
        UserSettings settings,
        IReadOnlyList<MusicSource> sources) : IApplicationShutdownParticipant
    {
        public ApplicationShutdownSnapshot CaptureAndBlock() =>
            new(new SettingsEditState(0, settings));

        public async Task PersistAsync(
            ApplicationShutdownSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            await data.SaveSettingsAsync(snapshot.Settings.Settings, cancellationToken);
            await data.SaveMusicSourcesAsync(sources.ToList(), cancellationToken);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
