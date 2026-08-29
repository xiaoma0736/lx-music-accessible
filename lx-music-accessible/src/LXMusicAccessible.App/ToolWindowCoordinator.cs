using System.Windows;
using System.Windows.Input;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.App;

internal enum ToolWindowKind
{
    Settings,
    SourceManager,
    DownloadManager,
    PlaylistImport
}

internal enum ToolWindowOpenResult
{
    Created,
    Activated
}

internal interface IToolWindowHandle
{
    ToolWindowKind Kind { get; }
    bool IsVisible { get; }
    bool IsActive { get; }
    bool IsClosed { get; }
    event EventHandler? Closed;
    void ShowModal();
    void Hide();
    void Restore();
    bool Activate();
    object? CaptureFocus();
    bool RestoreFocus(object? focus);
}

internal sealed record ToolWindowMemberSnapshot(IToolWindowHandle Window, object? Focus);

internal sealed record ToolWindowGroupSnapshot(
    IReadOnlyList<ToolWindowMemberSnapshot> VisibleMembers,
    IToolWindowHandle? ActiveWindow)
{
    internal static ToolWindowGroupSnapshot Empty { get; } = new([], null);
}

/// <summary>Owns the four fixed tool-window instances and their window-group visibility snapshot.</summary>
public sealed class ToolWindowCoordinator
{
    private readonly Func<Window, Func<KeyEventArgs, bool>, Action<bool>, Hotkey?, IToolWindowHandle>? _createSettings;
    private readonly Func<Window, Func<KeyEventArgs, bool>, IToolWindowHandle> _createSourceManager;
    private readonly Dictionary<ToolWindowKind, IToolWindowHandle> _windows = [];

    public ToolWindowCoordinator(ApplicationWindowFactory windows)
        : this(
            (owner, handleHotkey, setInputSuppressed, reconfigure) => new WpfToolWindowHandle(
                ToolWindowKind.Settings,
                windows.CreateSettings(owner, handleHotkey, setInputSuppressed, reconfigure),
                modal: true),
            (owner, handleHotkey) => new WpfToolWindowHandle(
                ToolWindowKind.SourceManager,
                windows.CreateSourceManager(owner, handleHotkey),
                modal: true))
    {
    }

    internal ToolWindowCoordinator(
        Func<Window, Func<KeyEventArgs, bool>, IToolWindowHandle> createSourceManager)
        : this(null, createSourceManager)
    {
    }

    internal ToolWindowCoordinator(
        Func<Window, Func<KeyEventArgs, bool>, Action<bool>, Hotkey?, IToolWindowHandle>? createSettings,
        Func<Window, Func<KeyEventArgs, bool>, IToolWindowHandle> createSourceManager)
    {
        ArgumentNullException.ThrowIfNull(createSourceManager);
        _createSettings = createSettings;
        _createSourceManager = createSourceManager;
    }

    internal ToolWindowOpenResult ShowSettings(
        Window owner,
        Func<KeyEventArgs, bool> handleHotkey,
        Action<bool> setInputSuppressed,
        Action restoreOwnerFocus,
        Hotkey? reconfigure = null)
    {
        if (_createSettings is null)
            throw new InvalidOperationException("The settings-window factory is unavailable.");
        ArgumentNullException.ThrowIfNull(setInputSuppressed);
        return ShowModal(
            ToolWindowKind.Settings,
            owner,
            handleHotkey,
            restoreOwnerFocus,
            () => _createSettings(owner, handleHotkey, setInputSuppressed, reconfigure));
    }

    internal ToolWindowOpenResult ShowSourceManager(
        Window owner,
        Func<KeyEventArgs, bool> handleHotkey,
        Action restoreOwnerFocus)
    {
        return ShowModal(
            ToolWindowKind.SourceManager,
            owner,
            handleHotkey,
            restoreOwnerFocus,
            () => _createSourceManager(owner, handleHotkey));
    }

    private ToolWindowOpenResult ShowModal(
        ToolWindowKind kind,
        Window owner,
        Func<KeyEventArgs, bool> handleHotkey,
        Action restoreOwnerFocus,
        Func<IToolWindowHandle> create)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(handleHotkey);
        ArgumentNullException.ThrowIfNull(restoreOwnerFocus);

