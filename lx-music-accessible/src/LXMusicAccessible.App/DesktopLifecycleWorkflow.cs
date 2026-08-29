using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using LXMusicAccessible.Services;

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

public enum DesktopLifecycleState
{
    Visible,
    Hidden,
    Exiting,
    Closed
}

public enum DesktopFocusTarget
{
    None,
    PreviousControl,
    PrimaryContent,
    MainWindow
}

public readonly record struct DesktopCommandResult(
    DesktopLifecycleState State,
    DesktopFocusTarget FocusTarget = DesktopFocusTarget.None,
    bool Applied = true);

internal sealed class DesktopCommandRequestedEventArgs(DesktopCommand command) : EventArgs
{
    internal DesktopCommand Command { get; } = command;
}

internal interface IDesktopIntegration : IDisposable
{
    event EventHandler<DesktopCommandRequestedEventArgs>? CommandRequested;

    bool IsWindowRestored { get; }
    void Attach(MainWindow window);
    bool HideToTray();
    DesktopFocusTarget RestoreWindow();
    void DeactivateTray();
    void CompleteExit();
}

/// <summary>
/// Owns desktop visibility, close policy, tray commands and focus restoration.
/// Callers and tests use the same command interface; WPF and tray details stay in
/// the desktop integration adapter.
/// </summary>
public sealed class DesktopLifecycleWorkflow : IDisposable
{
    private const string ActivityId = "desktop-lifecycle";
    private readonly object _gate = new();
    private readonly IDesktopIntegration _desktop;
    private readonly IApplicationShutdown _shutdown;
    private readonly IPlaybackCoordinator _playback;
    private readonly IAccessibleAnnouncer _announcer;
    private Task<DesktopCommandResult>? _exit;
    private DesktopLifecycleState _state = DesktopLifecycleState.Visible;
    private bool _allowWindowClose;
    private bool _disposed;

    internal DesktopLifecycleWorkflow(
        IDesktopIntegration desktop,
        IApplicationShutdown shutdown,
        IPlaybackCoordinator playback,
        IAccessibleAnnouncer announcer)
    {
        _desktop = desktop;
        _shutdown = shutdown;
        _playback = playback;
        _announcer = announcer;
        _desktop.CommandRequested += Desktop_CommandRequested;
    }

    public DesktopLifecycleState State
    {
        get { lock (_gate) return _state; }
    }

    internal bool AllowWindowClose
    {
        get { lock (_gate) return _allowWindowClose; }
    }

    internal void Attach(MainWindow window) => _desktop.Attach(window);

