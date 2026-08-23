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

/// <summary>
/// 音乐播放器服务接口
/// </summary>
public interface IMusicPlayerService
{
    /// <summary>
    /// 播放指定音乐
    /// </summary>
    Task PlayAsync(string url, MusicInfo musicInfo);

    /// <summary>
    /// 暂停播放
    /// </summary>
    void Pause();

    /// <summary>
    /// 停止播放
    /// </summary>
    void Stop();

    /// <summary>
    /// 下一首
    /// </summary>
    void Next();

    /// <summary>
    /// 上一首
    /// </summary>
    void Previous();

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
    /// 是否正在播放
    /// </summary>
    bool IsPlaying { get; }
    /// <summary>
    /// 是否处于暂停状态
    /// </summary>
    bool IsPaused { get; }

    /// <summary>
    /// 恢复播放，不重新加载，从暂停位置继续
    /// </summary>
    Task ResumeAsync();

    /// <summary>
    /// 从来源列表构建播放队列并定位当前歌曲，使上一首、下一首和自动连播作用于整个列表
    /// </summary>
    void SetQueue(List<MusicInfo> queue, MusicInfo currentMusic);

    /// <summary>
    /// 播放队列条目替换（同曲换源用）：以实际播放的同曲替换原条目，保持上一首/下一首位置正确
    /// </summary>
    void ReplaceQueueItem(MusicInfo oldItem, MusicInfo newItem);

    /// <summary>
    /// 当前播放音乐信息
    /// </summary>
    MusicInfo? CurrentMusic { get; }

    /// <summary>
    /// 当前实际播放音质，由播放器流的编码格式和平均码率确定；无法确定时为 null
    /// </summary>
    string? ActualQuality { get; }
    
    /// <summary>
    /// 循环播放模式
    /// </summary>
    bool IsLoopMode { get; set; }
    
    /// <summary>
    /// 随机播放模式
    /// </summary>
    bool IsShuffleMode { get; set; }

    /// <summary>
    /// 自动连播、上一首和下一首解析播放链接时使用的默认音质
    /// </summary>
    string DefaultQuality { get; set; }

    /// <summary>
    /// 播放器状态变化事件
    /// </summary>
    event EventHandler<PlayerStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 播放队列已播完事件，在顺序播放到末尾且非循环模式时触发，供自动翻页续播
    /// </summary>
    event EventHandler? QueueCompleted;

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
    Task<LyricResult?> GetLyricAsync(MusicInfo music);

    /// <summary>
    /// 获取当前歌词行
    /// </summary>
    LyricLine? GetCurrentLine(TimeSpan position);

    /// <summary>
    /// 获取所有歌词行
    /// </summary>
    List<LyricLine> GetAllLines();

    /// <summary>
    /// 歌词行变化事件
    /// </summary>
    event EventHandler<LyricLineChangedEventArgs>? LineChanged;

    /// <summary>
    /// 设置歌词
    /// </summary>
    void SetLyric(Lyric lyric);
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
/// 快捷键管理器接口
/// </summary>
public interface IHotkeyManager
{
    /// <summary>
    /// 注册快捷键
    /// </summary>
    void RegisterHotkey(Hotkey hotkey);

    /// <summary>
    /// 注销快捷键
    /// </summary>
    void UnregisterHotkey(Hotkey hotkey);

    /// <summary>
    /// 注册全局快捷键
    /// </summary>
    void RegisterGlobalHotkey(Hotkey hotkey);

    /// <summary>
    /// 注销全局快捷键
    /// </summary>
    void UnregisterGlobalHotkey(Hotkey hotkey);

    /// <summary>
    /// 快捷键按下事件
    /// </summary>
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    /// <summary>
    /// 检查快捷键冲突
    /// </summary>
    bool CheckConflict(Hotkey hotkey);

    /// <summary>
    /// 载入快捷键配置，缺失动作自动补默认值
    /// </summary>
    void LoadHotkeys(IEnumerable<Hotkey> hotkeys);

