using System.Text.Json.Serialization;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 咪咕音乐API响应基础模型
/// </summary>
public class MiguApiResponse<T>
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonIgnore]
    public bool IsSuccess => Code == "000000";
}

/// <summary>
/// 咪咕音乐搜索结果
/// </summary>
public class MiguSearchResult
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("data")]
    public MiguSearchData? Data { get; set; }
}

/// <summary>
/// 咪咕音乐搜索数据
/// </summary>
public class MiguSearchData
{
    [JsonPropertyName("musics")]
    public List<MiguSong>? Musics { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>
/// 咪咕音乐歌曲信息
/// </summary>
public class MiguSong
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("singer")]
    public string? Singer { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    [JsonPropertyName("playUrl")]
    public string? PlayUrl { get; set; }

    public string GetDurationString()
    {
        var duration = TimeSpan.FromSeconds(Duration);
        if (duration.Hours > 0)
            return duration.ToString(@"hh\:mm\:ss");
        return duration.ToString(@"mm\:ss");
    }
}

/// <summary>
/// 咪咕音乐播放URL响应
/// </summary>
public class MiguPlayUrlResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("data")]
    public MiguPlayUrlData? Data { get; set; }
}

/// <summary>
/// 咪咕音乐播放URL数据
/// </summary>
public class MiguPlayUrlData
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("rate")]
    public int Rate { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>
/// 咪咕音乐排行榜响应
/// </summary>
public class MiguRankingsResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("data")]
    public List<MiguRankingInfo>? Data { get; set; }
}

/// <summary>
/// 咪咕音乐排行榜信息
/// </summary>
public class MiguRankingInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("musics")]
    public List<MiguSong>? Musics { get; set; }
}

/// <summary>
/// 咪咕音乐歌单分类响应
/// </summary>
public class MiguPlaylistCategoriesResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("data")]
    public List<MiguCategoryInfo>? Data { get; set; }
}

/// <summary>
/// 咪咕音乐分类信息
/// </summary>
public class MiguCategoryInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("subTags")]
    public List<MiguTagInfo>? SubTags { get; set; }
}

/// <summary>
/// 咪咕音乐标签信息
/// </summary>
public class MiguTagInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
/// 咪咕音乐歌单列表响应
/// </summary>
public class MiguPlaylistListResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("data")]
    public MiguPlaylistListData? Data { get; set; }
}

/// <summary>
/// 咪咕音乐歌单列表数据
/// </summary>
public class MiguPlaylistListData
{
    [JsonPropertyName("playlists")]
    public List<MiguPlaylistInfo>? Playlists { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>
/// 咪咕音乐歌单信息
/// </summary>
public class MiguPlaylistInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}
