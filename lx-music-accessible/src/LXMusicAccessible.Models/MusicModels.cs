using System.Linq;

namespace LXMusicAccessible.Models;

/// <summary>
/// 音乐信息
/// </summary>
public class MusicInfo
{
    public string Id { get; set; } = string.Empty;

    /// <summary>平台专属 hash（酷狗/酷我等，LX 脚本 musicInfo.hash）</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>平台专属 songmid（QQ音乐，LX 脚本 musicInfo.songmid）</summary>
    public string Songmid { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? PlayUrl { get; set; }
    public string? LyricUrl { get; set; }
    public string? MvUrl { get; set; }
    public int Quality { get; set; }
    public DateTime AddedDate { get; set; } = DateTime.Now;
    public int PlayCount { get; set; }

    /// <summary>专辑搜索结果的专辑行标记（True 表示该行代表一张专辑而非单曲）</summary>
    public bool IsAlbum { get; set; }

    /// <summary>Screen-reader name for list items: non-empty fields joined with full-width comma。
    /// 本地歌曲跳过占位字段（本地/--:--），避免读屏重复朗读“本地、本地、--:--、Local”。</summary>
    public string AccessibleName
    {
        get
        {
            // 专辑搜索行：朗读“专辑名（专辑），歌手”，避免重复朗读专辑名
            if (IsAlbum)
            {
                var albumParts = new List<string> { Title + "（专辑）" };
                if (!string.IsNullOrWhiteSpace(Artist)) albumParts.Add(Artist);
                return string.Join("，", albumParts);
            }
            var isLocal = string.Equals(Source, "Local", StringComparison.OrdinalIgnoreCase);
            var parts = new List<string> { Title };
            if (isLocal)
            {
                if (!string.Equals(Artist, "本地", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Artist))
                    parts.Add(Artist);
                if (!string.Equals(Album, "本地", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Album))
                    parts.Add(Album);
                if (!string.Equals(Duration, "--:--", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Duration))
                    parts.Add(Duration);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(Artist)) parts.Add(Artist);
                if (!string.IsNullOrWhiteSpace(Album)) parts.Add(Album);
                if (!string.IsNullOrWhiteSpace(Duration)) parts.Add(Duration);
            }
            return string.Join("，", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    public override string ToString()
    {
        return $"{Artist} - {Title}";
    }
}

/// <summary>
/// 歌词行
/// </summary>
public class LyricLine
{
    public TimeSpan Time { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? Translation { get; set; }
}

/// <summary>
/// 歌词
/// </summary>
public class Lyric
{
    public List<LyricLine> Lines { get; set; } = new();
    public string? OriginalLyric { get; set; }
    public string? TranslationLyric { get; set; }
    public string? RomajiLyric { get; set; }
}

/// <summary>
/// 播放列表项
/// </summary>
public class PlaylistItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PlaylistId { get; set; } = string.Empty;
    public MusicInfo Music { get; set; } = new();
    public int Order { get; set; }
    public DateTime AddedDate { get; set; } = DateTime.Now;
}

/// <summary>
/// 播放列表
/// </summary>
public class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public List<PlaylistItem> Items { get; set; } = new();
    public int TotalCount => Items.Count;
    public TimeSpan TotalDuration => TimeSpan.FromMinutes(Items.Count * 3.5);
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;
    public PlaylistType Type { get; set; }
    public bool IsLocal { get; set; }

    /// <summary>在线收藏远程歌单的来源音源 id（如 qq/kg/kw/netease；空表示本地/普通歌单）</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>在线收藏远程歌单的远端 id（含来源前缀，如 netease_123；空表示本地歌单）</summary>
    public string RemoteId { get; set; } = string.Empty;
    /// <summary>是否为本地快照歌单（导入的远程个人歌单，歌曲已存到本地，离线可播）</summary>
    public bool IsSnapshot { get; set; }

    public override string ToString()
    {
        return string.IsNullOrEmpty(Name) ? "未命名" : Name;
    }
}

/// <summary>
/// 播放列表类型
/// </summary>
public enum PlaylistType
{
    User,           // 用户创建的歌单
    Local,          // 本地导入的歌单
    System,         // 系统默认歌单
    Cloud,          // 云端同步歌单
    Recommend       // 推荐歌单
}

/// <summary>
/// 搜索结果
/// </summary>
public class SearchResult
{
    public List<MusicInfo> Songs { get; set; } = new();
    public List<MusicInfo> Artists { get; set; } = new();
    public List<MusicInfo> Albums { get; set; } = new();
    public List<Playlist> Playlists { get; set; } = new();
    public int TotalSongs { get; set; }
    public int TotalArtists { get; set; }
    public int TotalAlbums { get; set; }
    public int TotalPlaylists { get; set; }
}

/// <summary>
/// 排行榜
/// </summary>
public class Ranking
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public List<MusicInfo> Songs { get; set; } = new();
    public DateTime UpdateDate { get; set; }
    public RankingType Type { get; set; }

    public override string ToString()
    {
        return Name;
    }
}

/// <summary>
/// 排行榜类型
/// </summary>
public enum RankingType
{
    Hot,            // 热歌榜
    New,            // 新歌榜
    Original,       // 原创榜
    Rising,         // 飙升榜
    Other           // 其他
}

/// <summary>
/// 音乐源
/// </summary>
public class MusicSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ScriptUrl { get; set; } = string.Empty;
    public string? ScriptContent { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public int Priority { get; set; }
    public DateTime AddedDate { get; set; } = DateTime.Now;
    public DateTime LastUpdateDate { get; set; }
    public SourceType Type { get; set; }
    public ScriptTrustState TrustState { get; set; }
    public string? ContentSha256 { get; set; }

    /// <summary>系统隐藏源（内置官方API）：不出现在音源管理列表，但仍是搜索/歌单/排行等的底层数据源</summary>
    public bool IsSystemHidden { get; set; }

    /// <summary>类型显示文本</summary>
    public string TypeText => Type switch
    {
        SourceType.BuiltIn => "内置",
        SourceType.Custom => "自定义",
        SourceType.Network => "网络",
        SourceType.Local => "本地",
        SourceType.Bundled => "随版本提供",
        _ => Type.ToString()
    };

    /// <summary>状态显示文本（默认优先）</summary>
    public string StatusText => TrustState == ScriptTrustState.Untrusted
        ? "已暂存，未信任"
        : IsDefault ? "默认" : (IsEnabled ? "已启用" : "已禁用");

    /// <summary>读屏聚焦名称包含音源标识和必要的启用/信任状态。</summary>
    public string AccessibleName
    {
        get
        {
            var name = string.IsNullOrEmpty(Name) ? "未命名音源" : Name;
            var ver = string.IsNullOrEmpty(Version) ? "" : $"，版本 {Version}";
            return $"{name}{ver}，{TypeText}，{StatusText}";
        }
    }

    public override string ToString() => string.IsNullOrEmpty(Name) ? "未命名音源" : Name;
}

/// <summary>
/// 音乐源类型
/// </summary>
public enum SourceType
{
    BuiltIn,        // 内置音乐源
    Custom,         // 自定义音乐源
    Network,        // 网络音乐源
    Local,          // 本地音乐源
    Bundled
}

public enum ScriptTrustState
{
    Untrusted,
    UserApproved,
    Bundled
}

public enum ScriptActivationStatus
{
    Activated,
    ContentMissing,
    DigestMismatch,
    ValidationFailed,
    PersistenceFailed,
    Cancelled
}

public sealed record ScriptActivationResult(
    bool Succeeded,
    ScriptActivationStatus Status,
    string Message);

/// <summary>
/// 歌单分类
/// </summary>
public class PlaylistCategory
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<PlaylistTag> Tags { get; set; } = new();

