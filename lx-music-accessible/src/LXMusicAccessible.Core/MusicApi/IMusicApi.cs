using LXMusicAccessible.Models;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 统一音源 API 接口 — 每个音乐平台服务必须实现的契约
/// </summary>
public interface IMusicApi
{
    /// <summary>音源标识（如 "kw"、"kg"、"qq"、"netease"、"migu"）</summary>
    string SourceId { get; }

    /// <summary>音源显示名称</summary>
    string SourceName { get; }

    /// <summary>搜索音乐（searchType：song 歌曲 / album 专辑，默认 song）</summary>
    Task<PagedResult<MusicInfo>> SearchAsync(string keyword, int page = 1, int pageSize = 30, string searchType = "song");

    /// <summary>获取专辑详情（歌曲列表；album 为专辑搜索返回的专辑行）</summary>
    Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50);

    /// <summary>获取播放 URL（支持音质等级）</summary>
    Task<string?> GetPlayUrlAsync(string musicId, string quality = "128k");

    /// <summary>该音源支持的音质列表（如 128k/192k/320k/flac/flac24bit）</summary>
    List<string> GetSupportedQualities();

    /// <summary>获取歌词信息（传入完整 MusicInfo，供酷狗取时长、咪咕取 lyricUrl）</summary>
    Task<Lyric?> GetLyricAsync(MusicInfo music);

    /// <summary>获取歌单分类列表</summary>
    Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync();

    /// <summary>按分类获取歌单列表</summary>
    Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string categoryId, int page = 1, int pageSize = 30);

    /// <summary>获取歌单详情（歌曲列表）</summary>
    Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50);

    /// <summary>获取排行榜列表</summary>
    Task<List<Ranking>> GetRankingsAsync();

    /// <summary>获取排行榜详情（歌曲列表）</summary>
    Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50);

}

