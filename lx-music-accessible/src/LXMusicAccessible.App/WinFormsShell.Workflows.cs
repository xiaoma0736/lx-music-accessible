using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>Explicit user-action handlers, browse workflows, playback and keyboard routing for the native shell.</summary>
public partial class WinFormsShell
{
    private sealed class LoadMoreAction
    {
    }

    private bool TryStartSearchLoadMore()
    {
        if (FocusedItem<LoadMoreAction>(SearchResults) is null
            || !SearchLoadMore.Enabled)
            return false;

        // Enter reaches both KeyDown and KeyPress. Start only once; the second event still reports
        // handled so it cannot fall through to song activation while the page request is running.
        if (!_searchLoading) _ = SearchAsync(false);
        return true;
    }

    private async Task<bool> TryActivateLoadMore(ListBox list, KeyEventArgs args)
    {
        await Task.CompletedTask;
        if (args.KeyCode != Keys.Enter
            || FocusedItem<LoadMoreAction>(list) is null)
            return false;

        if (list == SearchResults && TryStartSearchLoadMore())
        {
            args.Handled = true;
            return true;
        }
        if (list == FavoriteSongs && SavedPlaylistLoadMore.Enabled)
        {
            args.Handled = true;
            _ = LoadMoreSavedSongsAsync();
            return true;
        }
        if (list == CategoryPlaylists && CategoryPlaylistsLoadMore.Enabled)
        {
            args.Handled = true;
            _ = LoadCategoryPlaylistsAsync(GetCurrentCategoryPage() + 1, append: true);
            return true;
        }
        if (list == CategorySongs && CategorySongsLoadMore.Enabled)
        {
            args.Handled = true;
            _ = LoadMoreCategorySongsAsync();
            return true;
        }
        if (list == RankingSongs && RankingSongsLoadMore.Enabled)
        {
            args.Handled = true;
            _ = LoadMoreRankingSongsAsync();
            return true;
        }
        return false;
    }

    private async Task RunAsync<T>(Func<Task<T>> work, Action<T> render, string operation)
    {
        try
        {
            var result = await work();
            if (!IsDisposed) render(result);
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Operation cancelled");
        }
        catch (Exception ex)
        {
            Announce(operation + "失败，请检查网络", operation, AnnouncementKind.Error);
            if (!IsDisposed) WinFormsUi.Error(this, ex, operation);
        }
    }

    /// <summary>
    /// Switches to a page because of a command the user issued. Only this path may fill a page with data:
    /// moving focus across the tab headers with the arrow keys or Ctrl+Tab never reaches it.
    /// </summary>
    private void ChangeTabPage(int index)
    {
        _pageLoadRequested = true;
        try { MainTabs.SelectedIndex = index; }
        finally { _pageLoadRequested = false; }
    }

    private void SelectTab(int index)
    {
        ChangeTabPage(index);
        MainTabs.Focus();
    }

    /// <summary>Lazily loads the content of a page that the user opened on purpose.</summary>
    private void LoadTabPageContent(int index)
    {
        if (index == 1 && _services.SavedPlaylists is not null && FavoriteSongs.Items.Count == 0)
            _ = RunAsync(() => _services.SavedPlaylists.LoadAsync(), result => RenderSaved(result.Snapshot, result.Message), "刷新播放列表");
        else if (index == 2 && CategoryList.Items.Count == 0)
            LoadCategories();
        else if (index == 3 && RankingList.Items.Count == 0)
            LoadRankings();
    }

    private void ClearSearch()
    {
        _searchSongs.Clear();
        SearchResults.Items.Clear();
        SearchText.Clear();
        // The cleared list is no longer the album page, so its load-more state must go with it.
        _openAlbum = null;
        SearchLoadMore.AccessibleName = "加载更多搜索歌曲";
        UpdateLoadMore(SearchLoadMore, SearchResults, false);
        _ = _browse.ClearSearch();
        Announce("搜索已清空", "search-clear");
        SearchText.Focus();
    }

    private async Task DeleteHistory(bool clearAll)
    {
        if (clearAll)
        {
            if (!ConfirmClearSearchHistory()) return;
            var cleared = await _browse.ClearSearchHistoryAsync();
            if (cleared) SearchText.Clear();
            Announce(cleared ? "搜索历史已清空" : "没有可清空的搜索历史", "search-history-clear");
            return;
        }
        var deleted = await _browse.DeleteSearchHistoryAsync(SearchText.Text);
        if (deleted is not null) SearchText.Clear();
        Announce(deleted is null ? "没有可删除的当前搜索记录" : "当前搜索记录已删除", "search-history-delete");
    }

    protected virtual bool ConfirmClearSearchHistory() =>
        WinFormsUi.Confirm(this, "确定要清空全部搜索历史吗？", "清空搜索历史");

    private async Task SearchAsync(bool reset)
    {
        if (reset)
        {
            _openAlbum = null;
            SearchLoadMore.AccessibleName = "加载更多搜索歌曲";
        }
        else if (_openAlbum is not null)
        {
            await LoadMoreAlbumSongsAsync();
            return;
        }
        if (_searchLoading || (!reset && !SearchLoadMore.Enabled)) return;
        _searchLoading = true;
        var loadMoreSelected = !reset && FocusedItem<LoadMoreAction>(SearchResults) is not null;
        var focusNewSong = !reset && (loadMoreSelected || SearchLoadMore.Focused);
        // Search browse results are cumulative, so the old song count is the boundary of the next batch.
        var previousSongCount = reset ? 0 : _searchSongs.Count;
        try
        {
            if (reset)
            {
                var query = SearchText.Text.Trim();
                _browse.SelectSearch(query, SearchTypeName(), SourceId(SearchSource.Text));
                await _browse.RecordSearchAsync(query);
            }
            var result = await _browse.ActivateSearchAsync(reset);
            if (result.Succeeded && result.Value is not null)
            {
                _searchSongs = result.Value.Items.ToList();
                var firstNewSongIndex = _searchSongs.Count > previousSongCount ? previousSongCount : -1;
                IReadOnlyList<string>? preservedSongIds = reset ? null : SelectedSongIds(SearchResults);
                var selected = WinFormsUi.Selected<MusicInfo>(SearchResults)?.Id;
                WinFormsUi.FillSongs(SearchResults, _searchSongs);
                UpdateLoadMore(SearchLoadMore, SearchResults, result.Value.HasMore, () => SearchAsync(false));
                if (preservedSongIds is not null)
                {
                    RestoreSelectedSongs(SearchResults, _searchSongs, preservedSongIds);
                    if (focusNewSong) FocusFirstAppendedSong(SearchResults, firstNewSongIndex, preservedSongIds);
                }
                else
                {
                    RestoreSelection(SearchResults, _searchSongs, selected);
                }
                Announce(result.Message, "search-results");
            }
            else if (!string.IsNullOrWhiteSpace(result.Message))
            {
                Announce(result.Message, "search-results", AnnouncementKind.Error);
            }
        }
        finally
        {
            _searchLoading = false;
        }
    }

    private async Task LoadMoreAlbumSongsAsync()
    {
        var album = _openAlbum;
        if (album is null || _searchLoading || !SearchLoadMore.Enabled) return;
        var previousSongCount = _searchSongs.Count;
        var requestedPage = (_browse.CurrentAlbumPage?.Page ?? 0) + 1;
        var focusNewSong = SearchResults.ContainsFocus || SearchLoadMore.Focused;
        // The flag guards re-entry; disabling the focused load-more control would move focus away.
        _searchLoading = true;
        try
        {
            var result = await _browse.ActivateAlbumAsync(album, requestedPage, append: true);
            if (result.Succeeded && result.Value is not null)
            {
                var songs = _browse.CurrentAlbumPage?.Items.ToList() ?? result.Value.Items.ToList();
                _searchSongs = songs;
                var selected = WinFormsUi.Selected<MusicInfo>(SearchResults)?.Id;
                WinFormsUi.FillSongs(SearchResults, songs);
                RestoreSelection(SearchResults, songs, selected);
                UpdateLoadMore(SearchLoadMore, SearchResults, HasMore(result.Value.HasMore, result.Value.Items.Count, result.Value.TotalItems, songs.Count), () => LoadMoreAlbumSongsAsync());
                FocusFirstAppendedSong(SearchResults, previousSongCount, SelectedSongIds(SearchResults), focusNewSong);
                Announce(result.Message, "album");
            }
            else if (!string.IsNullOrWhiteSpace(result.Message))
            {
                Announce(result.Message, "album", AnnouncementKind.Error);
            }
        }
        finally
        {
            _searchLoading = false;
        }
    }

    private string SearchTypeName() => SearchType.Text switch
    {
        "歌手" => "artist",
        "专辑" => "album",
        _ => "song"
    };

