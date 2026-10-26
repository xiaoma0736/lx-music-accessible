using System.Diagnostics;
using System.Runtime.InteropServices;
using ManagedBass;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LXMusicAccessible.Audio;

/// <summary>
/// BASS 音频播放器实现（ManagedBass 4.x + bass/bassflac/bassape/bassopus 原生库）。
/// 对外契约（IAudioPlayer）与旧 NAudio 实现完全一致，替换播放器不改变任何操作习惯。
/// 说明：
///  - 在线歌曲默认"边下边播"（BASS URL 流式，启动更快）；个别链接建流失败时自动回退"先下载到临时文件再播放"。
///  - 音量：完全由 BASS 通道软件音量控制（各设备一致，切换设备后音量不变），只在软件内调整；
///    不读写 Windows 音量合成器（会话音量），避免切换设备时会话停在旧值导致音量被拉偏。
///    应用音量 _volume 为唯一真值。
///  - 位置/时长使用 BASS 精确解码位置，歌词对齐更准；暂停冻结/停止归零行为与旧播放器一致。
///  - 32 位浮点解码 + 高质量重采样配置，改善音质、降低过载/爆音。
/// </summary>
public class AudioPlayer : IAudioPlayer
{
    private const int DefaultOutputRate = 44100;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<AudioPlayer> _logger;

    private int _stream;                       // BASS 流句柄（0 = 无）
    private int _endSync;                      // BASS 播完同步句柄
    private string? _loadedSource;             // 当前加载的来源：URL（流式）或本地文件路径
    private string? _tempFilePath;             // 在线歌曲回退下载/第三方临时文件
    private AudioPlayerState _state = AudioPlayerState.Stopped;
    private double _volume = 0.8;
    private TimeSpan _position = TimeSpan.Zero; // 未播放/已暂停/已停止时的锚定位置
    private bool _manualStop;
    private bool _bassReady;
    private bool _pluginsLoaded;
    private int _disposed;
    private System.Threading.Timer? _playbackStateTimer;
    private DateTimeOffset? _stallStartedUtc;
    private static readonly TimeSpan StallRecoveryTimeout = TimeSpan.FromSeconds(30);

    // 输出设备：app 空间索引 -1 = 系统默认；具体设备 0..n
    // BASS Windows 设备布局：索引 0 = 无声、1 = 默认、2+ = 具体设备
    private int _outputDeviceIndex = -1;
    private int _initedBassDevice = int.MinValue; // 当前已初始化的 BASS 设备索引

    public event EventHandler<AudioPlayerState>? StateChanged;
    public event EventHandler? PlaybackCompleted;

