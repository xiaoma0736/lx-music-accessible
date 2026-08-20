namespace LXMusicAccessible.Audio;

/// <summary>
/// 音频播放器状态
/// </summary>
public enum AudioPlayerState
{
    Stopped,
    Playing,
    Paused,
    Loading,
    Error
}

/// <summary>
/// 音频输出设备信息
/// </summary>
public class OutputDeviceInfo
{
    /// <summary>设备索引（-1 表示系统默认设备）</summary>
    public int Index { get; set; }

    /// <summary>设备名称（如“扬声器 (2- Realtek(R) Audio)”）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>是否系统默认设备</summary>
    public bool IsDefault => Index < 0;

    public override string ToString() => Name;
}

/// <summary>
/// 音频播放器接口
/// </summary>
public interface IAudioPlayer
{
    Task LoadAsync(string url);
    Task PlayAsync();
    void Pause();
    void Stop();
    double Volume { get; set; }
    TimeSpan Position { get; set; }
    TimeSpan Duration { get; }

    /// <summary>读取当前实际播放音质（按流编码格式与码率映射，如 128k/192k/320k/flac）；无流或无法确定返回 null</summary>
    string? GetActualQuality();
    AudioPlayerState State { get; set; }
    event EventHandler<AudioPlayerState>? StateChanged;

    /// <summary>自然播放完毕事件（用于自动下一首）</summary>
    event EventHandler? PlaybackCompleted;

    /// <summary>获取所有可用输出设备（首项为“系统默认”）</summary>
    IReadOnlyList<OutputDeviceInfo> GetOutputDevices();

    /// <summary>当前输出设备索引（-1 = 系统默认）</summary>
    int CurrentOutputDeviceIndex { get; }

    /// <summary>切换输出设备：播放中热切换（保留进度继续播放），未播放时仅记录索引。返回是否成功。</summary>
    bool SwitchOutputDevice(int index);

    /// <summary>
    /// 定位到指定播放位置：重建输出播放器以清空缓冲，确保音频立即跳到新位置
    /// （避免快进/快退后旧缓冲仍先播放，导致歌词/进度先于听到的歌曲提前）。
    /// </summary>
    void SeekTo(TimeSpan position);

    void Dispose();
}