    private async Task ActivateSong(ListBox list, PlaybackQueueOrigin origin = PlaybackQueueOrigin.Search)
    {
        var song = FocusedItem<MusicInfo>(list);
        if (song is null) return;
        if (song.IsAlbum)
        {
            var paged = list == SearchResults;
            if (paged)
            {
                _openAlbum = song;
                SearchLoadMore.AccessibleName = "加载更多专辑歌曲";
                UpdateLoadMore(SearchLoadMore, SearchResults, false);
            }
            var albumResult = await _browse.ActivateAlbumAsync(song);
            if (albumResult.Succeeded && albumResult.Value is not null)
            {
                var songs = _browse.CurrentAlbumPage?.Items.ToList() ?? albumResult.Value.Items.ToList();
                var selected = WinFormsUi.Selected<MusicInfo>(list)?.Id;
                WinFormsUi.FillSongs(list, songs);
                RestoreSelection(list, songs, selected);
                if (paged)
                {
                    _searchSongs = songs;
                    UpdateLoadMore(SearchLoadMore, SearchResults, HasMore(albumResult.Value.HasMore, albumResult.Value.Items.Count, albumResult.Value.TotalItems, songs.Count), () => LoadMoreAlbumSongsAsync());
                }
                Announce(albumResult.Message, "album");
            }
            return;
        }
        await PlayMusicAsync(song, list, origin);
    }

    private async Task PlayMusicAsync(MusicInfo music, ListBox? source = null, PlaybackQueueOrigin origin = PlaybackQueueOrigin.Search)
    {
        if (_services.Playback is null) return;
        var queue = source is null ? new List<MusicInfo> { music } : source.Items.Cast<object>().OfType<ObjectBox<MusicInfo>>().Select(item => item.Value).ToList();
        if (queue.All(item => item.Id != music.Id)) queue.Add(music);
        var playbackQuality = QualityCatalog.Normalize(
            _services.Settings?.Current.Settings.DefaultPlayQuality ?? _services.Playback.DefaultQuality);
        var result = await _services.Playback.PlayAsync(new PlaybackRequest(
            queue,
            music,
            playbackQuality,
            origin,
            Trigger: PlaybackTrigger.UserInitiated));
        if (result.Status is PlaybackResultStatus.Playing or PlaybackResultStatus.QueueCompleted)
        {
            _currentPlaybackQuality = playbackQuality;
            ShowPlayback(music);
        }
        if (result.Status is PlaybackResultStatus.Playing or PlaybackResultStatus.QueueCompleted)
        {
            Announce($"正在播放：{music.Artist}，{music.Title}", "playback-song", AnnouncementKind.Success);
        }
        else if (result.Status == PlaybackResultStatus.Failed)
        {
            Announce($"播放失败：{result.Message}", "playback-song", AnnouncementKind.Error);
        }
        else if (result.Status == PlaybackResultStatus.NoChange)
        {
            Announce(result.Message, "playback-song", AnnouncementKind.Status);
        }
        // Superseded: a newer request owns the user-visible outcome; announcing it as a failure
        // was false because the newer request may already be playing.
    }

    private void OnPlaybackStateChanged(PlayerStateChangedEventArgs e)
    {
        if (e.NewState != PlayerState.Playing || e.CurrentMusic is null) return;
        _currentPlaybackQuality = QualityCatalog.Normalize(string.IsNullOrWhiteSpace(e.RequestedQuality) ? _currentPlaybackQuality : e.RequestedQuality);
        _currentPlaybackActualQuality = e.ActualQuality;
        _currentPlaybackInterfaceId = e.InterfaceId;
        _currentPlaybackInterfaceName = e.InterfaceName;
        ShowPlayback(e.CurrentMusic);
        if (e.Trigger != PlaybackTrigger.AutoContinuation) return;
        var song = e.CurrentMusic;
        Announce(
            "正在播放：" + song.Artist + "，" + song.Title,
            "playback-track:auto:" + Guid.NewGuid().ToString("N"),
            AnnouncementKind.Success);
    }

    private void ShowPlayback(MusicInfo music)
    {
        _current = music;
        Text = string.IsNullOrWhiteSpace(music.Artist) ? $"{music.Title} - {BaseTitle}" : $"{music.Artist} - {music.Title} - {BaseTitle}";
        RefreshPlaybackStatus();
    }

