using System.Text.Json.Serialization;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 网易云音乐API响应基础模型
/// </summary>
public class NeteaseApiResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonIgnore]
    public bool IsSuccess => Code == 200;
}

/// <summary>
/// 网易云音乐搜索结果
/// </summary>
public class NeteaseSearchResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("result")]
    public NeteaseSearchData? Result { get; set; }
}

/// <summary>
/// 网易云音乐搜索数据
/// </summary>
public class NeteaseSearchData
{
    [JsonPropertyName("songs")]
    public List<NeteaseSong>? Songs { get; set; }

    [JsonPropertyName("songCount")]
    public int SongCount { get; set; }
}

/// <summary>
/// 网易云音乐歌曲信息
/// </summary>
public class NeteaseSong
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("ar")]
    public List<NeteaseArtist>? Artists { get; set; }

    [JsonPropertyName("al")]
    public NeteaseAlbum? Album { get; set; }

    [JsonPropertyName("dt")]
    public long Duration { get; set; }

    [JsonPropertyName("mv")]
    public long? MvId { get; set; }

    [JsonPropertyName("privilege")]
    public NeteasePrivilege? Privilege { get; set; }

    public string GetArtistNames()
    {
        if (Artists == null || Artists.Count == 0) return "";
        return string.Join("、", Artists.Select(a => a.Name));
    }

    public TimeSpan GetDuration()
    {
        return TimeSpan.FromMilliseconds(Duration);
    }

    public string GetDurationString()
    {
        var duration = GetDuration();
        if (duration.Hours > 0)
            return duration.ToString(@"hh\:mm\:ss");
        return duration.ToString(@"mm\:ss");
    }
}

/// <summary>
/// 网易云音乐艺术家信息
/// </summary>
public class NeteaseArtist
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
/// 网易云音乐专辑信息
/// </summary>
public class NeteaseAlbum
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("picUrl")]
    public string? PicUrl { get; set; }
}

/// <summary>
/// 网易云音乐权限信息
/// </summary>
public class NeteasePrivilege
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("maxbr")]
    public int Maxbr { get; set; }

    [JsonPropertyName("maxBrLevel")]
    public string? MaxBrLevel { get; set; }

    [JsonPropertyName("fl")]
    public int? FlacQuality { get; set; }

    [JsonPropertyName("hr")]
    public NeteaseHiresInfo? HiresInfo { get; set; }
}

/// <summary>
/// 网易云音乐Hi-Res信息
/// </summary>
public class NeteaseHiresInfo
{
    [JsonPropertyName("br")]
    public int Bitrate { get; set; }

    [JsonPropertyName("fid")]
    public long? FileId { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

/// <summary>
/// 网易云音乐排行榜列表
/// </summary>
public class NeteaseTopList
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("list")]
    public List<NeteaseTopListDetail>? List { get; set; }

    [JsonIgnore]
    public List<NeteaseTopListDetail>? TopLists => List;
}

/// <summary>
/// 网易云音乐排行榜详情
/// </summary>
public class NeteaseTopListDetail
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("coverImgUrl")]
    public string? CoverImgUrl { get; set; }

    [JsonPropertyName("updateFrequency")]
    public string? UpdateFrequency { get; set; }

    [JsonPropertyName("updateTime")]
    public long UpdateTime { get; set; }

    [JsonPropertyName("tracks")]
    public List<NeteaseSong>? Tracks { get; set; }

    [JsonPropertyName("trackCount")]
    public int TrackCount { get; set; }
}

/// <summary>
/// 网易云音乐歌单分类
/// </summary>
public class NeteasePlaylistCategory
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("sub")]
    public List<NeteaseCategory>? Categories { get; set; }
}

/// <summary>
/// 网易云音乐分类
/// </summary>
public class NeteaseCategory
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("resourceType")]
    public string? ResourceType { get; set; }

    [JsonPropertyName("category")]
    public int Category { get; set; }

    [JsonPropertyName("hot")]
    public bool Hot { get; set; }
}

