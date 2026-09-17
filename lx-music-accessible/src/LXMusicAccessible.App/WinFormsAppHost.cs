using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Core;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LXMusicAccessible.App;

public enum DesktopCommand
{
    Escape,
    Restore,
    ToggleVisibility,
    AltF4,
    Exit,
    TogglePlayback,
    Previous,
    Next
}

public enum DesktopLifecycleState { Visible, Hidden, Exiting, Closed }
public enum DesktopFocusTarget { None, PreviousControl, PrimaryContent, MainWindow }

public readonly record struct DesktopCommandResult(DesktopLifecycleState State, DesktopFocusTarget FocusTarget = DesktopFocusTarget.None, bool Applied = true);


/// <summary>Owns tray visibility, window-group hiding and desktop playback commands.</summary>
public sealed class DesktopLifecycleWorkflow : IDisposable
{
    private readonly WinFormsDesktopIntegration _desktop;
    private readonly IApplicationShutdown _shutdown;
    private readonly IPlaybackCoordinator _playback;
    private readonly IAccessibleAnnouncer _announcer;
    private readonly object _exitGate = new();
    private volatile DesktopLifecycleState _state = DesktopLifecycleState.Visible;
    private Task<DesktopCommandResult>? _exit;
    private bool _disposed;

    public DesktopLifecycleWorkflow(
        WinFormsDesktopIntegration desktop,
        IApplicationShutdown shutdown,
        IPlaybackCoordinator playback,
        IAccessibleAnnouncer announcer)
    {
        _desktop = desktop;
        _shutdown = shutdown;
        _playback = playback;
        _announcer = announcer;
        _desktop.CommandRequested += (_, command) => _ = ExecuteAsync(command);
    }

    public DesktopLifecycleState State => _state;

    /// <summary>Outcome of the single shutdown request, for diagnostics and tests.</summary>
    internal ShutdownResult? LastShutdownResult { get; private set; }

    /// <summary>Programmatic exit owns the window close; the hide-on-close preference only applies to the user.</summary>
    internal bool AllowWindowClose => _state != DesktopLifecycleState.Visible;
    public void Attach(WinFormsShell shell) => _desktop.Attach(shell);

    public Task<DesktopCommandResult> ExecuteAsync(DesktopCommand command, bool hideOnAltF4 = false) => command switch
    {
        DesktopCommand.Escape => Task.FromResult(Hide()),
        DesktopCommand.Restore => Task.FromResult(Restore()),
        DesktopCommand.ToggleVisibility => Task.FromResult(_state == DesktopLifecycleState.Visible ? Hide() : Restore()),
        DesktopCommand.Exit => ExitAsync(),
        DesktopCommand.TogglePlayback => Task.FromResult(TogglePlayback()),
        DesktopCommand.Previous => Task.FromResult(Move(true)),
        DesktopCommand.Next => Task.FromResult(Move(false)),
        _ => Task.FromResult(new DesktopCommandResult(_state, Applied: false))
    };

    public void PrepareForShutdown()
    {
        if (_state is DesktopLifecycleState.Exiting or DesktopLifecycleState.Closed) return;
        _state = DesktopLifecycleState.Exiting;
        _desktop.DeactivateTray();
    }

    private DesktopCommandResult Hide()
    {
        if (_state != DesktopLifecycleState.Visible || !_desktop.HideToTray()) return new(_state, Applied: false);
        _state = DesktopLifecycleState.Hidden;
        Announce("已隐藏到系统托盘，音乐继续播放。", "desktop-lifecycle");
        return new(_state);
    }

    private DesktopCommandResult Restore()
    {
        var target = _desktop.RestoreWindow();
        if (!_desktop.IsWindowRestored) return new(_state, Applied: false);
        _state = DesktopLifecycleState.Visible;
        return new(_state, target);
    }

    private DesktopCommandResult TogglePlayback()
    {
        if (_playback is null) return new(_state, Applied: false);
        if (_playback.IsPlaying) _playback.Pause();
        else _ = _playback.ReplayAsync();
        return new(_state);
    }

