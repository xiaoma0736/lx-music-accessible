using Microsoft.Extensions.Logging;
using LXMusicAccessible.Models;
using LXMusicAccessible.Audio;
using Microsoft.Extensions.Logging.Abstractions;

namespace LXMusicAccessible.Services;

/// <summary>
/// 音乐播放器服务实现
/// </summary>
public class MusicPlayerService : IMusicPlayerService, IAsyncDisposable
{
    private const string PlaySlot = "music-player.play";
    private const string PrecacheSlot = "music-player.precache";
    private readonly ILogger<MusicPlayerService> _logger;
    private readonly IAudioPlayer _audioPlayer;
    private Models.PlayerState _state = Models.PlayerState.Stopped;
    private MusicInfo? _currentMusic;
    private List<MusicInfo> _playlist = new();
    private int _currentIndex = -1;
    private TimeSpan _resumePosition = TimeSpan.Zero;
    private double _volume = 0.8;
    private readonly IMusicSourceService _musicSourceService;
    private readonly BackgroundWorkHost _backgroundWork;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsBackgroundWork;
    private readonly bool _ownsHttpClient;
    private readonly string _precacheDirectory;
    private string _defaultQuality = "128k";
    // 预缓存（顺序连播）：(Id|Source) -> 预下载好的下一首临时文件
    private readonly Dictionary<string, string> _precachedPaths = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<Models.PlayerStateChangedEventArgs>? StateChanged;
    public event EventHandler? QueueCompleted;

    public bool IsPlaying => _state == Models.PlayerState.Playing;
    public bool IsPaused => _state == Models.PlayerState.Paused;
    public bool IsLoopMode { get; set; }
    public bool IsShuffleMode { get; set; }
    public TimeSpan Position => _audioPlayer.Position;
    public TimeSpan Duration => _audioPlayer.Duration;
    public MusicInfo? CurrentMusic => _currentMusic;
    public double Volume => _volume;

    /// <summary>当前实际播放音质（由播放器流读取：编码格式 + 平均码率），无法确定时为 null</summary>
    public string? ActualQuality => _audioPlayer.GetActualQuality();

