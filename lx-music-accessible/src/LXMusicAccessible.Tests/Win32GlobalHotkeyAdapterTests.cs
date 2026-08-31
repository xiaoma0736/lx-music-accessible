using System.Windows.Input;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class Win32GlobalHotkeyAdapterTests
{
    [Fact]
    public void Failed_register_records_last_win32_error()
    {
        var adapter = new Win32GlobalHotkeyAdapter();

        var registered = adapter.Register(IntPtr.Zero, 1, Key.None, ModifierKeys.None);

        Assert.False(registered);
        Assert.NotEqual(0, adapter.LastRegisterError);
    }
}
