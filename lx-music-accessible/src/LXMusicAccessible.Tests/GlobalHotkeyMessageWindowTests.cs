using System;
using System.Threading;
using System.Windows.Threading;
using LXMusicAccessible.App;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class GlobalHotkeyMessageWindowTests
{
    [Fact]
    public void Hidden_message_window_receives_global_hotkey_ids_and_disposes_idempotently()
    {
        var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new GlobalHotkeyMessageWindow();
                Assert.NotEqual(IntPtr.Zero, window.Handle);

                int? receivedId = null;
                var frame = new DispatcherFrame();
                window.HotkeyReceived += (_, id) =>
                {
                    receivedId = id;
                    frame.Continue = false;
                };

                Assert.True(PostMessage(window.Handle, 0x0312, new IntPtr(47), IntPtr.Zero));
                Dispatcher.PushFrame(frame);

                Assert.Equal(47, receivedId);

                window.Dispose();
                window.Dispose();
            }
            catch (Exception exception)
            {
                TestFailure = exception;
            }
            finally
            {
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        thread.Join(TimeSpan.FromSeconds(1));

        if (TestFailure is not null) throw TestFailure;
    }

    private static Exception? TestFailure { get; set; }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