    /// <summary>自动连播/上一首/下一首时解析播放链接使用的默认音质</summary>
    public string DefaultQuality
    {
        get => _defaultQuality;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                _defaultQuality = value;
        }
    }

    public MusicPlayerService(
        ILogger<MusicPlayerService> logger,
        IAudioPlayer audioPlayer,
        IMusicSourceService musicSourceService,
        BackgroundWorkHost? backgroundWork = null,
        HttpClient? httpClient = null,
        string? precacheDirectory = null)
    {
        _logger = logger;
        _audioPlayer = audioPlayer;
        _musicSourceService = musicSourceService;
        _backgroundWork = backgroundWork
            ?? new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsBackgroundWork = backgroundWork is null;
        _ownsHttpClient = httpClient is null;
        _precacheDirectory = Path.GetFullPath(precacheDirectory ?? Path.GetTempPath());
        _audioPlayer.PlaybackCompleted += AudioPlayer_PlaybackCompleted;
    }

    private void AudioPlayer_PlaybackCompleted(object? sender, EventArgs e)
    {
        _logger.LogInformation("歌曲播放结束，自动播放下一首");
        Next();
    }

    /// <summary>歌曲唯一键（Id|Source）</summary>
    private static string GetMusicKey(MusicInfo m) => $"{m.Source}|{m.Id}";

    /// <summary>计算下一首索引（顺序/循环/随机）；无下一首返回 -1</summary>
    private int ComputeNextIndex()
    {
        if (_playlist.Count == 0) return -1;
        if (IsShuffleMode) return new Random().Next(_playlist.Count);
        var idx = _currentIndex + 1;
        if (idx >= _playlist.Count)
        {
            if (IsLoopMode) return 0;
            return -1;
        }
        return idx;
    }

    /// <summary>取消进行中的预缓存并清理已缓存临时文件（队列/列表变更时调用）</summary>
    private void CancelPrecache()
    {
        _backgroundWork.Cancel(PrecacheSlot);
        if (_precachedPaths.Count == 0) return;
        foreach (var f in _precachedPaths.Values)
            DeletePrecacheFile(f);
        _precachedPaths.Clear();
    }

    public Task PlayAsync(string url, MusicInfo musicInfo) =>
        _backgroundWork.RunLatestAsync(
            PlaySlot,
            context => PlayCoreAsync(url, musicInfo, context));

    private async Task PlayCoreAsync(
        string url,
        MusicInfo musicInfo,
        BackgroundWorkContext context)
    {
        var cancellationToken = context.CancellationToken;
        try
        {
            _logger.LogInformation("播放音乐: {Title} - {Artist}", musicInfo.Title, musicInfo.Artist);
            
            // 暂停后恢复播放：仅同一首歌时恢复到暂停位置
            var resumePosition = _resumePosition;
            _resumePosition = TimeSpan.Zero;
            var sameMusic = _currentMusic != null &&
                string.Equals(_currentMusic.Id, musicInfo.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_currentMusic.Source, musicInfo.Source, StringComparison.OrdinalIgnoreCase);

            var oldState = _state;
            if (!context.TryCommit(() => _state = Models.PlayerState.Loading)) return;

            await _audioPlayer.LoadAsync(url, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (sameMusic && resumePosition > TimeSpan.Zero)
            {
                try
                {
                    if (resumePosition < _audioPlayer.Duration)
                        _audioPlayer.Position = resumePosition;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "恢复播放位置失败，从头播放");
                }
            }
            await _audioPlayer.PlayAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var committed = context.TryCommit(() =>
            {
                // 将歌曲加入内部播放列表（按 Id+Source 去重），使上一首/下一首可用
                var existingIndex = _playlist.FindIndex(m =>
                    m.Id == musicInfo.Id && m.Source == musicInfo.Source);
                if (existingIndex >= 0)
                    _currentIndex = existingIndex;
                else
                {
                    _playlist.Add(musicInfo);
                    _currentIndex = _playlist.Count - 1;
                }

                _currentMusic = musicInfo;
                _state = Models.PlayerState.Playing;
                StateChanged?.Invoke(this, new Models.PlayerStateChangedEventArgs
                {
                    OldState = oldState,
                    NewState = _state,
                    CurrentMusic = musicInfo
                });
            });

            if (!committed)
            {
                _audioPlayer.Stop();
                return;
            }

            // 播放成功后，后台预缓存下一首，顺序连播更顺畅
            StartPrecache();

            _logger.LogInformation("开始播放");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _audioPlayer.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogError("播放音乐失败: {ErrorType}", ex.GetType().Name);
            context.TryCommit(() =>
            {
                var errorState = _state;
                _state = Models.PlayerState.Error;
                StateChanged?.Invoke(this, new Models.PlayerStateChangedEventArgs
                {
                    OldState = errorState,
                    NewState = _state,
                    CurrentMusic = musicInfo
                });
            });
        }
    }

    public void Pause()
    {
        try
        {
            _logger.LogInformation("暂停播放");
            _resumePosition = _audioPlayer.Position;
            var oldState = _state;
            _audioPlayer.Pause();
            _state = Models.PlayerState.Paused;
            StateChanged?.Invoke(this, new Models.PlayerStateChangedEventArgs
            {
                OldState = oldState,
                NewState = _state,
                CurrentMusic = _currentMusic
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "暂停播放失败");
        }
    }

    /// <summary>恢复播放：直接继续（不重新加载，保留暂停位置）</summary>
    public async Task ResumeAsync()
    {
        if (_state != Models.PlayerState.Paused || _currentMusic == null) return;
        var oldState = _state;
        try
        {
            await _audioPlayer.PlayAsync();
            _state = Models.PlayerState.Playing;
            _resumePosition = TimeSpan.Zero;
            StateChanged?.Invoke(this, new Models.PlayerStateChangedEventArgs
            {
                OldState = oldState,
                NewState = _state,
                CurrentMusic = _currentMusic
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复播放失败");
            _state = Models.PlayerState.Error;
            StateChanged?.Invoke(this, new Models.PlayerStateChangedEventArgs
            {
                OldState = oldState,
                NewState = _state,
                CurrentMusic = _currentMusic
            });
        }
    }

    public void Stop()
    {
        try
        {
            _backgroundWork.Cancel(PlaySlot);
            CancelPrecache();
            _logger.LogInformation("停止播放");
            var oldState = _state;
            _audioPlayer.Stop();
            _state = Models.PlayerState.Stopped;
            _resumePosition = TimeSpan.Zero;
            StateChanged?.Invoke(this, new Models.PlayerStateChangedEventArgs
            {
                OldState = oldState,
                NewState = _state,
                CurrentMusic = _currentMusic
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止播放失败");
        }
    }

    public void Next()
    {
        try
        {
            _logger.LogInformation("播放下一首");

            var nextIdx = ComputeNextIndex();
            if (nextIdx < 0)
            {
                if (_playlist.Count > 0 && !IsLoopMode && !IsShuffleMode)
                {
                    // 顺序播放到末尾：通知上层自动翻页续播（无下一页时由上层决定停止）
                    _currentIndex = _playlist.Count - 1;
                    QueueCompleted?.Invoke(this, EventArgs.Empty);
                }
                return;
            }
            _currentIndex = nextIdx;
            StartQueueItem(_playlist[_currentIndex]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "播放下一首失败");
        }
    }

    public void Previous()
    {
        try
        {
            _logger.LogInformation("播放上一首");
            
            if (_playlist.Count == 0)
            {
                _logger.LogWarning("播放列表为空");
                return;
            }

            _currentIndex--;
            if (_currentIndex < 0)
            {
                if (IsLoopMode)
                {
                    _currentIndex = _playlist.Count - 1;
                }
                else
                {
                    _currentIndex = 0;
                    return;
                }
            }

            if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
            {
                StartQueueItem(_playlist[_currentIndex]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "播放上一首失败");
        }
    }

    /// <summary>
    /// 播放队列中的指定歌曲：无播放链接时按当前音源与默认音质解析后播放
    /// （本地歌曲直接使用本地路径），保证“下一首/上一首/自动连播”可作用于整个列表。
    /// </summary>
    private void StartQueueItem(MusicInfo music) =>
        _backgroundWork.StartLatest(
            PlaySlot,
            context => PlayQueueItemAsync(music, context));

    private async Task PlayQueueItemAsync(
        MusicInfo music,
        BackgroundWorkContext context)
    {
        try
        {
            if (music == null) return;

            bool isLocal = string.Equals(music.Source, "Local", StringComparison.OrdinalIgnoreCase);

            // 优先使用预缓存好的下一首临时文件（顺序连播几乎无缝）
            string? url = null;
            var key = GetMusicKey(music);
            if (_precachedPaths.TryGetValue(key, out var pre) && File.Exists(pre))
            {
                url = pre;
            }
            else
            {
                _precachedPaths.Remove(key);
            }

            if (url == null) url = music.PlayUrl;
            if (string.IsNullOrEmpty(url) && !isLocal && _musicSourceService != null)
            {
                url = await _musicSourceService.GetPlayUrlAsync(
                    music,
                    music.Source,
                    _defaultQuality,
                    context.CancellationToken) ?? string.Empty;
                context.TryCommit(() =>
                    music.PlayUrl = string.IsNullOrEmpty(url) ? null : url);
            }

            if (string.IsNullOrEmpty(url))
            {
                _logger.LogWarning("队列歌曲无播放链接，跳过: {Title} - {Artist}", music.Title, music.Artist);
                return;
            }

            await PlayCoreAsync(url, music, context);

            // 已消费的预缓存文件：从字典移除并尽力删除（被 BASS 占用时删除失败，交由“清理缓存”处理）
            context.TryCommit(() =>
            {
                if (_precachedPaths.Remove(key)) DeletePrecacheFile(pre!);
            });
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "播放队列歌曲失败: {Title}", music?.Title);
        }
    }

    /// <summary>后台预缓存下一首在线歌曲到临时文件，供顺序连播直接使用。</summary>
    private void StartPrecache() =>
        _backgroundWork.StartLatest(PrecacheSlot, PreCacheNextAsync);

    private async Task PreCacheNextAsync(BackgroundWorkContext context)
    {
        var token = context.CancellationToken;
        string? temporaryPath = null;
        try
        {
            var nextIdx = ComputeNextIndex();
            if (nextIdx < 0 || nextIdx >= _playlist.Count) return;
            var next = _playlist[nextIdx];
            if (string.Equals(next.Source, "Local", StringComparison.OrdinalIgnoreCase)) return;

            var key = GetMusicKey(next);
            if (_precachedPaths.ContainsKey(key)) return;

            var url = next.PlayUrl;
            if (string.IsNullOrEmpty(url))
            {
                url = await _musicSourceService.GetPlayUrlAsync(
                    next,
                    next.Source,
                    _defaultQuality,
                    token) ?? string.Empty;
            }
            if (string.IsNullOrEmpty(url)) return;
            token.ThrowIfCancellationRequested();

            Directory.CreateDirectory(_precacheDirectory);
            temporaryPath = Path.Combine(
                _precacheDirectory,
                $"lxmusic_pre_{Guid.NewGuid():N}.tmp");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using (var response = await _httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = File.Create(temporaryPath);
                await response.Content.CopyToAsync(stream, timeout.Token);
            }
            token.ThrowIfCancellationRequested();

            var published = context.TryCommit(() =>
            {
                next.PlayUrl = url;
                foreach (var cached in _precachedPaths.ToList())
                {
                    if (string.Equals(cached.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                    _precachedPaths.Remove(cached.Key);
                    DeletePrecacheFile(cached.Value);
                }
                _precachedPaths[key] = temporaryPath;
                _logger.LogInformation("已预缓存下一首: {Title}", next.Title);
            });
            if (published) temporaryPath = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "预缓存下一首失败: {ErrorType}",
                exception.GetType().Name);
        }
        finally
        {
            if (temporaryPath is not null) DeletePrecacheFile(temporaryPath);
        }
    }

    private void DeletePrecacheFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Precache file release failed: {ErrorType}",
                exception.GetType().Name);
        }
    }

    public void SetVolume(double volume)
    {
        _volume = volume;
        _audioPlayer.Volume = volume;
        _logger.LogInformation("音量设置为: {Volume}", volume);
    }

    /// <summary>音量已完全由软件内（BASS 通道音量）控制，不再与 Windows 音量合成器同步。
    /// AudioPlayer.Volume 即 _volume，此方法保留为兜底，仅确保两个 _volume 字段一致。</summary>
    public void SyncVolumeFromDevice()
    {
        try
        {
            var effective = _audioPlayer.Volume;
            if (Math.Abs(effective - _volume) > 0.01)
            {
                _volume = Math.Clamp(effective, 0.0, 1.0);
                _audioPlayer.Volume = _volume;
                _logger.LogInformation("音量已同步: {Volume}", _volume);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "同步设备音量失败");
        }
    }

    public void SetPosition(TimeSpan position)
    {
        _audioPlayer.Position = position;
        _logger.LogInformation("播放位置设置为: {Position}", position);
    }

    /// <summary>
    /// 定位到指定播放位置（清空输出缓冲，确保音频立即跳到新位置；供快进/快退使用，
    /// 避免快进后歌词先于听到的歌曲提前朗读）
    /// </summary>
    public void SeekTo(TimeSpan position)
    {
        _audioPlayer.SeekTo(position);
        _logger.LogInformation("快进/快退定位到: {Position}", position);
    }

    /// <summary>
    /// 设置播放列表
    /// </summary>
    public void SetPlaylist(List<MusicInfo> playlist)
    {
            CancelPrecache(); // 队列变更：取消/清理预缓存
        _playlist = playlist;
        _currentIndex = -1;
        _logger.LogInformation("设置播放列表，共 {Count} 首歌曲", playlist.Count);
    }

    /// <summary>
    /// 设置播放队列（整列表顺序播放）：从来源列表构建队列并定位当前歌曲，
    /// 使上一首/下一首/自动连播作用于整个列表（解决“下一首重复播放当前歌曲”问题）。
    /// </summary>
    public void SetQueue(List<MusicInfo> queue, MusicInfo currentMusic)
    {
            CancelPrecache(); // 队列变更：取消/清理预缓存
        if (queue == null || queue.Count == 0)
        {
            _logger.LogWarning("设置播放队列失败：列表为空");
            return;
        }


        _playlist = new List<MusicInfo>(queue);
        _currentIndex = _playlist.FindIndex(m =>
            string.Equals(m.Id, currentMusic?.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.Source, currentMusic?.Source, StringComparison.OrdinalIgnoreCase));
        if (_currentIndex < 0)
            _currentIndex = 0;
        _logger.LogInformation("设置播放队列，共 {Count} 首歌曲，当前定位第 {Index} 首", _playlist.Count, _currentIndex + 1);
    }
    /// <summary>播放队列条目替换（同曲换源用）：以实际播放的同曲替换原条目，保持上一首/下一首位置正确</summary>
    public void ReplaceQueueItem(MusicInfo oldItem, MusicInfo newItem)
    {
        if (oldItem == null || newItem == null || _playlist.Count == 0) return;
        var idx = _playlist.FindIndex(m =>
            string.Equals(m.Id, oldItem.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.Source, oldItem.Source, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        _playlist[idx] = newItem;
        if (_currentIndex == idx) _currentMusic = newItem;
        _logger.LogInformation("播放队列条目已替换（同曲换源）: [{Old}] → [{New}]",
            $"{oldItem.Source}|{oldItem.Id}", $"{newItem.Source}|{newItem.Id}");
    }

    /// <summary>
    /// 添加到播放列表
    /// </summary>
    public void AddToPlaylist(MusicInfo music)
    {
        _playlist.Add(music);
        _logger.LogInformation("添加到播放列表: {Title}", music.Title);
    }

    /// <summary>
    /// 清空播放列表
    /// </summary>
    public void ClearPlaylist()
    {
            CancelPrecache(); // 队列变更：取消/清理预缓存
        _playlist.Clear();
        _currentIndex = -1;
        _logger.LogInformation("清空播放列表");
    }
    // ============ 输出设备切换 ============

    /// <summary>获取所有可用输出设备（首项为“系统默认”）</summary>
    public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => _audioPlayer.GetOutputDevices();

    /// <summary>当前输出设备索引（-1 = 系统默认）</summary>
    public int CurrentOutputDeviceIndex => _audioPlayer.CurrentOutputDeviceIndex;

    /// <summary>当前输出设备名称（供播报）</summary>
    public string CurrentOutputDeviceName
    {
        get
        {
            var idx = _audioPlayer.CurrentOutputDeviceIndex;
            return _audioPlayer.GetOutputDevices().FirstOrDefault(d => d.Index == idx)?.Name ?? "系统默认";
        }
    }

    /// <summary>切换到下一个播放设备，返回新设备名称（失败返回 null）</summary>
    public string? SwitchToNextOutputDevice() => SwitchOutputDevice(_audioPlayer.CurrentOutputDeviceIndex + 1);

    /// <summary>切换到上一个播放设备，返回新设备名称（失败返回 null）</summary>
    public string? SwitchToPreviousOutputDevice() => SwitchOutputDevice(_audioPlayer.CurrentOutputDeviceIndex - 1);

    /// <summary>切换到指定输出设备（越界自动回绕），返回新设备名称（失败返回 null）</summary>
    public string? SwitchOutputDevice(int index)
    {
        try
        {
            var devices = _audioPlayer.GetOutputDevices();
            var maxConcrete = devices.Count - 2; // 去掉首项“系统默认”后的最大具体设备索引
            // 越界回绕：超过最后一个具体设备 → 系统默认；低于系统默认 → 最后一个具体设备
            if (index > maxConcrete) index = -1;
            if (index < -1) index = maxConcrete;

            if (_audioPlayer.SwitchOutputDevice(index))
            {
                _logger.LogInformation("切换输出设备成功: {Name} (Index={Index})", CurrentOutputDeviceName, index);
                return CurrentOutputDeviceName;
            }
            _logger.LogWarning("切换输出设备失败: Index={Index}", index);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换输出设备异常");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _audioPlayer.PlaybackCompleted -= AudioPlayer_PlaybackCompleted;
        _backgroundWork.Cancel(PlaySlot);
        CancelPrecache();
        await _backgroundWork.WaitAsync(PlaySlot);
        await _backgroundWork.WaitAsync(PrecacheSlot);
        if (_ownsBackgroundWork) await _backgroundWork.DisposeAsync();
        if (_ownsHttpClient) _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
