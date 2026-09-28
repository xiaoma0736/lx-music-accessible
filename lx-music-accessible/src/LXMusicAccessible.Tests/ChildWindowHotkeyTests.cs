using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>Child windows must preserve modifier keys when handing keys back to the shell dispatcher.</summary>
public sealed class ChildWindowHotkeyTests
{
    [Theory]
    [InlineData(Keys.Shift | Keys.F5)]
    [InlineData(Keys.Shift | Keys.F6)]
    [InlineData(Keys.Shift | Keys.F7)]
    [InlineData(Keys.Shift | Keys.F8)]
    public void Shell_child_hotkey_handler_preserves_shift(Keys keyData)
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var host = await TestApplicationHost.StartAsync();
            var settings = host.Services.GetRequiredService<SettingsWorkflow>();
            await settings.LoadAsync(IntPtr.Zero);
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = host.Announcer,
                BrowseSource = new FakeBrowseSource(),
                Playback = new RecordingPlayback(),
                Lyrics = host.Services.GetRequiredService<LyricsWorkflow>(),
                Settings = settings,
                Announcements = new AnnouncementToggleService(host.Announcer, settings),
                TemporaryDirectory = host.DataDirectory
            });

            var voiceBefore = settings.Current.Settings.EnableVoiceAnnouncement;
            var lyricAnnounceBefore = settings.Current.Settings.LyricAutoAnnounce;
            var offsetBefore = host.Services.GetRequiredService<LyricsWorkflow>().Current.OffsetMilliseconds;

            Assert.True(InvokeHandleHotkey(shell, keyData));

            if (keyData == (Keys.Shift | Keys.F5))
                Assert.NotEqual(voiceBefore, settings.Current.Settings.EnableVoiceAnnouncement);
            else if (keyData == (Keys.Shift | Keys.F6))
                Assert.NotEqual(lyricAnnounceBefore, settings.Current.Settings.LyricAutoAnnounce);
            else
            {
                var expected = keyData == (Keys.Shift | Keys.F7) ? offsetBefore + 500 : offsetBefore - 500;
                Assert.Equal(expected, host.Services.GetRequiredService<LyricsWorkflow>().Current.OffsetMilliseconds);
            }
        });
    }

    private static bool InvokeHandleHotkey(Control shell, Keys keyData)
    {
        var method = shell.GetType().GetMethod("HandleHotkey", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(shell, [new KeyEventArgs(keyData)]));
    }
}