    private DesktopCommandResult Move(bool previous)
    {
        _ = previous ? _playback.PreviousAsync() : _playback.NextAsync();
        return new(_state);
    }

    private Task<DesktopCommandResult> ExitAsync()
    {
        lock (_exitGate) return _exit ??= ExitCoreAsync();
    }

    private async Task<DesktopCommandResult> ExitCoreAsync()
    {
        // The shutdown pipeline never throws: it persists every pending change, releases the
        // application host and reports a failure. Exit therefore always completes and a failed
        // save leaves the previously stored data intact.
        PrepareForShutdown();
        var result = await _shutdown.RequestAsync().ConfigureAwait(false);
        try
        {
            _state = DesktopLifecycleState.Closed;
            _desktop.CompleteExit();
        }
        finally
        {
            LastShutdownResult = result;
        }
        return new(_state);
    }

    private void Announce(string message, string activity) =>
        _announcer.Announce(new AccessibleAnnouncement(message, AnnouncementKind.Status, activity));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _desktop.Dispose();
    }
}

/// <summary>NotifyIcon adapter. All tray commands are explicit clicks/menu commands.</summary>
public sealed class WinFormsDesktopIntegration : IDisposable
{
    private readonly IAccessibleAnnouncer _announcer;
    private NotifyIcon? _tray;
    private WinFormsShell? _shell;
    public event EventHandler<DesktopCommand>? CommandRequested;

    public WinFormsDesktopIntegration(IAccessibleAnnouncer announcer) => _announcer = announcer;

    public bool IsWindowRestored => _shell is { Visible: true, WindowState: FormWindowState.Normal or FormWindowState.Maximized };