    private async Task LoadSelectedSavedAsync()
    {
        var playlist = WinFormsUi.Selected<Playlist>(OnlinePlaylists);
        if (playlist is null || _services.SavedPlaylists is null) return;
        _services.SavedPlaylists.Select(playlist);
        var result = await _services.SavedPlaylists.ActivateAsync();
        RenderSaved(result.Snapshot, result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private async Task LoadMoreSavedSongsAsync()
    {
        if (_services.SavedPlaylists is null || _savedSongsLoading || !SavedPlaylistLoadMore.Enabled) return;
        var playlist = WinFormsUi.Selected<Playlist>(OnlinePlaylists);
        if (playlist is null) return;
        var current = _browse.CurrentSavedPlaylistPage;
        if (current is null || current.Playlist.Id != playlist.Id) return;
        var previousSongCount = _favoriteSongs.Count;
        var focusNewSong = FavoriteSongs.ContainsFocus || SavedPlaylistLoadMore.Focused;
        _savedSongsLoading = true;
        try
        {
            var selected = WinFormsUi.Selected<MusicInfo>(FavoriteSongs)?.Id;
            var result = await _services.SavedPlaylists.ActivateAsync(current.Page + 1, append: true);
            // A failed page is not the end of the list: keep the songs already loaded and leave the
            // control available so the user can retry the same page.
            if (result.Succeeded)
            {
                RenderSaved(result.Snapshot, result.Message, AnnouncementKind.Success, append: true);
                FocusFirstAppendedSong(FavoriteSongs, previousSongCount, SelectedSongIds(FavoriteSongs), focusNewSong);
            }
            else
            {
                Announce(result.Message, "saved-playlists", AnnouncementKind.Error);
                RestoreSelection(FavoriteSongs, _favoriteSongs, selected);
            }
        }
        finally
        {
            _savedSongsLoading = false;
        }
    }

    private Task RefreshSavedAsync() => RunAsync(
        () => _services.SavedPlaylists!.LoadAsync(),
        result => RenderSaved(result.Snapshot, result.Message),
        "刷新播放列表");

    private void RenderSaved(SavedPlaylistSnapshot snapshot, string message, AnnouncementKind kind = AnnouncementKind.Success, bool append = false)
    {
        if (!append)
        {
            OnlinePlaylists.Items.Clear();
            foreach (var playlist in snapshot.Playlists)
                OnlinePlaylists.Items.Add(new ObjectBox<Playlist>(playlist.Name, playlist));
            if (snapshot.Selected is not null)
            {
                var index = OnlinePlaylists.Items.Cast<ObjectBox<Playlist>>().ToList().FindIndex(item => item.Value.Id == snapshot.Selected.Id);
                OnlinePlaylists.SelectedIndex = index;
            }
        }
        _favoriteSongs = snapshot.Songs.ToList();
        WinFormsUi.FillSongs(FavoriteSongs, _favoriteSongs);
        UpdateLoadMore(SavedPlaylistLoadMore, FavoriteSongs, snapshot.HasMore, LoadMoreSavedSongsAsync);
        Announce(message, "saved-playlists", kind);
    }

    private async Task ManagePlaylistAsync(string prompt, Func<string?, Task<SavedPlaylistResult>> action)
    {
        if (_services.SavedPlaylists is null) return;
        var selectedPlaylist = WinFormsUi.Selected<Playlist>(OnlinePlaylists);
        if (prompt.StartsWith("重命名", StringComparison.Ordinal) && selectedPlaylist is not null)
            prompt = SavedPlaylistWorkflow.RenamePrompt(selectedPlaylist);
        var selected = selectedPlaylist?.Name;
        var value = WinFormsUi.AskText(this, prompt, prompt + "：", prompt.StartsWith("重命名", StringComparison.Ordinal) ? selected : null);
        if (string.IsNullOrWhiteSpace(value)) return;
        var result = await action(value);
        RenderSaved(result.Snapshot, result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private async Task DeleteSavedPlaylistAsync()
    {
        if (_services.SavedPlaylists is null) return;
        var playlist = WinFormsUi.Selected<Playlist>(OnlinePlaylists);
        if (playlist is null) return;
        if (!SavedPlaylistWorkflow.CanDelete(playlist))
        {
            Announce(playlist.IsLocal ? "默认本地歌曲歌单不能删除。" : "默认在线歌单不能删除。", "saved-playlists", AnnouncementKind.Error);
            return;
        }
        if (!WinFormsUi.Confirm(this, SavedPlaylistWorkflow.DeleteConfirmation(playlist))) return;
        var result = await _services.SavedPlaylists.DeleteSelectedAsync();
        RenderSaved(result.Snapshot, result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private async Task RemoveFavoriteAsync()
    {
        var song = WinFormsUi.Selected<MusicInfo>(FavoriteSongs);
        if (song is null || _services.SavedPlaylists is null) return;
        var result = await _services.SavedPlaylists.RemoveSongAsync(song);
        RenderSaved(result.Snapshot, result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private async Task AddLocalFilesToPlaylist()
    {
        try
        {
            using var dialog = new OpenFileDialog
            {
                Title = "添加本地音频到本地歌曲",
                Filter = "音频文件|*.mp3;*.flac;*.ape;*.m4a;*.wav;*.ogg;*.opus;*.aac;*.wma|所有文件|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await AddLocalPathsToPlaylistAsync(dialog.FileNames);
        }
        catch (Exception ex)
        {
            WinFormsUi.Error(this, ex, "添加本地音频");
        }
    }

    private async Task AddLocalFolderToPlaylist()
    {
        try
        {
            using var dialog = new FolderBrowserDialog { Description = "选择要添加到本地歌曲的音频文件夹" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mp3", ".flac", ".ape", ".m4a", ".wav", ".ogg", ".opus", ".aac", ".wma"
            };
            var paths = Directory.EnumerateFiles(dialog.SelectedPath, "*", SearchOption.AllDirectories)
                .Where(path => extensions.Contains(Path.GetExtension(path)))
                .ToArray();
            await AddLocalPathsToPlaylistAsync(paths);
        }
        catch (Exception ex)
        {
            WinFormsUi.Error(this, ex, "添加本地音频文件夹");
        }
    }

    private async Task AddLocalPathsToPlaylistAsync(IEnumerable<string> paths)
    {
        var songs = CreateLocalMusicInfos(paths, out var skippedCount);
        if (songs.Count == 0)
        {
            Announce("没有找到支持的音频文件。", "local-playlist", AnnouncementKind.Error);
            return;
        }

        if (_services.SavedPlaylists is null) return;
        var result = await _services.SavedPlaylists.AddSongsAsync(songs);
        var message = result.Message;
        if (result.Succeeded && skippedCount > 0)
            message += $"；已跳过 {skippedCount} 个不支持的文件";
        RenderSaved(result.Snapshot, message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private static IReadOnlyList<MusicInfo> CreateLocalMusicInfos(IEnumerable<string> paths, out int skippedCount)
    {
        if (paths is null) throw new ArgumentNullException(nameof(paths));
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".ape", ".m4a", ".wav", ".ogg", ".opus", ".aac", ".wma"
        };
        var songs = new List<MusicInfo>();
        skippedCount = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !extensions.Contains(Path.GetExtension(path)))
            {
                skippedCount++;
                continue;
            }

            songs.Add(new MusicInfo
            {
                Id = "Local|" + path,
                Title = Path.GetFileNameWithoutExtension(path),
                Source = "Local",
                PlayUrl = path
            });
        }

        return songs;
    }
    private async void ClearFavorites()
    {
        if (_services.SavedPlaylists is null) return;
        var playlist = WinFormsUi.Selected<Playlist>(OnlinePlaylists);
        if (playlist is null || !WinFormsUi.Confirm(this, SavedPlaylistWorkflow.ClearConfirmation(playlist))) return;
        var result = await _services.SavedPlaylists.ClearAsync();
        RenderSaved(result.Snapshot, result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private void LoadCategories()
    {
        _browse.SelectCategorySource(SourceId(CategorySource.Text), CategorySource.Text);
        _ = RunAsync(async () => await _browse.ActivateCategorySourceAsync(), result =>
        {
            if (!result.Succeeded || result.Value is null) return;
            CategoryList.Items.Clear();
            foreach (var category in result.Value)
                CategoryList.Items.Add(new ObjectBox<PlaylistCategory>(category.Name, category));
            Announce(result.Message, "categories");
        }, "加载分类");
    }

    private void LoadCategoryPlaylists(int page = 1) => _ = LoadCategoryPlaylistsAsync(page, append: false);

    private async Task LoadCategoryPlaylistsAsync(int page, bool append)
    {
        var category = WinFormsUi.Selected<PlaylistCategory>(CategoryList);
        if (category is null) return;
        // A repeat load-more while one is in flight is ignored; a fresh page for another category
        // supersedes the in-flight request instead of being silently dropped.
        if (append && (_categoryPlaylistsLoading || !CategoryPlaylistsLoadMore.Enabled)) return;
        var requestedPage = Math.Max(1, page);
        var revision = ++_categoryPlaylistsRevision;
        _categoryPlaylistsLoading = true;
        if (!append) UpdateLoadMore(CategoryPlaylistsLoadMore, CategoryPlaylists, false);
        try
        {
            if (!append) _browse.SelectCategory(category);
            var result = await _browse.ActivateCategoryAsync(requestedPage, append);
            if (result.Succeeded && result.Value is not null)
            {
                var items = _browse.CurrentCategoryPage?.Items ?? result.Value.Items;
                var selected = WinFormsUi.Selected<Playlist>(CategoryPlaylists)?.Id;
                CategoryPlaylists.Items.Clear();
                foreach (var playlist in items)
                    CategoryPlaylists.Items.Add(new ObjectBox<Playlist>(playlist.Name, playlist));
                RestoreSelection(CategoryPlaylists, items, selected);
                UpdateLoadMore(CategoryPlaylistsLoadMore, CategoryPlaylists, HasMore(result.Value.HasMore, result.Value.Items.Count, result.Value.TotalItems, items.Count), () => LoadCategoryPlaylistsAsync(GetCurrentCategoryPage() + 1, append: true));
                SetBrowseStatus(result.Message);
                if (!append && ContainsFocus) CategoryPlaylists.Focus();
            }
            else SetBrowseStatus(result.Message);
        }
        finally
        {
            if (revision == _categoryPlaylistsRevision) _categoryPlaylistsLoading = false;
        }
    }

    private Task LoadCategorySongs() => LoadCategorySongsAsync(1, append: false);

    private Task LoadMoreCategorySongsAsync() => LoadCategorySongsAsync((_browse.CurrentPlaylistPage?.Page ?? 0) + 1, append: true);

    private async Task LoadCategorySongsAsync(int page, bool append)
    {
        var playlist = WinFormsUi.Selected<Playlist>(CategoryPlaylists);
        if (playlist is null || _categorySongsLoading || (append && !CategorySongsLoadMore.Enabled)) return;
        var requestedPage = Math.Max(1, page);
        var previousSongCount = append ? _categorySongs.Count : -1;
        var focusNewSong = CategorySongs.ContainsFocus || CategorySongsLoadMore.Focused;
        _categorySongsLoading = true;
        if (!append) UpdateLoadMore(CategorySongsLoadMore, CategorySongs, false);
        try
        {
            if (!append) _browse.SelectPlaylist(playlist);
            var result = await _browse.ActivatePlaylistAsync(requestedPage, append);
            if (result.Succeeded && result.Value is not null)
            {
                _categorySongs = (_browse.CurrentPlaylistPage?.Items ?? result.Value.Items).ToList();
                var selected = WinFormsUi.Selected<MusicInfo>(CategorySongs)?.Id;
                WinFormsUi.FillSongs(CategorySongs, _categorySongs);
                RestoreSelection(CategorySongs, _categorySongs, selected);
                UpdateLoadMore(CategorySongsLoadMore, CategorySongs, HasMore(result.Value.HasMore, result.Value.Items.Count, result.Value.TotalItems, _categorySongs.Count), LoadMoreCategorySongsAsync);
                if (append) FocusFirstAppendedSong(CategorySongs, previousSongCount, SelectedSongIds(CategorySongs), focusNewSong);
                SetBrowseStatus(result.Message);
                if (!append && ContainsFocus) CategorySongs.Focus();
            }
            else SetBrowseStatus(result.Message);
        }
        finally
        {
            _categorySongsLoading = false;
        }
    }

    private int GetCurrentCategoryPage() => _browse.CurrentCategoryPage?.Page ?? 1;

    private int GetCurrentSongPage() => _browse.CurrentPlaylistPage?.Page ?? 1;

    /// <summary>
    /// Load-more stays available while the accumulated item count is still behind the provider total.
    /// The reported flag and a non-empty page are only fallbacks for providers without a known total.
    /// </summary>
    private static bool HasMore(bool reportedMore, int pageItemCount, int totalItems, int loadedCount) =>
        totalItems > 0 ? loadedCount < totalItems : reportedMore || pageItemCount > 0;

    /// <summary>
    /// Keeps the load-more control enabled while another page exists and disables it at the end of the list.
    /// Disabling a focused control would otherwise drop focus onto the tab control, so focus returns to the
    /// list that just finished loading instead of away from the reading position.
    /// </summary>
    private static void UpdateLoadMore(Button button, ListBox list, bool hasMore, Func<Task>? loadMore = null)
    {
        _ = loadMore;
        var restoreFocus = !hasMore && button.Focused;
        button.Enabled = hasMore;

        for (var index = list.Items.Count - 1; index >= 0; index--)
        {
            if (list.Items[index] is ObjectBox<LoadMoreAction>) list.Items.RemoveAt(index);
        }
        if (hasMore) list.Items.Add(new ObjectBox<LoadMoreAction>("加载更多", new LoadMoreAction()));

        if (restoreFocus && !list.IsDisposed && list.CanFocus) list.Focus();
    }

    /// <summary>
    /// Backspace returns to the list the current one was opened from. The focus move is the
    /// result: a screen reader already names the list that just took focus, so the 已返回 line only
    /// mirrors into the announcement row and never interrupts the reader.
    /// </summary>
    private bool HandleBrowseBack(ListBox? source = null)
    {
        source ??= ActiveControl as ListBox;

        if (source == SearchResults && _openAlbum is not null)
        {
            _ = ReturnToSearchAsync();
            return true;
        }
        if (source == CategorySongs)
        {
            CategoryPlaylists.Focus();
            Announce("已返回歌单列表", "browse-back", speech: AnnouncementSpeech.None);
            return true;
        }
        if (source == CategoryPlaylists)
        {
            CategoryList.Focus();
            Announce("已返回分类列表", "browse-back", speech: AnnouncementSpeech.None);
            return true;
        }
        if (source == RankingSongs)
        {
            RankingList.Focus();
            Announce("已返回榜单列表", "browse-back", speech: AnnouncementSpeech.None);
            return true;
        }
        if (source == FavoriteSongs)
        {
            OnlinePlaylists.Focus();
            Announce("已返回歌单列表", "browse-back", speech: AnnouncementSpeech.None);
            return true;
        }
        return false;
    }

    private Task ReturnToSearchAsync()
    {
        var search = _browse.CurrentSearch;
        var items = search.Items.ToList();
        _openAlbum = null;
        _searchSongs = items;
        SearchLoadMore.AccessibleName = "加载更多搜索歌曲";
        var selected = WinFormsUi.Selected<MusicInfo>(SearchResults)?.Id;
        WinFormsUi.FillSongs(SearchResults, items);
        UpdateLoadMore(SearchLoadMore, SearchResults, search.HasMore, () => SearchAsync(false));
        RestoreSelection(SearchResults, items, selected);
        SearchResults.Focus();
        Announce("已返回搜索结果", "browse-back", speech: AnnouncementSpeech.None);
        return Task.CompletedTask;
    }

    private static void FocusFirstAppendedSong(
        ListBox list,
        int previousSongCount,
        IReadOnlyList<string>? preservedSongIds = null,
        bool focusList = true)
    {
        if (previousSongCount < 0 || previousSongCount >= list.Items.Count) return;
        var index = previousSongCount;
        // Callers only pass true when the list or its explicit load-more command still owns focus;
        // a background page that finishes after the user moved away must not steal it back.
        if (focusList && !list.IsDisposed && list.CanFocus) list.Focus();
        if (preservedSongIds is not null && preservedSongIds.Count == 0)
            WinFormsUi.SelectOnly(list, index);
        else
            list.SetSelected(index, true);
        SetListCaretIndex(list, index);
    }
    private static void RestoreSelections(ListBox list, IReadOnlyList<int> indices)
    {
        foreach (var index in indices)
            if (index >= 0 && index < list.Items.Count)
                list.SetSelected(index, true);
    }
    private static IReadOnlyList<string> SelectedSongIds(ListBox list) =>
        list.SelectedItems.Cast<object>()
            .OfType<ObjectBox<MusicInfo>>()
            .Select(item => item.Value.Id)
            .ToArray();

    private static void RestoreSelectedSongs(ListBox list, IReadOnlyList<MusicInfo> items, IReadOnlyList<string>? ids)
    {
        if (ids is null) return;
        foreach (var id in ids)
        {
            var index = -1;
            for (var candidate = 0; candidate < items.Count; candidate++)
            {
                if (!string.Equals(items[candidate].Id, id, StringComparison.Ordinal)) continue;
                index = candidate;
                break;
            }
            if (index >= 0) list.SetSelected(index, true);
        }
    }
    private static void RestoreSelection<T>(ListBox list, IReadOnlyList<T> items, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var index = -1;
        for (var i = 0; i < items.Count; i++)
        {
            var identity = items[i] switch
            {
                MusicInfo song => song.Id,
                Playlist playlist => playlist.Id,
                _ => null
            };
            if (string.Equals(identity, id, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        if (index >= 0) list.SelectedIndex = index;
    }

    /// <summary>
    /// Mirrors a browse outcome into the visible status bar. The browse workflow announces the same
    /// message through the shared announcer with a fresh music-browse:{n} activity id, so the shell must
    /// not post a second terminal announcement for it: the announcer would deliver the first outcome
    /// twice and its terminal dedupe would then swallow every later one.
    /// </summary>
    private void SetBrowseStatus(string message)
    {
        if (!IsDisposed && !string.IsNullOrWhiteSpace(message)) StatusText.Text = message;
    }

    private void LoadRankings()
    {
        _browse.SelectRankingSource(SourceId(RankingSource.Text), RankingSource.Text);
        _ = RunAsync(async () => await _browse.ActivateRankingSourceAsync(), result =>
        {
            if (!result.Succeeded || result.Value is null) return;
            RankingList.Items.Clear();
            foreach (var ranking in result.Value)
                RankingList.Items.Add(new ObjectBox<Ranking>(ranking.Name, ranking));
            Announce(result.Message, "rankings");
        }, "加载排行榜");
    }

    private Task LoadRankingSongs() => LoadRankingSongsAsync(1, append: false);

    private Task LoadMoreRankingSongsAsync() => LoadRankingSongsAsync((_browse.CurrentRankingPage?.Page ?? 0) + 1, append: true);

    private async Task LoadRankingSongsAsync(int page, bool append)
    {
        var ranking = WinFormsUi.Selected<Ranking>(RankingList);
        if (ranking is null || _rankingSongsLoading || (append && !RankingSongsLoadMore.Enabled)) return;
        var requestedPage = Math.Max(1, page);
        var previousSongCount = append ? _rankingSongs.Count : -1;
        var focusNewSong = RankingSongs.ContainsFocus || RankingSongsLoadMore.Focused;
        _rankingSongsLoading = true;
        if (!append) UpdateLoadMore(RankingSongsLoadMore, RankingSongs, false);
        try
        {
            if (!append) _browse.SelectRanking(ranking);
            var result = await _browse.ActivateRankingAsync(requestedPage, append);
            if (result.Succeeded && result.Value is not null)
            {
                _rankingSongs = (_browse.CurrentRankingPage?.Items ?? result.Value.Items).ToList();
                var selected = WinFormsUi.Selected<MusicInfo>(RankingSongs)?.Id;
                WinFormsUi.FillSongs(RankingSongs, _rankingSongs);
                RestoreSelection(RankingSongs, _rankingSongs, selected);
                UpdateLoadMore(RankingSongsLoadMore, RankingSongs, HasMore(result.Value.HasMore, result.Value.Items.Count, result.Value.TotalItems, _rankingSongs.Count), LoadMoreRankingSongsAsync);
                if (append) FocusFirstAppendedSong(RankingSongs, previousSongCount, SelectedSongIds(RankingSongs), focusNewSong);
                SetBrowseStatus(result.Message);
            }
            else SetBrowseStatus(result.Message);
        }
        finally
        {
            _rankingSongsLoading = false;
        }
    }

    private async void FavoriteCategoryPlaylist()
    {
        var playlist = WinFormsUi.Selected<Playlist>(CategoryPlaylists);
        await FavoriteRemotePlaylist(playlist?.Name, SourceId(CategorySource.Text), playlist?.Id, PlaylistType.Cloud);
    }

    private async void FavoriteRanking()
    {
        var ranking = WinFormsUi.Selected<Ranking>(RankingList);
        await FavoriteRemotePlaylist(ranking?.Name, ranking?.Source ?? SourceId(RankingSource.Text), ranking?.Id, PlaylistType.Recommend);
    }

    private async Task FavoriteRemotePlaylist(string? name, string? source, string? remoteId, PlaylistType type)
    {
        if (name is null || source is null || remoteId is null || _services.SavedPlaylists is null) return;
        var result = await _services.SavedPlaylists.CollectRemoteAsync(new RemotePlaylistFavorite(name, source, remoteId, type));
        Announce(result.Message, "remote-favorite", result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private bool BeginPlaybackToggle()
    {
        var tick = Environment.TickCount;
        if (!_playbackToggleTickInitialized)
        {
            _playbackToggleTickInitialized = true;
            _lastPlaybackToggleTick = tick;
            return true;
        }
        if (tick - _lastPlaybackToggleTick is > -250 and < 250) return false;
        _lastPlaybackToggleTick = tick;
        return true;
    }

    private void TogglePlayback()
    {
        if (_services.Playback is null) return;
        if (!BeginPlaybackToggle()) return;
        if (_services.Playback.IsPlaying)
        {
            _services.Playback.Pause();
            // The music stopping is the feedback; speaking it only interrupts the screen reader.
            Announce("已暂停", "playback-state", AnnouncementKind.Status);
        }
        else
        {
            // ReplayAsync resumes a pause and restarts a stopped/ended song; ResumeAsync alone did
            // nothing after Stop or a completed queue while the shell still announced success.
            _ = ResumePlaybackAsync();
        }
    }

    private async Task ResumePlaybackAsync()
    {
        try
        {
            if (_services.Playback is null) return;
            if (_services.Playback.IsPaused)
            {
                await _services.Playback.ResumeAsync();
                Announce("恢复播放", "playback-state", AnnouncementKind.Success);
                return;
            }

            var result = await _services.Playback.ReplayAsync();
            if (result.Status == PlaybackResultStatus.Playing)
            {
                Announce("重新播放当前歌曲", "playback-state", AnnouncementKind.Success);
                return;
            }

            Announce(result.Message, "playback-state",
                result.Status == PlaybackResultStatus.Superseded
                    ? AnnouncementKind.Status
                    : AnnouncementKind.Error);
        }
        catch (Exception exception)
        {
            Announce("恢复播放失败", "playback-state", AnnouncementKind.Error);
            if (!IsDisposed) WinFormsUi.Error(this, exception, "恢复播放");
        }
    }

    private void StopPlayback()
    {
        _services.Playback?.Stop();
        _current = null;
        Text = BaseTitle;
        PlaybackInfoText.Text = "未播放";
        Announce("已停止播放", "playback-state", AnnouncementKind.Status);
    }

    private async Task MovePlayback(bool previous)
    {
        if (_services.Playback is null) return;
        var result = previous ? await _services.Playback.PreviousAsync() : await _services.Playback.NextAsync();
        if (result.Music is not null) ShowPlayback(result.Music);
        // Saying 已切换歌曲 without the title left the user to find the new song in the window caption, which
        // assistive technology does not re-read on its own. The title is the only way to learn what started.
        var started = result.Status is PlaybackResultStatus.Playing or PlaybackResultStatus.QueueCompleted;
        var spoken = !started
            ? result.Message
            : result.Music is null ? "已切换歌曲" : $"正在播放：{result.Music.Artist}，{result.Music.Title}";
        Announce(spoken, "playback-song", started ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private void Seek(double seconds)
    {
        if (_services.Audio is null) return;
        var target = _services.Audio.Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > _services.Audio.Duration) target = _services.Audio.Duration;
        _services.Audio.SeekTo(target);
        Announce($"已跳转到 {FormatTime(target)}", "seek", AnnouncementKind.Status);
    }

    private void ChangeVolume(double delta)
    {
        if (_services.Audio is null) return;
        var volume = Math.Max(0.0, Math.Min(1.0, _services.Audio.Volume + delta));
        _services.Audio.SetVolume(volume);
        var edit = _services.Settings?.Edit(settings => settings.DefaultVolume = volume);
        if (edit is not null) _ = SaveSettingsAsync(edit);
        // The changed loudness is audible; announcing every step competes with the music and the reader.
        Announce($"音量 {volume:0%}", "volume", AnnouncementKind.Status);
    }

    private void SwitchDevice(int direction)
    {
        if (_services.Audio is null) return;
        var name = direction < 0 ? _services.Audio.SwitchToPreviousOutputDevice() : _services.Audio.SwitchToNextOutputDevice();
        if (string.IsNullOrWhiteSpace(name)) return;
        Announce($"播放设备：{name}", "audio-device");
    }

    private void TogglePlayMode()
    {
        _playMode = (PlayMode)(((int)_playMode + 1) % 4);
        if (_services.Playback is not null) _services.Playback.Mode = _playMode;
        var edit = _services.Settings?.Edit(settings => settings.DefaultPlayMode = _playMode);
        // 语音播报 is the master switch for routine announcements, so a mode change obeys it: the sentence
        // still reaches the visible announcement line when speech is off. Only the lyric reader bypasses
        // this switch, because it is controlled by its own 歌词播报 item.
        Announce(PlayModeName(_playMode), "play-mode");
        if (edit is not null) _ = SaveSettingsAsync(edit);
    }

    /// <summary>Persists a settings edit at the moment of the user action and reports a failed write.</summary>
    private async Task SaveSettingsAsync(SettingsEditResult edit)
    {
        var settings = _services.Settings;
        if (settings is null || !edit.Accepted) return;
        try
        {
            var result = await settings.SaveAsync(edit.State);
            if (result.Succeeded) return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Announce("设置保存失败，将在退出时重试。", "settings", AnnouncementKind.Error);
    }

    private ListBox? CurrentSongList()
    {
        return MainTabs.SelectedIndex switch
        {
            0 => SearchResults,
            1 => FavoriteSongs,
            2 => CategorySongs,
            3 => RankingSongs,
            _ => null
        };
    }

    private static IReadOnlyList<MusicInfo> SelectedSongs(ListBox list) =>
        list.SelectedItems.Cast<object>()
            .OfType<ObjectBox<MusicInfo>>()
            .Select(item => item.Value)
            .ToArray();

    private T? FocusedItem<T>(ListBox list)
    {
        var index = ListCaretIndex(list);
        return index >= 0 && index < list.Items.Count && list.Items[index] is ObjectBox<T> box ? box.Value : default;
    }

    private string BatchDownloadLabel(ListBox list)
    {
        if (ReferenceEquals(list, SearchResults)) return "搜索结果选中歌曲";
        if (ReferenceEquals(list, FavoriteSongs)) return "收藏歌曲选中歌曲";
        if (ReferenceEquals(list, CategorySongs)) return "分类歌曲选中歌曲";
        if (ReferenceEquals(list, RankingSongs)) return "排行榜歌曲选中歌曲";
        return "选中歌曲";
    }

    /// <summary>
    /// Gives Space a single predictable playback meaning now that browsing lists are single-select.
    /// Explicit multi-selection belongs to the dedicated batch-selection window, not song lists.
    /// </summary>
    private bool HandleSongListSpace(ListBox list, KeyEventArgs e)
    {
        if (e.Control || e.Shift || e.Alt || e.KeyCode != Keys.Space) return false;
        e.Handled = true;
        // Only an active or paused stream owns Space. After Stop/queue end the focused row wins,
        // otherwise Space looked up CurrentMusic and silently did nothing.
        if (_services.Playback is { } playback && (playback.IsPlaying || playback.IsPaused))
        {
            TogglePlayback();
            return true;
        }

        var song = FocusedItem<MusicInfo>(list);
        if (song is not null) _ = ActivateSong(list, OriginForList(list));
        return true;
    }
    private static bool IsSongIndex(ListBox list, int index) =>
        index >= 0 && index < list.Items.Count && list.Items[index] is ObjectBox<MusicInfo>;


    private static IReadOnlyList<MusicInfo> AllSongs(ListBox list) =>
        list.Items.Cast<object>().OfType<ObjectBox<MusicInfo>>().Select(item => item.Value).ToArray();

    private string BatchSelectionTitle(ListBox list)
    {
        if (ReferenceEquals(list, SearchResults)) return "搜索结果";
        if (ReferenceEquals(list, FavoriteSongs)) return "收藏歌曲";
        if (ReferenceEquals(list, CategorySongs)) return "分类歌曲";
        if (ReferenceEquals(list, RankingSongs)) return "排行榜歌曲";
        return "歌曲";
    }

    private void OpenBatchSelection(ListBox list)
    {
        if (_services.Announcer is null) return;
        CloseBatchSelectionForms();
        var requester = list;
        var batch = new BatchSelectionForm(
            _services.Announcer,
            AllSongs(list),
            BatchSelectionTitle(list),
            songs => DownloadExplicitSelectionAsync(songs, BatchDownloadLabel(list)));
        batch.FormClosed += (_, _) =>
        {
            _batchSelectionForms.Remove(batch);
            if (requester is { IsDisposed: false, CanFocus: true } && requester.FindForm() is { Visible: true })
                requester.Focus();
        };
        _batchSelectionForms.Add(batch);
        batch.Show(this);
        batch.Focus();
    }

    private void CloseBatchSelectionForms()
    {
        foreach (var batch in _batchSelectionForms.Where(form => !form.IsDisposed).ToArray())
            batch.Close();
        _batchSelectionForms.Clear();
    }

    private async Task DownloadExplicitSelectionAsync(IReadOnlyList<MusicInfo> songs, string label)
    {
        if (_services.Downloads is null || songs.Count == 0) return;
        var quality = _services.Settings?.Current.Settings.DefaultDownloadQuality ?? "128k";
        try
        {
            await _services.Downloads.DownloadListAsync(new DownloadListRequest(songs, label, quality));
        }
        catch (Exception exception)
        {
            Announce("批量下载任务创建失败", "download-batch", AnnouncementKind.Error);
            WinFormsUi.Error(this, exception, "批量下载");
        }
    }
    private readonly List<BatchSelectionForm> _batchSelectionForms = new();

    private const int LB_GETCARETINDEX = 0x019F;
    private const int LB_SETCARETINDEX = 0x019E;

    private static int ListCaretIndex(ListBox list) =>
        SendMessage(list.Handle, LB_GETCARETINDEX, IntPtr.Zero, IntPtr.Zero).ToInt32();

    private static void SetListCaretIndex(ListBox list, int index) =>
        SendMessage(list.Handle, LB_SETCARETINDEX, (IntPtr)index, IntPtr.Zero);

    internal ContextMenu BuildSongContextMenu(ListBox list)
    {
        var menu = new ContextMenu();
        menu.MenuItems.Add(new MenuItem("播放", (_, _) => _ = ActivateSong(list)));
        menu.MenuItems.Add(new MenuItem("查看歌词", (_, _) => OpenLyrics(WinFormsUi.Selected<MusicInfo>(list))));
        menu.MenuItems.Add(new MenuItem("查看详情", (_, _) => _ = ShowSongDetails(list)));
        menu.MenuItems.Add(new MenuItem("收藏歌曲", (_, _) => _ = AddFavorites(WinFormsUi.Songs(list))));
        var focused = FocusedItem<MusicInfo>(list);
        var qualityDownload = new MenuItem("下载单曲音质");
        qualityDownload.Popup += (_, _) =>
        {
            var song = FocusedItem<MusicInfo>(list);
            _ = RefreshQualitySubmenuAsync(
                qualityDownload,
                list,
                song,
                "下载单曲音质",
                choice => DownloadQualityAsync(choice.Song, choice.Quality),
                "download-quality:" + song?.Id,
                QualityMenuKind.Download);
        };
        var playQuality = new MenuItem("播放音质");
        playQuality.Popup += (_, _) =>
        {
            var song = FocusedItem<MusicInfo>(list);
            _ = RefreshQualitySubmenuAsync(
                playQuality,
                list,
                song,
                "播放音质",
                choice => PlayQualityAsync(choice.Song, choice.Quality, OriginForList(list)),
                "context-play-quality:" + song?.Id,
                QualityMenuKind.Play);
        };
        menu.MenuItems.Add(qualityDownload);
        menu.MenuItems.Add(playQuality);
        var albumDownload = new MenuItem("下载整张专辑");
        albumDownload.Popup += (_, _) =>
        {
            var focusedAlbum = FocusedItem<MusicInfo>(list);
            albumDownload.Enabled = focusedAlbum is not null && focusedAlbum.IsAlbum && _openAlbum?.Id == focusedAlbum.Id && !_albumDownloadLoading;
        };
        albumDownload.Click += async (_, _) => await DownloadAlbumAsync();
        menu.MenuItems.Add(albumDownload);
        var batchDownload = new MenuItem("批量选择下载", (_, _) => OpenBatchSelection(list));
        menu.Popup += (_, _) =>
        {
            batchDownload.Enabled = WinFormsUi.Songs(list).Count > 0;
            // Prepare both native submenus while the outer menu is being created. A submenu that waits
            focused = FocusedItem<MusicInfo>(list);
            _ = RefreshQualitySubmenuAsync(
                qualityDownload,
                list,
                focused,
                "下载单曲音质",
                selected => DownloadQualityAsync(selected.Song, selected.Quality),
                "download-quality:" + focused?.Id,
                QualityMenuKind.Download);
            _ = RefreshQualitySubmenuAsync(
                playQuality,
                list,
                focused,
                "播放音质",
                selected => PlayQualityAsync(selected.Song, selected.Quality, OriginForList(list)),
                "context-play-quality:" + focused?.Id,
                QualityMenuKind.Play);
        };
        menu.MenuItems.Add(batchDownload);
        menu.MenuItems.Add(new MenuItem("切换音源播放", (_, _) => OpenManualSource(WinFormsUi.Selected<MusicInfo>(list))));
        var localMenu = new MenuItem("用第三方播放器播放", (_, _) => PlayExternally(WinFormsUi.Selected<MusicInfo>(list)));
        menu.MenuItems.Add(localMenu);
        return menu;
    }

    internal void AssignSongContextMenu(ListBox list)
    {
        list.ContextMenu = BuildSongContextMenu(list);
    }

    private async Task ShowSongDetails(ListBox list)
    {
        var song = WinFormsUi.Selected<MusicInfo>(list);
        if (song is null) return;
        var lines = await BuildSongDetailLinesAsync(song);
        using var dialog = new SongDetailForm(song.Title, lines);
        dialog.ShowDialog(this);
    }

    private async Task<IReadOnlyList<string>> BuildSongDetailLinesAsync(MusicInfo song)
    {
        var lines = new List<string>
        {
            $"标题：{song.Title}",
            $"歌手：{song.Artist}",
            $"专辑：{song.Album}",
            $"时长：{song.Duration}",
            $"音质：{TranslateQualityLevel(song.Quality)}"
        };

        var isCurrent = string.Equals(_current?.Id, song.Id, StringComparison.Ordinal)
            && string.Equals(_current?.Source, song.Source, StringComparison.OrdinalIgnoreCase);
        var actual = isCurrent
            ? _currentPlaybackActualQuality ?? _services.Audio?.ActualQuality
            : null;
        if (!string.IsNullOrWhiteSpace(actual))
            lines.Add($"当前实际播放音质：{QualityCatalog.Label(actual)}");

        var requested = isCurrent ? _currentPlaybackQuality : null;
        if (!string.IsNullOrWhiteSpace(requested))
            lines.Add($"当前请求播放音质：{QualityCatalog.Label(requested)}");

        if (isCurrent && !string.IsNullOrWhiteSpace(_currentPlaybackInterfaceName))
            lines.Add($"当前音源接口：{_currentPlaybackInterfaceName}");

        if (_services.QualityCandidates is not null)
        {
            var candidates = await _services.QualityCandidates.GetCandidatesAsync(song);
            foreach (var group in candidates
                .GroupBy(item => item.InterfaceId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group =>
                {
                    var first = group.First();
                    return (string.Equals(first.InterfaceId, _currentPlaybackInterfaceId, StringComparison.Ordinal) ? 0 : 1, first.Priority, first.InterfaceName);
                }))
            {
                var first = group.First();
                var qualities = QualityCatalog.Order(group.Select(item => item.Quality))
                    .Select(QualityCatalog.Label);
                if (isCurrent
                    && string.Equals(first.InterfaceId, _currentPlaybackInterfaceId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(_currentPlaybackInterfaceName))
                {
                    lines.Add($"当前接口可提供音质：{string.Join("、", qualities)}");
                }
                else
                {
                    lines.Add($"接口：{first.InterfaceName}；可提供音质：{string.Join("、", qualities)}");
                }
            }
        }

        lines.Add($"音源：{TranslateSource(song.Source)}");
        return lines;
    }
    private async Task AddFavorites(IReadOnlyList<MusicInfo> songs)
    {
        if (_services.SavedPlaylists is null || songs.Count == 0) return;
        var result = await _services.SavedPlaylists.AddSongsAsync(songs);
        Announce(result.Message, "favorite", result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private async void DownloadList(IReadOnlyList<MusicInfo> songs, string? subdirectory = null)
    {
        if (_services.Downloads is null || songs.Count == 0) return;
        var quality = _services.Settings?.Current.Settings.DefaultDownloadQuality ?? "128k";
        // DownloadWorkflow owns the batch busy/terminal announcements; repeating them here would
        // speak the same outcome twice and could override an error with a success kind.
        await _services.Downloads.DownloadListAsync(new DownloadListRequest(
            songs,
            MainTabs.SelectedTab?.Text ?? "歌曲",
            quality,
            Subdirectory: subdirectory));
    }

    private bool _albumDownloadLoading;

    private async Task DownloadAlbumAsync()
    {
        var album = _openAlbum;
        if (album is null || _services.Downloads is null || _albumDownloadLoading || _searchLoading)
        {
            if (_searchLoading)
                Announce("专辑仍在加载，请稍后整张下载", "album-download", AnnouncementKind.Busy);
            return;
        }
        _albumDownloadLoading = true;
        _searchLoading = true;
        try
        {
            var snapshot = _browse.CurrentAlbumPage;
            if (snapshot is null || snapshot.Album.Id != album.Id)
            {
                Announce("专辑歌曲尚未加载，无法整张下载", "album-download", AnnouncementKind.Error);
                return;
            }

            var songs = snapshot.Items.ToList();
            var hasMore = HasMore(false, 0, snapshot.TotalItems, songs.Count);
            while (hasMore)
            {
                var result = await _browse.ActivateAlbumAsync(album, snapshot.Page + 1, append: true);
                if (!result.Succeeded || result.Value is null)
                {
                    Announce($"整张专辑下载失败：{result.Message}", "album-download", AnnouncementKind.Error);
                    return;
                }

                snapshot = _browse.CurrentAlbumPage;
                if (snapshot is null) break;
                songs = snapshot.Items.ToList();
                hasMore = HasMore(result.Value.HasMore, result.Value.Items.Count, snapshot.TotalItems, songs.Count);
            }

            if (songs.Count == 0)
            {
                Announce("专辑中没有可下载歌曲", "album-download", AnnouncementKind.Error);
                return;
            }

            await DownloadListAsyncWithQuality(songs, album.Title);
        }
        finally
        {
            _albumDownloadLoading = false;
            _searchLoading = false;
        }
    }

    private async Task DownloadListAsyncWithQuality(IReadOnlyList<MusicInfo> songs, string? subdirectory)
    {
        if (_services.Downloads is null || songs.Count == 0) return;
        var quality = _services.Settings?.Current.Settings.DefaultDownloadQuality ?? "128k";
        await _services.Downloads.DownloadListAsync(new DownloadListRequest(
            songs,
            string.IsNullOrWhiteSpace(subdirectory) ? "歌曲" : subdirectory,
            quality,
            Subdirectory: subdirectory));
    }

    private sealed class QualityMenuChoice
    {
        public MusicInfo Song { get; init; } = new();
        public string Quality { get; init; } = "128k";
    }

    private sealed class QualityMenuState
    {
        public int Generation { get; set; }
    }

    private enum QualityMenuKind
    {
        Play,
        Download
    }

    private readonly Dictionary<MenuItem, QualityMenuState> _qualityMenuStates = new();

    private async Task RefreshQualitySubmenuAsync(
        MenuItem parent,
        Control? owner,
        MusicInfo? song,
        string parentLabel,
        Func<QualityMenuChoice, Task> activate,
        string activityId,
        QualityMenuKind kind)
    {
        var ownerControl = owner ?? this;
        if (ownerControl.IsDisposed || IsDisposed) return;
        var state = _qualityMenuStates.TryGetValue(parent, out var existing)
            ? existing
            : _qualityMenuStates[parent] = new QualityMenuState();
        var generation = ++state.Generation;
        if (song is null)
        {
            parent.Text = parentLabel + "（无歌曲）";
            ShowQualityMessage(parent, "暂无可探测歌曲");
            return;
        }

        parent.Text = parentLabel;
        var probe = _services.QualityProbe;
        if (probe is null)
        {
            ShowQualityMessage(parent, "音质探测不可用");
            Announce("音质探测不可用", activityId, AnnouncementKind.Error);
            return;
        }

        // Only the qualities the interface actually declares belong in the menu. A cached round can
        // render synchronously here, which makes every later opening show the real list immediately.
        var cached = probe.GetCached(song);
        if (cached.Options.Count > 0)
            RenderQualityOptions(parent, song, cached.Options, activate, kind);
        else
            ShowQualityMessage(parent, "正在探测音质，请稍候…");

        try
        {
            var result = await probe.ProbeAsync(song);
            if (generation != state.Generation || ownerControl.IsDisposed || IsDisposed) return;
            RenderQualityOptions(parent, song, result.Options, activate, kind);

            if (kind == QualityMenuKind.Play
                && result.Options.All(option => option.Status != QualityAvailability.Playable))
                Announce(result.Message, activityId, AnnouncementKind.Error);
        }
        catch (Exception exception)
        {
            if (generation != state.Generation || ownerControl.IsDisposed || IsDisposed) return;
            ShowQualityMessage(parent, "探测失败，请重新打开菜单");
            Announce($"音质探测失败：{exception.GetType().Name}", activityId, AnnouncementKind.Error);
        }
    }

    private void RenderQualityOptions(
        MenuItem parent,
        MusicInfo song,
        IReadOnlyList<QualityOption> options,
        Func<QualityMenuChoice, Task> activate,
        QualityMenuKind kind)
    {
        parent.MenuItems.Clear();
        if (options.Count == 0)
        {
            ShowQualityMessage(parent, "当前音源没有声明可用的音质");
            return;
        }

        foreach (var option in options)
        {
            var quality = option.Quality;
            var chosen = kind == QualityMenuKind.Download
                ? option.DownloadQuality ?? option.Quality
                : option.Quality;
            var item = new MenuItem(string.Empty, (_, _) =>
            {
                if (!IsDisposed)
                    _ = activate(new QualityMenuChoice { Song = song, Quality = chosen });
            });
            ApplyQualityOption(item, option, quality, kind);
            parent.MenuItems.Add(item);
        }
    }

    private static void ShowQualityMessage(MenuItem parent, string text)
    {
        parent.MenuItems.Clear();
        parent.MenuItems.Add(new MenuItem(text, (_, _) => { }) { Enabled = false });
    }

    private static void ApplyQualityOption(
        MenuItem item,
        QualityOption? option,
        string quality,
        QualityMenuKind kind)
    {
        if (option is null)
        {
            item.Text = QualityCatalog.Label(quality) + "，不可用";
            item.Enabled = false;
            return;
        }

        var label = string.IsNullOrWhiteSpace(option.Label) ? QualityCatalog.Label(option.Quality) : option.Label;
        if (option.Status == QualityAvailability.Failed)
        {
            item.Text = label + "，探测失败，可重试";
            item.Enabled = false;
            return;
        }

        if (option.Status != QualityAvailability.Playable)
        {
            item.Text = label + "，不可用";
            item.Enabled = false;
            return;
        }

        var verb = kind == QualityMenuKind.Download ? "可下载" : "可播放";
        item.Text = $"{label}，{verb}，{option.InterfaceName ?? "当前接口"}";
        item.Enabled = true;
    }
    private async Task PlayQualityAsync(
        MusicInfo song,
        string quality,
        PlaybackQueueOrigin origin = PlaybackQueueOrigin.Search)
    {
        if (_services.Playback is null) return;
        try
        {
            var playbackQuality = QualityCatalog.Normalize(quality);
            var result = await _services.Playback.PlayAsync(new PlaybackRequest(
                [song], song, playbackQuality, origin, ForceResolve: true));
            if (result.Status is PlaybackResultStatus.Playing or PlaybackResultStatus.QueueCompleted)
            {
                _currentPlaybackQuality = playbackQuality;
                Announce(
                    $"正在按{QualityCatalog.Label(quality)}播放：{song.Title}",
                    "play-quality:" + song.Source + ":" + song.Id,
                    AnnouncementKind.Success);
            }
            else if (result.Status == PlaybackResultStatus.Failed)
            {
                Announce("所选音质播放失败", "play-quality:" + song.Source + ":" + song.Id, AnnouncementKind.Error);
            }
        }
        catch (Exception exception)
        {
            Announce(
                $"所选音质播放失败：{exception.GetType().Name}",
                "play-quality:" + song.Source + ":" + song.Id,
                AnnouncementKind.Error);
        }
    }

    private bool DownloadCurrentPlayingQuality()
    {
        if (_current is null)
        {
            Announce("当前没有正在播放的歌曲", "f2-download", AnnouncementKind.Status);
            return false;
        }
        if (_services.Downloads is null)
        {
            Announce("下载功能不可用", "f2-download", AnnouncementKind.Error);
            return false;
        }

        var quality = QualityCatalog.Normalize(
            string.IsNullOrWhiteSpace(_currentPlaybackQuality)
                ? _services.Playback?.DefaultQuality
                : _currentPlaybackQuality);
        if (string.IsNullOrWhiteSpace(quality))
        {
            Announce("当前播放音质未知", "f2-download:" + _current.Id, AnnouncementKind.Error);
            return false;
        }

        _ = DownloadQualityAsync(_current, quality);
        return true;
    }
    private PlaybackQueueOrigin OriginForList(ListBox list) => list switch
    {
        var value when ReferenceEquals(value, CategorySongs) => PlaybackQueueOrigin.Category,
        var value when ReferenceEquals(value, RankingSongs) => PlaybackQueueOrigin.Ranking,
        _ => PlaybackQueueOrigin.Search
    };

    private async Task DownloadQualityAsync(MusicInfo song, string quality)
    {
        if (_services.Downloads is null) return;
        try
        {
            await _services.Downloads.DownloadAsync(new DownloadRequest(song, quality));
        }
        catch (Exception exception)
        {
            Announce("音质下载任务创建失败", "download-quality:" + song.Id, AnnouncementKind.Error);
            WinFormsUi.Error(this, exception, "音质下载");
        }
    }

    private async void PlayExternally(MusicInfo? song)
    {
        if (song is null || _services.Downloads is null || _services.Settings is null) return;
        var path = _services.Settings.Current.Settings.ThirdPartyPlayerPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            Announce("请先在设置中选择第三方播放器", "external-playback", AnnouncementKind.Error);
            return;
        }
        var target = await _services.Downloads.PrepareExternalPlaybackAsync(song, _services.Settings.Current.Settings.DefaultPlayQuality);
        if (target is null)
        {
            Announce("无法准备第三方播放", "external-playback", AnnouncementKind.Error);
            return;
        }
        var result = await _services.Downloads.PlayExternallyAsync(new(path, song, target.Value));
        Announce(result.Message, "external-playback", result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private void OpenLocalFile()
    {
        using var dialog = new OpenFileDialog { Filter = "音频文件|*.mp3;*.flac;*.ape;*.m4a;*.wav;*.ogg;*.opus;*.aac;*.wma|所有文件|*.*", Multiselect = true, Title = "打开本地音频" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in dialog.FileNames)
        {
            var music = new MusicInfo { Id = "Local|" + path, Title = Path.GetFileNameWithoutExtension(path), Source = "Local", PlayUrl = path };
            _ = PlayMusicAsync(music);
        }
    }

    private void OpenLyrics() => OpenLyrics(_current);

    private void OpenLyrics(MusicInfo? music)
    {
        if (_services.Windows is null) return;
        if (music is not null) _ = _services.Lyrics?.LoadAsync(music);
        if (BringToolToFront<LyricsForm>()) return;
        _services.Windows.CreateLyrics(HandleHotkey).Show(this);
    }

    private void OpenUserGuide()
    {
        if (BringToolToFront<UserGuideForm>()) return;
        _services.Windows?.CreateUserGuide(this);
    }

    private void OpenManualSource(MusicInfo? music = null)
    {
        if (_services.Windows is null) return;
        var target = music ?? _current;
        if (BringToolToFront<ManualSourceSwitchForm>(form => form.ShowTarget(target))) return;
        _services.Windows.CreateManualSource(this, HandleHotkey, _services.ManualSources, target).Show(this);
    }

    private void OpenTool<T>(Func<T?> create) where T : Form
    {
        try
        {
            if (BringToolToFront<T>()) return;
            var window = create();
            window?.Show(this);
        }
        catch (Exception ex)
        {
            WinFormsUi.Error(this, ex, "打开工具窗口");
        }
    }

    /// <summary>
    /// Brings an already open tool window of this kind forward instead of creating a second one, which is
    /// what ADR 0002 promises for every tool. A duplicated 下载管理 or 歌词 window subscribes to the same
    /// live list twice, so the user hears the same result twice and gets two identical Alt+Tab targets,
    /// and a duplicated 音源管理 or 使用说明 window is a second copy of the same thing to tell apart.
    /// Hidden members of the window group become visible again because the command asked for this window.
    /// </summary>
    private bool BringToolToFront<TForm>(Action<TForm>? onReuse = null) where TForm : Form
    {
        var open = OwnedForms.FirstOrDefault(form => !form.IsDisposed && form is TForm);
        if (open is null) return false;
        if (!open.Visible) open.Visible = true;
        onReuse?.Invoke((TForm)open);
        open.Activate();
        return true;
    }

    private void ShowSettings(Hotkey? reconfigure)
    {
        if (_services.Windows is null) return;
        var previousFocus = ActiveControl;
        var window = _services.Windows.CreateSettings(this, HandleHotkey, reconfigure);
        window.ShowDialog(this);
        previousFocus?.Focus();
    }

    /// <summary>
    /// Single entry point for hotkeys raised by child windows. It routes through the same
    /// configuration-driven lookup as ProcessCmdKey so a rebound key behaves identically wherever it
    /// is pressed, and it returns false for keys it did not handle so the window keeps its own keys,
    /// such as Escape closing the lyrics reader.
    /// </summary>
    private bool HandleHotkey(System.Windows.Forms.KeyEventArgs args)
    {
        if (IsHotkeySuppressed(IntPtr.Zero)) return false;
        if (_services.Settings is null)
        {
            ExecuteLocalHotkey(args.KeyCode);
            return false;
        }
        return ExecuteConfiguredHotkey(args);
    }

    private void ExecuteHotkey(string actionId)
    {
        switch (actionId)
        {
            case HotkeyActions.PlayPause: TogglePlayback(); break;
            case HotkeyActions.Stop: StopPlayback(); break;
            case HotkeyActions.Previous: _ = MovePlayback(true); break;
            case HotkeyActions.Next: _ = MovePlayback(false); break;
            case HotkeyActions.Rewind: Seek(-10); break;
            case HotkeyActions.Forward: Seek(10); break;
            case HotkeyActions.VolumeDown: ChangeVolume(-VolumeStep); break;
            case HotkeyActions.VolumeUp: ChangeVolume(VolumeStep); break;
            case HotkeyActions.LyricDelay: _services.Lyrics?.AdjustOffset(500); break;
            case HotkeyActions.LyricAdvance: _services.Lyrics?.AdjustOffset(-500); break;
            case HotkeyActions.TogglePlayMode: TogglePlayMode(); break;
            case HotkeyActions.Download: DownloadCurrentPlayingQuality(); break;
            case HotkeyActions.SwitchPreviousOutputDevice: SwitchDevice(-1); break;
            case HotkeyActions.SwitchNextOutputDevice: SwitchDevice(1); break;
            case HotkeyActions.ToggleWindow: ToggleVisibility(); break;
            case HotkeyActions.ToggleVoiceAnnouncement: _services.Announcements?.ToggleVoiceAnnouncement(); break;
            case HotkeyActions.ToggleLyricAnnouncement: _services.Announcements?.ToggleLyricAnnouncement(); break;
        }
    }

    /// <summary>
    /// Resolves one key against the configured bindings. A local binding wins; a global binding whose
    /// native registration is not live runs here as a fallback; a natively registered global binding
    /// is deliberately left to the message window so the action runs exactly once instead of twice.
    /// </summary>
    private bool ExecuteConfiguredHotkey(KeyEventArgs args)
    {
        var settings = _services.Settings;
        if (settings is null) return false;
        var key = KeyboardKeys.FromVirtualKey((uint)(args.KeyCode & Keys.KeyCode));
        if (key == PlatformKey.None) return false;
        var modifiers = ModifiersOf(args);
        var hotkey = settings.MatchLocalHotkey(key, modifiers) ?? settings.MatchFallback(key, modifiers);
        if (hotkey is null) return false;
        ExecuteHotkey(hotkey.ActionId);
        return true;
    }

    private static PlatformModifiers ModifiersOf(KeyEventArgs args)
    {
        var modifiers = PlatformModifiers.None;
        if ((args.KeyData & Keys.Control) != 0) modifiers |= PlatformModifiers.Control;
        if ((args.KeyData & Keys.Alt) != 0) modifiers |= PlatformModifiers.Alt;
        if ((args.KeyData & Keys.Shift) != 0) modifiers |= PlatformModifiers.Shift;
        return modifiers;
    }

    /// <summary>
    /// Keeps hotkeys silent while a writable text box owns the keyboard, so typing in the search box
    /// or a settings path never fires an action. Read-only readers such as the lyrics window keep
    /// their playback keys, and Escape is handled by each window before this check runs.
    /// </summary>
    private static bool IsHotkeySuppressed(IntPtr messageTarget)
    {
        var focused = messageTarget != IntPtr.Zero ? Control.FromHandle(messageTarget) : null;
        if (focused is null)
        {
            var handle = GetFocus();
            focused = handle == IntPtr.Zero ? null : Control.FromHandle(handle);
        }
        return focused is TextBoxBase { ReadOnly: false };
    }

    /// <summary>
    /// Legacy table for shells composed without a SettingsWorkflow (deterministic UI tests). Production
    /// shells resolve every local key through the configured bindings above. The return value says whether
    /// the key belonged to a command: several of these function keys are also menu shortcuts, and letting
    /// the base window process a command that has already run would perform it twice.
    /// </summary>
    private bool ExecuteLocalHotkey(Keys key)
    {
        switch (key)
        {
            case Keys.F5: TogglePlayback(); return true;
            case Keys.F6: StopPlayback(); return true;
            case Keys.F3: _ = MovePlayback(true); return true;
            case Keys.F4: _ = MovePlayback(false); return true;
            case Keys.F7: Seek(-10); return true;
            case Keys.F8: Seek(10); return true;
            case Keys.F9: ChangeVolume(-VolumeStep); return true;
            case Keys.F11: SwitchDevice(-1); return true;
            case Keys.F12: SwitchDevice(1); return true;
            case Keys.F1: TogglePlayMode(); return true;
            case Keys.F2: return DownloadCurrentPlayingQuality();
            case Keys.Up when ModifierKeys == Keys.Control: ChangeTabPage(Math.Max(0, MainTabs.SelectedIndex - 1)); return true;
            case Keys.Down when ModifierKeys == Keys.Control: ChangeTabPage(Math.Min(MainTabs.TabPages.Count - 1, MainTabs.SelectedIndex + 1)); return true;
            default: return false;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Back && HandleBrowseBack()) return true;
        if (keyData == (Keys.Control | Keys.F))
        {
            SelectTab(0);
            SearchText.Focus();
            return true;
        }
        if (keyData == Keys.Escape)
        {
            if (SearchText.Focused && SearchText.Text.Length > 0) ClearSearch();
            else _services.Desktop?.ExecuteAsync(DesktopCommand.Escape);
            return true;
        }
        if (keyData == (Keys.Control | Keys.R))
        {
            switch (MainTabs.SelectedIndex)
            {
                case 0: _ = SearchAsync(true); break;
                case 1: _ = LoadSelectedSavedAsync(); break;
                case 2: LoadCategories(); break;
                default: LoadRankings(); break;
            }
            return true;
        }
        if (keyData == (Keys.Alt | Keys.M)) { SelectTab(0); SearchSource.Focus(); return true; }
        if (keyData == (Keys.Alt | Keys.C)) { SelectTab(2); return true; }
        if (keyData == (Keys.Alt | Keys.P)) { SelectTab(1); return true; }
        if (keyData == (Keys.Alt | Keys.S)) { CurrentSongList()?.Focus(); return true; }
        if (keyData == (Keys.Alt | Keys.R)) { SelectTab(3); RankingList.Focus(); return true; }
        if (keyData == (Keys.Control | Keys.Up))
        {
            ChangeTabPage(Math.Max(0, MainTabs.SelectedIndex - 1));
            return true;
        }
        if (keyData == (Keys.Control | Keys.Down))
        {
            ChangeTabPage(Math.Min(MainTabs.TabPages.Count - 1, MainTabs.SelectedIndex + 1));
            return true;
        }
        if (_services.Settings is null)
        {
            if (keyData == Keys.F10)
            {
                ChangeVolume(VolumeStep);
                return true;
            }
            if (ExecuteLocalHotkey(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
        if (IsHotkeySuppressed(msg.HWnd)) return base.ProcessCmdKey(ref msg, keyData);
        if (ExecuteConfiguredHotkey(new KeyEventArgs(keyData))) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    private void AttachSongList(ListBox list)
    {
        AssignSongContextMenu(list);
        list.KeyDown += (_, e) =>
        {
            if (HandleSongListSpace(list, e)) return;
            if (e.Shift && e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                OpenLyrics(WinFormsUi.Selected<MusicInfo>(list));
                return;
            }
            if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = ActivateSong(list); }
        };
    }

    private void DetachSongList(ListBox list)
    {
        list.ContextMenu = null;
    }

    private void ToggleVisibility()
    {
        if (Visible) _services.Desktop?.ExecuteAsync(DesktopCommand.Escape);
        else _services.Desktop?.ExecuteAsync(DesktopCommand.Restore);
    }

    /// <summary>
    /// Brings the window back when a second launch of the same application is detected. Hiding to the tray
    /// minimises the shell and removes its taskbar button, so flipping Visible alone left a keyboard-only
    /// user with a window that was on screen but unreachable; this runs the same restore as the tray menu.
    /// </summary>
    internal void RestoreForSecondLaunch()
    {
        if (_services.Desktop is { } desktop)
        {
            _ = desktop.ExecuteAsync(DesktopCommand.Restore);
            return;
        }

        ShowInTaskbar = true;
        Visible = true;
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private async Task<bool> RequestExit()
    {
        if (_services.Desktop is not null)
        {
            var result = await _services.Desktop.ExecuteAsync(DesktopCommand.Exit);
            return result.State == DesktopLifecycleState.Closed;
        }
        Close();
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The hide-on-close preference only governs the user closing the window. An explicit exit
        // (tray menu, Ctrl+Q, Alt+F4 after the window is gone) must always be able to close it,
        // otherwise the message loop never ends and the pending save never runs.
        if (e.CloseReason == CloseReason.UserClosing
            && _services.Desktop?.AllowWindowClose == false
            && _services.Settings?.Current.Settings.HideToTrayOnAltF4 == true)
        {
            e.Cancel = true;
            _services.Desktop?.ExecuteAsync(DesktopCommand.Escape);
            return;
        }
        _services.Desktop?.PrepareForShutdown();
        base.OnFormClosing(e);
    }
}
