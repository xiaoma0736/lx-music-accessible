using LXMusicAccessible.Data;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Services;

public sealed record SavedPlaylistSnapshot(
    IReadOnlyList<Playlist> Playlists,
    Playlist? Selected,
    IReadOnlyList<MusicInfo> Songs,
    bool HasMore = false);

public sealed record SavedPlaylistResult(
    SavedPlaylistSnapshot Snapshot,
    string Message,
    BrowseFocusTarget FocusTarget = BrowseFocusTarget.KeepCurrent,
    bool Succeeded = true);

public sealed record RemotePlaylistFavorite(
    string Name,
    string Source,
    string RemoteId,
    PlaylistType Type);

/// <summary>
/// Owns saved-playlist selection and persistence. The window only presents snapshots
/// and forwards explicit create, rename, delete, add, remove, clear and activate intents.
/// </summary>
public sealed class SavedPlaylistWorkflow(
    IDataService data,
    MusicBrowseWorkflow browse)
{
    private readonly object _gate = new();
    private List<Playlist> _playlists = [];
    private Playlist? _selected;
    private IReadOnlyList<MusicInfo> _songs = [];
    private long _revision;

    public static string Describe(Playlist? playlist)
    {
        if (playlist is null) return "未选择歌单";
        return $"{playlist.Name}（{PlaylistKind(playlist)}）";
    }

    public static string PlaylistKind(Playlist playlist)
    {
        if (playlist.IsSnapshot) return "本地快照";
        if (playlist.IsLocal) return "本地歌曲";
        if (!string.IsNullOrWhiteSpace(playlist.RemoteId)) return "远程收藏";
        return "在线歌单";
    }

    public static bool CanDelete(Playlist? playlist) =>
        playlist is not null && playlist.Type != PlaylistType.System;

    public static string RenamePrompt(Playlist playlist)
    {
        if (playlist.IsLocal) return "重命名本地歌曲歌单";
        if (playlist.IsSnapshot) return "重命名本地快照歌单（重新导入时可能被远程名称覆盖）";
        if (!string.IsNullOrWhiteSpace(playlist.RemoteId)) return "重命名远程收藏（只修改本地显示名称）";
        return "重命名歌单";
    }

    public static string DeleteConfirmation(Playlist playlist)
    {
        if (playlist.IsSnapshot)
            return $"确定要删除本地快照歌单“{playlist.Name}”吗？将删除本地快照和歌曲列表，不影响远程原歌单。";
        if (!playlist.IsLocal && !string.IsNullOrWhiteSpace(playlist.RemoteId))
            return $"确定要删除远程收藏歌单“{playlist.Name}”吗？只删除本地收藏，不删除远程歌单。";
        if (playlist.IsLocal)
            return $"确定要删除本地歌曲歌单“{playlist.Name}”吗？歌曲将从本地歌单移除，不会删除磁盘上的音频文件。";
        return $"确定要删除在线歌单“{playlist.Name}”吗？";
    }

    public static string ClearConfirmation(Playlist playlist)
    {
        if (playlist.IsSnapshot)
            return $"确定要清空本地快照歌单“{playlist.Name}”吗？歌曲将从本地快照中永久移除。";
        if (!playlist.IsLocal && !string.IsNullOrWhiteSpace(playlist.RemoteId))
            return $"确定要清空远程收藏歌单“{playlist.Name}”吗？仅清空本地列表；下次进入可能从远程重新加载。";
        if (playlist.IsLocal)
            return $"确定要清空本地歌曲歌单“{playlist.Name}”吗？歌曲将从本地歌单移除，不会删除磁盘上的音频文件。";
        return $"确定要清空在线歌单“{playlist.Name}”吗？";
    }

    public SavedPlaylistSnapshot Current
    {
        get
        {
            lock (_gate) return Snapshot();
        }
    }

    public async Task<SavedPlaylistResult> LoadAsync()
    {
        var online = await data.GetFavoritePlaylistsAsync(local: false);
        var local = await data.GetFavoritePlaylistsAsync(local: true);
        var playlists = online.Concat(local).ToList();
        lock (_gate)
        {
            var selectedId = _selected?.Id;
            _playlists = playlists;
            _selected = playlists.FirstOrDefault(item => item.Id == selectedId)
                ?? playlists.FirstOrDefault(item => item.Type == PlaylistType.System && !item.IsLocal)
                ?? playlists.FirstOrDefault(item => item.Type == PlaylistType.System && item.IsLocal)
                ?? playlists.FirstOrDefault();
            _songs = [];
            _revision++;
            browse.SelectPlaylist(_selected);
            return new(Snapshot(), "在线收藏已刷新");
        }
    }

    public BrowseSelection Select(Playlist? playlist)
    {
        lock (_gate)
        {
            _selected = playlist is null
                ? null
                : _playlists.FirstOrDefault(item => item.Id == playlist.Id) ?? playlist;
            _songs = [];
            _revision++;
            return browse.SelectPlaylist(_selected);
        }
    }

    public async Task<SavedPlaylistResult> ActivateAsync(int page = 1, bool append = false)
    {
        Playlist? selected;
        long revision;
        lock (_gate)
        {
            selected = _selected;
            revision = _revision;
        }
        if (selected is null) return Failure("请先选择在线歌单");
        var remote = !selected.IsSnapshot && !string.IsNullOrWhiteSpace(selected.RemoteId);
        if (append && !remote) return Failure("该歌单没有更多歌曲");

        var focus = BrowseFocusTarget.SavedPlaylistSongs;
        var hasMore = false;
        IReadOnlyList<MusicInfo> songs = append ? [] : await data.GetPlaylistItemsAsync(selected.Id);
        if (remote && (append || songs.Count == 0))
        {
            var result = await browse.ActivateSavedPlaylistAsync(selected, page, append);
            if (!result.Succeeded || result.Value is null) return Failure(result.Message);
            songs = browse.CurrentSavedPlaylistPage?.Items ?? result.Value.Items;
            hasMore = result.Value.HasMore || songs.Count < result.Value.TotalItems;
            focus = result.FocusTarget;
        }

        lock (_gate)
        {
            if (revision != _revision || _selected?.Id != selected.Id)
                return Failure(string.Empty);
            _songs = songs.ToArray();
            var source = selected.IsSnapshot
                ? "本地快照"
                : selected.IsLocal
                    ? "本地歌曲列表"
                    : remote
                        ? songs.Count == 0 ? "远程" : "本地缓存"
                        : "本地歌单";
            string message;
            if (_songs.Count == 0 && source == "远程")
                message = $"已从远程加载：{Describe(selected)}；远程歌单暂无歌曲";
            else if (_songs.Count == 0)
                message = $"{selected.Name}暂无歌曲（{PlaylistKind(selected)}）";
            else if (source == "远程")
                message = $"已从远程加载：{Describe(selected)}";
            else
                message = $"已从{source}读取：{Describe(selected)}";
            return new(
                Snapshot() with { HasMore = hasMore },
                message,
                focus);
        }
    }

    public async Task<SavedPlaylistResult> CreateAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Failure("歌单名称不能为空");
        var playlist = new Playlist
        {
            Id = Guid.NewGuid().ToString(),
            Name = name.Trim(),
            Description = "在线收藏歌单",
            Type = PlaylistType.User,
            IsLocal = false
        };
        await data.SavePlaylistAsync(playlist);
        lock (_gate) _selected = playlist;
        var loaded = await LoadAsync();
        return loaded with { Message = $"已创建在线歌单：{playlist.Name}" };
    }

    public async Task<SavedPlaylistResult> RenameSelectedAsync(string name)
    {
        var selected = SelectedOrNull();
        if (selected is null) return Failure("请先选择在线歌单");
        if (string.IsNullOrWhiteSpace(name)) return Failure("名称不能为空");
        await data.RenamePlaylistAsync(selected.Id, name.Trim());
        var loaded = await LoadAsync();
        var kind = selected.IsSnapshot ? "本地快照歌单" : PlaylistKind(selected);
        var scope = selected.IsSnapshot
            ? "；重新导入时可能被远程名称覆盖"
            : !selected.IsLocal && !string.IsNullOrWhiteSpace(selected.RemoteId)
                ? "；只修改本地显示名称"
                : string.Empty;
        return loaded with { Message = $"{kind}已重命名为：{name.Trim()}{scope}" };
    }

    public async Task<SavedPlaylistResult> DeleteSelectedAsync()
    {
        var selected = SelectedOrNull();
        if (selected is null) return Failure("请先选择要删除的在线歌单");
        if (!CanDelete(selected))
            return Failure(selected.IsLocal ? "默认本地歌曲歌单不能删除" : "默认在线歌单不能删除");
        await data.DeletePlaylistAsync(selected.Id);
        lock (_gate) _selected = null;
        var loaded = await LoadAsync();
        return loaded with { Message = $"已删除{PlaylistKind(selected)}：{Describe(selected)}" };
    }

    public async Task<SavedPlaylistResult> AddSongsAsync(IEnumerable<MusicInfo> songs)
    {
        FrameworkCompatibility.ThrowIfNull(songs);
        var candidates = songs.Where(song => song is not null).ToArray();
        if (candidates.Length == 0) return Failure("没有可收藏的歌曲");

        // Local files have their own default playlist; both kinds are written durably here so the
        // favourite survives a crash or an orderly shutdown instead of waiting for exit.
        var remote = candidates.Where(song => !IsLocalSong(song)).ToArray();
        var local = candidates.Where(IsLocalSong).ToArray();
        var messages = new List<string>();

        if (remote.Length > 0)
        {
            var selected = SelectedOrNull() ?? await data.GetOrCreateDefaultPlaylistAsync(local: false);
            if (selected is null) return Failure("无法创建默认收藏歌单");
            lock (_gate) _selected = selected;
            var addition = await data.AddSongsToPlaylistAsync(selected.Id, remote);
            messages.Add(addition.AddedCount == 0
                ? $"所选歌曲已在在线收藏中：{selected.Name}"
                : $"已添加 {addition.AddedCount} 首歌曲到在线收藏：{selected.Name}"
                    + (addition.SkippedCount == 0 ? string.Empty : $"，跳过 {addition.SkippedCount} 首重复歌曲"));
        }

        if (local.Length > 0)
        {
            var localPlaylist = await data.GetOrCreateDefaultPlaylistAsync(local: true);
            if (localPlaylist is null) return Failure("无法创建本地歌曲歌单");
            var addition = await data.AddSongsToPlaylistAsync(localPlaylist.Id, local);
            if (remote.Length == 0) lock (_gate) _selected = localPlaylist;
            messages.Add(addition.AddedCount == 0
                ? $"所选歌曲已在本地歌曲中：{localPlaylist.Name}"
                : $"已添加 {addition.AddedCount} 首歌曲到本地歌曲：{localPlaylist.Name}"
                    + (addition.SkippedCount == 0 ? string.Empty : $"，跳过 {addition.SkippedCount} 首重复歌曲"));
        }

        await LoadAsync();
        var activated = await ActivateAsync();
        var snapshot = activated.Succeeded ? activated.Snapshot : Current;
        return new(snapshot, string.Join("；", messages));
    }

    private static bool IsLocalSong(MusicInfo song) =>
        string.Equals(song.Source, "Local", StringComparison.OrdinalIgnoreCase);

    public async Task<SavedPlaylistResult> RemoveSongAsync(MusicInfo music)
    {
        FrameworkCompatibility.ThrowIfNull(music);
        var selected = SelectedOrNull();
        if (selected is null) return Failure("请先选择在线歌单");
        await data.RemoveSongFromPlaylistAsync(selected.Id, music);
        var activated = await ActivateAsync();
        return activated with { Message = $"已从{Describe(selected)}移除：{music.Title}" };
    }

    public async Task<SavedPlaylistResult> ClearAsync()
    {
        var selected = SelectedOrNull();
        if (selected is null) return Failure("请先选择在线歌单");
        await data.ClearPlaylistItemsAsync(selected.Id);
        var activated = await ActivateAsync();
        var scope = !selected.IsSnapshot && !selected.IsLocal && !string.IsNullOrWhiteSpace(selected.RemoteId)
            ? "；重新进入时可能从远程重新加载"
            : string.Empty;
        var clearKind = selected.IsSnapshot ? "本地快照歌单" : PlaylistKind(selected);
        return activated with { Message = $"{clearKind}已清空：{Describe(selected)}{scope}" };
    }

    public async Task<SavedPlaylistResult> CollectRemoteAsync(RemotePlaylistFavorite request)
    {
        FrameworkCompatibility.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.RemoteId))
            return Failure("无法收藏该歌单");
        var existing = (await data.GetFavoritePlaylistsAsync(false)).FirstOrDefault(item =>
            string.Equals(item.RemoteId, request.RemoteId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Source, request.Source, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return Failure($"歌单“{request.Name}”已在在线收藏中");
        await data.SavePlaylistAsync(new Playlist
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            Description = "远程收藏歌单",
            Type = request.Type,
            IsLocal = false,
            Source = request.Source,
            RemoteId = request.RemoteId
        });
        await LoadAsync();
        return new(Current, $"已将歌单“{request.Name}”收藏到在线收藏");
    }

    private Playlist? SelectedOrNull()
    {
        lock (_gate) return _selected;
    }

    private SavedPlaylistResult Failure(string message) => new(Current, message, Succeeded: false);

    private SavedPlaylistSnapshot Snapshot() => new(
        _playlists.ToArray(),
        _selected,
        _songs.ToArray());
}
