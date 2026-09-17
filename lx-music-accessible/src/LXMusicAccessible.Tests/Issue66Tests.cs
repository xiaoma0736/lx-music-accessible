using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>
/// Regression coverage for issue #66: every paged playlist and song list offers an explicit load-more
/// action that appends in order, ignores repeat requests while one is in flight, preserves focus and the
/// selected row, disables itself at the end of the list and never speaks loaded item counts.
/// </summary>
public sealed class Issue66Tests
{
    /// <summary>已加载条目数（如“3 项”、“2 首”、“5 个歌单”、“4 条”）不得进入浏览页的播报。</summary>
    private static readonly Regex ForbiddenItemCount = new(@"\d+\s*(项|首|个|条)", RegexOptions.Compiled);

    private sealed class NoPlayback : IPlaybackCoordinator
    {
        public bool IsPlaying => false;

        public bool IsPaused => false;

        public MusicInfo? CurrentMusic => null;

        public PlayMode Mode { get; set; }

        public string DefaultQuality { get; set; } = "128k";

        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted
        {
            add { }
            remove { }
        }

        public Task<PlaybackResult> PlayAsync(PlaybackRequest request) =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, request.Selection, request.Origin, "playing"));

        public Task<PlaybackResult> ReplayAsync() => Task.FromResult(NoChange());

        public void Pause()
        {
        }

        public void Stop()
        {
        }

        public Task<PlaybackResult> NextAsync() => Task.FromResult(NoChange());

        public Task<PlaybackResult> PreviousAsync() => Task.FromResult(NoChange());

        public Task ResumeAsync() => Task.CompletedTask;

        private static PlaybackResult NoChange() =>
            new(PlaybackResultStatus.NoChange, null, PlaybackQueueOrigin.Static, string.Empty);
    }

    /// <summary>
    /// Deterministic paged source: one item per page with a configurable total. A page can be blocked so a
    /// test can observe an in-flight load-more request and prove that a repeat request is ignored.
    /// </summary>
    private sealed class Issue66BrowseSource : IMusicBrowseSource
    {
        private readonly TaskCompletionSource<object?> _categoryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _playlistDetailStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<object?>? _categoryRelease;
        private TaskCompletionSource<object?>? _playlistDetailRelease;

        public int BlockCategoryPlaylistPage { get; init; }

        public int BlockPlaylistDetailPage { get; init; }

        public int FailPlaylistDetailPage { get; init; }

        public int CategoryPlaylistTotal { get; init; } = 2;

        public int PlaylistSongTotal { get; init; } = 2;

        public int AlbumSongTotal { get; init; } = 1;

        public int CategoryPlaylistCalls { get; private set; }

        public int PlaylistDetailCalls { get; private set; }

        public bool StartedCategoryPlaylists => _categoryStarted.Task.IsCompleted;

        public bool StartedPlaylistDetail => _playlistDetailStarted.Task.IsCompleted;

        public async Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken cancellationToken)
        {
            CategoryPlaylistCalls++;
            if (BlockCategoryPlaylistPage == page)
            {
                _categoryRelease ??= new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _categoryStarted.TrySetResult(null);
                await _categoryRelease.Task.WaitAsync(cancellationToken);
            }
            return Page(
                new Playlist { Id = "playlist-" + page, Name = "Playlist " + page, Source = sourceId },
                page,
                CategoryPlaylistTotal);
        }

        public void ReleaseCategoryPlaylists() => _categoryRelease?.TrySetResult(null);

        public async Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page, int pageSize, CancellationToken cancellationToken)
        {
            PlaylistDetailCalls++;
            if (FailPlaylistDetailPage == page) throw new HttpRequestException("fixture network");
            if (BlockPlaylistDetailPage == page)
            {
                _playlistDetailRelease ??= new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _playlistDetailStarted.TrySetResult(null);
                await _playlistDetailRelease.Task.WaitAsync(cancellationToken);
            }
            return SongPage(page, PlaylistSongTotal);
        }

        public void ReleasePlaylistDetail() => _playlistDetailRelease?.TrySetResult(null);

        public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page, int pageSize, string searchType, string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(SongPage(page, 2));

        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(SongPage(page, AlbumSongTotal));

        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(new List<PlaylistCategory> { Category() });

        public Task<List<Ranking>> GetRankingsAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(new List<Ranking> { new() { Id = "ranking-1", Name = "Ranking", Source = sourceId } });

        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(SongPage(page, 2));

        private static PagedResult<Playlist> Page(Playlist item, int page, int total) => new()
        {
            CurrentPage = page,
            PageSize = 20,
            TotalItems = total,
            HasMore = page < total,
            Items = new List<Playlist> { item }
        };

        private static PagedResult<MusicInfo> SongPage(int page, int total) => new()
        {
            CurrentPage = page,
            PageSize = 20,
            TotalItems = total,
            HasMore = page < total,
            Items = new List<MusicInfo> { new() { Id = "song-" + page, Title = "Song " + page, Source = "netease" } }
        };
    }

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<UserSettings?>(null);

        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingHotkeyAdapter : IGlobalHotkeyAdapter
    {
        public List<(string actionId, PlatformKey key, PlatformModifiers modifiers)> Registered { get; } = new();

        public int LastRegisterError { get; private set; }

        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers)
        {
            if (windowHandle == IntPtr.Zero)
            {
                LastRegisterError = 1400;
                return false;
            }
            Registered.Add(("id-" + id, key, modifiers));
            return true;
        }

        public bool Unregister(IntPtr windowHandle, int id) => true;
    }

    /// <summary>Isolated SQLite database for the saved-playlist paging tests.</summary>
    private sealed class TemporaryDatabase : IDisposable
    {
        private const string Prefix = "LXMusicAccessible.Issue66Tests-";

        private TemporaryDatabase(string path)
        {
            Path = path;
            Data = new DataService(NullLogger<DataService>.Instance, path);
        }

        public string Path { get; }

        public IDataService Data { get; }

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var database = new TemporaryDatabase(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
            var initialized = await new DatabaseInitializer(NullLogger<DatabaseInitializer>.Instance, database.Path).InitializeAsync();
            Assert.True(initialized.Succeeded, initialized.Message);
            return database;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            var fullPath = System.IO.Path.GetFullPath(Path);
            if (!System.IO.Path.GetFileName(fullPath).StartsWith(Prefix, StringComparison.Ordinal)) return;
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        }
    }
    [Fact]
    public async Task Category_playlist_load_more_appends_pages_in_workflow_order()
    {
        var workflow = CreateWorkflow(new Issue66BrowseSource());
        workflow.SelectCategory(Category());
        Assert.True((await workflow.ActivateCategoryAsync()).Succeeded);
        Assert.True((await workflow.ActivateCategoryAsync(2, append: true)).Succeeded);
        Assert.Equal(new[] { "playlist-1", "playlist-2" }, workflow.CurrentCategoryPage!.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Playlist_load_more_appends_songs_in_workflow_order()
    {
        var workflow = CreateWorkflow(new Issue66BrowseSource());
        workflow.SelectPlaylist(new Playlist { Id = "playlist-1", Name = "Playlist" });
        Assert.True((await workflow.ActivatePlaylistAsync()).Succeeded);
        Assert.True((await workflow.ActivatePlaylistAsync(2, append: true)).Succeeded);
        Assert.Equal(new[] { "song-1", "song-2" }, workflow.CurrentPlaylistPage!.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Ranking_load_more_appends_songs_in_workflow_order()
    {
        var workflow = CreateWorkflow(new Issue66BrowseSource());
        workflow.SelectRanking(new Ranking { Id = "ranking-1", Name = "Ranking", Source = "netease" });
        Assert.True((await workflow.ActivateRankingAsync()).Succeeded);
        Assert.True((await workflow.ActivateRankingAsync(2, append: true)).Succeeded);
        Assert.Equal(new[] { "song-1", "song-2" }, workflow.CurrentRankingPage!.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Search_load_more_appends_results_in_workflow_order()
    {
        var workflow = CreateWorkflow(new Issue66BrowseSource());
        workflow.SelectSearch("song", "song", "netease");
        Assert.True((await workflow.ActivateSearchAsync(reset: true)).Succeeded);
        Assert.True((await workflow.ActivateSearchAsync(reset: false)).Succeeded);
        Assert.Equal(new[] { "song-1", "song-2" }, workflow.CurrentSearch.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Album_load_more_appends_songs_in_workflow_order()
    {
        var workflow = CreateWorkflow(new Issue66BrowseSource { AlbumSongTotal = 2 });
        var album = new MusicInfo { Id = "album-1", Title = "Album", Source = "netease", IsAlbum = true };
        Assert.True((await workflow.ActivateAlbumAsync(album)).Succeeded);
        Assert.True((await workflow.ActivateAlbumAsync(album, 2, append: true)).Succeeded);
        Assert.Equal(new[] { "song-1", "song-2" }, workflow.CurrentAlbumPage!.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Successful_browse_results_do_not_announce_loaded_item_counts()
    {
        var announcer = new RecordingAnnouncer();
        var workflow = CreateWorkflow(new Issue66BrowseSource { AlbumSongTotal = 2 }, announcer);
        workflow.SelectCategory(Category());
        await workflow.ActivateCategoryAsync();
        workflow.SelectPlaylist(new Playlist { Id = "playlist-1", Name = "Playlist" });
        await workflow.ActivatePlaylistAsync();
        workflow.SelectRanking(new Ranking { Id = "ranking-1", Name = "Ranking", Source = "netease" });
        await workflow.ActivateRankingAsync();
        await workflow.ActivateAlbumAsync(new MusicInfo { Id = "album-1", Title = "Album", Source = "netease", IsAlbum = true });

        var success = announcer.Announcements
            .Where(item => item.Kind == AnnouncementKind.Success)
            .Select(item => item.Text)
            .ToArray();
        Assert.NotEmpty(success);
        Assert.All(success, message =>
        {
            Assert.DoesNotContain("已加载", message, StringComparison.Ordinal);
            Assert.DoesNotContain("加载了", message, StringComparison.Ordinal);
            Assert.False(Regex.IsMatch(message, "\\d"), message);
            Assert.False(ForbiddenItemCount.IsMatch(message), message);
        });
    }
    [Fact]
    public void Every_paged_list_has_a_named_load_more_control()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new Issue66BrowseSource());
            foreach (var name in new[] { "SearchLoadMore", "CategoryPlaylistsLoadMore", "SavedPlaylistLoadMore", "CategorySongsLoadMore", "RankingSongsLoadMore" })
            {
                var matches = shell.Controls.Find(name, true);
                Assert.True(matches.Length == 1, name + " matches=" + matches.Length + "; all=" + string.Join(",", AllNames(shell)));
                var button = Assert.IsType<Button>(matches.Single());
                Assert.False(button.TabStop);
                Assert.False(string.IsNullOrWhiteSpace(button.AccessibleName));
                Assert.Contains("加载更多", button.AccessibleName, StringComparison.Ordinal);
                Assert.DoesNotContain("按钮", button.AccessibleName, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void Concurrent_category_playlist_load_more_requests_are_guarded()
    {
        var source = new Issue66BrowseSource { BlockCategoryPlaylistPage = 2 };
        StaAsync(async () =>
        {
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();
            AddCategory(shell);
            shell.LoadCategoryPlaylistsForTests();
            await WaitFor(() => source.CategoryPlaylistCalls == 1);

            var loadMore = Assert.IsType<Button>(Assert.Single(shell.Controls.Find("CategoryPlaylistsLoadMore", true)));
            Assert.True(loadMore.Enabled, "Load-more stays available while another page exists.");

            _ = shell.LoadMoreCategoryPlaylistsForTests();
            await WaitFor(() => source.StartedCategoryPlaylists);
            _ = shell.LoadMoreCategoryPlaylistsForTests();
            Assert.Equal(2, source.CategoryPlaylistCalls);

            source.ReleaseCategoryPlaylists();
            await WaitFor(() => !loadMore.Enabled);
            Assert.Equal(2, source.CategoryPlaylistCalls);

            var playlists = Assert.IsType<ListBox>(Assert.Single(shell.Controls.Find("CategoryPlaylists", true)));
            Assert.Equal(
                new[] { "playlist-1", "playlist-2" },
                playlists.Items.Cast<ObjectBox<Playlist>>().Select(item => item.Value.Id).ToArray());
        });
    }

    [Fact]
    public void Song_load_more_preserves_focus_button_and_selected_song()
    {
        var source = new Issue66BrowseSource { PlaylistSongTotal = 3 };
        StaAsync(async () =>
        {
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();
            shell.SelectTabForTests(2);
            Application.DoEvents();
            AddCategory(shell);
            shell.LoadCategoryPlaylistsForTests();
            await WaitFor(() => source.CategoryPlaylistCalls == 1);

            var playlists = Assert.IsType<ListBox>(Assert.Single(shell.Controls.Find("CategoryPlaylists", true)));
            playlists.SelectedIndex = 0;
            await shell.LoadCategorySongsForTests();

            var songs = Assert.IsType<ListBox>(Assert.Single(shell.Controls.Find("CategorySongs", true)));
            songs.SelectedIndex = 0;
            var loadMore = Assert.IsType<Button>(Assert.Single(shell.Controls.Find("CategorySongsLoadMore", true)));
            Assert.True(loadMore.Enabled, "Load-more stays available while another page exists.");
            loadMore.Focus();
            Assert.True(loadMore.Focused, "The load-more control must be focusable for keyboard users.");

            await shell.LoadMoreCategorySongsForTests();

            Assert.True(loadMore.Focused, "Load-more focus must not move after appending.");
            Assert.Equal("song-1", WinFormsUi.Selected<MusicInfo>(songs)!.Id);
            Assert.Equal(new[] { "song-1", "song-2" }, Songs(songs).Select(song => song.Id).ToArray());
        });
    }

    [Fact]
    public void Album_songs_load_more_appends_in_order_and_disables_at_the_end()
    {
        var source = new Issue66BrowseSource { AlbumSongTotal = 2 };
        StaAsync(async () =>
        {
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();
            var results = shell.SearchResultsForTests;
            results.Items.Add(new ObjectBox<MusicInfo>("Album", new MusicInfo { Id = "album-1", Title = "Album", Source = "netease", IsAlbum = true }));
            results.SelectedIndex = 0;

            await shell.ActivateSelectedSearchSongForTests();

            Assert.Equal("加载更多专辑歌曲", shell.SearchLoadMoreForTests.AccessibleName);
            Assert.True(shell.SearchLoadMoreForTests.Enabled, "The album page must keep load-more available while songs remain.");
            Assert.Equal(new[] { "song-1" }, Songs(results).Select(song => song.Id).ToArray());

            await shell.LoadMoreSearchResultsForTests();

            Assert.Equal(new[] { "song-1", "song-2" }, Songs(results).Select(song => song.Id).ToArray());
            Assert.False(shell.SearchLoadMoreForTests.Enabled, "The end-of-list control must be disabled.");
        });
    }

    [Fact]
    public void Clearing_the_search_results_resets_the_album_load_more_state()
    {
        var source = new Issue66BrowseSource { AlbumSongTotal = 2 };
        StaAsync(async () =>
        {
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();
            var results = shell.SearchResultsForTests;
            results.Items.Add(new ObjectBox<MusicInfo>("Album", new MusicInfo { Id = "album-1", Title = "Album", Source = "netease", IsAlbum = true }));
            results.SelectedIndex = 0;
            await shell.ActivateSelectedSearchSongForTests();
            Assert.True(shell.SearchLoadMoreForTests.Enabled, "The album page must offer its next page.");

            var clear = shell.GetType().GetMethod("ClearSearch", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(clear);
            clear!.Invoke(shell, Array.Empty<object?>());

            Assert.False(shell.SearchLoadMoreForTests.Enabled, "A cleared list has no next page.");
            Assert.Equal("加载更多搜索歌曲", shell.SearchLoadMoreForTests.AccessibleName);
            await shell.LoadMoreSearchResultsForTests();
            Assert.Empty(Songs(results));
        });
    }

    [Fact]
    public void Ranking_load_more_appends_and_reaches_end_of_list()
    {
        var source = new Issue66BrowseSource();
        StaAsync(async () =>
        {
            using var shell = CreateShell(source);
            shell.Show();
            var rankings = Assert.IsType<ListBox>(Assert.Single(shell.Controls.Find("RankingList", true)));
            rankings.Items.Add(new ObjectBox<Ranking>("Ranking", new Ranking { Id = "ranking-1", Name = "Ranking", Source = "netease" }));
            rankings.SelectedIndex = 0;

            await shell.LoadRankingSongsForTests();
            Assert.True(Assert.IsType<Button>(Assert.Single(shell.Controls.Find("RankingSongsLoadMore", true))).Enabled);

            await shell.LoadMoreRankingSongsForTests();

            Assert.Equal(new[] { "song-1", "song-2" }, Songs(shell.RankingSongsForTests).Select(song => song.Id).ToArray());
            Assert.False(Assert.IsType<Button>(Assert.Single(shell.Controls.Find("RankingSongsLoadMore", true))).Enabled);
        });
    }
    [Fact]
    public async Task Saved_remote_playlist_load_more_appends_and_disables_at_the_end()
    {
        using var database = await TemporaryDatabase.CreateAsync();
        var source = new Issue66BrowseSource();
        var browse = CreateWorkflow(source);
        var saved = new SavedPlaylistWorkflow(database.Data, browse);
        await database.Data.SavePlaylistAsync(RemotePlaylist());

        StaAsync(async () =>
        {
            using var shell = CreateShell(source, browse: browse, saved: saved);
            shell.Show();
            Application.DoEvents();
            await shell.RefreshSavedPlaylistsForTests();
            SelectSavedPlaylist(shell, RemotePlaylist().Id);
            await shell.LoadSelectedSavedForTests();

            Assert.Equal(new[] { "song-1" }, Songs(shell.FavoriteSongsForTests).Select(song => song.Id).ToArray());
            Assert.True(shell.SavedPlaylistLoadMoreForTests.Enabled, "A remote playlist with another page must keep load-more available.");

            await shell.LoadMoreSavedSongsForTests();

            Assert.Equal(new[] { "song-1", "song-2" }, Songs(shell.FavoriteSongsForTests).Select(song => song.Id).ToArray());
            Assert.False(shell.SavedPlaylistLoadMoreForTests.Enabled, "The end-of-list control must be disabled.");
        });
    }

    [Fact]
    public async Task Saved_remote_playlist_load_more_is_guarded_and_preserves_focus_and_selection()
    {
        using var database = await TemporaryDatabase.CreateAsync();
        var source = new Issue66BrowseSource { BlockPlaylistDetailPage = 2, PlaylistSongTotal = 3 };
        var browse = CreateWorkflow(source);
        var saved = new SavedPlaylistWorkflow(database.Data, browse);
        await database.Data.SavePlaylistAsync(RemotePlaylist());

        StaAsync(async () =>
        {
            using var shell = CreateShell(source, browse: browse, saved: saved);
            shell.Show();
            Application.DoEvents();
            shell.SelectTabForTests(1);
            Application.DoEvents();
            await WaitFor(() => SavedPlaylistList(shell).Items.Count > 0);
            SelectSavedPlaylist(shell, RemotePlaylist().Id);
            await shell.LoadSelectedSavedForTests();

            var songs = shell.FavoriteSongsForTests;
            songs.SelectedIndex = 0;
            var loadMore = shell.SavedPlaylistLoadMoreForTests;
            Assert.True(loadMore.Enabled, "Load-more stays available while another page exists.");
            loadMore.Focus();
            Assert.True(loadMore.Focused, "The load-more control must be focusable for keyboard users.");

            _ = shell.LoadMoreSavedSongsForTests();
            await WaitFor(() => source.StartedPlaylistDetail);
            _ = shell.LoadMoreSavedSongsForTests();
            Assert.Equal(2, source.PlaylistDetailCalls);

            source.ReleasePlaylistDetail();
            await WaitFor(() => Songs(songs).Count == 2);
            Assert.Equal(2, source.PlaylistDetailCalls);
            Assert.True(loadMore.Focused, "Load-more focus must not move after appending.");
            Assert.Equal("song-1", WinFormsUi.Selected<MusicInfo>(songs)!.Id);
            Assert.Equal(new[] { "song-1", "song-2" }, Songs(songs).Select(song => song.Id).ToArray());
            Assert.True(loadMore.Enabled, "The control stays enabled while another page exists.");
        });
    }

    [Fact]
    public async Task Failed_saved_playlist_load_more_keeps_the_loaded_songs_and_stays_available()
    {
        using var database = await TemporaryDatabase.CreateAsync();
        var source = new Issue66BrowseSource { FailPlaylistDetailPage = 2, PlaylistSongTotal = 3 };
        var browse = CreateWorkflow(source);
        var saved = new SavedPlaylistWorkflow(database.Data, browse);
        await database.Data.SavePlaylistAsync(RemotePlaylist());

        StaAsync(async () =>
        {
            var shellAnnouncer = new RecordingAnnouncer();
            using var shell = CreateShell(source, browse: browse, saved: saved, announcer: shellAnnouncer);
            shell.Show();
            Application.DoEvents();
            await shell.RefreshSavedPlaylistsForTests();
            SelectSavedPlaylist(shell, RemotePlaylist().Id);
            await shell.LoadSelectedSavedForTests();

            var songs = shell.FavoriteSongsForTests;
            songs.SelectedIndex = 0;
            Assert.Single(Songs(songs));

            await shell.LoadMoreSavedSongsForTests();

            Assert.Equal(new[] { "song-1" }, Songs(songs).Select(song => song.Id).ToArray());
            Assert.Equal("song-1", WinFormsUi.Selected<MusicInfo>(songs)!.Id);
            Assert.True(shell.SavedPlaylistLoadMoreForTests.Enabled, "A failed page is not the end of the list, so load-more must stay available for a retry.");
            Assert.Contains(shellAnnouncer.Announcements, item => item.Kind == AnnouncementKind.Error);
        });
    }

    [Fact]
    public void Global_hotkey_action_ids_reach_shell_playback_commands()
    {
        var playback = new StatefulPlayback();
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new Issue66BrowseSource(), playback);
            var method = shell.GetType().GetMethod("ExecuteHotkey", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method!.Invoke(shell, new object[] { "play_pause" });
            Assert.True(playback.IsPaused);
        });
    }

    private static PlaylistCategory Category() => new()
    {
        Id = "tag",
        Name = "Tag",
        Tags = new List<PlaylistTag> { new() { Id = "tag", Name = "Tag" } }
    };

    private static Playlist RemotePlaylist() => new()
    {
        Id = "remote-1",
        Name = "Remote",
        Description = "远程收藏歌单",
        Type = PlaylistType.User,
        IsLocal = false,
        Source = "netease",
        RemoteId = "66"
    };

    private static MusicBrowseWorkflow CreateWorkflow(IMusicBrowseSource source, RecordingAnnouncer? announcer = null) => new(
        source,
        new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance),
        announcer ?? new RecordingAnnouncer(),
        new NoPlayback(),
        new LocalSearchHistoryStore(Path.Combine(Path.GetTempPath(), "issue66-" + Guid.NewGuid().ToString("N"))),
        NullLogger<MusicBrowseWorkflow>.Instance);

    private static SettingsWorkflow CreateSettingsWorkflow(IGlobalHotkeyAdapter adapter) => new(
        new RecordingSettingsStore(),
        new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, adapter),
        NullLogger<SettingsWorkflow>.Instance);

    private static WinFormsShell CreateShell(
        IMusicBrowseSource source,
        IPlaybackCoordinator? playback = null,
        MusicBrowseWorkflow? browse = null,
        SavedPlaylistWorkflow? saved = null,
        RecordingAnnouncer? announcer = null) => new(new WinFormsShellServices
        {
            Announcer = announcer ?? new RecordingAnnouncer(),
            BrowseSource = source,
            Playback = playback ?? new NoPlayback(),
            BrowseWorkflow = browse,
            SavedPlaylists = saved,
            TemporaryDirectory = Path.Combine(Path.GetTempPath(), "issue66-shell-" + Guid.NewGuid().ToString("N"))
        });

    /// <summary>The songs the list currently shows, in display order.</summary>
    private static IReadOnlyList<MusicInfo> Songs(ListBox list) =>
        list.Items.Cast<object>().OfType<ObjectBox<MusicInfo>>().Select(item => item.Value).ToArray();

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static IEnumerable<string> AllNames(Control root) => Descendants(root).Select(child => child.Name ?? string.Empty);

    private static ListBox SavedPlaylistList(WinFormsShell shell) =>
        Assert.IsType<ListBox>(Assert.Single(shell.Controls.Find("OnlinePlaylists", true)));

    /// <summary>Selects one saved playlist the way a keyboard user would, before an explicit load.</summary>
    private static void SelectSavedPlaylist(WinFormsShell shell, string playlistId)
    {
        var list = SavedPlaylistList(shell);
        var index = -1;
        for (var position = 0; position < list.Items.Count; position++)
        {
            if (list.Items[position] is ObjectBox<Playlist> box && box.Value.Id == playlistId) { index = position; break; }
        }
        Assert.True(index >= 0, "The saved playlist must be listed in the 播放列表 tab.");
        list.SelectedIndex = index;
    }

    private static void AddCategory(WinFormsShell shell)
    {
        var list = Assert.IsType<ListBox>(Assert.Single(shell.Controls.Find("CategoryList", true)));
        list.Items.Add(new ObjectBox<PlaylistCategory>("Tag", Category()));
        list.SelectedIndex = 0;
    }

    private static void StaAsync(Func<Task> test) => WinFormsTestRunner.RunStaAsync(test);

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("The awaited condition was not reached.");
    }
}