using System.Text.Json.Serialization;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 酷我音乐API响应基础模型
/// </summary>
public class KuwoApiResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonIgnore]
    public bool IsSuccess => Code == 200;
}

/// <summary>
/// 酷我音乐搜索结果
/// </summary>
public class KuwoSearchResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    public KuwoSearchData? Data { get; set; }
}

/// <summary>
/// 酷我音乐搜索数据
/// </summary>
public class KuwoSearchData
{
    [JsonPropertyName("list")]
    public List<KuwoSong>? List { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>
/// 酷我音乐歌曲信息
/// </summary>
public class KuwoSong
{
    [JsonPropertyName("musicrid")]
    public string? MusicRid { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("albumid")]
    public string? AlbumId { get; set; }

    public string GetDurationString()
    {
        var duration = TimeSpan.FromSeconds(Duration);
        if (duration.Hours > 0)
            return duration.ToString(@"hh\:mm\:ss");
        return duration.ToString(@"mm\:ss");
    }
}

/// <summary>
/// 酷我音乐播放URL响应
/// </summary>
public class KuwoPlayUrlResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    public KuwoPlayUrlData? Data { get; set; }
}

/// <summary>
/// 酷我音乐播放URL数据
/// </summary>
public class KuwoPlayUrlData
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }
}
