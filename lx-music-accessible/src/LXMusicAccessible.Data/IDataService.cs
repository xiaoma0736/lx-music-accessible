using LXMusicAccessible.Models;

namespace LXMusicAccessible.Data;

/// <summary>
/// 数据服务接口
/// </summary>
public interface IDataService
{
    /// <summary>
    /// 原子替换播放列表快照。元数据、旧项目删除和新项目写入要么全部提交，要么全部回滚。
    /// 歌曲按 Id（缺失时按 PlayUrl）与 Source 去重并保留首次出现顺序；大型快照有界分批、单次提交。
    /// </summary>
    Task<PlaylistSnapshotReplacement> ReplacePlaylistSnapshotAsync(
        Playlist playlist,
        IEnumerable<MusicInfo> songs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存播放列表
    /// </summary>
    Task SavePlaylistAsync(Playlist playlist);

    /// <summary>
    /// 获取播放列表
    /// </summary>
    Task<List<Playlist>> GetPlaylistsAsync();

    /// <summary>
    /// 删除播放列表
    /// </summary>
    Task DeletePlaylistAsync(string playlistId);

    /// <summary>
    /// 保存设置
    /// </summary>
    Task SaveSettingsAsync(UserSettings settings);

    /// <summary>
    /// 获取设置
    /// </summary>
    Task<UserSettings?> GetSettingsAsync();

    /// <summary>
    /// 保存音源
    /// </summary>
    Task SaveMusicSourcesAsync(List<MusicSource> sources);

    /// <summary>
    /// 获取音源
    /// </summary>
    Task<List<MusicSource>> GetMusicSourcesAsync();

    /// <summary>
    /// 删除指定音源
    /// </summary>
    Task DeleteMusicSourcesAsync(List<string> ids);

    /// <summary>
    /// 保存播放历史
    /// </summary>
    Task SavePlayHistoryAsync(MusicInfo music);

    /// <summary>
    /// 获取播放历史
    /// </summary>
    Task<List<MusicInfo>> GetPlayHistoryAsync(int count = 50);
    
    /// <summary>
    /// 保存播放列表项目
    /// </summary>
    Task SavePlaylistItemAsync(string playlistId, MusicInfo music, int orderNum);
    
    /// <summary>
    /// 获取播放列表项目
    /// </summary>
    Task<List<MusicInfo>> GetPlaylistItemsAsync(string playlistId);
    
    /// <summary>
    /// 删除播放列表项目
    /// </summary>
    Task DeletePlaylistItemAsync(string itemId);
    
    /// <summary>
    /// 清空播放列表项目
    /// </summary>
    Task ClearPlaylistItemsAsync(string playlistId);

    /// <summary>
    /// 获取收藏歌单（local=true 本地收藏，false 在线收藏）
    /// </summary>
    Task<List<Playlist>> GetFavoritePlaylistsAsync(bool local);

    /// <summary>
    /// 获取（不存在则创建）默认歌单：本地“我的本地歌曲” / 在线“默认收藏”
    /// </summary>
    Task<Playlist?> GetOrCreateDefaultPlaylistAsync(bool local);

    /// <summary>
    /// 把歌曲原子追加到指定歌单。既有歌曲和输入歌曲按 Id（缺失时按 PlayUrl）与 Source
    /// 去重并保留既有顺序及输入首次出现顺序；大型列表有界分批、单次提交。
    /// 返回实际新增数和因既有或输入重复而跳过的数量；故障或取消不会留下部分写入。
    /// </summary>
    Task<PlaylistSongAddition> AddSongsToPlaylistAsync(
        string playlistId,
        IEnumerable<MusicInfo> songs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 从指定歌单移除歌曲
    /// </summary>
    Task RemoveSongFromPlaylistAsync(string playlistId, MusicInfo music);


    /// <summary>
    /// 重命名歌单
    /// </summary>
    Task RenamePlaylistAsync(string playlistId, string newName);

    /// <summary>
    /// 保存平台账号登录态（凭据 DPAPI 加密后落盘）
    /// </summary>
    Task SaveAccountAsync(AccountInfo account);

    /// <summary>
    /// 获取平台账号登录态（自动解密凭据到 AuthData）
    /// </summary>
    Task<AccountInfo?> GetAccountAsync(string platform);

    /// <summary>
    /// 删除平台账号登录态
    /// </summary>
    Task DeleteAccountAsync(string platform);

    /// <summary>
    /// 读取 Settings 表任意键的原始值（通用设置存储用；无则返回 null）
    /// </summary>
    Task<string?> GetSettingValueAsync(string key);

    /// <summary>
    /// 写入 Settings 表任意键的原始值；value 为 null 时删除该键
    /// </summary>
    Task SetSettingValueAsync(string key, string? value);
}

public readonly record struct PlaylistSnapshotReplacement(int ImportedCount, int SkippedCount);

public readonly record struct PlaylistSongAddition(int AddedCount, int SkippedCount);
