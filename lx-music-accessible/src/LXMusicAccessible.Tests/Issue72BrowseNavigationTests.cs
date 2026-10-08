using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>Issue #72: list-owned load-more, stable list focus, Backspace parent navigation, TTS-only output.</summary>
public sealed class Issue72BrowseNavigationTests
{
    [Fact]
    public void Search_list_load_more_marker_activates_only_on_enter_and_keeps_focus()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new PagedBrowseSource { SearchTotal = 4 };
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();

            var search = RequiredListBox(shell, "SearchResults");
            RequiredTextBox(shell).Text = "query";
            await InvokeAsync(shell, "SearchAsync", true);
            await WaitForList(search, 2);

            WinFormsUi.SelectOnly(search, search.Items.Count - 1);
            search.Focus();
            Assert.Equal(1, source.SearchCalls);
            Assert.Contains("加载更多", search.SelectedItem!.ToString(), StringComparison.Ordinal);

            Assert.Same(search, shell.ActiveControl);

            RaiseKeyDown(search, Keys.Enter);
            await WaitFor(() => source.SearchCalls == 2);
            Assert.Same(search, shell.ActiveControl);

            // #76 supersedes the former marker-selection behavior: after an explicit load, both
            // selection and the native reading cursor move to the newest song; reselect the marker
            // only to prove the next Enter still activates it.
            WinFormsUi.SelectOnly(search, search.Items.Count - 1);
            RaiseKeyDown(search, Keys.Enter);
            await WaitFor(() => source.SearchCalls == 3);
            await WaitForList(search, 4);
            Assert.Equal("song-3", WinFormsUi.Selected<MusicInfo>(search)!.Id);
        });
    }

    [Fact]
    public void Backspace_from_album_songs_restores_search_results_and_focus()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new PagedBrowseSource { SearchTotal = 1, AlbumSongTotal = 2 };
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();

            var search = RequiredListBox(shell, "SearchResults");
            RequiredTextBox(shell).Text = "query";
            await InvokeAsync(shell, "SearchAsync", true);
            await WaitForList(search, 1);

            var album = new MusicInfo { Id = "album-1", Title = "Album", Source = "netease", IsAlbum = true };
            search.Items.Add(new ObjectBox<MusicInfo>(album.AccessibleName, album));
            WinFormsUi.SelectOnly(search, 1);
            await InvokeAsync(shell, "ActivateSong", search, PlaybackQueueOrigin.Search);
            await WaitForList(search, 2);

            search.Focus();
            RaiseKeyDown(search, Keys.Back);
            await WaitForList(search, 1);
            await WaitFor(() => ContainsAnnouncement(shell, "已返回搜索结果"));
            Assert.Same(search, shell.ActiveControl);
            Assert.Equal("song-1", WinFormsUi.Selected<MusicInfo>(search)!.Id);
        });
    }

    [Fact]
    public void Category_playlist_load_more_marker_then_backspace_returns_to_playlists()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new PagedBrowseSource { CategoryPlaylistTotal = 2, PlaylistSongTotal = 1 };
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();

            shell.SelectTabForTests(2);
            shell.LoadCategoryPlaylistsForTests();
            await WaitFor(() => source.CategoryCalls > 0);
            var categories = RequiredListBox(shell, "CategoryList");
            await WaitForList(categories, 1);
            Select(categories, 0);

            shell.LoadCategoryPlaylistsForTests();
            var playlists = RequiredListBox(shell, "CategoryPlaylists");
            await WaitForList(playlists, 2);
            playlists.SelectedIndex = playlists.Items.Count - 1;
            playlists.Focus();
            Assert.Equal(1, source.CategoryPlaylistCalls);

            RaiseKeyDown(playlists, Keys.Enter);
            await WaitFor(() => source.CategoryPlaylistCalls == 2);
            Assert.Same(playlists, shell.ActiveControl);

            Select(playlists, 0);
            await shell.LoadCategorySongsForTests();
            var songs = RequiredListBox(shell, "CategorySongs");
            await WaitForList(songs, 1);
            songs.Focus();

            RaiseKeyDown(songs, Keys.Back);
            await WaitFor(() => ContainsAnnouncement(shell, "已返回歌单列表"));
            Assert.Same(playlists, shell.ActiveControl);
            // Going back is a focus move, and the reader names the list that just took focus: the mirror
            // line must stay silent so the command is not spoken a second time.
            Assert.Equal(AnnouncementSpeech.None, Announcer(shell).Announcements
                .Last(item => item.Text.Contains("已返回歌单列表", StringComparison.Ordinal)).Speech);
        });
    }

    [Fact]
    public void Backspace_from_ranking_songs_returns_to_rankings()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new PagedBrowseSource { RankingSongTotal = 1 };
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();

            shell.SelectTabForTests(3);
            var rankings = RequiredListBox(shell, "RankingList");
            await WaitForList(rankings, 1);
            Select(rankings, 0);

            await shell.LoadRankingSongsForTests();
            var songs = RequiredListBox(shell, "RankingSongs");
            await WaitForList(songs, 1);
            songs.Focus();

            RaiseKeyDown(songs, Keys.Back);
            await WaitFor(() => ContainsAnnouncement(shell, "已返回榜单列表"));
            Assert.Same(rankings, shell.ActiveControl);
            Assert.Equal("ranking-1", WinFormsUi.Selected<Ranking>(rankings)!.Id);
        });
    }

    [Fact]
    public void Backspace_from_saved_songs_returns_to_playlist_list_without_loading()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new PagedBrowseSource());
            var playlists = RequiredListBox(shell, "OnlinePlaylists");
            var songs = RequiredListBox(shell, "FavoriteSongs");
            playlists.Items.Add(new ObjectBox<Playlist>("Saved", new Playlist { Id = "saved", Name = "Saved" }));
            songs.Items.Add(new ObjectBox<MusicInfo>("Song", new MusicInfo { Id = "song", Source = "netease" }));
            shell.Show();
            Application.DoEvents();

            songs.SelectedIndex = 0;
            songs.Focus();
            RaiseKeyDown(songs, Keys.Back);

            Assert.Contains("已返回歌单列表", Announcer(shell).Announcements.Last().Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Backspace_in_search_text_does_not_replace_editing()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new PagedBrowseSource());
            var text = RequiredTextBox(shell);
            text.Text = "query";
            shell.Show();
            text.Focus();
            text.SelectionStart = text.Text.Length;
            Application.DoEvents();

            var method = typeof(Form).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance);
            var arguments = new object?[] { new Message(), Keys.Back };
            var handled = Assert.IsType<bool>(method!.Invoke(shell, arguments));
            Assert.False(handled);
        });
    }

    private static WinFormsShell CreateShell(PagedBrowseSource source) => new(new WinFormsShellServices
    {
        Announcer = AnnouncerHolder.Announcer,
        BrowseSource = source,
        Playback = new RecordingPlayback(),
        TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-issue72-" + Guid.NewGuid().ToString("N"))
    });

    private static RecordingAnnouncer Announcer(Form shell)
    {
        var field = typeof(WinFormsShell).GetField("_announcer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return Assert.IsType<RecordingAnnouncer>(field.GetValue(shell));
    }

    private static bool ContainsAnnouncement(Form shell, string text) =>
        Announcer(shell).Announcements.Any(item => item.Text.Contains(text, StringComparison.Ordinal));

    private static TabControl Tabs(Control shell) => Descendants<TabControl>(shell).Single();

    private static ListBox RequiredListBox(Control root, string name) =>
        Assert.IsType<ListBox>(Descendants<Control>(root).Single(control => control.Name == name));

    private static TextBox RequiredTextBox(Control root) => Descendants<TextBox>(root).Single();

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed) yield return typed;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }

    private static void Select(ListBox list, int index)
    {
        list.SelectedIndex = index;
        Application.DoEvents();
    }

    private static void RaiseKeyDown(Control control, Keys key)
    {
        var method = control.GetType().GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(control, new object[] { new KeyEventArgs(key) });
        Application.DoEvents();
    }

    private static async Task InvokeAsync(object target, string method, params object?[] arguments)
    {
        var found = target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(found);
        var result = found!.Invoke(target, arguments);
        if (result is Task task) await task;
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

    private static async Task WaitForList(ListBox list, int count) => await WaitFor(() => list.Items.Count >= count);

    private static class AnnouncerHolder
    {
        public static readonly RecordingAnnouncer Announcer = new();
    }

    public sealed class PagedBrowseSource : IMusicBrowseSource
    {
        public int SearchTotal { get; init; } = 1;
        public int AlbumSongTotal { get; init; } = 1;
        public int CategoryPlaylistTotal { get; init; } = 1;
        public int PlaylistSongTotal { get; init; } = 1;
        public int RankingSongTotal { get; init; } = 1;

        public int SearchCalls { get; private set; }
        public int AlbumCalls { get; private set; }
        public int CategoryCalls { get; private set; }
        public int CategoryPlaylistCalls { get; private set; }
        public int PlaylistSongCalls { get; private set; }
        public int RankingCalls { get; private set; }
        public int RankingSongCalls { get; private set; }

        public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page, int pageSize, string searchType, string sourceId, CancellationToken cancellationToken)
        {
            SearchCalls++;
            return Task.FromResult(SongPage(page, SearchTotal, "song"));
        }

        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken cancellationToken)
        {
            AlbumCalls++;
            return Task.FromResult(SongPage(page, AlbumSongTotal, "album-song"));
        }

        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken cancellationToken)
        {
            CategoryCalls++;
            return Task.FromResult(new List<PlaylistCategory>
            {
                new() { Id = sourceId, Name = "Category", Tags = new List<PlaylistTag> { new() { Id = "all", Name = "All" } } }
            });
        }

        public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken cancellationToken)
        {
            CategoryPlaylistCalls++;
            return Task.FromResult(new PagedResult<Playlist>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = CategoryPlaylistTotal,
                HasMore = page < CategoryPlaylistTotal,
                Items = new List<Playlist> { new() { Id = "playlist-" + page, Name = "Playlist " + page, Source = sourceId } }
            });
        }

        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page, int pageSize, CancellationToken cancellationToken)
        {
            PlaylistSongCalls++;
            return Task.FromResult(SongPage(page, PlaylistSongTotal, "playlist-song"));
        }

        public Task<List<Ranking>> GetRankingsAsync(string sourceId, CancellationToken cancellationToken)
        {
            RankingCalls++;
            return Task.FromResult(new List<Ranking> { new() { Id = "ranking-1", Name = "Ranking", Source = sourceId } });
        }

        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page, int pageSize, CancellationToken cancellationToken)
        {
            RankingSongCalls++;
            return Task.FromResult(SongPage(page, RankingSongTotal, "ranking-song"));
        }

        private static PagedResult<MusicInfo> SongPage(int page, int total, string prefix) => new()
        {
            CurrentPage = page,
            PageSize = 20,
            TotalItems = total,
            HasMore = page < total,
            Items = new List<MusicInfo> { new() { Id = prefix + "-" + page, Title = prefix + " " + page, Source = "netease" } }
        };
    }
}
