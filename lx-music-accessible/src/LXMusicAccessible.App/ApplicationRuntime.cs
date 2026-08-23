using System.IO;
using LXMusicAccessible.Data;
using LXMusicAccessible.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.App;

public enum ApplicationStartOutcome
{
    Started,
    ExitRequested
}

/// <summary>
/// Owns application startup and its Host. Callers only start, retry through the
/// failure prompt, and stop; ordering and cleanup stay inside this module.
/// </summary>
public sealed class ApplicationRuntime : IAsyncDisposable
{
    private readonly Func<IHost> _createHost;
    private readonly IStartupInitializer _initializer;
    private readonly IStartupFailurePresenter _failurePresenter;
    private IHost? _host;
    private BackgroundWorkHost? _backgroundWork;
    private ApplicationShutdownCoordinator? _shutdownCoordinator;
    private bool _started;
    private bool _disposed;

    internal ApplicationRuntime(
        Func<IHost> createHost,
        IStartupInitializer initializer,
        IStartupFailurePresenter failurePresenter)
    {
        _createHost = createHost;
        _initializer = initializer;
        _failurePresenter = failurePresenter;
    }

    internal IServiceProvider? Services => _host?.Services;

    public async Task<ApplicationStartOutcome> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return ApplicationStartOutcome.Started;

        while (true)
        {
            try
            {
                _host = _createHost();
                await _host.StartAsync(cancellationToken);
                await _initializer.InitializeAsync(_host.Services, cancellationToken);

                BindShutdown(_host.Services);

                var shell = _host.Services.GetRequiredService<IApplicationShell>();
                shell.Show();
                _started = true;
                try { StartOptionalBackgroundWork(_host.Services); }
                catch (Exception ex)
                {
                    _host.Services.GetService<ILogger<ApplicationRuntime>>()?
                        .LogWarning(ex, "Optional background work could not be started");
                }
                return ApplicationStartOutcome.Started;
            }
            catch (OperationCanceledException)
            {
                await ReleaseHostAsync();
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Exception failure = ex;
                try { await ReleaseHostAsync(); }
                catch (Exception releaseException)
                {
                    failure = new AggregateException(ex, releaseException);
                }

                if (await _failurePresenter.ShowAsync(failure, cancellationToken) == StartupFailureDecision.Exit)
                    return ApplicationStartOutcome.ExitRequested;
            }
        }
    }

    public Task<ApplicationStartOutcome> RetryAsync(CancellationToken cancellationToken = default) =>
        StartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        if (_shutdownCoordinator is not null)
        {
            await _shutdownCoordinator.RequestAsync().WaitAsync(cancellationToken);
            return;
        }

        _started = false;
        await QuiesceBackgroundAsync(cancellationToken);
        await ReleaseHostAsync(cancellationToken);
    }

    internal void FinalizeExit()
    {
        if (_disposed) return;
        _disposed = true;
        _started = false;
        Interlocked.Exchange(ref _backgroundWork, null);
        Interlocked.Exchange(ref _host, null)?.Dispose();
    }

    private async Task QuiesceBackgroundAsync(CancellationToken cancellationToken)
    {
        _started = false;
        var backgroundWork = Interlocked.Exchange(ref _backgroundWork, null);
        if (backgroundWork is not null)
            await backgroundWork.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync();
        _disposed = true;
    }

    private void StartOptionalBackgroundWork(IServiceProvider services)
    {
        _backgroundWork = services.GetRequiredService<BackgroundWorkHost>();

        if (services.GetService<IMusicSourceService>() is not MusicSourceService sourceService)
            return;

        _backgroundWork.StartLatest(
            "script-source.initialize",
            context => RunOptionalBackgroundWorkAsync(
                sourceService,
                services.GetRequiredService<ILogger<ApplicationRuntime>>(),
                context.CancellationToken));
    }

    private void BindShutdown(IServiceProvider services)
    {
        var request = services.GetRequiredService<ApplicationShutdownRequest>();
        _shutdownCoordinator = new ApplicationShutdownCoordinator(
            () => services.GetRequiredService<IApplicationShutdownParticipant>(),
            QuiesceBackgroundAsync,
            ReleaseHostAsync,
            services.GetRequiredService<IShutdownFeedback>(),
            services.GetRequiredService<ILogger<ApplicationShutdownCoordinator>>(),
            TimeSpan.FromSeconds(15));
        request.Bind(_shutdownCoordinator.RequestAsync);
    }

    private static async Task RunOptionalBackgroundWorkAsync(
        MusicSourceService sourceService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await sourceService.InitializeScriptSourcesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Optional script source initialization failed");
        }
    }

    private async Task ReleaseHostAsync(CancellationToken cancellationToken = default)
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host is null) return;
        try { await host.StopAsync(cancellationToken); }
        finally { await ApplicationHost.DisposeHostAsync(host); }
    }
}

internal interface IStartupInitializer
{
    Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken);
}

internal sealed class ProductionStartupInitializer(string dataDirectory) : IStartupInitializer
{
    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var preparedDirectory = AppDataPaths.PrepareDataDir();
        if (!string.Equals(
                Path.GetFullPath(preparedDirectory),
                Path.GetFullPath(dataDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The application data directory changed during startup.");
        }

        Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));
        var migration = await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        if (!migration.Succeeded)
            throw new InvalidOperationException(migration.Message);

        _ = services.GetRequiredService<IDataService>();
            _ = services.GetRequiredService<IAccessibleAnnouncer>();
            _ = services.GetRequiredService<IPlaybackCoordinator>();
            _ = services.GetRequiredService<IAudioPlaybackControl>();
        _ = services.GetRequiredService<IMusicSourceService>();
        _ = services.GetRequiredService<IDownloadService>();
        _ = services.GetRequiredService<ILyricService>();
        _ = services.GetRequiredService<LyricsWorkflow>();
        _ = services.GetRequiredService<IHotkeyManager>();
        _ = services.GetRequiredService<IPlaylistImportService>();
        cancellationToken.ThrowIfCancellationRequested();
    }
}

internal interface IApplicationShell
{
    void Show();
}

internal sealed class WpfApplicationShell(MainWindow window) : IApplicationShell
{
    public void Show() => window.Show();
}

internal enum StartupFailureDecision
{
    Retry,
    Exit
}

internal interface IStartupFailurePresenter
{
    Task<StartupFailureDecision> ShowAsync(Exception exception, CancellationToken cancellationToken);
}

internal sealed class WpfStartupFailurePresenter : IStartupFailurePresenter
{
    public Task<StartupFailureDecision> ShowAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = System.Windows.MessageBox.Show(
            $"应用初始化失败：{exception.Message}\n\n选择“确定”再次初始化，或选择“取消”安全退出。",
            "落雪音乐无障碍版启动失败",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Error,
            System.Windows.MessageBoxResult.OK);
        return Task.FromResult(result == System.Windows.MessageBoxResult.OK
            ? StartupFailureDecision.Retry
            : StartupFailureDecision.Exit);
    }
}
