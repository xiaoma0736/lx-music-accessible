using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class DesktopLifecycleAnnouncementTests
{
    [Fact]
    public void Hide_and_restore_use_short_announcements()
    {
        WinFormsTestRunner.Run(() =>
        {
            var announcer = new RecordingAnnouncer();
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = announcer,
                BrowseSource = new FakeBrowseSource(),
                Playback = new RecordingPlayback(),
                TemporaryDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
            });
            shell.Show();
            Application.DoEvents();
            using var desktop = new WinFormsDesktopIntegration(announcer);
            using var lifecycle = new DesktopLifecycleWorkflow(desktop, new ImmediateShutdown(), new RecordingPlayback(), announcer);
            lifecycle.Attach(shell);

            var hidden = lifecycle.ExecuteAsync(DesktopCommand.Escape).GetAwaiter().GetResult();
            var restored = lifecycle.ExecuteAsync(DesktopCommand.Restore).GetAwaiter().GetResult();

            Assert.Equal(DesktopLifecycleState.Hidden, hidden.State);
            Assert.Equal(DesktopLifecycleState.Visible, restored.State);
            Assert.Collection(
                announcer.Announcements.Where(item => item.ActivityId == "desktop-lifecycle").ToList(),
                item => Assert.Equal("\u5df2\u9690\u85cf", item.Text),
                item => Assert.Equal("\u5df2\u663e\u793a", item.Text));
        });
    }

    private sealed class ImmediateShutdown : IApplicationShutdown
    {
        public Task<ShutdownResult> RequestAsync() =>
            Task.FromResult(new ShutdownResult(ShutdownStatus.Completed, "completed"));
    }
}