    public void Attach(WinFormsShell shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    public bool HideToTray()
    {
        if (_shell is null) return false;
        _shell.WindowState = FormWindowState.Minimized;
        _shell.ShowInTaskbar = false;
        _shell.Visible = false;
        EnsureTray().Visible = true;
        return true;
    }

    public DesktopFocusTarget RestoreWindow()
    {
        if (_shell is null || _shell.IsDisposed) return DesktopFocusTarget.None;
        _shell.ShowInTaskbar = true;
        _shell.Visible = true;
        _shell.WindowState = FormWindowState.Normal;
        _shell.Activate();
        return DesktopFocusTarget.MainWindow;
    }

    private NotifyIcon EnsureTray()
    {
        if (_tray is not null) return _tray;
        _tray = new NotifyIcon
        {
            Text = "落雪音乐无障碍版",
            Icon = SystemIcons.Application,
            Visible = true
        };
        var menu = new ContextMenu();
        menu.MenuItems.Add(new MenuItem("显示主窗口", (_, _) => CommandRequested?.Invoke(this, DesktopCommand.Restore)));
        menu.MenuItems.Add(new MenuItem("播放/暂停", (_, _) => CommandRequested?.Invoke(this, DesktopCommand.TogglePlayback)));
        menu.MenuItems.Add(new MenuItem("上一首", (_, _) => CommandRequested?.Invoke(this, DesktopCommand.Previous)));
        menu.MenuItems.Add(new MenuItem("下一首", (_, _) => CommandRequested?.Invoke(this, DesktopCommand.Next)));
        menu.MenuItems.Add(new MenuItem("-"));
        menu.MenuItems.Add(new MenuItem("退出", (_, _) => CommandRequested?.Invoke(this, DesktopCommand.Exit)));
        _tray.ContextMenu = menu;
        _tray.DoubleClick += (_, _) => CommandRequested?.Invoke(this, DesktopCommand.Restore);
        return _tray;
    }

    public void DeactivateTray()
    {
        if (_tray is not null) _tray.Visible = false;
    }

    public void CompleteExit()
    {
        var shell = _shell;
        if (shell is { IsDisposed: false })
        {
            // The exit workflow can finish on a background thread. Closing the window from there
            // disposes it without ending the message loop, so marshal onto the owning UI thread.
            if (shell.IsHandleCreated && shell.InvokeRequired) shell.BeginInvoke(new Action(shell.Close));
            else shell.Close();
        }
        _tray?.Dispose();
        _tray = null;
    }

    public void Dispose()
    {
        _tray?.Dispose();
        _tray = null;
    }
}

/// <summary>Creates runtime-dependent native secondary windows from the Host-owned dependency graph.</summary>
public sealed class ApplicationWindowFactory(
    IAccessibleAnnouncer announcer,
    LyricsWorkflow lyrics,
    IMusicSourceService sources,
    SettingsWorkflow settings,
    ITtsService tts,
    IDownloadWorkflow downloads,
    IPlaylistImportService playlistImport,
    IPlaylistLoginWindowFactory playlistLoginWindows,
    IManualSourceDiscoveryWorkflow manualSources)
{
    public UserGuideForm CreateUserGuide(Form? owner)
    {
        var form = new UserGuideForm(announcer);
        if (owner is not null) form.Show(owner);
        return form;
    }

    public SponsorForm CreateSponsor(Form? owner)
    {
        var form = new SponsorForm(announcer);
        form.Owner = owner;
        return form;
    }

    public LyricsForm CreateLyrics(Func<KeyEventArgs, bool> handleHotkey) => new(announcer, lyrics, handleHotkey);

    public SourceManagerForm CreateSourceManager(Form owner, Func<KeyEventArgs, bool> handleHotkey) => new(announcer, sources, handleHotkey) { Owner = owner };

    public DownloadManagerForm CreateDownloadManager(Form owner, Func<KeyEventArgs, bool> handleHotkey) => new(announcer, downloads) { Owner = owner };

    public SettingsForm CreateSettings(
        Form owner,
        Func<KeyEventArgs, bool> handleHotkey,
        Action<bool> setInputSuppressed,
        Hotkey? reconfigure = null) => new(announcer, settings, tts, downloads, handleHotkey, setInputSuppressed, reconfigure) { Owner = owner };

    public PlaylistImportForm CreatePlaylistImport(
        Form owner,
        Func<KeyEventArgs, bool> handleHotkey,
        Action<ListBox> attachSongList,
        Action<ListBox> detachSongList) => new(playlistImport, announcer, playlistLoginWindows, list => attachSongList(list)) { Owner = owner };

    public ManualSourceSwitchForm CreateManualSource(
        Form owner,
        Func<KeyEventArgs, bool> handleHotkey,
        IManualSourceDiscoveryWorkflow? workflow,
        MusicInfo? music) => new(announcer, workflow ?? manualSources, music, handleHotkey) { Owner = owner };
}

/// <summary>The single composition root for the native desktop process.</summary>
public static class ApplicationHost
{
    internal static ApplicationRuntime Create()
    {
        var dataDirectory = AppDataPaths.ResolveDataDir();
        return new ApplicationRuntime(
            () => CreateBuilder(dataDirectory).Build(),
            new ProductionStartupInitializer(dataDirectory),
            new WinFormsStartupFailurePresenter());
    }

