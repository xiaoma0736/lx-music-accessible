using System.Text.Json.Serialization;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// QQ音乐API响应基础模型
/// </summary>
public class QqApiResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonIgnore]
    public bool IsSuccess => Code == 0;
}

/// <summary>
/// QQ音乐搜索结果
/// </summary>
public class QqSearchResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    public QqSearchData? Data { get; set; }
}

/// <summary>
/// QQ音乐搜索数据
/// </summary>
public class QqSearchData
{
    [JsonPropertyName("song")]
    public QqSongList? Song { get; set; }
}

/// <summary>
/// QQ音乐歌曲列表
/// </summary>
public class QqSongList
{
    [JsonPropertyName("list")]
    public List<QqSong>? List { get; set; }

    [JsonPropertyName("totalnum")]
    public int TotalNum { get; set; }
}

/// <summary>
/// QQ音乐歌曲信息
/// </summary>
public class QqSong
{
    [JsonPropertyName("songid")]
    public long SongId { get; set; }

    [JsonPropertyName("songname")]
    public string? SongName { get; set; }

    [JsonPropertyName("singername")]
    public string? SingerName { get; set; }

    [JsonPropertyName("albumname")]
    public string? AlbumName { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }

    [JsonPropertyName("strMediaMid")]
    public string? StrMediaMid { get; set; }

    [JsonPropertyName("albummid")]
    public string? AlbumMid { get; set; }

    public string GetDurationString()
    {
        var duration = TimeSpan.FromSeconds(Interval);
        if (duration.Hours > 0)
            return duration.ToString(@"hh\:mm\:ss");
        return duration.ToString(@"mm\:ss");
    }
}

/// <summary>
/// QQ音乐播放URL响应
/// </summary>
public class QqPlayUrlResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    public List<QqPlayUrlData>? Data { get; set; }
}

/// <summary>
/// QQ音乐播放URL数据
/// </summary>
public class QqPlayUrlData
{
    [JsonPropertyName("songid")]
    public long SongId { get; set; }

    [JsonPropertyName("songmid")]
    public string? SongMid { get; set; }

    [JsonPropertyName("songurl")]
    public string? SongUrl { get; set; }
}
