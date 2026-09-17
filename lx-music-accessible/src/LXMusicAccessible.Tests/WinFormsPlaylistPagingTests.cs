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
    [Fact]
    public void Category_paging_controls_have_accessible_names_and_names_do_not_repeat_roles()
    {
        RunSta(() =>
        {
            using var shell = CreateShell(new CountingBrowseSource());
            foreach (var name in new[] { "CategoryPlaylistPreviousPage", "CategoryPlaylistNextPage", "CategoryPlaylistPage", "CategorySongsLoadMore" })
            {
                var controls = shell.Controls.Find(name, true);
                Assert.True(controls.Length == 1, name + " was not found");
                Assert.Equal(name, controls[0].AccessibleName);
                if (controls[0] is Button button) Assert.False(button.TabStop);
            }
            WinFormsAccessibilityAudit.AssertAccessible(shell);
        });
    }

    [Fact]
    public async Task Selection_changes_are_local_and_explicit_actions_load_playlist_pages()
    {
        var source = new CountingBrowseSource();
        await Task.Yield();
        RunSta(async () =>
        {
            await Task.Yield();
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();
            AddCategory(shell);
            Assert.Equal(0, source.CategoryCalls);
            shell.Controls.Find("CategoryList", true)[0].Focus();
            RaiseEnter(shell, shell.Controls.Find("CategoryList", true)[0]);
            await WaitForAsync(() => source.CategoryCalls == 1);
            Assert.Equal(1, source.LastCategoryPage);
            Assert.True(shell.Controls.Find("CategoryList", true)[0].Focused, "Category list should receive explicit-action focus.");

            shell.Controls.Find("CategoryPlaylistNextPage", true)[0].Focus();
            PerformClick(shell.Controls.Find("CategoryPlaylistNextPage", true)[0]);
            await WaitForAsync(() => source.CategoryCalls == 2);
            Assert.Equal(2, source.LastCategoryPage);
            Assert.True(shell.Controls.Find("CategoryPlaylists", true)[0].Focused, "Playlist list should receive explicit-action focus.");

            var playlists = Assert.IsType<ListBox>(shell.Controls.Find("CategoryPlaylists", true)[0]);
            playlists.Items.Add(new ObjectBox<Playlist>("Playlist", new Playlist { Id = "remote", Name = "Playlist" }));
            playlists.SelectedIndex = 0;
            PerformClick(shell.Controls.Find("CategorySongNextPage", true)[0]);
            await WaitForAsync(() => source.SongCalls == 1);
            Assert.Equal(2, source.LastSongPage);
        });
    }

    [Fact]
    public async Task Category_songs_append_when_load_more_is_used()
    {
        var source = new CountingBrowseSource();
        await Task.Yield();
        RunSta(async () =>
        {
            await Task.Yield();
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
    public async Task Network_error_sets_visible_status_and_announces_error()
    {
        var source = new CountingBrowseSource { ThrowCategories = true };
        await Task.Yield();
        RunSta(async () =>
        {
            await Task.Yield();
            using var shell = CreateShell(source);
            shell.Show();
            AddCategory(shell);
            RaiseEnter(shell, shell.Controls.Find("CategoryList", true)[0]);
            await WaitForAsync(() => LastAnnouncer.Announcements.Any(a => a.Kind == AnnouncementKind.Error));
            Assert.Contains("网络", shell.StatusTextForTests.Text, StringComparison.Ordinal);
            Assert.Contains("网络", Assert.Single(LastAnnouncer.Announcements, a => a.Kind == AnnouncementKind.Error).Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Rapid_category_changes_cancel_the_inflight_request()
    {
        var source = new CountingBrowseSource { DelayCategories = true };
        await Task.Yield();
        RunSta(async () =>
        {
            await Task.Yield();
            using var shell = CreateShell(source);
            shell.Show();
            AddCategory(shell, "old");
            RaiseEnter(shell, shell.Controls.Find("CategoryList", true)[0]);
            await WaitForAsync(() => source.Started);

            AddCategory(shell, "new");
            RaiseEnter(shell, shell.Controls.Find("CategoryList", true)[0]);
            await WaitForAsync(() => source.Cancelled);
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

    private static void RunSta(Action test) => WinFormsTestRunner.Run(test);

    private static void RunSta(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(async () =>
        {
            try { await test(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
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
        list.SelectedIndex = 0;
    }

    private static void RaiseEnter(Form shell, Control control)
    {
        var method = shell.GetType().GetMethod("LoadCategoryPlaylists", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(shell, Array.Empty<object?>());
    }

    private static void PerformClick(Control control) =>
        ((Button)control).PerformClick();

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
