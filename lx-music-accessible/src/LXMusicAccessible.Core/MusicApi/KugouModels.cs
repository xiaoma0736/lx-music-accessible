using System.Text.Json.Serialization;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 酷狗音乐API响应基础模型
/// </summary>
public class KugouApiResponse<T>
{
    [JsonPropertyName("error_code")]
    public int ErrorCode { get; set; }

    [JsonPropertyName("error_msg")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonIgnore]
    public bool IsSuccess => ErrorCode == 0;
}

/// <summary>
/// 酷狗音乐搜索结果
/// </summary>
public class KugouSearchResult
{
    [JsonPropertyName("error_code")]
    public int ErrorCode { get; set; }

    [JsonPropertyName("data")]
    public KugouSearchData? Data { get; set; }
}

/// <summary>
/// 酷狗音乐搜索数据
/// </summary>
public class KugouSearchData
{
    [JsonPropertyName("info")]
    public List<KugouSong>? Info { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>
/// 酷狗音乐歌曲信息
/// </summary>
public class KugouSong
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("songname")]
    public string? SongName { get; set; }

    [JsonPropertyName("singername")]
    public string? SingerName { get; set; }

    [JsonPropertyName("album_name")]
    public string? AlbumName { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("album_id")]
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
/// 酷狗音乐播放URL响应
/// </summary>
public class KugouPlayUrlResponse
{
    [JsonPropertyName("error_code")]
    public int ErrorCode { get; set; }

    [JsonPropertyName("data")]
    public List<KugouPlayUrlData>? Data { get; set; }
}

/// <summary>
/// 酷狗音乐播放URL数据
/// </summary>
public class KugouPlayUrlData
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("time")]
    public int Time { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