    internal static IHostBuilder CreateBuilder(string dataDirectory, Action<IServiceCollection>? replaceAdapters = null)
    {
        return Host.CreateDefaultBuilder()
            .UseDefaultServiceProvider((_, options) => { options.ValidateOnBuild = true; options.ValidateScopes = true; })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSerilog();
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
                services.AddSingleton<MusicHttpClient>();
                services.AddSingleton<NeteaseMusicService>();
                services.AddSingleton<QqMusicService>();
                services.AddSingleton<KugouMusicService>();
                services.AddSingleton<KuwoMusicService>();
                services.AddSingleton<MiguMusicService>();
                services.AddSingleton<ScriptNetworkClient>();
                services.AddSingleton<ScriptMusicSource>();
                services.AddSingleton<IAccessibleAnnouncer, WinFormsAccessibleAnnouncer>();
                services.AddSingleton<IDataService>(sp => new DataService(sp.GetRequiredService<ILogger<DataService>>(), dataDirectory));
                services.AddSingleton(sp => new DatabaseInitializer(sp.GetRequiredService<ILogger<DatabaseInitializer>>(), dataDirectory));
                services.AddSingleton<IAudioPlayer, AudioPlayer>();
                services.AddSingleton<BackgroundWorkHost>();
                services.AddSingleton<IPlaybackSourceAdapter, MusicPlaybackSourceAdapter>();
                services.AddSingleton(sp => new MusicPlayerService(
                    sp.GetRequiredService<ILogger<MusicPlayerService>>(),
                    sp.GetRequiredService<IAudioPlayer>(),
                    sp.GetRequiredService<IPlaybackSourceAdapter>(),
                    sp.GetRequiredService<IAccessibleAnnouncer>(),
                    sp.GetRequiredService<BackgroundWorkHost>(),
                    sp.GetRequiredService<HttpClient>(),
                    Path.Combine(dataDirectory, "cache")));
                services.AddSingleton<IPlaybackCoordinator>(sp => sp.GetRequiredService<MusicPlayerService>());
                services.AddSingleton<IAudioPlaybackControl>(sp => sp.GetRequiredService<MusicPlayerService>());
                services.AddSingleton<IExactPlaybackCoordinator>(sp => sp.GetRequiredService<MusicPlayerService>());
                services.AddSingleton<MusicSourceService>();
                services.AddSingleton<IManualSourceGateway>(sp => sp.GetRequiredService<MusicSourceService>());
                services.AddSingleton<IManualSourceDiscoveryWorkflow, ManualSourceDiscoveryWorkflow>();
                services.AddSingleton<IMusicSourceService>(sp => sp.GetRequiredService<MusicSourceService>());
                services.AddSingleton<IMusicBrowseSource>(sp => sp.GetRequiredService<MusicSourceService>());
                services.AddSingleton(_ => new LocalSearchHistoryStore(Path.Combine(dataDirectory, "file-storage")));
                services.AddSingleton<MusicBrowseWorkflow>();
                services.AddSingleton<SavedPlaylistWorkflow>();
                services.AddSingleton<IDownloadWorkflow, DownloadWorkflow>();
                services.AddSingleton<ILyricService, LyricService>();
                services.AddSingleton<LyricsWorkflow>();
                services.AddSingleton<LxScriptEngine>();
                services.AddSingleton<ITtsService, RoutingTtsService>();
                services.AddSingleton<SettingsWorkflow>();
                services.AddSingleton<GlobalHotkeyMessageWindow>();
                services.AddSingleton<IProcessLauncher, SystemProcessLauncher>();
                services.AddSingleton<IPlaylistImportService, PlaylistImportService>();
                services.AddSingleton<IPlaylistImportProvider, NeteasePlaylistProvider>();
                services.AddSingleton<IPlaylistImportProvider, QqPlaylistProvider>();
                services.AddSingleton<IPlaylistImportProvider, KugouPlaylistProvider>();
                services.AddSingleton<IPlaylistImportProvider, KuwoPlaylistProvider>();
                services.AddSingleton<KugouQrLoginService>();
                services.AddSingleton<IPlaylistLoginWindowFactory, PlaylistLoginWindowFactory>();
                services.AddSingleton<ApplicationWindowFactory>();
                services.AddSingleton<AnnouncementToggleService>();
                services.AddSingleton<ApplicationShutdownRequest>();
                services.AddSingleton<WinFormsDesktopIntegration>();
                services.AddSingleton(sp => new DesktopLifecycleWorkflow(
                    sp.GetRequiredService<WinFormsDesktopIntegration>(),
                    sp.GetRequiredService<ApplicationShutdownRequest>(),
                    sp.GetRequiredService<IPlaybackCoordinator>(),
                    sp.GetRequiredService<IAccessibleAnnouncer>()));
                services.AddSingleton<IApplicationShutdownParticipant, ShellShutdownParticipant>();
                services.AddSingleton<IShutdownFeedback, ShutdownFeedback>();
                services.AddSingleton<WinFormsShell>(sp => new WinFormsShell(new WinFormsShellServices
                {
                    Announcer = sp.GetRequiredService<IAccessibleAnnouncer>(),
                    BrowseSource = sp.GetRequiredService<IMusicBrowseSource>(),
                    Playback = sp.GetRequiredService<IPlaybackCoordinator>(),
                    Audio = sp.GetRequiredService<IAudioPlaybackControl>(),
                    BrowseWorkflow = sp.GetRequiredService<MusicBrowseWorkflow>(),
                    SavedPlaylists = sp.GetRequiredService<SavedPlaylistWorkflow>(),
                    Lyrics = sp.GetRequiredService<LyricsWorkflow>(),
                    Downloads = sp.GetRequiredService<IDownloadWorkflow>(),
                    Settings = sp.GetRequiredService<SettingsWorkflow>(),
                    Sources = sp.GetRequiredService<IMusicSourceService>(),
                    ManualSources = sp.GetRequiredService<IManualSourceDiscoveryWorkflow>(),
                    Windows = sp.GetRequiredService<ApplicationWindowFactory>(),
                    Desktop = sp.GetRequiredService<DesktopLifecycleWorkflow>(),
                    Announcements = sp.GetRequiredService<AnnouncementToggleService>()
                }));
                replaceAdapters?.Invoke(services);
            });
    }

    internal static async Task ReleaseAsync(IHost host, CancellationToken cancellationToken = default)
    {
        try { await host.StopAsync(cancellationToken); }
        finally { host.Dispose(); }
    }
}