        if (_windows.TryGetValue(kind, out var existing))
        {
            existing.Activate();
            return ToolWindowOpenResult.Activated;
        }

        var window = create();
        if (window.Kind != kind)
            throw new InvalidOperationException("The tool-window factory returned the wrong kind.");
        _windows.Add(window.Kind, window);
        window.Closed += Window_Closed;
        try
        {
            window.ShowModal();
        }
        finally
        {
            if (!_windows.ContainsKey(window.Kind) && owner.IsVisible)
                restoreOwnerFocus();
        }
        return ToolWindowOpenResult.Created;
    }

    internal ToolWindowGroupSnapshot HideVisibleWindows()
    {
        var visible = _windows.Values
            .Where(window => window.IsVisible && !window.IsClosed)
            .Select(window => new ToolWindowMemberSnapshot(
                window,
                window.IsActive ? window.CaptureFocus() : null))
            .ToArray();
        var active = visible.Select(member => member.Window).FirstOrDefault(window => window.IsActive);
        foreach (var member in visible.Reverse()) member.Window.Hide();
        return new(visible, active);
    }

    internal bool RestoreVisibleWindows(ToolWindowGroupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var member in snapshot.VisibleMembers)
        {
            if (IsRegistered(member.Window)) member.Window.Restore();
        }

        if (snapshot.ActiveWindow is null || !IsRegistered(snapshot.ActiveWindow)) return false;
        snapshot.ActiveWindow.Activate();
        var focus = snapshot.VisibleMembers
            .First(member => ReferenceEquals(member.Window, snapshot.ActiveWindow))
            .Focus;
        return snapshot.ActiveWindow.RestoreFocus(focus);
    }

    internal bool IsOpen(ToolWindowKind kind) => _windows.ContainsKey(kind);

    private bool IsRegistered(IToolWindowHandle window) =>
        _windows.TryGetValue(window.Kind, out var current) && ReferenceEquals(current, window);

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (sender is not IToolWindowHandle window || !IsRegistered(window)) return;
        window.Closed -= Window_Closed;
        _windows.Remove(window.Kind);
    }
}

internal sealed class WpfToolWindowHandle : IToolWindowHandle
{
    private readonly Window _window;
    private readonly bool _modal;
    private bool _closed;

    internal WpfToolWindowHandle(ToolWindowKind kind, Window window, bool modal)
    {
        Kind = kind;
        _window = window;
        _modal = modal;
        _window.Closed += Window_Closed;
    }

    public ToolWindowKind Kind { get; }
    public bool IsVisible => _window.IsVisible;
    public bool IsActive => _window.IsActive;
    public bool IsClosed => _closed;
    public event EventHandler? Closed;

    public void ShowModal() => _window.ShowDialog();
    public void Hide()
    {
        if (_window.IsVisible) _window.Hide();
    }

    public void Restore()
    {
        if (_closed || _window.IsVisible) return;
        if (_modal)
        {
            _ = _window.Dispatcher.BeginInvoke(() =>
            {
                if (!_closed && !_window.IsVisible) _window.ShowDialog();
            });
        }
        else
        {
            _window.Show();
        }
    }

    public bool Activate() => !_closed && _window.Activate();

    public object? CaptureFocus()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        return focused is not null && IsDescendantOf(focused, _window) ? focused : null;
    }

    public bool RestoreFocus(object? focus)
    {
        if (_modal && !_window.IsVisible && !_closed)
        {
            _ = _window.Dispatcher.BeginInvoke(() =>
            {
                if (!_closed && _window.IsVisible)
                {
                    _window.Activate();
                    RestoreFocusCore(focus);
                }
            });
            return true;
        }

        return RestoreFocusCore(focus);
    }

    private bool RestoreFocusCore(object? focus)
    {
        if (focus is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } element
            && element.Focus())
        {
            return true;
        }
        return _window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)) || _window.Focus();
    }

    private static bool IsDescendantOf(DependencyObject current, DependencyObject ancestor)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            var visualParent = System.Windows.Media.VisualTreeHelper.GetParent(current);
            current = visualParent ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        _window.Closed -= Window_Closed;
        Closed?.Invoke(this, EventArgs.Empty);
        Closed = null;
    }
}
