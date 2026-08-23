using System.Windows.Input;
using System.ComponentModel;

namespace LXMusicAccessible.Models;

/// <summary>
/// 无障碍相关模型
/// </summary>
public class AccessibilitySettings
{
    /// <summary>
    /// 是否启用屏幕阅读器
    /// </summary>
    public bool EnableScreenReader { get; set; } = true;

    /// <summary>
    /// 语音提示音量
    /// </summary>
    public double AnnouncementVolume { get; set; } = 0.8;

    /// <summary>
    /// 语音提示语速（1.0 = 标准）
    /// </summary>
    public double AnnouncementRate { get; set; } = 1.0;

    /// <summary>
    /// 朗读语音 ID（Prism 后端语音序号或 SAPI 语音 ID；空 = 默认）
    /// </summary>
    public string AnnouncementVoiceId { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用焦点导航提示
    /// </summary>
    public bool EnableFocusAnnouncements { get; set; } = true;

    /// <summary>
    /// 焦点导航延迟（毫秒）
    /// </summary>
    public int FocusAnnouncementDelay { get; set; } = 300;
}

/// <summary>
/// 播放器状态变化事件参数
/// </summary>
public class PlayerStateChangedEventArgs : EventArgs
{
    public PlayerState OldState { get; set; }
    public PlayerState NewState { get; set; }
    public MusicInfo? CurrentMusic { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// 播放器状态
/// </summary>
public enum PlayerState
{
    Stopped,
    Playing,
    Paused,
    Loading,
    Error
}

/// <summary>
/// 歌词行变化事件参数
/// </summary>
public class LyricLineChangedEventArgs : EventArgs
{
    public LyricLine? CurrentLine { get; set; }
    public LyricLine? PreviousLine { get; set; }
    public TimeSpan Position { get; set; }
    public int CurrentIndex { get; set; }
    public int PreviousIndex { get; set; }
}

/// <summary>
/// 快捷键
/// </summary>
public class Hotkey
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>动作标识（对应 HotkeyActions 常量，用于分发执行）</summary>
    public string ActionId { get; set; } = string.Empty;
    public Key Key { get; set; }
    public ModifierKeys Modifiers { get; set; }
    public bool IsGlobal { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// 可配置快捷键动作常量
/// </summary>
public static class HotkeyActions
{
    public const string PlayPause = "play_pause";
    public const string Stop = "stop";
    public const string Previous = "previous";
    public const string Next = "next";
    public const string Rewind = "rewind";
    public const string Forward = "forward";
    public const string TogglePlayMode = "toggle_play_mode";
    public const string Download = "download";
    public const string VolumeUp = "volume_up";
    public const string VolumeDown = "volume_down";
    public const string SwitchNextOutputDevice = "switch_next_output_device";
    public const string SwitchPreviousOutputDevice = "switch_previous_output_device";
    public const string LyricDelay = "lyric_delay";
    public const string LyricAdvance = "lyric_advance";
    public const string ToggleWindow = "toggle_window";

    public static readonly string[] All =
    {
        PlayPause, Stop, Previous, Next, Rewind, Forward,
        TogglePlayMode, Download, VolumeUp, VolumeDown,
        SwitchNextOutputDevice, SwitchPreviousOutputDevice,
        LyricDelay, LyricAdvance,
        ToggleWindow
    };
}

/// <summary>
/// 快捷键设置页列表项（设置界面绑定用）
/// </summary>
public class HotkeySettingItem : INotifyPropertyChanged
{
    public string ActionId { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    /// <summary>是否为全局快捷键；false 表示局部快捷键（仅程序内生效）</summary>
    public bool IsGlobal { get; set; } = true;
    public bool IsEnabled { get; set; } = true;

    private string _shortcutText = string.Empty;
    public string ShortcutText
    {
        get => _shortcutText;
        set { if (_shortcutText != value) { _shortcutText = value; OnPropertyChanged(nameof(ShortcutText)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public override string ToString() => $"{ActionName}：{ShortcutText}";
}

/// <summary>
/// 快捷键按下事件参数
/// </summary>
public class HotkeyPressedEventArgs : EventArgs
{
    public Hotkey Hotkey { get; set; } = null!;
    public DateTime PressedTime { get; set; }
    public Key Key { get; set; }
    public ModifierKeys Modifiers { get; set; }
}

/// <summary>
/// 用户设置
/// </summary>
/// <summary>
/// 下载文件命名顺序
/// </summary>
public enum DownloadNameOrder
{
    /// <summary>艺术家 - 歌名</summary>
    ArtistTitle = 0,

    /// <summary>歌名 - 艺术家</summary>
    TitleArtist = 1
}
public class UserSettings
{
    /// <summary>
    /// 默认音量
    /// </summary>
    public double DefaultVolume { get; set; } = 0.8;

    /// <summary>
    /// 窗口宽度
    /// </summary>
    public int WindowWidth { get; set; } = 1200;

    /// <summary>
    /// 窗口高度
    /// </summary>
    public int WindowHeight { get; set; } = 800;

    /// <summary>
    /// 是否自动播放
    /// </summary>
    public bool AutoPlay { get; set; } = true;

    /// <summary>
    /// 是否循环播放
    /// </summary>
    public bool LoopMode { get; set; } = false;

    /// <summary>
    /// 是否随机播放
    /// </summary>
    public bool ShuffleMode { get; set; } = false;

    /// <summary>
    /// 默认搜索音源
    /// </summary>
    public string DefaultSearchSource { get; set; } = "all";

    /// <summary>
    /// 默认播放音质
    /// </summary>
    public string DefaultPlayQuality { get; set; } = "128k";

    /// <summary>
    /// 下载目录（空则使用 我的音乐\落雪音乐下载）
    /// </summary>
    public string DownloadPath { get; set; } = string.Empty;

    /// <summary>
    /// 默认下载音质（批量下载/整张专辑下载/单曲下载使用；下载不了该音质时回退到最高可用音质）
    /// </summary>
    public string DefaultDownloadQuality { get; set; } = "128k";

    /// <summary>
    /// 下载时是否同时下载歌词（.lrc 文件）
    /// </summary>
    public bool DownloadLyric { get; set; } = true;

    /// <summary>
    /// 下载文件命名顺序（ArtistTitle=艺术家-歌名，TitleArtist=歌名-艺术家）
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public DownloadNameOrder DownloadNameOrder { get; set; } = DownloadNameOrder.ArtistTitle;

    /// <summary>
    /// 下载文件名是否包含音质标签（如 [mp3]）
    /// </summary>
    public bool DownloadNameIncludeQuality { get; set; } = true;

    /// <summary>
    /// 播放时是否自动朗读当前歌词
    /// </summary>
    public bool LyricAutoAnnounce { get; set; } = true;

    /// <summary>
    /// 歌词页是否自动滚动到当前句
    /// </summary>
    public bool LyricAutoScroll { get; set; } = true;

    /// <summary>
    /// 默认音源ID（搜索默认选中 + 播放解析优先）
    /// </summary>
    public string DefaultSourceId { get; set; } = "netease";

    /// <summary>
    /// 无障碍设置
    /// </summary>
    public AccessibilitySettings AccessibilitySettings { get; set; } = new();

    /// <summary>
    /// 自定义快捷键
    /// </summary>
    public List<Hotkey> CustomHotkeys { get; set; } = new();

    /// <summary>
    /// 输出设备索引（-1 = 系统默认，重启后自动恢复）
    /// </summary>
    public int OutputDeviceIndex { get; set; } = -1;

    /// <summary>上次关闭时捕获的播放状态；启动时可读取，但不会隐式联网或自动播放。</summary>
    public PlaybackSessionState Playback { get; set; } = new();

    /// <summary>
    /// 本地收藏（网络歌曲收藏）的显示名称，可在播放列表页重命名
    /// </summary>
    public string FavoritePlaylistName { get; set; } = "网络歌曲收藏";

    /// <summary>
    /// 按 Alt+F4 时是关闭应用（false）还是隐藏到系统托盘（true）
    /// </summary>
    public bool HideToTrayOnAltF4 { get; set; } = false;

    /// <summary>
    /// 第三方播放器 exe 路径（歌曲右键菜单“用第三方播放器播放”）
    /// </summary>
    public string ThirdPartyPlayerPath { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用屏幕阅读器
    /// </summary>
    public bool EnableScreenReader { get; set; } = true;

    /// <summary>
    /// 是否启用语音提示
    /// </summary>
    public bool EnableVoiceAnnouncement { get; set; } = true;

    /// <summary>Per-song lyric time offset in ms, keyed by "source|id", to compensate
    /// for lyrics whose timestamps are shifted relative to the audio (e.g. AI-generated
    /// or a different version). Positive = announce later (fixes lyrics reading early).</summary>
    public Dictionary<string, int> LyricOffsets { get; set; } = new();
}

public sealed class PlaybackSessionState
{
    public MusicInfo? CurrentMusic { get; set; }
    public double PositionSeconds { get; set; }
    public bool WasPlaying { get; set; }
    public bool WasPaused { get; set; }
}

/// <summary>
/// 搜索历史记录
/// </summary>
public class SearchHistory
{
    public string Keyword { get; set; } = string.Empty;
    public DateTime SearchTime { get; set; } = DateTime.Now;
    public string Source { get; set; } = string.Empty;
    public int ResultCount { get; set; }

    public override string ToString() => Keyword;
}