    /// <summary>
    /// 获取全部快捷键
    /// </summary>
    List<Hotkey> GetAllHotkeys();

    /// <summary>
    /// 初始化全局快捷键注册，失败时触发 HotkeysRegistrationFailed
    /// </summary>
    void InitializeGlobalHotkeys(IntPtr windowHandle);

    /// <summary>
    /// 重新注册全部全局快捷键
    /// </summary>
    void RefreshGlobalHotkeys();

    /// <summary>
    /// 注销全部全局快捷键
    /// </summary>
    void UnregisterAllGlobal();

    /// <summary>
    /// 按全局注册 ID 触发快捷键，由 WM_HOTKEY 调用
    /// </summary>
    void RaiseGlobalHotkey(int id);

    /// <summary>
    /// 程序内兜底匹配，仅返回全局注册失败的组合
    /// </summary>
    Hotkey? MatchFallback(Key key, ModifierKeys modifiers);

    /// <summary>
    /// 程序内局部快捷键匹配，仅匹配 IsGlobal=false 的快捷键，由主窗口 PreviewKeyDown 调用
    /// </summary>
    Hotkey? MatchLocalHotkey(Key key, ModifierKeys modifiers);

    /// <summary>
    /// 全局快捷键注册失败事件
    /// </summary>
    event EventHandler<HotkeysRegistrationFailedEventArgs>? HotkeysRegistrationFailed;

    /// <summary>
    /// 检测组合键是否已被其他程序占用，使用临时注册探测并在成功后立即注销
    /// </summary>
    bool IsComboOccupied(Key key, ModifierKeys modifiers);

    /// <summary>
    /// 判断指定全局快捷键当前是否已在系统注册成功，供设置列表标记冲突
    /// </summary>
    bool IsGlobalHotkeyRegistered(Hotkey hotkey);
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
    Task<LxLyricRaw?> GetLyricFromScriptsAsync(MusicInfo music);

    /// <summary>
    /// 通过脚本源的 pic 动作解析封面 URL（失败返回 null）
    /// </summary>
    Task<string?> GetPicFromScriptAsync(MusicInfo music);
    /// <summary>
    /// 搜索音乐
    /// </summary>
    Task<PagedResult<MusicInfo>> SearchAsync(string query, int page = 1, int pageSize = 20, string searchType = "song", string sourceId = "all");

    /// <summary>
    /// 获取音乐信息
    /// </summary>
    Task<MusicInfo?> GetMusicInfoAsync(string musicId, string source);

    /// <summary>
    /// 获取播放 URL
    /// </summary>
    Task<string?> GetPlayUrlAsync(string musicId, string source, string quality = "128k");

    /// <summary>
    /// 使用完整歌曲信息获取播放 URL，供脚本源通过 Jint 执行
    /// </summary>
    Task<string?> GetPlayUrlAsync(MusicInfo music, string source, string quality = "128k");

    /// <summary>
    /// 获取播放列表或分类歌单
    /// </summary>
    Task<PagedResult<Playlist>> GetPlaylistsAsync(int page = 1, int pageSize = 20, string category = "全部");

    /// <summary>
    /// 获取歌单分类
    /// </summary>
    Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId = "netease");

    /// <summary>
    /// 获取歌单详情
    /// </summary>
    Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50);
    Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page = 1, int pageSize = 30, string sourceId = "netease");

    /// <summary>
    /// 获取排行榜
    /// </summary>
    Task<List<Ranking>> GetRankingsAsync(string sourceId = "netease");

    /// <summary>
    /// 获取排行榜详情
    /// </summary>
    Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50);

    /// <summary>
    /// 获取专辑详情及歌曲列表
    /// </summary>
    Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50);

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
    Task PersistSourcesAsync();

    /// <summary>
    /// 获取已启用的音源
    /// </summary>
    List<MusicSource> GetEnabledSources();

    /// <summary>
    /// 清除缓存
    /// </summary>
    void ClearCache();
}