    public TimeSpan Position
    {
        get
        {
            // 仅"正在播放"时读 BASS 实时解码位置；暂停/停止时返回锚定值。
            // BASS 在 Pause 后 ChannelGetPosition 仍会因内部缓冲略微前移、Stop 后返回旧位置，
            // 因此不能无条件读取，否则与旧播放器的暂停冻结/停止归零行为不一致。
            if (_stream != 0 && Bass.ChannelIsActive(_stream) == PlaybackState.Playing)
            {
                long bytes = Bass.ChannelGetPosition(_stream, PositionFlags.Bytes);
                if (bytes > 0)
                {
                    double secs = Bass.ChannelBytes2Seconds(_stream, bytes);
                    if (secs > 0) return TimeSpan.FromSeconds(secs);
                }
            }
            return _position;
        }
        set
        {
            _position = value;
            if (_stream != 0 && value >= TimeSpan.Zero)
            {
                Bass.ChannelSetPosition(_stream, Bass.ChannelSeconds2Bytes(_stream, value.TotalSeconds), PositionFlags.Bytes);
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            if (_stream != 0)
            {
                long len = Bass.ChannelGetLength(_stream, PositionFlags.Bytes);
                if (len > 0)
                {
                    double secs = Bass.ChannelBytes2Seconds(_stream, len);
                    if (secs > 0) return TimeSpan.FromSeconds(secs);
                }
            }
            return TimeSpan.Zero;
        }
    }

    /// <summary>读取当前 BASS 流实际播放音质（按编码格式与码率映射为 128k/192k/320k/flac 等标签；无流或无法确定时返回 null）</summary>
    public string? GetActualQuality()
    {
        if (_stream == 0) return null;
        try
        {
            if (!Bass.ChannelGetInfo(_stream, out var info)) return null;
            switch (info.ChannelType)
            {
                case ChannelType.FLAC:
                case ChannelType.FLAC_OGG:
                    return "flac";
                case ChannelType.APE:
                    return "ape";
                case ChannelType.ALAC:
                    return "alac";
                case ChannelType.OPUS:
                    return "opus";
                case ChannelType.Wave:
                case ChannelType.WaveFloat:
                case ChannelType.WavePCM:
                    return "wav";
                case ChannelType.MP1:
                case ChannelType.MP2:
                case ChannelType.MP3:
                case ChannelType.OGG:
                case ChannelType.AAC:
                case ChannelType.MP4:
                case ChannelType.WMA:
                case ChannelType.WMA_MP3:
                    return BitrateLabel(GetStreamBitrate());
                default:
                    return null;
            }
        }
        catch (Exception exception)
        {
            LogNativeFallback("quality", exception);
            return null;
        }
    }

    /// <summary>读取当前流的平均码率（kbps）；不可用返回 null</summary>
    private float? GetStreamBitrate()
    {
        if (_stream == 0) return null;
        try
        {
            if (Bass.ChannelGetAttribute(_stream, ChannelAttribute.Bitrate, out float bitrate) && bitrate > 0)
                return bitrate;
        }
        catch (Exception exception) { LogNativeFallback("bitrate", exception); }
        return null;
    }

    /// <summary>把平均码率映射为标准音质标签（128k/192k/320k）；码率未知返回 null</summary>
    private static string? BitrateLabel(float? kbps)
    {
        if (!kbps.HasValue || kbps.Value <= 0) return null;
        var kb = (int)Math.Round(kbps.Value);
        return kb switch
        {
            <= 160 => "128k",
            <= 256 => "192k",
            _ => "320k"
        };
    }

    public double Volume
    {
        get
        {
            // 应用音量以 _volume 为唯一真值（稳定源）。音量完全由 BASS 通道软件音量控制，
            // 不读写 Windows 音量合成器（会话音量），因此不会因会话停在旧值而被拉偏。
            return _volume;
        }
        set
        {
            _volume = FrameworkCompatibility.Clamp(value, 0.0, 1.0);
            ApplyVolume();
        }
    }

    public AudioPlayerState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, value);
            }
        }
    }

    public int CurrentOutputDeviceIndex => _outputDeviceIndex;

    public AudioPlayer()
        : this(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<AudioPlayer>.Instance,
            ownsHttpClient: true)
    {
    }

    public AudioPlayer(HttpClient httpClient, ILogger<AudioPlayer> logger)
        : this(httpClient, logger, ownsHttpClient: false)
    {
    }

    private AudioPlayer(
        HttpClient httpClient,
        ILogger<AudioPlayer> logger,
        bool ownsHttpClient)
    {
        _httpClient = httpClient;
        _logger = logger;
        _ownsHttpClient = ownsHttpClient;
        // 第一次触碰 Bass 静态成员时 ManagedBass 会自动从程序目录加载 bass.dll；
        // 这里主动读取一次版本以便尽早暴露"缺 bass.dll"问题。
        try { _ = Bass.Version; }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "BASS library load failed: {ErrorType}",
                exception.GetType().Name);
        }
        // 启动即初始化 BASS（插件/配置/默认设备），让第一首歌播放时不再付出初始化耗时
        try { EnsureBassReady(); }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "BASS startup initialization failed: {ErrorType}",
                exception.GetType().Name);
        }
    }

    // ============ BASS 初始化 / 设备管理 ============

    /// <summary>把 app 空间设备索引映射为 BASS 初始化设备索引（-1→默认(-1)；具体 k→k+2）</summary>
    private int MapAppToBass(int appIndex) => appIndex == -1 ? Bass.DefaultDevice : (appIndex + 2);

    /// <summary>把 app 空间设备索引映射为 BASS 设备上下文/移动目标索引（默认→其真实索引，通常 1；具体 k→k+2；绝不返回 -1）</summary>
    private int BassIndexForApp(int appIndex)
    {
        if (appIndex != -1) return appIndex + 2;
        // 默认设备：按 IsDefault 扫描取最低索引（BASS Windows 布局下通常为 1）
        try
        {
            int count = Bass.DeviceCount;
            for (int i = 1; i < count; i++)
            {
                var info = Bass.GetDeviceInfo(i);
                if (info.IsDefault) return i;
            }
        }
        catch (Exception exception) { LogNativeFallback("default-device", exception); }
        return 1;
    }

    /// <summary>当前输出设备的 BASS 线程设备上下文索引（供 Bass.CurrentDevice 使用）</summary>
    private int BassDeviceIndexForCurrent => BassIndexForApp(_outputDeviceIndex);

    private void EnsureBassReady()
    {
        if (!_bassReady)
        {
            // 加载格式插件（FLAC/APE/Opus），dll 与 bass.dll 同目录（程序根目录）
            if (!_pluginsLoaded)
            {
                LoadPlugin("bassflac.dll");
                LoadPlugin("bassape.dll");
                LoadPlugin("bassopus.dll");
                _pluginsLoaded = true;
            }
            // 高质量配置：32 位浮点 DSP + 高质量重采样（0~5），减少过载/爆音
            TryConfigureBass(() => Bass.FloatingPointDSP = true, "floating-point-dsp");
            TryConfigureBass(() => Bass.Configure(Configuration.SRCQuality, 5), "sample-rate-quality");
            // 流式网络配置：浏览器 UA 降低被 CDN 拒绝风险；预缓冲/缓冲/超时调优
            TryConfigureBass(
                () => Bass.NetAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36",
                "network-agent");
            TryConfigureBass(() => Bass.NetPreBuffer = 40, "network-prebuffer");
            TryConfigureBass(() => Bass.NetBufferLength = 262144, "network-buffer");
            TryConfigureBass(() => Bass.NetTimeOut = 15000, "network-timeout");
            _bassReady = true;
        }

        var target = MapAppToBass(_outputDeviceIndex);
        if (_initedBassDevice != target)
        {
            if (!Bass.Init(target, DefaultOutputRate, DeviceInitFlags.Default, IntPtr.Zero, IntPtr.Zero))
            {
                var err = Bass.LastError;
                if (err != Errors.Already)
                    throw new InvalidOperationException($"BASS 初始化失败(设备 {_outputDeviceIndex}): {err}");
                // 设备已初始化（切换设备时再次初始化同设备），视为成功
            }
            _initedBassDevice = target;
        }
    }

    private void LoadPlugin(string dll)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, dll);
            if (!File.Exists(path))
            {
                Debug.WriteLine($"BASS 插件缺失: {path}");
                return;
            }
            Bass.PluginLoad(path);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "BASS plugin load failed ({Plugin}): {ErrorType}",
                Path.GetFileName(dll),
                exception.GetType().Name);
        }
    }

    public IReadOnlyList<OutputDeviceInfo> GetOutputDevices()
    {
        var list = new List<OutputDeviceInfo> { new() { Index = -1, Name = "系统默认" } };
        try
        {
            int count = Bass.DeviceCount;
            for (int i = 2; i < count; i++)
            {
                try
                {
                    var info = Bass.GetDeviceInfo(i);
                    if (!info.IsEnabled || string.IsNullOrEmpty(info.Name)) continue;
                    list.Add(new OutputDeviceInfo { Index = i - 2, Name = info.Name });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("音频设备枚举失败: DeviceIndex={DeviceIndex}, ErrorType={ErrorType}",
                        i, ex.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"枚举输出设备失败: {ex.Message}");
        }
        return list;
    }

    /// <summary>切换输出设备：播放中热切换（用 BASS_ChannelSetDevice 把现有流搬到目标设备，保留缓冲与进度，不重连/重下；默认设备用其真实索引 1），未播放时仅记录索引</summary>
    public bool SwitchOutputDevice(int index)
    {
        if (index < -1) index = -1;
        var devices = GetOutputDevices();
        if (index != -1 && !devices.Any(device => device.Index == index))
            return false;
        if (index == _outputDeviceIndex) return true;

        // 未播放/未加载：仅记录索引，下次加载生效
        if (_stream == 0)
        {
            _outputDeviceIndex = index;
            _initedBassDevice = int.MinValue; // 强制下次加载时重新初始化目标设备
            return true;
        }

        var previousIndex = _outputDeviceIndex;
        try
        {
            var targetBassIndex = BassIndexForApp(index);
            var wasPlaying = Bass.ChannelIsActive(_stream) == PlaybackState.Playing;

            // 初始化目标设备
            _outputDeviceIndex = index;
            _initedBassDevice = int.MinValue;
            EnsureBassReady();

            // 把现有流直接搬到目标设备：保留已缓冲数据与播放进度，避免流式歌曲重连/重新下载
            if (!Bass.ChannelSetDevice(_stream, targetBassIndex))
            {
                Debug.WriteLine($"BASS 移动流到设备({index})失败: {Bass.LastError}");
                // 回退：在新设备上重建流（本地文件可靠；流式若 URL 失效则失败）
                var src = _loadedSource;
                ReleaseStream();
                _stream = CreateStreamCore(src!);
                if (_stream == 0) throw new InvalidOperationException($"BASS 重建流失败: {Bass.LastError}");
                AttachEndSync();
            }

            // ChannelSetDevice 通常保持播放；防御性恢复
            if (wasPlaying && Bass.ChannelIsActive(_stream) != PlaybackState.Playing)
                Bass.ChannelPlay(_stream, false);

            ApplyVolume();
            _position = Position;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"切换输出设备失败: {ex.Message}");
            // Do not leave the failed device selected: otherwise every later LoadAsync tries it
            // again and the whole player stays unusable until the user finds the device menu.
            _outputDeviceIndex = previousIndex == -1 || devices.Any(device => device.Index == previousIndex)
                ? previousIndex
                : -1;
            _initedBassDevice = int.MinValue;
            try { EnsureBassReady(); }
            catch (Exception restoreException)
            {
                _initedBassDevice = int.MinValue;
                LogNativeFallback("device-restore", restoreException);
            }
            CleanupFailedDeviceSwitch();
            return false;
        }
    }

    // ============ 加载 / 播放控制 ============

    public async Task LoadAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleaseStream();
            // A previous fallback stream may have downloaded to a temp file; a new load has already
            // released that stream, so the old temp copy must not survive another session.
            DeleteTempFile();
            _manualStop = false;
            _position = TimeSpan.Zero;

            EnsureBassReady();
            if (IsHttpUrl(url))
            {
                // 在线歌曲：默认边下边播（BASS URL 流式，启动更快）
                _loadedSource = url;
                _stream = CreateStreamCore(_loadedSource);
                if (_stream == 0)
                {
                    // URL 建流失败（个别 CDN/HTTPS 兼容问题）：回退为先下载到临时文件再播放
                    Debug.WriteLine($"URL 流式建流失败({Bass.LastError})，回退下载后播放");
                    await DownloadToTempAsync(url, cancellationToken);
                    _loadedSource = _tempFilePath
                        ?? throw new InvalidOperationException("Audio temporary file was not created.");
                    _stream = CreateStreamCore(_loadedSource);
                }
            }
            else
            {
                // 本地文件：直接建文件流
                _loadedSource = url;
                _stream = CreateStreamCore(_loadedSource);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_stream == 0)
            {
                State = AudioPlayerState.Error;
                throw new InvalidOperationException($"无法创建音频流: {Bass.LastError}");
            }

            AttachEndSync();
            ApplyVolume();

            State = AudioPlayerState.Stopped;
            await Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            ReleaseStream();
            DeleteTempFile();
            throw;
        }
        catch (Exception ex)
        {
            ReleaseStream();
            DeleteTempFile();
            State = AudioPlayerState.Error;
            throw new InvalidOperationException("无法加载音频文件", ex);
    }
    }

    /// <summary>把在线 URL 完整下载到临时文件（回退/第三方播放共用）</summary>
    private async Task DownloadToTempAsync(string url, CancellationToken cancellationToken)
    {
        DeleteTempFile();
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"lxmusic_{Guid.NewGuid():N}.tmp");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await _httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            using var fs = File.Create(_tempFilePath);
            using var contentStream = await response.Content.ReadAsStreamAsync();
            await contentStream.CopyToAsync(fs, 81920, timeout.Token);
        }
        catch
        {
            DeleteTempFile();
            throw;
        }
    }

    /// <summary>在当前输出设备上创建解码流：URL 走流式，本地路径走文件流（均优先 32 位浮点，失败回退默认标志）</summary>
    private int CreateStreamCore(string source)
    {
        EnsureBassReady();
        Bass.CurrentDevice = BassDeviceIndexForCurrent; // 确立当前线程设备上下文（BASS 不接受 -1）
        if (IsHttpUrl(source))
        {
            var s = Bass.CreateStream(source, 0, BassFlags.Float, null, IntPtr.Zero);
            if (s == 0)
                s = Bass.CreateStream(source, 0, BassFlags.Default, null, IntPtr.Zero);
            return s;
        }
        var stream = Bass.CreateStream(source, 0, 0, BassFlags.Float);
        if (stream == 0)
            stream = Bass.CreateStream(source, 0, 0, BassFlags.Default);
        return stream;
    }

    public async Task PlayAsync()
    {
        if (_stream == 0)
        {
            State = AudioPlayerState.Error;
            throw new InvalidOperationException("音频文件未加载");
        }
        if (!Bass.ChannelPlay(_stream, false))
        {
            State = AudioPlayerState.Error;
            throw new InvalidOperationException($"播放失败: {Bass.LastError}");
        }
        State = AudioPlayerState.Playing;
        StartPlaybackStateMonitor();
        await Task.CompletedTask;
    }

    public void Pause()
    {
        if (_stream != 0)
        {
            _position = Position; // 暂停前锚定位置（BASS Pause 后位置仍会轻微前移，故以锚定值为准）
            Bass.ChannelPause(_stream);
            StopPlaybackStateMonitor();
            State = AudioPlayerState.Paused;
        }
    }

    public void Stop()
    {
        _manualStop = true;
        if (_stream != 0)
        {
            Bass.ChannelStop(_stream);
        }
        StopPlaybackStateMonitor();
        _position = TimeSpan.Zero;
        State = AudioPlayerState.Stopped;
    }

    /// <summary>定位到指定播放位置（供快进/快退；BASS 设置位置会清空输出缓冲）</summary>
    public void SeekTo(TimeSpan position)
    {
        if (_stream == 0) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        var dur = Duration;
        if (dur > TimeSpan.Zero && position > dur) position = dur;

        var wasPlaying = Bass.ChannelIsActive(_stream) == PlaybackState.Playing;
        if (!Bass.ChannelSetPosition(_stream, Bass.ChannelSeconds2Bytes(_stream, position.TotalSeconds), PositionFlags.Bytes))
            return;
        _position = Position;
        if (wasPlaying)
            Bass.ChannelPlay(_stream, false);
    }

    // ============ 音量 ============

    /// <summary>
    /// 应用音量：完全由 BASS 通道软件音量控制，不与 Windows 音量合成器（会话音量）交互。
    /// 音量只在软件内调整；合成器会话在切换设备时可能停在旧值上导致音量被拉偏，因此完全不读写会话。
    /// </summary>
    private void ApplyVolume()
    {
        var v = FrameworkCompatibility.Clamp(_volume, 0.0, 1.0);
        if (_stream != 0)
            Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, (float)v);
    }

    // ============ 播完自动下一首 ============

    private void AttachEndSync()
    {
        if (_stream == 0) return;
        _endSync = Bass.ChannelSetSync(_stream, SyncFlags.End, 0, EndSyncProc, IntPtr.Zero);
    }

    private void EndSyncProc(int handle, int channel, int data, IntPtr user)
    {
        // BASS 线程回调，必须保持轻量、不阻塞
        if (_manualStop) return;
        if (channel != _stream) return;
        _position = Duration;
        State = AudioPlayerState.Stopped;
        PlaybackCompleted?.Invoke(this, EventArgs.Empty);
    }

    // ============ 释放 ============

    private void StartPlaybackStateMonitor()
    {
        StopPlaybackStateMonitor();
        _playbackStateTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (_stream == 0 || _disposed != 0) return;
                // The production timer is the single owner of stall observation: it flips Playing to
                // Loading, resumes on recovery, and releases a stream that stays stalled past the bound.
                ObservePlaybackState(Bass.ChannelIsActive(_stream), DateTimeOffset.UtcNow);
            }
            catch (Exception exception)
            {
                LogNativeFallback("playback-state-monitor", exception);
            }
        }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    internal void ObservePlaybackState(PlaybackState active, DateTimeOffset now)
    {
        if (active == PlaybackState.Stalled)
        {
            if (_stallStartedUtc is null) _stallStartedUtc = now;
            if (State == AudioPlayerState.Playing) State = AudioPlayerState.Loading;
            RecoverIfStallExpired(now);
            return;
        }

        _stallStartedUtc = null;
        if (State == AudioPlayerState.Loading && active == PlaybackState.Playing)
            State = AudioPlayerState.Playing;
    }

    internal void CleanupFailedDeviceSwitch()
    {
        ReleaseStream();
        DeleteTempFile();
        _loadedSource = null;
        _position = TimeSpan.Zero;
        State = AudioPlayerState.Stopped;
    }
    internal void RecoverIfStallExpired(DateTimeOffset now)
    {
        if (_stallStartedUtc is null || now - _stallStartedUtc.Value < StallRecoveryTimeout) return;
        _stallStartedUtc = null;
        ReleaseStream();
        DeleteTempFile();
        _loadedSource = null;
        _position = TimeSpan.Zero;
        State = AudioPlayerState.Error;
    }
    private void StopPlaybackStateMonitor()
    {
        _playbackStateTimer?.Dispose();
        _playbackStateTimer = null;
    }
    private void ReleaseStream()
    {
        StopPlaybackStateMonitor();
        if (_stream != 0)
        {
            if (_endSync != 0)
            {
                try { Bass.ChannelRemoveSync(_stream, _endSync); }
                catch (Exception exception) { LogReleaseFailure("sync", exception); }
                _endSync = 0;
            }
            try { Bass.StreamFree(_stream); }
            catch (Exception exception) { LogReleaseFailure("stream", exception); }
            _stream = 0;
        }
    }

    private void DeleteTempFile()
    {
        var path = Interlocked.Exchange(ref _tempFilePath, null);
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception exception) { LogReleaseFailure("temporary-file", exception); }
    }

    private void LogReleaseFailure(string resource, Exception exception) =>
        _logger.LogWarning(
            "Audio resource release failed for {Resource}: {ErrorType}",
            resource,
            exception.GetType().Name);

    private void TryConfigureBass(Action configure, string setting)
    {
        try { configure(); }
        catch (Exception exception) { LogNativeFallback(setting, exception); }
    }

    private void LogNativeFallback(string operation, Exception exception) =>
        _logger.LogDebug(
            "Audio native fallback used for {Operation}: {ErrorType}",
            operation,
            exception.GetType().Name);

    private static bool IsHttpUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Releases the native stream and any fallback temp file before the process exits.</summary>
    public void Unload()
    {
        StopPlaybackStateMonitor();
        ReleaseStream();
        DeleteTempFile();
        _loadedSource = null;
        _position = TimeSpan.Zero;
        State = AudioPlayerState.Stopped;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopPlaybackStateMonitor();
        ReleaseStream();
        DeleteTempFile();
        if (_ownsHttpClient) _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
