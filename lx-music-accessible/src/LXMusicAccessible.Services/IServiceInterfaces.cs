using LXMusicAccessible.Models;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Audio;
using System.Windows.Input;

namespace LXMusicAccessible.Services;

/// <summary>通过标准 UI Automation 主通道播报动态状态。</summary>
public interface IAccessibleAnnouncer
{
    /// <summary>是否同时使用可选语音适配器；不影响 UI Automation 主通道。</summary>
    bool SpeechEnabled { get; set; }

    /// <summary>提交一条带有优先级、合并键和语音策略的状态播报。</summary>
    void Announce(AccessibleAnnouncement announcement);
}

/// <summary>可感知状态的语义。</summary>
public enum AnnouncementKind
{
    Status,
    Busy,
    Progress,
    Success,
    Error
}

/// <summary>可选 TTS 的使用方式。</summary>
public enum AnnouncementSpeech
{
    FollowPreference,
    Always,
    Queued,
    None
}

/// <summary>一条可访问状态；ActivityId 用于合并进度并保证终态只播报一次。</summary>
public readonly record struct AccessibleAnnouncement(
    string Text,
    AnnouncementKind Kind = AnnouncementKind.Status,
    string? ActivityId = null,
    AnnouncementSpeech Speech = AnnouncementSpeech.FollowPreference)
{
    public static implicit operator AccessibleAnnouncement(string text) => new(text);
}

public enum PlaybackQueueOrigin
{
    Static,
    Search,
    Category,
    Ranking
}

public enum PlaybackResultStatus
{
    Playing,
    QueueCompleted,
    Failed,
    Superseded,
    NoChange
}

/// <summary>一个完整的播放意图；模块据此原子替换队列并播放所选歌曲。</summary>
public sealed record PlaybackRequest(
    IReadOnlyList<MusicInfo> Queue,
    MusicInfo Selection,
    string Quality = "128k",
    PlaybackQueueOrigin Origin = PlaybackQueueOrigin.Static,
    bool ForceResolve = false);

/// <summary>播放意图完成后的可观察结果。</summary>
public sealed record PlaybackResult(
    PlaybackResultStatus Status,
    MusicInfo? Music,
    PlaybackQueueOrigin Origin,
    string Message);

public sealed class PlaybackQueueCompletedEventArgs(
    PlaybackQueueOrigin origin) : EventArgs
{
    public PlaybackQueueOrigin Origin { get; } = origin;
}

/// <summary>
/// 播放协调模块接口。队列、索引、模式、换源重试和预缓存均隐藏在此接口之后。
/// </summary>
public interface IPlaybackCoordinator
{
    Task<PlaybackResult> PlayAsync(PlaybackRequest request);
    Task<PlaybackResult> ReplayAsync();
    void Pause();
    void Stop();
    Task<PlaybackResult> NextAsync();
    Task<PlaybackResult> PreviousAsync();
    bool IsPlaying { get; }
    bool IsPaused { get; }
    Task ResumeAsync();
    MusicInfo? CurrentMusic { get; }
    PlayMode Mode { get; set; }
    string DefaultQuality { get; set; }
    event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
    event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted;
}

/// <summary>播放协调模块提供的音量、时间轴与输出设备控制面。</summary>
public interface IAudioPlaybackControl
{
    /// <summary>
    /// 设置音量
    /// </summary>
    void SetVolume(double volume);
    
    /// <summary>
    /// 获取当前音量
    /// </summary>
    double Volume { get; }

    /// <summary>
    /// 把设备（音量合成器）的当前音量同步到内部状态，供定时轮询调用，实现热键与合成器双向同步
    /// </summary>
    void SyncVolumeFromDevice();

    /// <summary>
    /// 设置播放位置
    /// </summary>
    void SetPosition(TimeSpan position);

    /// <summary>
    /// 定位到指定播放位置，清空输出缓冲以确保音频立即跳转，供快进和快退使用
    /// </summary>
    void SeekTo(TimeSpan position);