internal sealed class ShellShutdownParticipant(
    SettingsWorkflow settings,
    IMusicSourceService sources) : IApplicationShutdownParticipant
{
    public ApplicationShutdownSnapshot CaptureAndBlock() => new(settings.Current);

    public async Task PersistAsync(ApplicationShutdownSnapshot snapshot, CancellationToken cancellationToken)
    {
        var saved = await settings.SaveAsync(snapshot.Settings, cancellationToken);
        if (!saved.Succeeded) throw new InvalidOperationException("Settings persistence failed");
        await sources.PersistSourcesOrThrowAsync(cancellationToken);
    }
}

internal sealed class WinFormsStartupFailurePresenter : IStartupFailurePresenter
{
    public Task<StartupFailureDecision> ShowAsync(Exception exception, CancellationToken cancellationToken)
    {
        var result = MessageBox.Show(
            "应用初始化失败：" + exception.Message + "\n\n选择确定再次初始化，或选择取消安全退出。",
            "落雪音乐无障碍版启动失败",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Error,
            MessageBoxDefaultButton.Button1);
        return Task.FromResult(result == DialogResult.OK ? StartupFailureDecision.Retry : StartupFailureDecision.Exit);
    }
}

internal interface IStartupInitializer { Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken); }
internal interface IStartupFailurePresenter { Task<StartupFailureDecision> ShowAsync(Exception exception, CancellationToken cancellationToken); }
internal enum StartupFailureDecision { Retry, Exit }

internal sealed class ProductionStartupInitializer(string dataDirectory) : IStartupInitializer
{
    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));
        var migration = await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        if (!migration.Succeeded) throw new InvalidOperationException(migration.Message);
        _ = services.GetRequiredService<IDataService>();
        _ = services.GetRequiredService<IAccessibleAnnouncer>();
        _ = services.GetRequiredService<IPlaybackCoordinator>();
        _ = services.GetRequiredService<IAudioPlaybackControl>();
        // The message window and the persisted bindings are wired before source initialization:
        // registration needs a live window handle, and source scripts can spin up work that must not
        // delay the global hotkeys the user asked for. The window handle is created on the message
        // loop thread, so bindings stay active for the whole session.
        var globalHotkeys = services.GetRequiredService<GlobalHotkeyMessageWindow>();
        var settings = services.GetRequiredService<SettingsWorkflow>();
        globalHotkeys.HotkeyReceived += (_, id) => settings.RaiseGlobalHotkey(id);
        await settings.LoadAsync(globalHotkeys.Handle);
        var sources = services.GetRequiredService<IMusicSourceService>();
        await sources.InitializeScriptSourcesAsync(cancellationToken);
        _ = services.GetRequiredService<IDownloadWorkflow>();
        _ = services.GetRequiredService<ILyricService>();
        _ = services.GetRequiredService<LyricsWorkflow>();
        _ = services.GetRequiredService<IPlaylistImportService>();
        cancellationToken.ThrowIfCancellationRequested();
    }
}