/// <summary>
/// 网易云音乐歌单列表
/// </summary>
public class NeteasePlaylistResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("playlists")]
    public List<NeteasePlaylist>? Playlists { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("more")]
    public bool More { get; set; }
}

/// <summary>
/// 网易云音乐歌单
/// </summary>
public class NeteasePlaylist
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("coverImgUrl")]
    public string? CoverImgUrl { get; set; }

    [JsonPropertyName("creator")]
    public NeteasePlaylistCreator? Creator { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("trackCount")]
    public int TrackCount { get; set; }

    [JsonPropertyName("playCount")]
    public long PlayCount { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}

/// <summary>
/// 网易云音乐歌单创建者
/// </summary>
public class NeteasePlaylistCreator
{
    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("userId")]
    public long UserId { get; set; }
}

/// <summary>
/// 网易云音乐歌单详情
/// </summary>
public class NeteasePlaylistDetail
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("playlist")]
    public NeteasePlaylistDetailInfo? Playlist { get; set; }
}

/// <summary>
/// 网易云音乐歌单详细信息
/// </summary>
public class NeteasePlaylistDetailInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("coverImgUrl")]
    public string? CoverImgUrl { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("trackCount")]
    public int TrackCount { get; set; }

    [JsonPropertyName("playCount")]
    public long PlayCount { get; set; }

    [JsonPropertyName("tracks")]
    public List<NeteaseSong>? Tracks { get; set; }

    [JsonPropertyName("trackIds")]
    public List<NeteaseTrackId>? TrackIds { get; set; }
}

/// <summary>
/// 网易云音乐歌曲ID
/// </summary>
public class NeteaseTrackId
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("v")]
    public int Version { get; set; }
}

/// <summary>
/// 播放URL响应
/// </summary>
public class NeteasePlayUrlResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    public List<NeteasePlayUrlData> Data { get; set; } = new();
}

/// <summary>
/// 播放URL数据
/// </summary>
public class NeteasePlayUrlData
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("br")]
    public int Bitrate { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }
}

/// <summary>
/// 歌单歌曲响应
/// </summary>
public class NeteasePlaylistTrackResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("songs")]
    public List<NeteaseSong>? Songs { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}


// ========== eapi 搜索响应模型 ==========

public class NeteaseEapiSearchResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    public NeteaseEapiSearchData? Data { get; set; }
}

public class NeteaseEapiSearchData
{
    [JsonPropertyName("resources")]
    public List<NeteaseEapiResource>? Resources { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

public class NeteaseEapiResource
{
    [JsonPropertyName("baseInfo")]
    public NeteaseEapiBaseInfo? BaseInfo { get; set; }
}

public class NeteaseEapiBaseInfo
{
    [JsonPropertyName("simpleSongData")]
    public NeteaseSong? SimpleSongData { get; set; }
}

// ========== weapi 分类响应 ==========

public class NeteaseCategoryResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("sub")]
    public List<NeteaseCategory>? Sub { get; set; }

    [JsonPropertyName("categories")]
    public Dictionary<string, string>? Categories { get; set; }
}

// ========== weapi 歌单/排行榜详情 ==========

public class NeteasePlaylistDetailResp
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("playlist")]
    public NeteasePlaylistDetailInfo? Playlist { get; set; }
}

// ========== linuxapi 响应 ==========

public class NeteaseLinuxApiResp
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("playlist")]
    public NeteasePlaylistDetailInfo? Playlist { get; set; }
}

// ========== 热门搜索 ==========

public class NeteaseHotSearchResp
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    public List<NeteaseHotSearchItem>? Data { get; set; }
}

public class NeteaseHotSearchItem
{
    [JsonPropertyName("searchWord")]
    public string? SearchWord { get; set; }

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

// ========== ???? ==========

public class NeteaseLyricResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("lrc")]
    public NeteaseLrcData? LrcData { get; set; }

    [JsonPropertyName("tlyric")]
    public NeteaseLrcData? TranslatedData { get; set; }
}

public class NeteaseLrcData
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("lyric")]
    public string? Lyric { get; set; }
}

