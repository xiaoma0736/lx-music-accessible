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

    /// <summary>
    /// ADR 0002 allows one instance per tool. A second lyrics window subscribes to the same lyric state twice,
    /// so the same line would be read twice and Alt+Tab would offer two identical windows; asking for the
    /// viewer again must bring the existing window forward and load the newly requested song into it.
    /// </summary>
    [Fact]
    public void Opening_the_lyrics_viewer_twice_reuses_one_window() =>
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var host = await TestApplicationHost.StartAsync();
            var lyrics = host.Services.GetRequiredService<LyricsWorkflow>();
            var factory = CreateFactory(host, lyrics, _ => false);
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = host.Announcer,
                BrowseSource = new FakeBrowseSource(),
                Playback = new RecordingPlayback(),
                Lyrics = lyrics,
                TemporaryDirectory = host.DataDirectory,
                Windows = factory
            });
            shell.Show();
            Application.DoEvents();

            var search = shell.Controls.Find("SearchResults", true).OfType<ListBox>().Single();
            search.Items.Add(new ObjectBox<MusicInfo>("Artist，Song", Song()));
            search.Items.Add(new ObjectBox<MusicInfo>("Artist，Other", new MusicInfo { Id = "song-2", Title = "Other", Artist = "Artist", Source = "netease" }));

            search.SelectedIndex = 0;
            RaiseKeyDown(search, Keys.Shift | Keys.Enter);
            var first = shell.OwnedForms.OfType<LyricsForm>().Single();

            search.SelectedIndex = 1;
            RaiseKeyDown(search, Keys.Shift | Keys.Enter);

            Assert.Same(first, Assert.Single(shell.OwnedForms.OfType<LyricsForm>()));
            foreach (var form in shell.OwnedForms) form.Close();
            shell.Close();
        });

    /// <summary>
    /// The same single-instance rule protects the download manager: two windows would both listen to the
    /// task collection and announce every task result twice.
    /// </summary>
    [Fact]
    public void Opening_a_tool_window_twice_reuses_one_window() =>
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var host = await TestApplicationHost.StartAsync();
            var lyrics = host.Services.GetRequiredService<LyricsWorkflow>();
            var factory = CreateFactory(host, lyrics, _ => false);
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = host.Announcer,
                BrowseSource = new FakeBrowseSource(),
                Playback = new RecordingPlayback(),
                Downloads = host.Services.GetRequiredService<IDownloadWorkflow>(),
                TemporaryDirectory = host.DataDirectory,
                Windows = factory
            });
            shell.Show();
            Application.DoEvents();

            var openTool = typeof(WinFormsShell)
                .GetMethod("OpenTool", BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(typeof(DownloadManagerForm));
            Func<DownloadManagerForm?> create = () => factory.CreateDownloadManager(shell, _ => false);
            openTool.Invoke(shell, new object[] { create });
            openTool.Invoke(shell, new object[] { create });

            var windows = shell.OwnedForms.OfType<DownloadManagerForm>().ToArray();
            Assert.Single(windows);
            foreach (var form in shell.OwnedForms) form.Close();
            shell.Close();
        });

    /// <summary>
    /// The source switcher is the last tool window that still allowed copies. Two windows titled 切换音源 are
    /// indistinguishable in Alt+Tab and in a screen reader's window list, so the second command brings the
    /// existing window forward and points it at the requested song instead of opening another one.
    /// </summary>
    [Fact]
    public void Opening_the_source_switcher_twice_reuses_one_window() =>
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var host = await TestApplicationHost.StartAsync();
            var discovery = new CountingManualWorkflow();
            var factory = CreateFactory(host, host.Services.GetRequiredService<LyricsWorkflow>(), _ => false);
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = host.Announcer,
                BrowseSource = new FakeBrowseSource(),
                Playback = new RecordingPlayback(),
                ManualSources = discovery,
                TemporaryDirectory = host.DataDirectory,
                Windows = factory
            });
            shell.Show();
            Application.DoEvents();

            var open = typeof(WinFormsShell)
                .GetMethod("OpenManualSource", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var song = new object?[] { Song() };
            open.Invoke(shell, song);
            Application.DoEvents();
            open.Invoke(shell, song);
            Application.DoEvents();

            var window = Assert.Single(shell.OwnedForms.OfType<ManualSourceSwitchForm>());
            Assert.NotNull(window);
            Assert.Equal(2, discovery.DiscoverCalls);
            foreach (var form in shell.OwnedForms) form.Close();
            shell.Close();
        });

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
                factory.CreateSettings(owner, handler),
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

/// <summary>Counts the searches a reused source-switch window runs, without touching the network.</summary>
internal sealed class CountingManualWorkflow : IManualSourceDiscoveryWorkflow
{
    public int DiscoverCalls { get; private set; }
    public Task<ManualSourceDiscoveryResult> DiscoverAsync(
        MusicInfo target,
        CancellationToken cancellationToken = default)
    {
        DiscoverCalls++;
        return Task.FromResult(ManualSourceDiscoveryResult.Failed("not used"));
    }
    public Task<PlaybackResult> PlayAsync(
        ManualSourcePlaybackRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Dispose() { }
}

internal sealed class NoPlaylistLoginFactory : IPlaylistLoginWindowFactory
{
    public PlaylistLoginMethod? SelectLoginMethod(string platform, Form? owner) => null;
    public PlaylistLoginDialogResult ShowWebLogin(string platform, Form? owner) => new(false);
    public bool ShowKugouQrLogin(Form? owner) => false;
}