public enum ApplicationStartOutcome { Started, ExitRequested }

/// <summary>Owns native application startup and Host lifetime.</summary>
public sealed class ApplicationRuntime : IAsyncDisposable
{
    private readonly Func<IHost> _createHost;
    private readonly IStartupInitializer _initializer;
    private readonly IStartupFailurePresenter _failurePresenter;
    private readonly Action<IServiceCollection>? _replaceAdapters;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);
    private IHost? _host;
    private ApplicationShutdownCoordinator? _shutdown;
    private bool _started;
    private bool _disposed;

    internal ApplicationRuntime(
        Func<IHost> createHost,
        IStartupInitializer initializer,
        IStartupFailurePresenter failurePresenter,
        Action<IServiceCollection>? replaceAdapters = null)
    {
        _createHost = createHost;
        _initializer = initializer;
        _failurePresenter = failurePresenter;
        _replaceAdapters = replaceAdapters;
    }

    public IServiceProvider? Services => _host?.Services;

    public async Task<ApplicationStartOutcome> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return ApplicationStartOutcome.Started;
        while (true)
        {
            try
            {
                _host = _createHost();
                await _host.StartAsync(cancellationToken);
                await _initializer.InitializeAsync(_host.Services, cancellationToken);
                BindShutdown(_host);
                var shell = _host.Services.GetRequiredService<WinFormsShell>();
                _host.Services.GetRequiredService<DesktopLifecycleWorkflow>().Attach(shell);
                shell.Show();
                _started = true;
                return ApplicationStartOutcome.Started;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _shutdown = null;
                if (_host is not null) await ApplicationHost.ReleaseAsync(_host, cancellationToken);
                _host = null;
                if (await _failurePresenter.ShowAsync(ex, cancellationToken) == StartupFailureDecision.Exit)
                    return ApplicationStartOutcome.ExitRequested;
            }
        }
    }

    /// <summary>
    /// Connects the exit path to the single shutdown pipeline before the main window appears, so a
    /// tray exit, a menu exit and the window close button all persist the same pending state.
    /// </summary>
    private void BindShutdown(IHost host)
    {
        var request = host.Services.GetService<ApplicationShutdownRequest>();
        if (request is null || request.IsBound) return;

        var services = host.Services;
        var coordinator = new ApplicationShutdownCoordinator(
            () => services.GetRequiredService<IApplicationShutdownParticipant>(),
            token => services.GetRequiredService<BackgroundWorkHost>().StopAsync(token),
            token => ApplicationHost.ReleaseAsync(host, token),
            services.GetRequiredService<IShutdownFeedback>(),
            services.GetRequiredService<ILogger<ApplicationShutdownCoordinator>>(),
            ShutdownTimeout);
        request.Bind(coordinator.RequestAsync);
        _shutdown = coordinator;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        var host = Interlocked.Exchange(ref _host, null);
        _started = false;
        var shutdown = _shutdown;
        if (shutdown is not null)
        {
            // The coordinator persists the pending state and releases the host exactly once, so the
            // window close button and the explicit exit command share one durable save.
            await shutdown.RequestAsync();
            return;
        }

        if (host is not null) await ApplicationHost.ReleaseAsync(host, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync();
        _disposed = true;
    }
}