    /// <summary>
    /// 当前播放位置
    /// </summary>
    TimeSpan Position { get; }

    /// <summary>
    /// 音乐总时长
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// 当前实际播放音质，由播放器流的编码格式和平均码率确定；无法确定时为 null
    /// </summary>
    string? ActualQuality { get; }
    
    /// <summary>
    /// 获取所有可用输出设备，首项为“系统默认”
    /// </summary>
    IReadOnlyList<OutputDeviceInfo> GetOutputDevices();

    /// <summary>
    /// 当前输出设备索引，-1 表示系统默认
    /// </summary>
    int CurrentOutputDeviceIndex { get; }

    /// <summary>
    /// 当前输出设备名称，供播报使用
    /// </summary>
    string CurrentOutputDeviceName { get; }

    /// <summary>
    /// 切换到下一个播放设备，返回新设备名称；失败时返回 null
    /// </summary>
    string? SwitchToNextOutputDevice();

    /// <summary>
    /// 切换到上一个播放设备，返回新设备名称；失败时返回 null
    /// </summary>
    string? SwitchToPreviousOutputDevice();

    /// <summary>
    /// 切换到指定输出设备，播放中热切换并保留进度；返回新设备名称，失败时返回 null
    /// </summary>
    string? SwitchOutputDevice(int index);
}

/// <summary>
/// 歌词结果，包含来源及是否跨源搜索
/// </summary>
public class LyricResult
{
    /// <summary>解析后的歌词</summary>
    public Lyric Lyric { get; set; } = new();

    /// <summary>歌词实际来源的音源 ID，可能来自同平台或跨平台搜索</summary>
    public string SourceId { get; set; } = "";

    /// <summary>是否通过跨平台搜索获得</summary>
    public bool FromSearch { get; set; }
}