    public override string ToString()
    {
        return string.IsNullOrEmpty(Name) ? "未命名" : Name;
    }
}

/// <summary>
/// 歌单标签
/// </summary>
public class PlaylistTag
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public override string ToString()
    {
        return string.IsNullOrEmpty(Name) ? "未命名" : Name;
    }
}

/// <summary>
/// 音乐源枚举
/// </summary>
public enum MusicSourceEnum
{
    /// <summary>
    /// 网易云音乐源
    /// </summary>
    Netease,

    /// <summary>
    /// QQ 音乐源
    /// </summary>
    QQ,

    /// <summary>
    /// 酷狗音乐源
    /// </summary>
    Kugou,

    /// <summary>
    /// 酷我音乐源
    /// </summary>
    Kuwo,

    /// <summary>
    /// 咪咕音乐源
    /// </summary>
    Migu,

    /// <summary>
    /// 全部
    /// </summary>
    All
}


/// <summary>
/// 下载任务状态
/// </summary>
public enum DownloadTaskStatus
{
    Pending,
    Downloading,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// 下载任务
/// </summary>
public class DownloadTask : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public MusicInfo Music { get; set; } = new();
    public string Quality { get; set; } = "128k";
    /// <summary>Actual audio format of the finished download (sniffed), e.g. "mp3" / "flac".</summary>
    public string? ActualQuality { get; set; }
    public string? SavePath { get; set; }
    /// <summary>同步下载的歌词文件路径（.lrc），未下载为 null</summary>
    public string? LyricPath { get; set; }
    /// <summary>是否为批量下载任务（批量任务完成时不再逐条播报，避免刷屏）</summary>
    public bool IsBatch { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    private DownloadTaskStatus _status = DownloadTaskStatus.Pending;
    public DownloadTaskStatus Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(AccessibleStatus)); OnPropertyChanged(nameof(AccessibleName)); } }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set { if (Math.Abs(_progress - value) > 0.01) { _progress = value; OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(AccessibleStatus)); OnPropertyChanged(nameof(AccessibleName)); } }
    }

    private long _downloadedBytes;
    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set { if (_downloadedBytes != value) { _downloadedBytes = value; OnPropertyChanged(nameof(DownloadedBytes)); } }
    }

    private long _totalBytes;
    public long TotalBytes
    {
        get => _totalBytes;
        set { if (_totalBytes != value) { _totalBytes = value; OnPropertyChanged(nameof(TotalBytes)); } }
    }

    public string? ErrorMessage { get; set; }

    public string DisplayName => Music.Source == "Local"
        ? $"{Music.Title}（本地复制）"
        : $"{Music.Artist} - {Music.Title} [{ActualQuality ?? Quality}]";

    public string StatusText => Status switch
    {
        DownloadTaskStatus.Pending => "等待中",
        DownloadTaskStatus.Downloading => "下载中",
        DownloadTaskStatus.Completed => "已完成",
        DownloadTaskStatus.Failed => "失败",
        DownloadTaskStatus.Cancelled => "已取消",
        _ => Status.ToString()
    };

    public string ProgressText => Status switch
    {
        DownloadTaskStatus.Downloading => $"{Progress:0}%",
        DownloadTaskStatus.Completed => "100%",
        _ => StatusText
    };

    /// <summary>读屏聚焦名称包含任务标识和精简后的进度/状态。</summary>
    public string AccessibleName => $"{DisplayName}，{AccessibleStatus}";

    public string AccessibleStatus => Status == DownloadTaskStatus.Downloading
        ? $"{ProgressText}，{StatusText}"
        : StatusText;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
