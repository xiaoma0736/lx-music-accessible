using System;
using System.Windows.Interop;

namespace LXMusicAccessible.App;

/// <summary>
/// Receives Win32 global hotkey messages on a window whose lifetime is independent
/// of the main application window visibility and close/shutdown lifecycle.
/// </summary>
internal sealed class GlobalHotkeyMessageWindow : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private bool _disposed;

    public event EventHandler<int>? HotkeyReceived;

    public IntPtr Handle => _disposed ? IntPtr.Zero : _source.Handle;

    public GlobalHotkeyMessageWindow()
    {
        var parameters = default(HwndSourceParameters);
        parameters.WindowName = "LXMusicAccessibleGlobalHotkeys";
        parameters.Width = 0;
        parameters.Height = 0;
        parameters.WindowStyle = unchecked((int)0x80000000); // WS_POPUP
        parameters.ExtendedWindowStyle = unchecked((int)0x08000000) | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
            return IntPtr.Zero;

        HotkeyReceived?.Invoke(this, wParam.ToInt32());
        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
