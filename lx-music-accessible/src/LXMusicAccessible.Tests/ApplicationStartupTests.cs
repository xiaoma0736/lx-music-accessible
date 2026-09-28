using System;
using System.Linq;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ApplicationStartupTests
{
    [Fact]
    public void Application_entry_point_is_marked_STA()
    {
        // WebView2 cannot create its environment when the real process entry point starts as MTA.
        // An async Main can carry [STAThread] on the source method, but the compiler-generated
        // entry point does not, so the executable's actual entry point must be checked here.
        var entryPoint = typeof(App.WinFormsShell).Assembly.EntryPoint;

        Assert.NotNull(entryPoint);
        Assert.True(
            entryPoint!.GetCustomAttributes(typeof(STAThreadAttribute), inherit: false).Any(),
            "The executable entry point must have STAThreadAttribute.");
    }
}
