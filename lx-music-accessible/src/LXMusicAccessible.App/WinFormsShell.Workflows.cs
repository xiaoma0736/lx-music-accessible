using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>Explicit user-action handlers, browse workflows, playback and keyboard routing for the native shell.</summary>
public partial class WinFormsShell
{
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
            Announce(operation + "失败，请检查网络", operation);
            if (!IsDisposed) WinFormsUi.Error(this, ex, operation);
        }
    }

    private void SelectTab(int index)
    {
        MainTabs.SelectedIndex = index;
        Control focus = index switch
        {
            0 => SearchText,
            1 => FavoriteSongs.Items.Count > 0 ? FavoriteSongs : OnlinePlaylists,
            2 => CategoryList,
            _ => RankingList
        };
        focus.Focus();
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
            if (!WinFormsUi.Confirm(this, "确定要清空全部搜索历史吗？", "清空搜索历史")) return;
            var cleared = await _browse.ClearSearchHistoryAsync();
            Announce(cleared ? "搜索历史已清空" : "没有可清空的搜索历史", "search-history-clear");
            return;
        }
        var deleted = await _browse.DeleteSearchHistoryAsync(SearchText.Text);
        Announce(deleted is null ? "没有可删除的当前搜索记录" : "当前搜索记录已删除", "search-history-delete");
    }

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
        try
        {
            if (reset)
            {
                var query = SearchText.Text.Trim();
                _browse.SelectSearch(query, SearchTypeName(), SourceId(SearchSource.Text));
                await _browse.RecordSearchAsync(query);
            }
            var result = await _browse.ActivateSearchAsync(!reset);
            if (result.Succeeded && result.Value is not null)
            {
                _searchSongs = result.Value.Items.ToList();
                var selected = WinFormsUi.Selected<MusicInfo>(SearchResults)?.Id;
                WinFormsUi.FillSongs(SearchResults, _searchSongs);
                RestoreSelection(SearchResults, _searchSongs, selected);
                UpdateLoadMore(SearchLoadMore, SearchResults, result.Value.HasMore);
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
        var requestedPage = (_browse.CurrentAlbumPage?.Page ?? 0) + 1;
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
                UpdateLoadMore(SearchLoadMore, SearchResults, HasMore(result.Value.HasMore, result.Value.Items.Count, result.Value.TotalItems, songs.Count));
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
        var song = WinFormsUi.Selected<MusicInfo>(list);
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
                    UpdateLoadMore(SearchLoadMore, SearchResults, HasMore(albumResult.Value.HasMore, albumResult.Value.Items.Count, albumResult.Value.TotalItems, songs.Count));
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
        var result = await _services.Playback.PlayAsync(new PlaybackRequest(
            queue,
            music,
            _services.Settings?.Current.Settings.DefaultPlayQuality ?? _services.Playback.DefaultQuality,
            origin,
            Trigger: PlaybackTrigger.UserInitiated));
        if (result.Status is PlaybackResultStatus.Playing or PlaybackResultStatus.QueueCompleted)
            ShowPlayback(music);
        Announce(result.Status == PlaybackResultStatus.Playing ? $"正在播放：{music.Artist}，{music.Title}" : $"播放失败：{result.Message}", "playback-song", result.Status is PlaybackResultStatus.Playing or PlaybackResultStatus.QueueCompleted ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private void ShowPlayback(MusicInfo music)
    {
        _current = music;
        CurrentTitle.Text = music.Title;
        CurrentArtist.Text = music.Artist;
        CurrentAlbum.Text = music.Album;
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
        _savedSongsLoading = true;
        try
        {
            var selected = WinFormsUi.Selected<MusicInfo>(FavoriteSongs)?.Id;
            var result = await _services.SavedPlaylists.ActivateAsync(current.Page + 1, append: true);
            // A failed page is not the end of the list: keep the songs already loaded and leave the
            // control available so the user can retry the same page.
            if (result.Succeeded)
                RenderSaved(result.Snapshot, result.Message, AnnouncementKind.Success, append: true);
            else
                Announce(result.Message, "saved-playlists", AnnouncementKind.Error);
            RestoreSelection(FavoriteSongs, _favoriteSongs, selected);
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
        UpdateLoadMore(SavedPlaylistLoadMore, FavoriteSongs, snapshot.HasMore);
        Announce(message, "saved-playlists", kind);
    }

    private async Task ManagePlaylistAsync(string prompt, Func<string?, Task<SavedPlaylistResult>> action)
    {
        if (_services.SavedPlaylists is null) return;
        var selected = WinFormsUi.Selected<Playlist>(OnlinePlaylists)?.Name;
        var value = WinFormsUi.AskText(this, prompt, prompt + "：", prompt.StartsWith("重命名", StringComparison.Ordinal) ? selected : null);
        if (string.IsNullOrWhiteSpace(value)) return;
        var result = await action(value);
        RenderSaved(result.Snapshot, result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private async Task DeleteSavedPlaylistAsync()
    {
        if (_services.SavedPlaylists is null) return;
        var playlist = WinFormsUi.Selected<Playlist>(OnlinePlaylists);
        if (playlist is null || !WinFormsUi.Confirm(this, $"确定要删除歌单“{playlist.Name}”吗？")) return;
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

    private async void ClearFavorites()
    {
        if (_services.SavedPlaylists is null || !WinFormsUi.Confirm(this, "确定要清空当前在线歌单中的全部歌曲吗？")) return;
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
            if (ContainsFocus) CategoryList.Focus();
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
                UpdateLoadMore(CategoryPlaylistsLoadMore, CategoryPlaylists, HasMore(result.Value.HasMore, result.Value.Items.Count, result.Value.TotalItems, items.Count));
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
                UpdateLoadMore(CategorySongsLoadMore, CategorySongs, HasMore(result.Value.HasMore, result.Value.Items.Count, result.Value.TotalItems, _categorySongs.Count));
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
    private static void UpdateLoadMore(Button button, ListBox list, bool hasMore)
    {
        var restoreFocus = !hasMore && button.Focused;
        button.Enabled = hasMore;
        if (restoreFocus && !list.IsDisposed && list.CanFocus) list.Focus();
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
            if (ContainsFocus) RankingList.Focus();
        }, "加载排行榜");
    }

    private Task LoadRankingSongs() => LoadRankingSongsAsync(1, append: false);

    private Task LoadMoreRankingSongsAsync() => LoadRankingSongsAsync((_browse.CurrentRankingPage?.Page ?? 0) + 1, append: true);

    private async Task LoadRankingSongsAsync(int page, bool append)
    {
        var ranking = WinFormsUi.Selected<Ranking>(RankingList);
        if (ranking is null || _rankingSongsLoading || (append && !RankingSongsLoadMore.Enabled)) return;
        var requestedPage = Math.Max(1, page);
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
                UpdateLoadMore(RankingSongsLoadMore, RankingSongs, HasMore(result.Value.HasMore, result.Value.Items.Count, result.Value.TotalItems, _rankingSongs.Count));
                SetBrowseStatus(result.Message);
                if (!append && ContainsFocus) RankingSongs.Focus();
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
        await FavoriteRemotePlaylist(playlist?.Name, SourceId(CategorySource.Text), playlist?.Id, PlaylistType.Recommend);
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
            Announce("已暂停", "playback-state");
        }
        else
        {
            _ = RunAsync(async () => await _services.Playback.ReplayAsync(), _ => { }, "恢复播放");
        }
    }

    private void StopPlayback()
    {
        _services.Playback?.Stop();
        _current = null;
        CurrentTitle.Text = "未播放";
        Text = BaseTitle;
        PlaybackInfoText.Text = "未播放";
        Announce("已停止播放", "playback-state");
    }

    private async Task MovePlayback(bool previous)
    {
        if (_services.Playback is null) return;
        var result = previous ? await _services.Playback.PreviousAsync() : await _services.Playback.NextAsync();
        if (result.Music is not null) ShowPlayback(result.Music);
        Announce(result.Status == PlaybackResultStatus.Playing || result.Status == PlaybackResultStatus.QueueCompleted ? "已切换歌曲" : result.Message, "playback-song", result.Status is PlaybackResultStatus.Playing or PlaybackResultStatus.QueueCompleted ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private void Seek(double seconds)
    {
        if (_services.Audio is null) return;
        var target = _services.Audio.Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > _services.Audio.Duration) target = _services.Audio.Duration;
        _services.Audio.SeekTo(target);
    }

    private void ChangeVolume(double delta)
    {
        if (_services.Audio is null) return;
        var volume = Math.Max(0.0, Math.Min(1.0, _services.Audio.Volume + delta));
        _services.Audio.SetVolume(volume);
        Announce($"音量 {volume:0%}", "volume");
    }

    private void SwitchDevice(int direction)
    {
        if (_services.Audio is null) return;
        var name = direction < 0 ? _services.Audio.SwitchToPreviousOutputDevice() : _services.Audio.SwitchToNextOutputDevice();
        Announce(string.IsNullOrWhiteSpace(name) ? "没有更多播放设备" : $"播放设备：{name}", "audio-device");
    }

    private void TogglePlayMode()
    {
        _playMode = (PlayMode)(((int)_playMode + 1) % 4);
        CurrentPlayModeText.Text = "播放模式：" + PlayModeName(_playMode);
        var edit = _services.Settings?.Edit(settings => settings.DefaultPlayMode = _playMode);
        Announce(PlayModeName(_playMode), "play-mode", AnnouncementKind.Status, AnnouncementSpeech.Always);
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

    private async void DownloadSelected()
    {
        var song = _current ?? WinFormsUi.Selected<MusicInfo>(CurrentSongList());
        if (song is null || _services.Downloads is null) return;
        try
        {
            await _services.Downloads.DownloadAsync(new DownloadRequest(song, _services.Settings?.Current.Settings.DefaultDownloadQuality ?? "128k"));
            Announce($"已加入下载：{song.Title}", "download:" + song.Id);
        }
        catch (Exception ex)
        {
            Announce("下载任务创建失败", "download:" + song.Id, AnnouncementKind.Error);
            WinFormsUi.Error(this, ex, "创建下载");
        }
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

    private void AssignSongContextMenu(ListBox list)
    {
        var menu = new ContextMenu();
        menu.MenuItems.Add(new MenuItem("播放", (_, _) => _ = ActivateSong(list)));
        menu.MenuItems.Add(new MenuItem("查看详情", (_, _) => ShowSongDetails(list)));
        menu.MenuItems.Add(new MenuItem("收藏歌曲", (_, _) => _ = AddFavorites(WinFormsUi.Songs(list))));
        menu.MenuItems.Add(new MenuItem("下载", (_, _) => DownloadList(WinFormsUi.Songs(list))));
        menu.MenuItems.Add(new MenuItem("切换音源播放", (_, _) => OpenManualSource(WinFormsUi.Selected<MusicInfo>(list))));
        var localMenu = new MenuItem("用第三方播放器播放", (_, _) => PlayExternally(WinFormsUi.Selected<MusicInfo>(list)));
        menu.MenuItems.Add(localMenu);
        list.ContextMenu = menu;
    }

    private void ShowSongDetails(ListBox list)
    {
        var song = WinFormsUi.Selected<MusicInfo>(list);
        if (song is null) return;
        var lines = new[] { $"标题：{song.Title}", $"歌手：{song.Artist}", $"专辑：{song.Album}", $"时长：{song.Duration}", $"音源：{song.Source}" };
        using var dialog = new SongDetailForm(song.Title, lines);
        dialog.ShowDialog(this);
    }

    private async Task AddFavorites(IReadOnlyList<MusicInfo> songs)
    {
        if (_services.SavedPlaylists is null || songs.Count == 0) return;
        var result = await _services.SavedPlaylists.AddSongsAsync(songs);
        Announce(result.Message, "favorite", result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private async void DownloadList(IReadOnlyList<MusicInfo> songs)
    {
        if (_services.Downloads is null || songs.Count == 0) return;
        var quality = _services.Settings?.Current.Settings.DefaultDownloadQuality ?? "128k";
        var result = await _services.Downloads.DownloadListAsync(new DownloadListRequest(songs, MainTabs.SelectedTab?.Text ?? "歌曲", quality));
        Announce(result.Message, "download-list:" + result.Requested, AnnouncementKind.Success);
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
        using var dialog = new OpenFileDialog { Filter = "音频文件|*.mp3;*.flac;*.ape;*.m4a;*.wav;*.ogg|所有文件|*.*", Multiselect = true, Title = "打开本地音频" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in dialog.FileNames)
        {
            var music = new MusicInfo { Id = "Local|" + path, Title = Path.GetFileNameWithoutExtension(path), Source = "Local", PlayUrl = path };
            _ = PlayMusicAsync(music);
        }
    }

    private void OpenLyrics()
    {
        if (_services.Windows is null) return;
        var window = _services.Windows.CreateLyrics(HandleHotkey);
        if (_current is not null) _ = _services.Lyrics?.LoadAsync(_current);
        window.Show(this);
    }

    private void OpenManualSource(MusicInfo? music = null)
    {
        if (_services.Windows is null) return;
        var window = _services.Windows.CreateManualSource(this, HandleHotkey, _services.ManualSources, music ?? _current);
        window.Show(this);
    }

    private void OpenTool<T>(Func<T?> create) where T : Form
    {
        try
        {
            var window = create();
            window?.Show(this);
        }
        catch (Exception ex)
        {
            WinFormsUi.Error(this, ex, "打开工具窗口");
        }
    }

    private void ShowSettings(Hotkey? reconfigure)
    {
        if (_services.Windows is null) return;
        var previousFocus = ActiveControl;
        var window = _services.Windows.CreateSettings(this, HandleHotkey, suppress => { }, reconfigure);
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
        return ExecuteConfiguredHotkey(args.KeyCode);
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
            case HotkeyActions.Download: DownloadSelected(); break;
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
    private bool ExecuteConfiguredHotkey(Keys keyData)
    {
        var settings = _services.Settings;
        if (settings is null) return false;
        var key = KeyboardKeys.FromVirtualKey((uint)(keyData & Keys.KeyCode));
        if (key == PlatformKey.None) return false;
        var modifiers = ModifiersOf(keyData);
        var hotkey = settings.MatchLocalHotkey(key, modifiers) ?? settings.MatchFallback(key, modifiers);
        if (hotkey is null) return false;
        ExecuteHotkey(hotkey.ActionId);
        return true;
    }

    private static PlatformModifiers ModifiersOf(Keys keyData)
    {
        var modifiers = PlatformModifiers.None;
        if ((keyData & Keys.Control) != 0) modifiers |= PlatformModifiers.Control;
        if ((keyData & Keys.Alt) != 0) modifiers |= PlatformModifiers.Alt;
        if ((keyData & Keys.Shift) != 0) modifiers |= PlatformModifiers.Shift;
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

    private void ExecuteLocalHotkey(Keys key)
    {
        // Legacy table for shells composed without a SettingsWorkflow (deterministic UI tests).
        // Production shells resolve every local key through the configured bindings above.
        switch (key)
        {
            case Keys.F5: TogglePlayback(); break;
            case Keys.F6: StopPlayback(); break;
            case Keys.F3: _ = MovePlayback(true); break;
            case Keys.F4: _ = MovePlayback(false); break;
            case Keys.F7: Seek(-10); break;
            case Keys.F8: Seek(10); break;
            case Keys.F9: ChangeVolume(-VolumeStep); break;
            case Keys.F11: SwitchDevice(-1); break;
            case Keys.F12: SwitchDevice(1); break;
            case Keys.F1: TogglePlayMode(); break;
            case Keys.F2: DownloadSelected(); break;
            case Keys.Up when ModifierKeys == Keys.Control: MainTabs.SelectedIndex = Math.Max(0, MainTabs.SelectedIndex - 1); break;
            case Keys.Down when ModifierKeys == Keys.Control: MainTabs.SelectedIndex = Math.Min(MainTabs.TabPages.Count - 1, MainTabs.SelectedIndex + 1); break;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
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
        if (keyData == (Keys.Alt | Keys.S)) { SelectTab(MainTabs.SelectedIndex == 2 ? 2 : 3); (MainTabs.SelectedIndex == 2 ? (Control)CategorySongs : RankingSongs).Focus(); return true; }
        if (keyData == (Keys.Alt | Keys.R)) { SelectTab(3); RankingList.Focus(); return true; }
        if (keyData == (Keys.Control | Keys.Up))
        {
            MainTabs.SelectedIndex = Math.Max(0, MainTabs.SelectedIndex - 1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Down))
        {
            MainTabs.SelectedIndex = Math.Min(MainTabs.TabPages.Count - 1, MainTabs.SelectedIndex + 1);
            return true;
        }
        if (_services.Settings is null)
        {
            if (keyData == Keys.F10)
            {
                ChangeVolume(VolumeStep);
                return true;
            }
            ExecuteLocalHotkey(keyData);
            return base.ProcessCmdKey(ref msg, keyData);
        }
        if (IsHotkeySuppressed(msg.HWnd)) return base.ProcessCmdKey(ref msg, keyData);
        if (ExecuteConfiguredHotkey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    private void AttachSongList(ListBox list)
    {
        AssignSongContextMenu(list);
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = ActivateSong(list); } else if (e.KeyCode == Keys.Space) { e.Handled = true; TogglePlayback(); } };
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
