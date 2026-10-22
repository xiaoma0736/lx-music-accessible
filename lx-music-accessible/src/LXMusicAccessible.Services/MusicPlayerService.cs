using LXMusicAccessible.Audio;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LXMusicAccessible.Services;

/// <summary>
/// 播放协调深模块：唯一拥有队列、索引、播放模式、来源重试、代际和预缓存。
/// 调用者只提交播放意图并观察结果或状态事件。
/// </summary>
public sealed class MusicPlayerService : IPlaybackCoordinator, IExactPlaybackCoordinator, IAudioPlaybackControl, IPlaybackCacheCleanup, IAsyncDisposable, IDisposable
{
    private const string PlaySlot = "music-player.play";
    private const string PrecacheSlot = "music-player.precache";
    private const int MaxAlternativeSources = 5;
    private const int MaxMatchesPerSource = 2;
    private static readonly TimeSpan PlaybackAttemptBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RequestedPlaybackBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DowngradePlaybackBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AlternativeResolutionBudget = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly ILogger<MusicPlayerService> _logger;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IPlaybackSourceAdapter _sources;
    private readonly IAccessibleAnnouncer _announcer;
    private readonly BackgroundWorkHost _backgroundWork;
    private readonly HttpClient _httpClient;
    private readonly Random _random;
    private readonly bool _ownsBackgroundWork;
    private readonly bool _ownsHttpClient;
    private readonly string _precacheDirectory;
    private readonly Dictionary<string, string> _precachedPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activePrecacheFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _playbackCacheCleanupInProgress;

    private PlayerState _state = PlayerState.Stopped;
    private MusicInfo? _currentMusic;
    private List<MusicInfo> _queue = [];
    private int _currentIndex = -1;
    private long _queueRevision;
    private PlaybackQueueOrigin _queueOrigin;
    private PlayMode _mode;
    private TimeSpan _resumePosition;
    private double _volume = 0.8;
    private string _defaultQuality = "128k";
    private PlaybackSourceResolution? _lastPlaybackResolution;

    public MusicPlayerService(
        ILogger<MusicPlayerService> logger,
        IAudioPlayer audioPlayer,
        IPlaybackSourceAdapter sources,
        IAccessibleAnnouncer announcer,
        BackgroundWorkHost? backgroundWork = null,
        HttpClient? httpClient = null,
        string? precacheDirectory = null,
        Random? random = null)
    {
        _logger = logger;
        _audioPlayer = audioPlayer;
        _sources = sources;
        _announcer = announcer;
        _backgroundWork = backgroundWork
            ?? new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsBackgroundWork = backgroundWork is null;
        _ownsHttpClient = httpClient is null;
        _precacheDirectory = Path.GetFullPath(precacheDirectory ?? Path.GetTempPath());
        _random = random ?? FrameworkCompatibility.SharedRandom;
        _audioPlayer.PlaybackCompleted += AudioPlayer_PlaybackCompleted;
    }

    public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
    public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted;

    public bool IsPlaying { get { lock (_gate) return _state == PlayerState.Playing; } }
    public bool IsPaused { get { lock (_gate) return _state == PlayerState.Paused; } }
    public TimeSpan Position => _audioPlayer.Position;
    public TimeSpan Duration => _audioPlayer.Duration;
    public MusicInfo? CurrentMusic { get { lock (_gate) return _currentMusic; } }
    public double Volume => _volume;
    public string? ActualQuality => _audioPlayer.GetActualQuality();

    public PlayMode Mode
    {
        get { lock (_gate) return _mode; }
        set
        {
            lock (_gate) _mode = value;
            CancelPrecache();
            StartPrecacheForCurrent();
        }
    }

