using LXMusicAccessible.Data;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Services;

public sealed record SavedPlaylistSnapshot(
    IReadOnlyList<Playlist> Playlists,
    Playlist? Selected,
    IReadOnlyList<MusicInfo> Songs);

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

    public SavedPlaylistSnapshot Current
    {
        get
        {
            lock (_gate) return Snapshot();
        }
    }

    public async Task<SavedPlaylistResult> LoadAsync()
    {
        var playlists = await data.GetFavoritePlaylistsAsync(local: false);
        lock (_gate)
        {
            var selectedId = _selected?.Id;
            _playlists = playlists;
            _selected = playlists.FirstOrDefault(item => item.Id == selectedId)
                ?? playlists.FirstOrDefault(item => item.Type == PlaylistType.System)
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

    public async Task<SavedPlaylistResult> ActivateAsync()
    {
        Playlist? selected;
        long revision;
        lock (_gate)
        {
            selected = _selected;
            revision = _revision;
        }
        if (selected is null) return Failure("请先选择在线歌单");

        IReadOnlyList<MusicInfo> songs = await data.GetPlaylistItemsAsync(selected.Id);
        var focus = BrowseFocusTarget.SavedPlaylistSongs;
        if (songs.Count == 0 && !selected.IsSnapshot && !string.IsNullOrWhiteSpace(selected.RemoteId))
        {
            var remote = await browse.ActivateSavedPlaylistAsync(selected);
            if (!remote.Succeeded || remote.Value is null) return Failure(remote.Message);
            songs = remote.Value.Items;
            focus = remote.FocusTarget;
        }

        lock (_gate)
        {
            if (revision != _revision || _selected?.Id != selected.Id)
                return Failure(string.Empty);
            _songs = songs.ToArray();
            return new(Snapshot(), $"已加载 {_songs.Count} 首收藏歌曲", focus);
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
        return loaded with { Message = $"在线歌单已重命名为：{name.Trim()}" };
    }

    public async Task<SavedPlaylistResult> DeleteSelectedAsync()
    {
        var selected = SelectedOrNull();
        if (selected is null) return Failure("请先选择要删除的在线歌单");
        if (selected.Type == PlaylistType.System) return Failure("默认收藏歌单不能删除");
        await data.DeletePlaylistAsync(selected.Id);
        lock (_gate) _selected = null;
        var loaded = await LoadAsync();
        return loaded with { Message = $"已删除在线歌单：{selected.Name}" };
    }

    public async Task<SavedPlaylistResult> AddSongsAsync(IEnumerable<MusicInfo> songs)
    {
        ArgumentNullException.ThrowIfNull(songs);
        var candidates = songs
            .Where(song => song is not null
                && !string.Equals(song.Source, "Local", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0) return Failure("本地歌曲无需收藏，请收藏网络歌曲");

        var selected = SelectedOrNull() ?? await data.GetOrCreateDefaultPlaylistAsync(local: false);
        if (selected is null) return Failure("无法创建默认收藏歌单");
        lock (_gate) _selected = selected;
        var addition = await data.AddSongsToPlaylistAsync(selected.Id, candidates);
        await LoadAsync();
        return new(
            Current,
            addition.AddedCount == 0
                ? $"所选歌曲已在在线收藏中：{selected.Name}"
                : $"已添加 {addition.AddedCount} 首歌曲到在线收藏：{selected.Name}"
                    + (addition.SkippedCount == 0 ? string.Empty : $"，跳过 {addition.SkippedCount} 首重复歌曲"));
    }

    public async Task<SavedPlaylistResult> RemoveSongAsync(MusicInfo music)
    {
        ArgumentNullException.ThrowIfNull(music);
        var selected = SelectedOrNull();
        if (selected is null) return Failure("请先选择在线歌单");
        await data.RemoveSongFromPlaylistAsync(selected.Id, music);
        var activated = await ActivateAsync();
        return activated with { Message = $"已从{selected.Name}移除：{music.Title}" };
    }

    public async Task<SavedPlaylistResult> ClearAsync()
    {
        var selected = SelectedOrNull();
        if (selected is null) return Failure("请先选择在线歌单");
        await data.ClearPlaylistItemsAsync(selected.Id);
        var activated = await ActivateAsync();
        return activated with { Message = $"在线歌单已清空：{selected.Name}" };
    }

    public async Task<SavedPlaylistResult> CollectRemoteAsync(RemotePlaylistFavorite request)
    {
        ArgumentNullException.ThrowIfNull(request);
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