/// <summary>
/// 歌词服务接口
/// </summary>
public interface ILyricService
{
    /// <summary>
    /// 获取歌词：同平台优先，缺失时按 netease→qq→kg→kw→migu 顺序跨平台搜索
    /// </summary>
    Task<LyricResult?> GetLyricAsync(
        MusicInfo music,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// TTS 服务接口
/// </summary>
public interface ITtsService
{
    /// <summary>
    /// 是否有可用语音输出，包括 Prism 后端或 SAPI 兜底
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 当前活动朗读后端的显示名称
    /// </summary>
    string ActiveBackendName { get; }

    /// <summary>
    /// 朗读文本
    /// </summary>
    Task SpeakAsync(string text);

    /// <summary>
    /// 以排队模式朗读文本，不打断当前朗读，追加到语音队列末尾
    /// </summary>
    Task SpeakQueuedAsync(string text);

    /// <summary>
    /// 停止朗读
    /// </summary>
    void Stop();

    /// <summary>
    /// 设置语速
    /// </summary>
    void SetRate(double rate);

    /// <summary>
    /// 设置语音
    /// </summary>
    void SetVoice(string voiceId);

    /// <summary>
    /// 获取可用语音列表
    /// </summary>
    List<VoiceInfo> GetAvailableVoices();
}

/// <summary>
/// 语音信息
/// </summary>
public class VoiceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Culture { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public int Age { get; set; }
}

/// <summary>
/// 全局快捷键注册失败事件参数
/// </summary>
public class HotkeysRegistrationFailedEventArgs : EventArgs
{
    /// <summary>
    /// 本次刷新中注册失败的快捷键列表
    /// </summary>
    public IReadOnlyList<Hotkey> FailedHotkeys { get; set; } = Array.Empty<Hotkey>();
}

/// <summary>
/// 音源管理服务接口
/// </summary>
public interface IMusicSourceService
{
    /// <summary>
    /// 音源列表变化事件（默认/启用/禁用/增删等），供界面刷新音源列表
    /// </summary>
    event EventHandler? SourcesChanged;

    /// <summary>
    /// 脚本调用 lx.send(updateAlert) 时上抛（供界面提示音源更新）
    /// </summary>
    event EventHandler<LxScriptUpdateAlertEventArgs>? ScriptUpdateAlert;

    /// <summary>
    /// 通过脚本源的 lyric 动作解析歌词（失败/无歌词返回 null）
    /// </summary>
    Task<LxLyricRaw?> GetLyricFromScriptsAsync(
        MusicInfo music,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 通过脚本源的 pic 动作解析封面 URL（失败返回 null）
    /// </summary>
    Task<string?> GetPicFromScriptAsync(
        MusicInfo music,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// 搜索音乐
    /// </summary>
    Task<PagedResult<MusicInfo>> SearchAsync(
        string query,
        int page = 1,
        int pageSize = 20,
        string searchType = "song",
        string sourceId = "all",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取音乐信息
    /// </summary>
    Task<MusicInfo?> GetMusicInfoAsync(string musicId, string source);

    /// <summary>
    /// 获取播放 URL
    /// </summary>
    Task<string?> GetPlayUrlAsync(
        string musicId,
        string source,
        string quality = "128k",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 使用完整歌曲信息获取播放 URL，供脚本源通过 Jint 执行
    /// </summary>
    Task<string?> GetPlayUrlAsync(
        MusicInfo music,
        string source,
        string quality = "128k",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取播放列表或分类歌单
    /// </summary>
    Task<PagedResult<Playlist>> GetPlaylistsAsync(int page = 1, int pageSize = 20, string category = "全部");

    /// <summary>
    /// 获取歌单分类
    /// </summary>
    Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(
        string sourceId = "netease",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取歌单详情
    /// </summary>
    Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(
        string playlistId,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default);
    Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(
        string category,
        int page = 1,
        int pageSize = 30,
        string sourceId = "netease",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取排行榜
    /// </summary>
    Task<List<Ranking>> GetRankingsAsync(
        string sourceId = "netease",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取排行榜详情
    /// </summary>
    Task<PagedResult<MusicInfo>> GetRankingDetailAsync(
        string rankingId,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取专辑详情及歌曲列表
    /// </summary>
    Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(
        MusicInfo album,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定音源 API，供歌词等功能使用
    /// </summary>
    IMusicApi? GetApi(string id);

    /// <summary>
    /// 从 URL 导入音源
    /// </summary>
    Task<MusicSource?> ImportFromUrlAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// 从文件导入音源
    /// </summary>
    Task<MusicSource?> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 测试音源
    /// </summary>
    Task<bool> TestSourceAsync(MusicSource source);

    /// <summary>
    /// 启用音源
    /// </summary>
    Task<ScriptActivationResult> EnableSourceAsync(MusicSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// 禁用音源
    /// </summary>
    void DisableSource(MusicSource source);

    /// <summary>
    /// 删除音源
    /// </summary>
    void RemoveSource(MusicSource source);

    /// <summary>
    /// 更新音源
    /// </summary>
    Task<bool> UpdateSourceAsync(MusicSource source);

    /// <summary>
    /// 获取所有音源
    /// </summary>
    List<MusicSource> GetAllSources();

    /// <summary>
    /// 获取默认音源
    /// </summary>
    MusicSource? GetDefaultSource();

    /// <summary>
    /// 设置唯一默认音源并持久化
    /// </summary>
    void SetDefaultSource(string id);

    /// <summary>
    /// 获取指定音源支持的音质列表，合并脚本源与内置源结果
    /// </summary>
    Task<List<string>> GetQualitiesForSourceAsync(string sourceId);

    /// <summary>
    /// 重新加载网络、本地或内嵌内容的脚本音源；内置源直接返回 true
    /// </summary>
    Task<bool> ReloadSourceAsync(MusicSource source);

    /// <summary>
    /// 持久化全部音源
    /// </summary>
    Task PersistSourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取已启用的音源
    /// </summary>
    List<MusicSource> GetEnabledSources();

    /// <summary>
    /// 清除缓存
    /// </summary>
    void ClearCache();
}
