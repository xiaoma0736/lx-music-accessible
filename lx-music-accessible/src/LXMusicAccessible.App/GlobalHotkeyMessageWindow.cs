using System;
using System.Windows.Forms;

namespace LXMusicAccessible.App;

/// <summary>Hidden native window that receives global RegisterHotkey messages on the UI thread.</summary>
public sealed class GlobalHotkeyMessageWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private bool _disposed;

    public event EventHandler<int>? HotkeyReceived;

    public GlobalHotkeyMessageWindow() => CreateHandle(new CreateParams { Caption = "LXMusicAccessible.GlobalHotkeys" });

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WM_HOTKEY)
        {
            HotkeyReceived?.Invoke(this, unchecked((int)message.WParam));
            return;
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyHandle();
    }
}
