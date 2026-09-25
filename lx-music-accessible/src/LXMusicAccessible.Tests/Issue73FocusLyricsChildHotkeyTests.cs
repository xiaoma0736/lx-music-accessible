#pragma warning disable xUnit1031
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>Issue #73: non-stealing tabs, quiet routine speech, accessible lyrics viewer and child hotkeys.</summary>
public sealed class Issue73FocusLyricsChildHotkeyTests
{
    [Fact]
    public void Selecting_playlist_or_ranking_tabs_keeps_focus_on_tab_header()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = new RecordingAnnouncer(),
                BrowseSource = new FakeBrowseSource(),
                Playback = new RecordingPlayback(),
                TemporaryDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
            });
            shell.Show();
            Application.DoEvents();

            InvokeSelectTab(shell, 1);
            Assert.Same(Tabs(shell), shell.ActiveControl);
            InvokeSelectTab(shell, 2);
            Assert.Same(Tabs(shell), shell.ActiveControl);
            InvokeSelectTab(shell, 3);
            Application.DoEvents();
            Assert.Same(Tabs(shell), shell.ActiveControl);
        });
    }

    [Theory]
    [InlineData("welcome", AnnouncementKind.Status, false)]
    [InlineData("music-browse:1", AnnouncementKind.Busy, false)]
    [InlineData("music-browse:2", AnnouncementKind.Success, false)]
    [InlineData("search-results", AnnouncementKind.Status, false)]
    [InlineData("saved-playlists", AnnouncementKind.Success, false)]
    [InlineData("music-browse:3", AnnouncementKind.Error, true)]
    [InlineData("playback-song", AnnouncementKind.Success, true)]
    public void Routine_browse_speech_is_quiet_while_errors_and_playback_remain(
        string activityId,
        AnnouncementKind kind,
        bool expected)
    {
        Assert.Equal(expected, AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("状态", kind, activityId)));
    }

    [Fact]
    public void Lyrics_viewer_uses_visible_accessible_text_and_supports_copy_hotkey()
    {
        WinFormsTestRunner.Run(() =>
        {
            var announcer = new RecordingAnnouncer();
            using var form = new LyricsForm(announcer, null!, _ => false);
            var text = form.Controls.OfType<RichTextBox>().Single();
            text.Text = "第一行" + Environment.NewLine + "第二行";
            var expectedText = text.Text;
            form.Show();
            Application.DoEvents();

            Assert.Equal("歌词", text.AccessibleName);
            Assert.NotEqual(AccessibleRole.None, text.AccessibilityObject.Role);
            Assert.False((text.AccessibilityObject.State & AccessibleStates.Invisible) != 0);
            Assert.True(text.ReadOnly);

            InvokeKeyDown(form, Keys.Control | Keys.Shift | Keys.C);
            Assert.Contains("复制", announcer.Announcements.Last().Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Shift_enter_in_song_list_opens_selected_lyrics_without_playing()
    {
        using var host = await TestApplicationHost.StartAsync();
        var playback = new RecordingPlayback();
        var lyrics = host.Services.GetRequiredService<LyricsWorkflow>();
        var factory = CreateFactory(host, lyrics, _ => false);
        var shell = new WinFormsShell(new WinFormsShellServices
        {
            Announcer = host.Announcer,
            BrowseSource = new FakeBrowseSource(),
            Playback = playback,
            Lyrics = lyrics,
            TemporaryDirectory = host.DataDirectory,
            Windows = factory
        });
        shell.Show();
        Application.DoEvents();

        var search = shell.Controls.Find("SearchResults", true).OfType<ListBox>().Single();
        var song = new MusicInfo { Id = "song-1", Title = "Song", Artist = "Artist", Source = "netease" };
        search.Items.Add(new ObjectBox<MusicInfo>("Artist，Song", song));
        search.SelectedIndex = 0;

        RaiseKeyDown(search, Keys.Shift | Keys.Enter);
        await WaitFor(() => shell.OwnedForms.OfType<LyricsForm>().Any());
        var viewer = shell.OwnedForms.OfType<LyricsForm>().Single();
        var text = viewer.Controls.OfType<RichTextBox>().Single();

        Assert.Equal("歌词", text.AccessibleName);
        Assert.Empty(playback.Requests);

        viewer.Close();
        shell.Close();
    }

    [Fact]
    public async Task Child_windows_route_local_playback_hotkeys()
    {
        using var host = await TestApplicationHost.StartAsync();
        await host.Services.GetRequiredService<SettingsWorkflow>().LoadAsync(IntPtr.Zero);
        var owner = new Form { ShowInTaskbar = false };
        owner.Show();
        var forms = new List<Form>();
        var handled = 0;
        Func<KeyEventArgs, bool> handler = _ =>
        {
            handled++;
            return true;
        };
        var factory = CreateFactory(host, host.Services.GetRequiredService<LyricsWorkflow>(), handler);

        try
        {
            Form[] children =
            [
                factory.CreateLyrics(handler),
                factory.CreateSourceManager(owner, handler),
                factory.CreateDownloadManager(owner, handler),
                factory.CreateSettings(owner, handler, _ => { }),
                factory.CreatePlaylistImport(owner, handler, _ => { }, _ => { }),
                factory.CreateManualSource(owner, handler, host.Services.GetRequiredService<IManualSourceDiscoveryWorkflow>(), Song())
            ];
            forms.AddRange(children);

            foreach (var form in children)
            {
                handled = 0;
                InvokeKeyDown(form, Keys.F5);
                Assert.Equal(1, handled);
            }
        }
        finally
        {
            foreach (var form in Enumerable.Reverse(forms)) form.Dispose();
            owner.Dispose();
        }
    }

    private static ApplicationWindowFactory CreateFactory(
        TestApplicationHost host,
        LyricsWorkflow lyrics,
        Func<KeyEventArgs, bool> handler) =>
        new(
            host.Announcer,
            lyrics,
            host.Services.GetRequiredService<IMusicSourceService>(),
            host.Services.GetRequiredService<SettingsWorkflow>(),
            host.Services.GetRequiredService<ITtsService>(),
            host.Services.GetRequiredService<IDownloadWorkflow>(),
            host.Services.GetRequiredService<IPlaylistImportService>(),
            new NoPlaylistLoginFactory(),
            host.Services.GetRequiredService<IManualSourceDiscoveryWorkflow>());

    private static TabControl Tabs(Control shell) => Descendants<TabControl>(shell).Single();

    private static void InvokeSelectTab(Control shell, int index)
    {
        var method = shell.GetType().GetMethod("SelectTab", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(int)], null);
        Assert.NotNull(method);
        method!.Invoke(shell, [index]);
        Application.DoEvents();
    }

    private static void InvokeKeyDown(Control control, Keys key)
    {
        var method = control.GetType().GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(control, [new KeyEventArgs(key)]);
        Application.DoEvents();
    }

    private static void RaiseKeyDown(Control control, Keys key) => InvokeKeyDown(control, key);

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Application.DoEvents();
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "The awaited UI condition was not reached.");
    }

    private static MusicInfo Song() => new() { Id = "song-1", Title = "Song", Artist = "Artist", Source = "netease" };
}

internal sealed class NoPlaylistLoginFactory : IPlaylistLoginWindowFactory
{
    public PlaylistLoginMethod? SelectLoginMethod(string platform, Form? owner) => null;
    public PlaylistLoginDialogResult ShowWebLogin(string platform, Form? owner) => new(false);
    public bool ShowKugouQrLogin(Form? owner) => false;
}
