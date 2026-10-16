using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class WinFormsPlaylistPagingTests
{
    /// <summary>
    /// Every list pages with exactly one reachable command: a load-more button whose name says what it
    /// loads. The old first/previous/next page buttons and the page label were permanently hidden, which
    /// made them unreachable for keyboard and screen reader users, so they are gone for good.
    /// </summary>
    [Fact]
    public void Paging_is_one_reachable_command_per_list_with_a_specific_accessible_name()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new CountingBrowseSource());
            var expected = new Dictionary<string, string>
            {
                ["SearchLoadMore"] = "加载更多搜索歌曲",
                ["SavedPlaylistLoadMore"] = "加载更多收藏歌曲",
                ["CategoryPlaylistsLoadMore"] = "加载更多歌单",
                ["CategorySongsLoadMore"] = "加载更多歌单歌曲",
                ["RankingSongsLoadMore"] = "加载更多榜单歌曲"
            };
            foreach (var (name, accessibleName) in expected)
            {
                var controls = shell.Controls.Find(name, true);
                Assert.True(controls.Length == 1, name + " was not found");
                var button = Assert.IsType<Button>(controls[0]);
                Assert.Equal(accessibleName, button.AccessibleName);
                Assert.DoesNotContain("按钮", button.AccessibleName, StringComparison.Ordinal);
                Assert.False(button.TabStop, name + " would duplicate the list's own keyboard command in the tab chain.");
            }

            // Nothing in the shell is named after the removed paging widgets.
            Assert.Empty(shell.Controls.Find("CategoryPlaylistPreviousPage", true));
            Assert.Empty(shell.Controls.Find("CategoryPlaylistNextPage", true));
            Assert.Empty(shell.Controls.Find("CategoryPlaylistPage", true));
            WinFormsAccessibilityAudit.AssertAccessible(shell);
        });
    }

    [Fact]
    public void Selection_changes_are_local_and_explicit_actions_load_playlist_pages()
    {
        var source = new CountingBrowseSource();
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();
            AddCategory(shell);
            shell.SelectTabForTests(2);
            Application.DoEvents();
            Assert.Equal(0, source.CategoryCalls);
            RaiseEnter(shell, shell.CategoryListForTests);
            await WaitForAsync(() => source.CategoryCalls == 1);
            Assert.Equal(1, source.LastCategoryPage);
            Assert.True(shell.CategoryPlaylistsForTests.Focused, "The explicit Enter action loads the playlist list and moves focus onto it.");

            var loadMore = Assert.IsType<Button>(Assert.Single(shell.Controls.Find("CategoryPlaylistsLoadMore", true)));
            var playlists = shell.CategoryPlaylistsForTests;
            loadMore.Focus();
            Assert.True(loadMore.Focused);
            PerformClick(loadMore);
            await WaitForAsync(() => source.CategoryCalls == 2);
            Assert.Equal(2, source.LastCategoryPage);
            Assert.False(loadMore.Enabled, "The control is disabled once the list is complete.");
            Assert.True(playlists.Focused, "Focus stays inside the list that just finished loading.");

            playlists.SelectedIndex = 0;
            await shell.LoadCategorySongsForTests();
            Assert.Equal(1, source.SongCalls);
            Assert.Equal(1, source.LastSongPage);

            var songsLoadMore = Assert.IsType<Button>(Assert.Single(shell.Controls.Find("CategorySongsLoadMore", true)));
            PerformClick(songsLoadMore);
            await WaitForAsync(() => source.SongCalls == 2);
            Assert.Equal(2, source.LastSongPage);
        });
    }

    [Fact]
    public void Category_songs_append_when_load_more_is_used()
    {
        var source = new CountingBrowseSource();
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var shell = CreateShell(source);
            shell.Show();
            AddCategory(shell);
            shell.LoadCategoryPlaylistsForTests();
            await WaitForAsync(() => source.CategoryCalls == 1);
            var playlists = Assert.IsType<ListBox>(shell.Controls.Find("CategoryPlaylists", true)[0]);
            playlists.SelectedIndex = 0;
            await shell.LoadCategorySongsForTests();
            await shell.LoadMoreCategorySongsForTests();
            var songs = Assert.IsType<ListBox>(shell.Controls.Find("CategorySongs", true)[0]);
            Assert.Equal(2, songs.Items.Count);
            Assert.Equal(2, source.SongCalls);
            Assert.False(((Button)shell.Controls.Find("CategorySongsLoadMore", true)[0]).Enabled);
        });
    }

    [Fact]
    public void Category_songs_append_focuses_first_new_song()
    {
        var source = new CountingBrowseSource();
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();
            AddCategory(shell);
            shell.LoadCategoryPlaylistsForTests();
            await WaitForAsync(() => shell.CategoryPlaylistsForTests.Items.Count > 0);
            shell.CategoryPlaylistsForTests.SelectedIndex = 0;
            await shell.LoadCategorySongsForTests();
            var songs = shell.CategorySongsForTests;
            var previousCount = songs.Items.Cast<object>().OfType<ObjectBox<MusicInfo>>().Count();
            Assert.Equal(1, previousCount);
            songs.SelectedIndex = 0;

            await shell.LoadMoreCategorySongsForTests();

            Assert.Equal(2, songs.Items.Count);
            Assert.Equal(previousCount, songs.SelectedIndex);
            Assert.Equal(previousCount, ListCaretIndex(songs));
        });
    }
    [Fact]
    public void Network_error_sets_visible_status_and_announces_error()
    {
        var source = new CountingBrowseSource { ThrowCategoryPlaylists = true };
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var shell = CreateShell(source);
            shell.Show();
            AddCategory(shell);
            RaiseEnter(shell, shell.Controls.Find("CategoryList", true)[0]);
            await WaitForAsync(() => LastAnnouncer.Announcements.Any(a => a.Kind == AnnouncementKind.Error));
            var error = Assert.Single(LastAnnouncer.Announcements
                .Where(a => a.Kind == AnnouncementKind.Error)
                .Select(a => a.Text)
                .Distinct());
            Assert.Contains("网络", shell.StatusTextForTests.Text, StringComparison.Ordinal);
            Assert.Contains("网络", error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Rapid_category_changes_cancel_the_inflight_request()
    {
        var source = new CountingBrowseSource { DelayCategoryPlaylists = true };
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var shell = CreateShell(source);
            shell.Show();
            AddCategory(shell, "old");
            RaiseEnter(shell, shell.Controls.Find("CategoryList", true)[0]);
            await WaitForAsync(() => source.Started);

            AddCategory(shell, "new");
            RaiseEnter(shell, shell.Controls.Find("CategoryList", true)[0]);
            await WaitForAsync(() => source.Cancelled);
            Assert.Equal(2, source.CategoryCalls);
        });
    }

    private static RecordingAnnouncer LastAnnouncer { get; set; } = new();

    private static WinFormsShell CreateShell(CountingBrowseSource source)
    {
        LastAnnouncer = new RecordingAnnouncer();
        return new WinFormsShell(new WinFormsShellServices
        {
            Announcer = LastAnnouncer,
        BrowseSource = source,
            TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-playlist-paging-" + Guid.NewGuid().ToString("N"))
        });
    }


    private static void AddCategory(Form shell, string id = "tag")
    {
        var list = Assert.IsType<ListBox>(shell.Controls.Find("CategoryList", true)[0]);
        list.Items.Add(new ObjectBox<PlaylistCategory>(id, new PlaylistCategory
        {
            Id = id,
            Name = id,
            Tags = new List<PlaylistTag> { new() { Id = id, Name = id } }
        }));
        list.SelectedIndex = list.Items.Count - 1;
    }

    private static void RaiseEnter(Form shell, Control control)
    {
        control.Focus();
        var method = shell.GetType().GetMethod("LoadCategoryPlaylistsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(shell, new object?[] { 1, false });
    }

    private static void PerformClick(Control control) =>
        ((Button)control).PerformClick();

    private static int ListCaretIndex(ListBox list) =>
        SendMessage(list.Handle, 0x019F, IntPtr.Zero, IntPtr.Zero).ToInt32();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "The awaited shell condition was not reached.");
    }
}