    public string DefaultQuality
    {
        get { lock (_gate) return _defaultQuality; }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            lock (_gate) _defaultQuality = value;
        }
    }

    public Task<PlaybackResult> PlayAsync(PlaybackRequest request)
    {
        FrameworkCompatibility.ThrowIfNull(request);
        FrameworkCompatibility.ThrowIfNull(request.Selection);

        var queue = request.Queue?.Where(item => item is not null).ToList() ?? [];
        var index = FindIndex(queue, request.Selection);
        if (index < 0)
        {
            queue.Add(request.Selection);
            index = queue.Count - 1;
        }

        long revision;
        lock (_gate)
        {
            revision = ++_queueRevision;
            _queue = queue;
            _currentIndex = index;
            _queueOrigin = request.Origin;
            if (!string.IsNullOrWhiteSpace(request.Quality))
                _defaultQuality = request.Quality;
        }

        CancelGenerationWork();
        return RunPlayAsync(
            revision,
            index,
            request.Selection,
            request.Quality,
            request.ForceResolve,
            request.Trigger);
    }

    public Task<PlaybackResult> PlayExactAsync(
        PlaybackRequest request,
        ManualSourceOption option)
    {
        FrameworkCompatibility.ThrowIfNull(request);
        FrameworkCompatibility.ThrowIfNull(option);
        FrameworkCompatibility.ThrowIfNull(request.Selection);
        if (!MusicSearchMatcher.IsSameSong(option.Candidate, request.Selection)
            || !string.Equals(option.Candidate.Source, option.Platform, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new PlaybackResult(
                PlaybackResultStatus.Failed,
                null,
                request.Origin,
                "\u6362\u6e90\u5019\u9009\u4e0d\u662f\u4e25\u683c\u540c\u66f2",
                request.Trigger));
        }

        var queue = request.Queue?.Where(item => item is not null).ToList() ?? [];
        var index = FindIndex(queue, request.Selection);
        if (index < 0)
        {
            queue.Add(request.Selection);
            index = queue.Count - 1;
        }
        else
        {
            queue[index] = option.Candidate;
        }

        long revision;
        lock (_gate)
        {
            revision = ++_queueRevision;
            _queue = queue;
            _currentIndex = index;
            _queueOrigin = request.Origin;
        }

        CancelGenerationWork();
        return RunExactPlayAsync(
            revision,
            index,
            option.Candidate,
            option.SourceId,
            request.Quality,
            request.Trigger);
    }


    private async Task<PlaybackResult> RunExactPlayAsync(
        long revision,
        int index,
        MusicInfo music,
        string sourceId,
        string quality,
        PlaybackTrigger trigger)
    {
        var result = Superseded(trigger);
        await _backgroundWork.RunLatestAsync(PlaySlot, async context =>
        {
            result = await PlayExactGenerationAsync(
                revision,
                index,
                music,
                sourceId,
                string.IsNullOrWhiteSpace(quality) ? DefaultQuality : quality,
                trigger,
                context);
        });
        return result;
    }

    private async Task<PlaybackResult> PlayExactGenerationAsync(
        long revision,
        int index,
        MusicInfo music,
        string sourceId,
        string quality,
        PlaybackTrigger trigger,
        BackgroundWorkContext context)
    {
        var generationToken = context.CancellationToken;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(generationToken);
        attempt.CancelAfter(PlaybackAttemptBudget);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
        budget.CancelAfter(RequestedPlaybackBudget);

        if (!PublishState(context, revision, PlayerState.Loading, music))
            return Superseded(trigger);

        try
        {
                        var resolution = await TryResolveExactWithinBudgetAsync(
                music,
                sourceId,
                quality,
                generationToken,
                budget);
            var url = resolution?.Url;
            if (string.IsNullOrWhiteSpace(url)
                || !await TryLoadWithinBudgetAsync(
                    music,
                    url,
                    quality,
                    generationToken,
                    budget))
            {
                return CommitFailure(context, revision, music, trigger,
                    $"选定接口播放失败：{music.Title}（{sourceId}，{quality}）");
            }

            return CommitPlaying(context, revision, index, music, url!, quality, trigger);
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested)
        {
            _audioPlayer.Stop();
            return Superseded(trigger);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            _audioPlayer.Stop();
            return CommitFailure(context, revision, music, trigger,
                $"选定接口播放失败：{music.Title}（{sourceId}，{quality}）");
        }
    }

    private async Task<PlaybackSourceResolution?> TryResolveExactWithinBudgetAsync(
        MusicInfo music,
        string sourceId,
        string quality,
        CancellationToken generationToken,
        CancellationTokenSource budgetSource)
    {
        try
        {
            var resolution = await _sources.ResolveExactAsync(
                music,
                sourceId,
                quality,
                budgetSource.Token);;
        if (resolution is null) return null;
        lock (_gate) _lastPlaybackResolution = resolution;
        return resolution;
        }
        catch (OperationCanceledException) when (
            budgetSource.IsCancellationRequested
            && !generationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Exact playback URL resolution failed ({SourceId}): {ErrorType}",
                sourceId,
                exception.GetType().Name);
            return null;
        }
    }

    public async Task<PlaybackResult> ReplayAsync()
    {
        bool resumeRequested;
        lock (_gate) resumeRequested = _state == PlayerState.Paused && _currentMusic is not null;
        if (resumeRequested)
            return await ResumePlaybackAsync(_queueOrigin, PlaybackTrigger.UserInitiated);

        return await ReplayFromStartAsync();
    }

    private Task<PlaybackResult> ReplayFromStartAsync()
    {
        MusicInfo? music;
        int index;
        long revision;
        string quality;
        PlaybackQueueOrigin origin;
        lock (_gate)
        {
            music = _currentMusic;
            index = _currentIndex;
            origin = _queueOrigin;
            quality = _defaultQuality;
            revision = music is null || index < 0 ? _queueRevision : ++_queueRevision;
        }

        if (music is null || index < 0)
            return Task.FromResult(NoChange(origin, "当前没有可重播的歌曲"));

        CancelGenerationWork();
        return RunPlayAsync(revision, index, music, quality, forceResolve: false, trigger: PlaybackTrigger.UserInitiated);
    }

    public Task<PlaybackResult> NextAsync() => AdvanceAsync(1, naturalEnd: false);

    public Task<PlaybackResult> PreviousAsync() => AdvanceAsync(-1, naturalEnd: false);

    private Task<PlaybackResult> AdvanceAsync(int direction, bool naturalEnd)
    {
        MusicInfo? target = null;
        int targetIndex = -1;
        long revision;
        string quality;
        PlaybackQueueOrigin origin;
        PlayerStateChangedEventArgs? completedStateChange = null;
        var trigger = naturalEnd
            ? PlaybackTrigger.AutoContinuation
            : PlaybackTrigger.UserInitiated;

        lock (_gate)
        {
            origin = _queueOrigin;
            if (naturalEnd && _state != PlayerState.Playing)
            {
                return Task.FromResult(NoChange(origin, "\u5f53\u524d\u6ca1\u6709\u53ef\u81ea\u52a8\u5207\u6362\u7684\u6b4c\u66f2"));
            }

            quality = _defaultQuality;
            revision = ++_queueRevision;
            if (_queue.Count > 0)
            {
                targetIndex = ComputeTargetIndex(direction, naturalEnd);
                if (targetIndex < 0)
                {
                    if (direction > 0)
                    {
                        completedStateChange = StateChange(_state, PlayerState.Stopped, _currentMusic);
                        _state = PlayerState.Stopped;
                        _resumePosition = TimeSpan.Zero;
                    }
                }
                else
                {
                    target = _queue[targetIndex];
                    _currentIndex = targetIndex;
                }
            }
        }

        CancelGenerationWork();
        if (completedStateChange is not null)
        {
            _audioPlayer.Stop();
            if (completedStateChange.OldState != PlayerState.Stopped)
                RaiseStateChanged(completedStateChange);

            var result = new PlaybackResult(
                PlaybackResultStatus.QueueCompleted,
                CurrentMusic,
                origin,
                "当前播放队列已结束");
            RaiseQueueCompleted(origin);
            return Task.FromResult(result);
        }

        if (target is null)
            return Task.FromResult(NoChange(origin, "播放队列中没有可播放的歌曲"));

        return RunPlayAsync(
            revision,
            targetIndex,
            target,
            quality,
            forceResolve: false,
            trigger);
    }

    private int ComputeTargetIndex(int direction, bool naturalEnd)
    {
        if (_queue.Count == 0) return -1;
        if (naturalEnd && _mode == PlayMode.SingleLoop)
            return FrameworkCompatibility.Clamp(_currentIndex, 0, _queue.Count - 1);
        if (_mode == PlayMode.Shuffle)
            return _random.Next(_queue.Count);

        var index = FrameworkCompatibility.Clamp(_currentIndex, 0, _queue.Count - 1) + direction;
        if (index >= _queue.Count)
            return _mode == PlayMode.Loop ? 0 : -1;
        if (index < 0)
            return _mode == PlayMode.Loop ? _queue.Count - 1 : -1;
        return index;
    }

    private async Task<PlaybackResult> RunPlayAsync(
        long revision,
        int index,
        MusicInfo music,
        string quality,
        bool forceResolve,
        PlaybackTrigger trigger)
    {
        var result = Superseded(trigger);
        await _backgroundWork.RunLatestAsync(PlaySlot, async context =>
        {
            result = await PlayGenerationAsync(
                revision,
                index,
                music,
                string.IsNullOrWhiteSpace(quality) ? DefaultQuality : quality,
                forceResolve,
                trigger,
                context);
        });
        return result;
    }

    private async Task<PlaybackResult> PlayGenerationAsync(
        long revision,
        int index,
        MusicInfo requestedMusic,
        string quality,
        bool forceResolve,
        PlaybackTrigger trigger,
        BackgroundWorkContext context)
    {
        var generationToken = context.CancellationToken;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(generationToken);
        attempt.CancelAfter(PlaybackAttemptBudget);
        var token = attempt.Token;
        if (!PublishState(context, revision, PlayerState.Loading, requestedMusic))
            return Superseded(trigger);

        try
        {
            var directUrl = forceResolve ? null : GetDirectUrl(requestedMusic);
            using var requestedBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestedBudget.CancelAfter(RequestedPlaybackBudget);
            var (started, playedUrl) = await TryPlayAtQualityAsync(
                requestedMusic,
                directUrl,
                quality,
                forceResolve,
                generationToken,
                requestedBudget);
            if (started && !string.IsNullOrWhiteSpace(playedUrl))
                return CommitPlaying(context, revision, index, requestedMusic, playedUrl, quality, trigger);

            var downgradeQuality = GetDowngradeQuality(quality);
            if (!string.IsNullOrWhiteSpace(downgradeQuality))
            {
                using var downgradeBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                downgradeBudget.CancelAfter(DowngradePlaybackBudget);
                (started, playedUrl) = await TryPlayAtQualityAsync(
                    requestedMusic,
                    directUrl,
                    downgradeQuality,
                    forceResolve,
                    generationToken,
                    downgradeBudget);
                if (started && !string.IsNullOrWhiteSpace(playedUrl))
                    return CommitPlaying(context, revision, index, requestedMusic, playedUrl, quality, trigger);
            }

            if (!IsLocal(requestedMusic))
            {
                AnnounceIfCurrent(revision, new(
                    "正在切换音源接口",
                    AnnouncementKind.Busy,
                    ActivityId(revision)));
                var switched = await TryAlternativeSourcesAsync(
                    context,
                    revision,
                    index,
                    requestedMusic,
                    downgradeQuality ?? quality,
                    trigger,
                    token);
                if (switched is not null) return switched;
            }

            return CommitFailure(context, revision, requestedMusic, trigger);
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested)
        {
            _audioPlayer.Stop();
            return Superseded(trigger);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            _audioPlayer.Stop();
            _logger.LogDebug("Playback generation reached its fixed budget");
            return CommitFailure(context, revision, requestedMusic, trigger);
        }
    }

    private async Task<(bool Started, string? Url)> TryPlayAtQualityAsync(
        MusicInfo music,
        string? directUrl,
        string quality,
        bool forceResolve,
        CancellationToken generationToken,
        CancellationTokenSource budgetSource)
    {
        if (!forceResolve
            && !string.IsNullOrWhiteSpace(directUrl)
            && await TryLoadWithinBudgetAsync(
                music,
                directUrl,
                quality,
                generationToken,
                budgetSource))
        {
            lock (_gate) _lastPlaybackResolution = new(directUrl, music.Source, music.Source, quality);
            return (true, directUrl);
        }

        var resolvedUrl = await TryResolveWithinBudgetAsync(
            music,
            quality,
            generationToken,
            budgetSource);
        if (string.IsNullOrWhiteSpace(resolvedUrl)
            || string.Equals(resolvedUrl, directUrl, StringComparison.Ordinal))
        {
            return (false, null);
        }

        var started = await TryLoadWithinBudgetAsync(
            music,
            resolvedUrl,
            quality,
            generationToken,
            budgetSource);
        return started ? (true, resolvedUrl) : (false, null);
    }

    private async Task<PlaybackResult?> TryAlternativeSourcesAsync(
        BackgroundWorkContext context,
        long revision,
        int index,
        MusicInfo requestedMusic,
        string quality,
        PlaybackTrigger trigger,
        CancellationToken token)
    {
        var sourceIds = GetAlternativeSourceIds(requestedMusic);
        using var fallback = CancellationTokenSource.CreateLinkedTokenSource(token);
        fallback.CancelAfter(AlternativeResolutionBudget);
        var pending = sourceIds
            .Select(sourceId => TryResolveAlternativeAsync(
                requestedMusic,
                sourceId,
                quality,
                fallback.Token))
            .ToList();
        var allAttempts = pending.ToArray();
        try
        {
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                AlternativeResolution? alternative;
                try { alternative = await completed; }
                catch (OperationCanceledException) when (
                    fallback.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    _logger.LogDebug("Playback alternative resolution reached its fixed budget");
                    break;
                }
                if (alternative is null) continue;
                var candidate = alternative.Music;
                var url = alternative.Url;
                if (!await TryLoadWithinBudgetAsync(
                        candidate,
                        url,
                        quality,
                        token,
                        fallback))
                {
                    continue;
                }

                var switched = CommitPlaying(
                    context,
                    revision,
                    index,
                    candidate,
                    url,
                    quality,
                    trigger);
                if (switched.Status == PlaybackResultStatus.Playing)
                {
                    AnnounceIfCurrent(revision, new(
                        $"已自动切换音源播放：{candidate.Title}",
                        AnnouncementKind.Success,
                        ActivityId(revision)));
                }
                return switched;
            }
        }
        finally
        {
            fallback.Cancel();
            try { await Task.WhenAll(allAttempts); }
            catch (OperationCanceledException) when (fallback.IsCancellationRequested)
            {
                _logger.LogDebug("Playback alternative resolution was cancelled");
            }
        }

        return null;
    }

    private async Task<string?> TryResolveAsync(
        MusicInfo music,
        string quality,
        CancellationToken cancellationToken)
    {
        if (IsLocal(music))
        {
            lock (_gate) _lastPlaybackResolution = new(music.PlayUrl ?? string.Empty, music.Source, music.Source, quality);
            return music.PlayUrl;
        }

        try
        {
            var resolution = await _sources.ResolveAsync(music, quality, cancellationToken);
            if (resolution is null) return null;
            lock (_gate) _lastPlaybackResolution = resolution;
            return resolution.Url;
        }
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Playback URL resolution failed ({SourceId}): {ErrorType}",
                music.Source,
                exception.GetType().Name);
            return null;
        }
    }

    private async Task<IReadOnlyList<MusicInfo>> TryFindAlternativesAsync(
        MusicInfo music,
        string sourceId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _sources.FindAlternativesAsync(
                music,
                sourceId,
                cancellationToken) ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Playback alternative search failed ({SourceId}): {ErrorType}",
                sourceId,
                exception.GetType().Name);
            return [];
        }
    }

    private async Task<AlternativeResolution?> TryResolveAlternativeAsync(
        MusicInfo requestedMusic,
        string sourceId,
        string quality,
        CancellationToken cancellationToken)
    {
        var alternatives = await TryFindAlternativesAsync(
            requestedMusic,
            sourceId,
            cancellationToken);
        foreach (var candidate in alternatives.Take(MaxMatchesPerSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MusicSearchMatcher.IsSameSong(candidate, requestedMusic)) continue;
            var url = await TryResolveAsync(candidate, quality, cancellationToken);
            if (!string.IsNullOrWhiteSpace(url)) return new(candidate, url);
        }
        return null;
    }

    private async Task<string?> TryResolveWithinBudgetAsync(
        MusicInfo music,
        string quality,
        CancellationToken generationToken,
        CancellationTokenSource budgetSource)
    {
        try
        {
            return await TryResolveAsync(music, quality, budgetSource.Token);
        }
        catch (OperationCanceledException) when (
            budgetSource.IsCancellationRequested
            && !generationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Playback resolution reached its fixed budget");
            return null;
        }
    }

    private async Task<bool> TryLoadWithinBudgetAsync(
        MusicInfo music,
        string? url,
        string quality,
        CancellationToken generationToken,
        CancellationTokenSource budgetSource)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            return await TryLoadAsync(music, url, quality, budgetSource.Token);
        }
        catch (OperationCanceledException) when (
            budgetSource.IsCancellationRequested
            && !generationToken.IsCancellationRequested)
        {
            _audioPlayer.Stop();
            _logger.LogDebug("Audio playback attempt reached its fixed budget");
            return false;
        }
    }

    private async Task<bool> TryLoadAsync(
        MusicInfo music,
        string url,
        string quality,
        CancellationToken cancellationToken)
    {
        try
        {
            await _audioPlayer.LoadAsync(url, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var actual = _audioPlayer.GetActualQuality();
            if (!IsLocal(music) && !MusicQuality.AcceptsPlayback(quality, actual))
            {
                _audioPlayer.Stop();
                _logger.LogWarning(
                    "Playback media quality was lower than requested ({SourceId}, Requested={Requested}, Actual={Actual})",
                    music.Source,
                    quality,
                    actual);
                return false;
            }
            await StartPlaybackAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return _audioPlayer.State == AudioPlayerState.Playing;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _audioPlayer.Stop();
            _logger.LogWarning(
                "Audio playback attempt failed ({SourceId}): {ErrorType}",
                music.Source,
                exception.GetType().Name);
            return false;
        }
    }

    private async Task StartPlaybackAsync(CancellationToken cancellationToken)
    {
        var playbackTask = _audioPlayer.PlayAsync();
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(playbackTask, timeoutTask);
        if (completed != playbackTask)
        {
            IgnorePlaybackException(playbackTask);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        await playbackTask;
        while (_audioPlayer.State != AudioPlayerState.Playing)
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
    }

    private static void IgnorePlaybackException(Task playbackTask)
    {
        _ = playbackTask.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private PlaybackResult CommitPlaying(
        BackgroundWorkContext context,
        long revision,
        int index,
        MusicInfo music,
        string url,
        string requestedQuality,
        PlaybackTrigger trigger)
    {
        PlaybackSourceResolution? resolution;
        lock (_gate) resolution = _lastPlaybackResolution;
        PlaybackQueueOrigin origin = PlaybackQueueOrigin.Static;
        var applied = false;
        context.TryCommit(() =>
        {
            lock (_gate)
            {
                if (revision != _queueRevision) return;
                var oldState = _state;
                music.PlayUrl = url;
                if (index >= 0 && index < _queue.Count) _queue[index] = music;
                _currentIndex = index;
                _currentMusic = music;
                _state = PlayerState.Playing;
                _resumePosition = TimeSpan.Zero;
                origin = _queueOrigin;
                applied = true;
                RaiseStateChanged(new PlayerStateChangedEventArgs
                {
                    OldState = oldState,
                    NewState = _state,
                    CurrentMusic = music,
                    Trigger = trigger,
                    RequestedQuality = requestedQuality,
                    ActualQuality = _audioPlayer.GetActualQuality(),
                    InterfaceId = resolution?.InterfaceId,
                    InterfaceName = resolution?.InterfaceName
                });
            }
        });

        if (!applied)
        {
            _audioPlayer.Stop();
            return Superseded(trigger);
        }

        ConsumePrecache(music);
        StartPrecache(revision);
        return new PlaybackResult(
            PlaybackResultStatus.Playing,
            music,
            origin,
            $"正在播放：{music.Title}",
            trigger);
    }

    private PlaybackResult CommitFailure(
        BackgroundWorkContext context,
        long revision,
        MusicInfo music,
        PlaybackTrigger trigger,
        string? message = null)
    {
        PlaybackQueueOrigin origin = PlaybackQueueOrigin.Static;
        var applied = false;
        var failureMessage = message ?? (IsLocal(music)
            ? $"本地歌曲播放失败：{music.Title}"
            : $"无法播放《{music.Title}》，已尝试所有可用音源");
        context.TryCommit(() =>
        {
            lock (_gate)
            {
                if (revision != _queueRevision) return;
                var oldState = _state;
                _state = PlayerState.Error;
                origin = _queueOrigin;
                applied = true;
                RaiseStateChanged(new PlayerStateChangedEventArgs
                {
                    OldState = oldState,
                    NewState = _state,
                    CurrentMusic = music,
                    ErrorMessage = failureMessage
                });
                SafeAnnounce(new(
                    failureMessage,
                    AnnouncementKind.Error,
                    ActivityId(revision)));
            }
        });

        return applied
            ? new PlaybackResult(PlaybackResultStatus.Failed, music, origin, failureMessage, trigger)
            : Superseded(trigger);
    }

    private bool PublishState(
        BackgroundWorkContext context,
        long revision,
        PlayerState state,
        MusicInfo? music)
    {
        var applied = false;
        context.TryCommit(() =>
        {
            lock (_gate)
            {
                if (revision != _queueRevision) return;
                var oldState = _state;
                _state = state;
                applied = true;
                RaiseStateChanged(StateChange(oldState, state, music));
            }
        });
        return applied;
    }

    private string[] GetAlternativeSourceIds(MusicInfo music)
    {
        try
        {
            return (_sources.GetAlternativeSourceIds(music.Source) ?? [])
                .Where(source => !string.IsNullOrWhiteSpace(source)
                    && !string.Equals(
                        source,
                        music.Source,
                        StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxAlternativeSources)
                .ToArray();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Playback source enumeration failed: {ErrorType}",
                exception.GetType().Name);
            return [];
        }
    }

    private void AnnounceIfCurrent(long revision, AccessibleAnnouncement announcement)
    {
        lock (_gate)
        {
            if (revision == _queueRevision) SafeAnnounce(announcement);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_state != PlayerState.Playing) return;
            try
            {
                _resumePosition = _audioPlayer.Position;
                var oldState = _state;
                _audioPlayer.Pause();
                _state = PlayerState.Paused;
                RaiseStateChanged(StateChange(oldState, _state, _currentMusic));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Audio pause failed: {ErrorType}",
                    exception.GetType().Name);
            }
        }
    }

    public Task ResumeAsync() => ResumePlaybackAsync(_queueOrigin, PlaybackTrigger.UserInitiated);

    private async Task<PlaybackResult> ResumePlaybackAsync(
        PlaybackQueueOrigin origin,
        PlaybackTrigger trigger)
    {
        MusicInfo? music;
        PlayerState oldState;
        lock (_gate)
        {
            if (_state != PlayerState.Paused || _currentMusic is null)
                return new(PlaybackResultStatus.NoChange, _currentMusic, origin, "当前没有可继续播放的歌曲", trigger);
            music = _currentMusic;
            oldState = _state;
        }

        try
        {
            await _audioPlayer.PlayAsync();
            lock (_gate)
            {
                if (_state != PlayerState.Paused)
                    return new(PlaybackResultStatus.Superseded, music, origin, "播放状态已更新", trigger);
                _state = PlayerState.Playing;
                _resumePosition = TimeSpan.Zero;
                RaiseStateChanged(StateChange(oldState, _state, music));
            }
            return new(PlaybackResultStatus.Playing, music, origin, "已继续播放", trigger);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Audio resume failed: {ErrorType}", exception.GetType().Name);
            lock (_gate)
            {
                _state = PlayerState.Error;
                RaiseStateChanged(new PlayerStateChangedEventArgs
                {
                    OldState = oldState,
                    NewState = _state,
                    CurrentMusic = music,
                    ErrorMessage = "无法继续播放当前歌曲"
                });
            }
            SafeAnnounce(new("无法继续播放当前歌曲", AnnouncementKind.Error));
            return new(PlaybackResultStatus.Failed, music, origin, "无法继续播放当前歌曲", trigger);
        }
    }

    public void Stop()
    {
        PlayerStateChangedEventArgs change;
        lock (_gate)
        {
            ++_queueRevision;
            var oldState = _state;
            _state = PlayerState.Stopped;
            _resumePosition = TimeSpan.Zero;
            change = StateChange(oldState, _state, _currentMusic);
        }
        CancelGenerationWork();
        _audioPlayer.Stop();
        RaiseStateChanged(change);
    }

    private void AudioPlayer_PlaybackCompleted(object? sender, EventArgs e)
    {
        try { _ = AdvanceAsync(1, naturalEnd: true); }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("Natural playback completion arrived during shutdown");
        }
    }

    private void CancelGenerationWork()
    {
        _backgroundWork.Cancel(PlaySlot);
        CancelPrecache();
    }

    private void CancelPrecache()
    {
        _backgroundWork.Cancel(PrecacheSlot);
        string[] files;
        lock (_gate)
        {
            files = _precachedPaths.Values.ToArray();
            _precachedPaths.Clear();
        }
        foreach (var file in files) DeletePrecacheFile(file);
    }

    private void StartPrecacheForCurrent()
    {
        long revision;
        lock (_gate)
        {
            if (_state != PlayerState.Playing) return;
            revision = _queueRevision;
        }
        StartPrecache(revision);
    }

    private void StartPrecache(long revision)
    {
        PlayMode mode;
        lock (_gate) mode = _mode;
        if (mode is PlayMode.Shuffle or PlayMode.SingleLoop) return;
        _backgroundWork.StartLatest(
            PrecacheSlot,
            context => PrecacheNextAsync(revision, context));
    }

    private async Task PrecacheNextAsync(
        long revision,
        BackgroundWorkContext context)
    {
        var token = context.CancellationToken;
        MusicInfo? next;
        lock (_gate)
        {
            if (revision != _queueRevision || _queue.Count == 0)
                return;
            var nextIndex = _currentIndex + 1;
            if (nextIndex >= _queue.Count)
                nextIndex = _mode == PlayMode.Loop ? 0 : -1;
            next = nextIndex >= 0 ? _queue[nextIndex] : null;
        }

        if (next is null || IsLocal(next)) return;
        var key = MusicKey(next);
        lock (_gate)
        {
            if (_precachedPaths.ContainsKey(key)) return;
        }

        string? temporaryPath = null;
        try
        {
            var url = GetDirectUrl(next) ?? await TryResolveAsync(next, DefaultQuality, token);
            if (string.IsNullOrWhiteSpace(url)) return;
            token.ThrowIfCancellationRequested();

            // Register the candidate atomically with the cleanup-in-progress check. Otherwise
            // cleanup could take its active-file snapshot between registration and enumeration
            // and delete a file that playback is about to use.
            lock (_gate)
            {
                if (_playbackCacheCleanupInProgress || revision != _queueRevision)
                    return;
                temporaryPath = Path.Combine(
                    _precacheDirectory,
                    $"lxmusic_pre_{Guid.NewGuid():N}.tmp");
                _activePrecacheFiles.Add(temporaryPath);
            }

            Directory.CreateDirectory(_precacheDirectory);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using (var response = await _httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token))
            {
                response.EnsureSuccessStatusCode();
                using var stream = File.Create(temporaryPath);
                using var input = await response.Content.ReadAsStreamAsync();
                await input.CopyToAsync(stream, 81920, timeout.Token);
            }
            token.ThrowIfCancellationRequested();

            var published = false;
            context.TryCommit(() =>
            {
                lock (_gate)
                {
                    if (revision != _queueRevision) return;
                    foreach (var cached in _precachedPaths.ToArray())
                    {
                        if (string.Equals(cached.Key, key, StringComparison.OrdinalIgnoreCase))
                            continue;
                        _precachedPaths.Remove(cached.Key);
                        DeletePrecacheFile(cached.Value);
                    }
                    _precachedPaths[key] = temporaryPath;
                    _activePrecacheFiles.Remove(temporaryPath);
                    published = true;
                }
            });
            if (published) temporaryPath = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Playback precache failed ({SourceId}): {ErrorType}",
                next.Source,
                exception.GetType().Name);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                lock (_gate) _activePrecacheFiles.Remove(temporaryPath);
                DeletePrecacheFile(temporaryPath);
            }
        }
    }

    private string? GetDirectUrl(MusicInfo music)
    {
        var key = MusicKey(music);
        lock (_gate)
        {
            if (_precachedPaths.TryGetValue(key, out var path) && File.Exists(path))
                return path;
            _precachedPaths.Remove(key);
        }
        return music.PlayUrl;
    }

    private void ConsumePrecache(MusicInfo music)
    {
        string? path = null;
        lock (_gate)
        {
            if (_precachedPaths.TryGetValue(MusicKey(music), out var cached)) { path = cached; _precachedPaths.Remove(MusicKey(music)); }
        }
        if (path is not null) DeletePrecacheFile(path);
    }

    private void DeletePrecacheFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Playback precache release failed: {ErrorType}",
                exception.GetType().Name);
        }
    }

    public void SetVolume(double volume)
    {
        _volume = FrameworkCompatibility.Clamp(volume, 0, 1);
        _audioPlayer.Volume = _volume;
    }

    public void SyncVolumeFromDevice()
    {
        try
        {
            var effective = _audioPlayer.Volume;
            if (Math.Abs(effective - _volume) <= 0.01) return;
            _volume = FrameworkCompatibility.Clamp(effective, 0, 1);
            _audioPlayer.Volume = _volume;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Audio volume sync failed: {ErrorType}",
                exception.GetType().Name);
        }
    }

    public void SetPosition(TimeSpan position) => _audioPlayer.Position = position;
    public void SeekTo(TimeSpan position) => _audioPlayer.SeekTo(position);
    public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => _audioPlayer.GetOutputDevices();
    public int CurrentOutputDeviceIndex => _audioPlayer.CurrentOutputDeviceIndex;
    public string CurrentOutputDeviceName =>
        _audioPlayer.GetOutputDevices()
            .FirstOrDefault(device => device.Index == _audioPlayer.CurrentOutputDeviceIndex)?.Name
        ?? "系统默认";
    public string? SwitchToNextOutputDevice() =>
        SwitchOutputDevice(_audioPlayer.CurrentOutputDeviceIndex + 1);
    public string? SwitchToPreviousOutputDevice() =>
        SwitchOutputDevice(_audioPlayer.CurrentOutputDeviceIndex - 1);

    public string? SwitchOutputDevice(int index)
    {
        try
        {
            var devices = _audioPlayer.GetOutputDevices();
            var maxConcrete = devices.Count - 2;
            if (index > maxConcrete) index = -1;
            if (index < -1) index = maxConcrete;
            return _audioPlayer.SwitchOutputDevice(index) ? CurrentOutputDeviceName : null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Audio output switch failed: {ErrorType}",
                exception.GetType().Name);
            return null;
        }
    }

    public DownloadCacheCleanupResult ClearPlaybackCache()
    {
        lock (_gate)
        {
            if (_playbackCacheCleanupInProgress) return new(0, 0, 0);
            _playbackCacheCleanupInProgress = true;
        }

        var deleted = 0;
        var skipped = 0;
        long freedBytes = 0;
        try
        {
            Directory.CreateDirectory(_precacheDirectory);
            var cacheDirectory = new DirectoryInfo(_precacheDirectory);
            if (cacheDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return new(0, 0, 0);

            string[] activeFiles;
            lock (_gate)
                activeFiles = _precachedPaths.Values.Concat(_activePrecacheFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            foreach (var file in Directory.EnumerateFiles(_precacheDirectory, "lxmusic_pre_*.tmp"))
            {
                if (activeFiles.Contains(Path.GetFullPath(file), StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    deleted++;
                    freedBytes += length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    _logger.LogDebug(
                        "Playback precache cleanup skipped {ErrorType}",
                        exception.GetType().Name);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(
                "Playback precache cleanup failed {ErrorType}",
                exception.GetType().Name);
        }
        finally
        {
            lock (_gate) _playbackCacheCleanupInProgress = false;
        }

        return new(deleted, freedBytes, skipped);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();


    public async ValueTask DisposeAsync()
    {
        _audioPlayer.PlaybackCompleted -= AudioPlayer_PlaybackCompleted;
        lock (_gate) ++_queueRevision;
        CancelGenerationWork();
        using var disposalTimeout = new CancellationTokenSource(BackgroundWorkHost.DisposalWait);
        try
        {
            await _backgroundWork.WaitAsync(PlaySlot, disposalTimeout.Token);
            await _backgroundWork.WaitAsync(PrecacheSlot, disposalTimeout.Token);
        }
        catch (OperationCanceledException) when (disposalTimeout.IsCancellationRequested)
        {
            _logger.LogWarning("Music player disposal timed out waiting for background work.");
        }
        if (_ownsBackgroundWork) await _backgroundWork.DisposeAsync();
        if (_ownsHttpClient) _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private static int FindIndex(IReadOnlyList<MusicInfo> queue, MusicInfo selection)
    {
        for (var index = 0; index < queue.Count; index++)
        {
            if (SameIdentity(queue[index], selection)) return index;
        }
        return -1;
    }

    private static bool SameIdentity(MusicInfo left, MusicInfo right) =>
        string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase);

    private static bool IsLocal(MusicInfo music) =>
        string.Equals(music.Source, "Local", StringComparison.OrdinalIgnoreCase);

    private static string MusicKey(MusicInfo music) => $"{music.Source}|{music.Id}";
    private sealed record AlternativeResolution(MusicInfo Music, string Url);
    private static string ActivityId(long revision) => $"playback-track:{revision}";
    private static string? GetDowngradeQuality(string quality) =>
        string.Equals(quality.Trim(), "128k", StringComparison.OrdinalIgnoreCase)
            ? null
            : "128k";

    private static PlaybackResult Superseded(PlaybackTrigger trigger) => new(
        PlaybackResultStatus.Superseded,
        null,
        PlaybackQueueOrigin.Static,
        "播放请求已被更新的请求替代",
        trigger);
    private static PlaybackResult NoChange(PlaybackQueueOrigin origin, string message) =>
        new(PlaybackResultStatus.NoChange, null, origin, message);
    private static PlayerStateChangedEventArgs StateChange(
        PlayerState oldState,
        PlayerState newState,
        MusicInfo? music) => new()
        {
            OldState = oldState,
            NewState = newState,
            CurrentMusic = music
        };

    private void RaiseStateChanged(PlayerStateChangedEventArgs change)
    {
        foreach (EventHandler<PlayerStateChangedEventArgs> handler in
            StateChanged?.GetInvocationList() ?? [])
        {
            try { handler(this, change); }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Playback state observer failed: {ErrorType}",
                    exception.GetType().Name);
            }
        }
    }

    private void SafeAnnounce(AccessibleAnnouncement announcement)
    {
        try { _announcer.Announce(announcement); }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Playback announcement failed: {ErrorType}",
                exception.GetType().Name);
        }
    }

    private void RaiseQueueCompleted(PlaybackQueueOrigin origin)
    {
        var args = new PlaybackQueueCompletedEventArgs(origin);
        foreach (EventHandler<PlaybackQueueCompletedEventArgs> handler in
            QueueCompleted?.GetInvocationList() ?? [])
        {
            try { handler(this, args); }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Playback queue observer failed: {ErrorType}",
                    exception.GetType().Name);
            }
        }
    }
}
