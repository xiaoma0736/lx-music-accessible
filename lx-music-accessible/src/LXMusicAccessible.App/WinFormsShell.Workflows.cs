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
            var query = SearchText.Text.Trim();
            _browse.SelectSearch(query, SearchTypeName(), SourceId(SearchSource.Text));
            await _browse.RecordSearchAsync(query);
        }
        var result = await _browse.ActivateSearchAsync(!reset);
        if (result.Succeeded && result.Value is not null)
        {
            _searchSongs = result.Value.Items.ToList();
            WinFormsUi.FillSongs(SearchResults, _searchSongs);
            Announce(result.Message, "search-results");
            if (ContainsFocus) SearchResults.Focus();
        }
        else if (!string.IsNullOrWhiteSpace(result.Message))
        {
            Announce(result.Message, "search-results", AnnouncementKind.Error);
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
            var albumResult = await _browse.ActivateAlbumAsync(song);
            if (albumResult.Succeeded && albumResult.Value is not null)
            {
                _searchSongs = albumResult.Value.ToList();
                WinFormsUi.FillSongs(list, _searchSongs);
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
        ShowPlayback(music);
        Announce(result.Status == PlaybackResultStatus.Playing ? $"正在播放：{music.Artist}，{music.Title}" : $"播放失败：{result.Message}", "playback-song", result.Status is PlaybackResultStatus.Playing or PlaybackResultStatus.QueueCompleted ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private void ShowPlayback(MusicInfo music)
    {
        _current = music;
        CurrentTitle.Text = music.Title;
        CurrentArtist.Text = music.Artist;
        CurrentAlbum.Text = music.Album;
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

    private void RenderSaved(SavedPlaylistSnapshot snapshot, string message, AnnouncementKind kind = AnnouncementKind.Success)
    {
        OnlinePlaylists.Items.Clear();
        foreach (var playlist in snapshot.Playlists)
            OnlinePlaylists.Items.Add(new ObjectBox<Playlist>(playlist.Name, playlist));
        if (snapshot.Selected is not null)
        {
            var index = OnlinePlaylists.Items.Cast<ObjectBox<Playlist>>().ToList().FindIndex(item => item.Value.Id == snapshot.Selected.Id);
            OnlinePlaylists.SelectedIndex = index;
        }
        _favoriteSongs = snapshot.Songs.ToList();
        WinFormsUi.FillSongs(FavoriteSongs, _favoriteSongs);
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

    private void LoadCategoryPlaylists(int page = 1)
    {
        var category = WinFormsUi.Selected<PlaylistCategory>(CategoryList);
        if (category is null) return;
        var requestedPage = Math.Max(1, page);
        _browse.SelectCategory(category);
        _ = RunAsync(async () => await _browse.ActivateCategoryAsync(requestedPage), result =>
        {
            if (!result.Succeeded || result.Value is null)
            {
                SetBrowseStatus(result.Message, AnnouncementKind.Error);
                return;
            }
            CategoryPlaylists.Items.Clear();
            foreach (var playlist in result.Value.Items)
                CategoryPlaylists.Items.Add(new ObjectBox<Playlist>(playlist.Name, playlist));
            PageControl("CategoryPlaylistPreviousPage", "CategoryPlaylistNextPage", "CategoryPlaylistPage", requestedPage, result.Value.TotalItems);
            SetBrowseStatus(result.Message, AnnouncementKind.Success);
            if (ContainsFocus) CategoryPlaylists.Focus();
        }, "加载歌单");
    }

    private async Task LoadCategorySongs(int page = 1)
    {
        var playlist = WinFormsUi.Selected<Playlist>(CategoryPlaylists);
        if (playlist is null) return;
        var requestedPage = Math.Max(1, page);
        _browse.SelectPlaylist(playlist);
        var result = await _browse.ActivatePlaylistAsync(requestedPage);
        if (result.Succeeded && result.Value is not null)
        {
            _categorySongs = result.Value.Items.ToList();
            WinFormsUi.FillSongs(CategorySongs, _categorySongs);
            PageControl("CategorySongPreviousPage", "CategorySongNextPage", "CategorySongPage", requestedPage, result.Value.TotalItems);
            SetBrowseStatus(result.Message, AnnouncementKind.Success);
            if (ContainsFocus) CategorySongs.Focus();
        }
        else SetBrowseStatus(result.Message, AnnouncementKind.Error);
    }

    private int GetCurrentCategoryPage() => _browse.CurrentCategoryPage?.Page ?? 1;
    private int GetCurrentSongPage() => _browse.CurrentPlaylistPage?.Page ?? 1;

    private void PageControl(string previousName, string nextName, string pageName, int page, int totalItems)
    {
        var pageSize = 20;
        var totalPages = totalItems > 0 ? Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize)) : page + 1;
        Controls.Find(previousName, true)[0].Enabled = page > 1;
        Controls.Find(nextName, true)[0].Enabled = page < totalPages;
        Controls.Find(pageName, true)[0].Text = $"第 {page} 页";
    }

    private void SetBrowseStatus(string message, AnnouncementKind kind)
    {
        if (!IsDisposed && !string.IsNullOrWhiteSpace(message)) StatusText.Text = message;
        Announce(message, "browse-status", kind);
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

    private async Task LoadRankingSongs()
    {
        var ranking = WinFormsUi.Selected<Ranking>(RankingList);
        if (ranking is null) return;
        _browse.SelectRanking(ranking);
        var result = await _browse.ActivateRankingAsync();
        if (result.Succeeded && result.Value is not null)
        {
            _rankingSongs = result.Value.Items.ToList();
            WinFormsUi.FillSongs(RankingSongs, _rankingSongs);
            Announce(result.Message, "ranking-songs");
            if (ContainsFocus) RankingSongs.Focus();
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

    private void TogglePlayback()
    {
        if (_services.Playback is null) return;
        if (_services.Playback.IsPlaying) { _services.Playback.Pause(); Announce("已暂停", "playback-state"); }
        else _ = RunAsync(async () => await _services.Playback.ReplayAsync(), _ => { }, "恢复播放");
    }

    private void StopPlayback()
    {
        _services.Playback?.Stop();
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
        _services.Settings?.Edit(settings => settings.DefaultPlayMode = _playMode);
        Announce(PlayModeName(_playMode), "play-mode", AnnouncementKind.Status, AnnouncementSpeech.Always);
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
        if (CurrentTitle.Text != "未播放") _ = _services.Lyrics?.LoadAsync(_current!);
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

    private bool HandleHotkey(System.Windows.Forms.KeyEventArgs args)
    {
        ExecuteLocalHotkey(args.KeyCode);
        return true;
    }

    private void ExecuteHotkey(string actionId)
    {
        switch (actionId)
        {
            case "play-pause": TogglePlayback(); break;
            case "stop": StopPlayback(); break;
            case "previous": _ = MovePlayback(true); break;
            case "next": _ = MovePlayback(false); break;
            case "volume-down": ChangeVolume(-0.1); break;
            case "volume-up": ChangeVolume(0.1); break;
            case "lyrics-delay": _services.Lyrics?.AdjustOffset(500); break;
            case "lyrics-advance": _services.Lyrics?.AdjustOffset(-500); break;
            case "play-mode": TogglePlayMode(); break;
            case "download-current": DownloadSelected(); break;
        }
    }

    private void ExecuteLocalHotkey(Keys key)
    {
        switch (key)
        {
            case Keys.F5: TogglePlayback(); break;
            case Keys.F6: StopPlayback(); break;
            case Keys.F3: _ = MovePlayback(true); break;
            case Keys.F4: _ = MovePlayback(false); break;
            case Keys.F7: Seek(-10); break;
            case Keys.F8: Seek(10); break;
            case Keys.F9: ChangeVolume(-0.1); break;
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
        ExecuteLocalHotkey(keyData);
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void AttachSongList(ListBox list)
    {
        AssignSongContextMenu(list);
        list.KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; _ = ActivateSong(list); } };
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
        if (e.CloseReason == CloseReason.UserClosing && _services.Settings?.Current.Settings.HideToTrayOnAltF4 == true)
        {
            e.Cancel = true;
            _services.Desktop?.ExecuteAsync(DesktopCommand.Escape);
            return;
        }
        _services.Desktop?.PrepareForShutdown();
        base.OnFormClosing(e);
    }
}
