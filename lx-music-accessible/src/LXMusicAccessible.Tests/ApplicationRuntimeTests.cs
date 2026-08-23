using System.IO;
using System.Net;
using LXMusicAccessible.App;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ApplicationRuntimeTests
{
    [Fact]
    public async Task Start_shows_the_window_only_after_host_database_and_required_modules_are_ready()
    {
        var events = new List<string>();
        var directory = Directory.CreateTempSubdirectory("LXMusicAccessible.Runtime-").FullName;
        var shell = new RecordingShell(events);
        var initializer = new RecordingInitializer(directory, events);
        var failures = new RecordingFailurePresenter(StartupFailureDecision.Exit, events);

        await using var runtime = CreateRuntime(directory, initializer, failures, shell, events);
        var outcome = await runtime.StartAsync();

        Assert.Equal(ApplicationStartOutcome.Started, outcome);
        Assert.Equal(
            new[] { "host-start", "data-directory", "database", "required-modules", "window-show" },
            events.Take(5));
        Assert.True(File.Exists(Path.Combine(directory, "lxmusic.db")));
        Assert.Empty(failures.Exceptions);
        Assert.Equal(1, events.Count(item => item == "host-start"));

        var services = Assert.IsAssignableFrom<IServiceProvider>(runtime.Services);
        Assert.Same(
            services.GetRequiredService<IDataService>(),
            services.GetRequiredService<IDataService>());
        Assert.Same(
            services.GetRequiredService<IMusicSourceService>(),
            services.GetRequiredService<IMusicSourceService>());

        await runtime.StopAsync();
        Assert.Equal("host-stop", events[^2]);
        Assert.Equal("host-dispose", events[^1]);
        Cleanup(directory);
    }

    [Fact]
    public async Task Failed_initialization_releases_the_host_before_retry_and_never_shows_a_partial_window()
    {
        var events = new List<string>();
        var directory = Directory.CreateTempSubdirectory("LXMusicAccessible.Runtime-").FullName;
        var shell = new RecordingShell(events);
        var initializer = new RecordingInitializer(directory, events, failuresBeforeSuccess: 1);
        var failures = new RecordingFailurePresenter(StartupFailureDecision.Retry, events);

        await using var runtime = CreateRuntime(directory, initializer, failures, shell, events);
        var outcome = await runtime.StartAsync();

        Assert.Equal(ApplicationStartOutcome.Started, outcome);
        Assert.Single(failures.Exceptions);
        Assert.Equal(1, shell.ShowCount);
        var firstFailure = events.IndexOf("initialization-failed");
        Assert.True(firstFailure >= 0);
        Assert.Equal("host-stop", events[firstFailure + 1]);
        Assert.Equal("host-dispose", events[firstFailure + 2]);
        Assert.Equal("failure-prompt", events[firstFailure + 3]);
        Assert.Equal("host-start", events[firstFailure + 4]);
        Assert.DoesNotContain("window-show", events.Take(firstFailure + 1));

        await runtime.StopAsync();
        Cleanup(directory);
    }

    [Fact]
    public async Task Failed_initialization_can_exit_without_a_window_and_disposes_the_host()
    {
        var events = new List<string>();
        var directory = Directory.CreateTempSubdirectory("LXMusicAccessible.Runtime-").FullName;
        var shell = new RecordingShell(events);
        var initializer = new RecordingInitializer(directory, events, failuresBeforeSuccess: int.MaxValue);
        var failures = new RecordingFailurePresenter(StartupFailureDecision.Exit, events);

        await using var runtime = CreateRuntime(directory, initializer, failures, shell, events);
        var outcome = await runtime.StartAsync();

        Assert.Equal(ApplicationStartOutcome.ExitRequested, outcome);
        Assert.Equal(0, shell.ShowCount);
        Assert.Single(failures.Exceptions);
        Assert.Contains("host-stop", events);
        Assert.Contains("host-dispose", events);
        Cleanup(directory);
    }

    [Fact]
    public async Task Stop_quiesces_host_owned_work_before_persistence_and_host_release()
    {
        var events = new List<string>();
        var directory = Directory.CreateTempSubdirectory("LXMusicAccessible.Runtime-").FullName;
        var shell = new RecordingShell(events);
        var initializer = new RecordingInitializer(directory, events);
        var failures = new RecordingFailurePresenter(StartupFailureDecision.Exit, events);
        var probe = new AsyncReleaseProbe(events);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var runtime = CreateRuntime(directory, initializer, failures, shell, events);
        Assert.Equal(ApplicationStartOutcome.Started, await runtime.StartAsync());
        var background = runtime.Services!.GetRequiredService<BackgroundWorkHost>();
        background.StartLatest("download.fixture", async context =>
        {
            await using var owned = probe;
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        });
        await started.Task;

        await runtime.StopAsync();

        Assert.Equal(0, background.ActiveCount);
        Assert.Equal(1, probe.ReleaseCount);
        Assert.True(events.IndexOf("background-release") < events.IndexOf("shutdown-persist"));
        Assert.True(events.IndexOf("shutdown-persist") < events.LastIndexOf("host-stop"));
        Cleanup(directory);
    }

    private static ApplicationRuntime CreateRuntime(
        string directory,
        IStartupInitializer initializer,
        IStartupFailurePresenter failurePresenter,
        RecordingShell shell,
        List<string> events)
    {
        var httpHandler = new RejectingHttpMessageHandler();
        var announcer = new RecordingAnnouncer();
        var speech = new RecordingTtsAdapter();
        var audio = new RecordingAudioPlayer();

        return ApplicationHost.CreateForTesting(
            directory,
            initializer,
            failurePresenter,
            services =>
            {
                services.Replace(ServiceDescriptor.Singleton<MusicHttpClient>(sp =>
                    new MusicHttpClient(sp.GetRequiredService<ILogger<MusicHttpClient>>(), httpHandler)));
                services.Replace(ServiceDescriptor.Singleton(
                    new ScriptNetworkClient(httpHandler, ResolveFixtureAddressAsync)));
                services.Replace(ServiceDescriptor.Singleton<IAccessibleAnnouncer>(announcer));
                services.Replace(ServiceDescriptor.Singleton<ITtsService>(speech));
                services.Replace(ServiceDescriptor.Singleton<IAudioPlayer>(audio));
                services.Replace(ServiceDescriptor.Singleton<IProcessLauncher>(new RecordingProcessLauncher()));
                services.Replace(ServiceDescriptor.Singleton<IApplicationShell>(shell));
                services.Replace(ServiceDescriptor.Singleton<IApplicationShutdownParticipant>(
                    new RecordingShutdownParticipant(events)));
                services.Replace(ServiceDescriptor.Singleton<IShutdownFeedback>(
                    new RecordingShutdownFeedback()));
                services.AddSingleton<IHostedService>(_ => new RecordingHostedService(events));
            });
    }

    private static Task<IPAddress[]> ResolveFixtureAddressAsync(
        string host,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
    }

    private static void Cleanup(string directory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class RecordingInitializer(
        string directory,
        List<string> events,
        int failuresBeforeSuccess = 0) : IStartupInitializer
    {
        private int _attempt;

        public async Task InitializeAsync(
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
            if (_attempt++ < failuresBeforeSuccess)
            {
                events.Add("initialization-failed");
                throw new InvalidOperationException("controlled startup failure");
            }

            Directory.CreateDirectory(directory);
            events.Add("data-directory");
            var migration = await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            if (!migration.Succeeded)
                throw new InvalidOperationException(migration.Message);
            events.Add("database");
            _ = services.GetRequiredService<IDataService>();
            _ = services.GetRequiredService<IMusicSourceService>();
            _ = services.GetRequiredService<IPlaybackCoordinator>();
            _ = services.GetRequiredService<IAudioPlaybackControl>();
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("required-modules");
        }
    }

    private sealed class RecordingShell(List<string> events) : IApplicationShell
    {
        public int ShowCount { get; private set; }

        public void Show()
        {
            ShowCount++;
            events.Add("window-show");
        }
    }

    private sealed class RecordingFailurePresenter(
        StartupFailureDecision decision,
        List<string> events)
        : IStartupFailurePresenter
    {
        public List<Exception> Exceptions { get; } = new();

        public Task<StartupFailureDecision> ShowAsync(
            Exception exception,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exceptions.Add(exception);
            events.Add("failure-prompt");
            return Task.FromResult(decision);
        }
    }

    private sealed class RecordingHostedService(List<string> events) : IHostedService, IDisposable
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            events.Add("host-start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            events.Add("host-stop");
            return Task.CompletedTask;
        }

        public void Dispose() => events.Add("host-dispose");
    }

    private sealed class RecordingShutdownParticipant(List<string> events)
        : IApplicationShutdownParticipant
    {
        public ApplicationShutdownSnapshot CaptureAndBlock()
        {
            events.Add("shutdown-capture");
            return new(new SettingsEditState(0, new UserSettings()), Array.Empty<MusicSource>());
        }

        public Task PersistAsync(
            ApplicationShutdownSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("shutdown-persist");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingShutdownFeedback : IShutdownFeedback
    {
        public void Report(ShutdownResult result) { }
    }

    private sealed class AsyncReleaseProbe(List<string> events) : IAsyncDisposable
    {
        public int ReleaseCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            ReleaseCount++;
            events.Add("background-release");
            return ValueTask.CompletedTask;
        }
    }
}