    public Task<DesktopCommandResult> ExecuteAsync(
        DesktopCommand command,
        bool hideOnAltF4 = false) => command switch
    {
        DesktopCommand.Escape => Task.FromResult(Hide()),
        DesktopCommand.Restore => Task.FromResult(Restore()),
        DesktopCommand.ToggleVisibility => Task.FromResult(ToggleVisibility()),
        DesktopCommand.AltF4 => hideOnAltF4
            ? Task.FromResult(Hide())
            : ExitAsync(),
        DesktopCommand.Exit => ExitAsync(),
        DesktopCommand.TogglePlayback => TogglePlaybackAsync(),
        DesktopCommand.Previous => MovePlaybackAsync(previous: true),
        DesktopCommand.Next => MovePlaybackAsync(previous: false),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    internal void PrepareForShutdown()
    {
        lock (_gate)
        {
            if (_state is DesktopLifecycleState.Closed or DesktopLifecycleState.Exiting) return;
            _state = DesktopLifecycleState.Exiting;
        }
        TryDeactivateTray();
    }

    private DesktopCommandResult Hide()
    {
        lock (_gate)
        {
            if (_disposed || _state != DesktopLifecycleState.Visible || !_desktop.IsWindowRestored)
                return new(_state, Applied: false);
            if (!_desktop.HideToTray())
                return new(_state, Applied: false);
            _state = DesktopLifecycleState.Hidden;
        }

        Announce("已隐藏到系统托盘，音乐继续播放");
        return new(DesktopLifecycleState.Hidden);
    }

    private DesktopCommandResult Restore()
    {
        DesktopFocusTarget focusTarget;
        lock (_gate)
        {
            if (_disposed || _state is DesktopLifecycleState.Exiting or DesktopLifecycleState.Closed)
                return new(_state, Applied: false);
            if (_state == DesktopLifecycleState.Visible && _desktop.IsWindowRestored)
                return new(_state, Applied: false);
            focusTarget = _desktop.RestoreWindow();
            _state = DesktopLifecycleState.Visible;
        }

        Announce("已恢复主窗口");
        return new(DesktopLifecycleState.Visible, focusTarget);
    }

    private DesktopCommandResult ToggleVisibility() =>
        State == DesktopLifecycleState.Hidden || !_desktop.IsWindowRestored
            ? Restore()
            : Hide();

    private async Task<DesktopCommandResult> TogglePlaybackAsync()
    {
        if (_playback.IsPlaying)
        {
            _playback.Pause();
            Announce("已暂停");
        }
        else if (_playback.IsPaused && _playback.CurrentMusic is not null)
        {
            await _playback.ResumeAsync();
            Announce("继续播放");
        }
        else if (_playback.CurrentMusic is not null)
        {
            await _playback.ReplayAsync();
            Announce("继续播放");
        }
        else
        {
            Announce("当前没有正在播放的歌曲");
        }

        return new(State, Applied: _playback.CurrentMusic is not null);
    }

    private async Task<DesktopCommandResult> MovePlaybackAsync(bool previous)
    {
        if (previous)
        {
            await _playback.PreviousAsync();
            Announce("上一首");
        }
        else
        {
            await _playback.NextAsync();
            Announce("下一首");
        }
        return new(State);
    }

    private Task<DesktopCommandResult> ExitAsync()
    {
        TaskCompletionSource<DesktopCommandResult>? start = null;
        lock (_gate)
        {
            if (_exit is not null) return _exit;
            if (_disposed) return Task.FromResult(new DesktopCommandResult(_state, Applied: false));

            _state = DesktopLifecycleState.Exiting;
            start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _exit = start.Task;
        }

        TryDeactivateTray();
        _ = CompleteExitAsync(start);
        return _exit;
    }

    private async Task CompleteExitAsync(TaskCompletionSource<DesktopCommandResult> completion)
    {
        try
        {
            await _shutdown.RequestAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Shutdown coordination failed: {exception.GetType().Name}");
        }
        finally
        {
            lock (_gate)
            {
                _allowWindowClose = true;
                _state = DesktopLifecycleState.Closed;
            }
            try { _desktop.CompleteExit(); }
            catch (Exception exception)
            {
                Debug.WriteLine($"Desktop close failed: {exception.GetType().Name}");
            }
            finally { completion.TrySetResult(new(DesktopLifecycleState.Closed)); }
        }
    }

    private async void Desktop_CommandRequested(object? sender, DesktopCommandRequestedEventArgs e)
    {
        try { await ExecuteAsync(e.Command); }
        catch (Exception exception)
        {
            Debug.WriteLine($"Desktop command failed: {exception.GetType().Name}");
            _announcer.Announce(new(
                "桌面命令执行失败",
                AnnouncementKind.Error,
                ActivityId,
                AnnouncementSpeech.FollowPreference));
        }
    }

    private void Announce(string text) => _announcer.Announce(new(
        text,
        AnnouncementKind.Status,
        ActivityId,
        AnnouncementSpeech.FollowPreference));

    private void TryDeactivateTray()
    {
        try { _desktop.DeactivateTray(); }
        catch (Exception exception)
        {
            Debug.WriteLine($"Tray deactivation failed: {exception.GetType().Name}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _desktop.CommandRequested -= Desktop_CommandRequested;
    }
}

internal sealed class WpfDesktopIntegration(ToolWindowCoordinator toolWindows) : IDesktopIntegration
{
    private MainWindow? _window;
    private IInputElement? _lastFocusedElement;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private System.Drawing.Icon? _appIcon;
    private ToolWindowGroupSnapshot _toolSnapshot = ToolWindowGroupSnapshot.Empty;
    private bool _disposed;

    public event EventHandler<DesktopCommandRequestedEventArgs>? CommandRequested;

    public bool IsWindowRestored =>
        _window is { IsVisible: true, WindowState: not WindowState.Minimized };

    public void Attach(MainWindow window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is not null && !ReferenceEquals(_window, window))
            throw new InvalidOperationException("The desktop window is already attached.");
        _window = window;
    }

    public bool HideToTray()
    {
        var window = RequireWindow();
        if (!EnsureTrayIcon()) return false;

        _lastFocusedElement = Keyboard.FocusedElement;
        _toolSnapshot = toolWindows.HideVisibleWindows();
        window.Hide();
        try
        {
            _trayIcon!.ShowBalloonTip(
                1500,
                "落雪音乐 无障碍版",
                "已隐藏到系统托盘，音乐继续播放。双击托盘图标可恢复窗口。",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (InvalidOperationException exception)
        {
            Debug.WriteLine($"Tray notification unavailable: {exception.GetType().Name}");
        }
        return true;
    }

    public DesktopFocusTarget RestoreWindow()
    {
        var window = RequireWindow();
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;

        if (toolWindows.RestoreVisibleWindows(_toolSnapshot))
            return DesktopFocusTarget.PreviousControl;

        if (_lastFocusedElement is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } previous
            && previous.Focus())
        {
            return DesktopFocusTarget.PreviousControl;
        }
        if (window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
            return DesktopFocusTarget.PrimaryContent;
        window.Focus();
        return DesktopFocusTarget.MainWindow;
    }

    public void DeactivateTray()
    {
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    public void CompleteExit()
    {
        var window = _window;
        if (window is null) return;
        if (window.Dispatcher.CheckAccess()) window.Close();
        else window.Dispatcher.BeginInvoke(window.Close);
    }

    private MainWindow RequireWindow() =>
        _window ?? throw new InvalidOperationException("The desktop window is not attached.");

    private bool EnsureTrayIcon()
    {
        if (_trayIcon is not null) return true;
        System.Windows.Forms.ContextMenuStrip? menu = null;
        System.Windows.Forms.NotifyIcon? icon = null;
        System.Drawing.Icon? appIcon = null;
        try
        {
            menu = new System.Windows.Forms.ContextMenuStrip();
            AddMenuItem(menu, "显示主窗口", DesktopCommand.Restore);
            AddMenuItem(menu, "播放/暂停", DesktopCommand.TogglePlayback);
            AddMenuItem(menu, "上一首", DesktopCommand.Previous);
            AddMenuItem(menu, "下一首", DesktopCommand.Next);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            AddMenuItem(menu, "退出", DesktopCommand.Exit);

            appIcon = CreateAppIcon();
            icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = appIcon,
                Text = "落雪音乐 无障碍版",
                Visible = true,
                ContextMenuStrip = menu
            };
            icon.DoubleClick += TrayIcon_DoubleClick;
            _trayMenu = menu;
            _trayIcon = icon;
            _appIcon = appIcon;
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Tray initialization failed: {exception.GetType().Name}");
            icon?.Dispose();
            menu?.Dispose();
            appIcon?.Dispose();
            return false;
        }
    }

    private void AddMenuItem(
        System.Windows.Forms.ContextMenuStrip menu,
        string text,
        DesktopCommand command)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text) { Tag = command };
        item.Click += TrayMenuItem_Click;
        menu.Items.Add(item);
    }

    private void TrayMenuItem_Click(object? sender, EventArgs e)
    {
        if (sender is System.Windows.Forms.ToolStripMenuItem { Tag: DesktopCommand command })
            Raise(command);
    }

    private void TrayIcon_DoubleClick(object? sender, EventArgs e) => Raise(DesktopCommand.Restore);

    private void Raise(DesktopCommand command)
    {
        if (_disposed) return;
        var window = _window;
        if (window is not null && !window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.BeginInvoke(() => Raise(command));
            return;
        }
        CommandRequested?.Invoke(this, new(command));
    }

    private static System.Drawing.Icon CreateAppIcon()
    {
        try
        {
            using var bitmap = new System.Drawing.Bitmap(32, 32);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(System.Drawing.Color.Transparent);
                using var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0, 120, 215));
                graphics.FillEllipse(background, 2, 2, 28, 28);
                using var foreground = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2.5f);
                graphics.FillEllipse(foreground, 9, 16, 6, 6);
                graphics.FillEllipse(foreground, 20, 12, 6, 6);
                graphics.DrawLine(pen, 12, 17, 12, 7);
                graphics.DrawLine(pen, 23, 13, 23, 5);
            }

            var handle = bitmap.GetHicon();
            try
            {
                using var icon = System.Drawing.Icon.FromHandle(handle);
                return (System.Drawing.Icon)icon.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch
        {
            return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_trayIcon is not null)
        {
            _trayIcon.DoubleClick -= TrayIcon_DoubleClick;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        if (_trayMenu is not null)
        {
            foreach (var item in _trayMenu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>())
                item.Click -= TrayMenuItem_Click;
            _trayMenu.Dispose();
            _trayMenu = null;
        }
        _appIcon?.Dispose();
        _appIcon = null;
        CommandRequested = null;
        _lastFocusedElement = null;
        _toolSnapshot = ToolWindowGroupSnapshot.Empty;
    }
}
