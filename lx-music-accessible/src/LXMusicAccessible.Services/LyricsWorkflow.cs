using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Services;

public enum LyricsLoadStatus
{
    Empty,
    Loading,
    Ready,
    Missing,
    Failed
}

public sealed record LyricsLineState(TimeSpan Time, string Text, string? Translation)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Translation)
        ? Text
        : $"{Text}\n{Translation}";

    public string AccessibleText => string.IsNullOrWhiteSpace(Translation)
        ? Text
        : $"{Text}，翻译：{Translation}";
}

public sealed record LyricsState(
    MusicInfo? Music,
    IReadOnlyList<LyricsLineState> Lines,
    int CurrentIndex,
    int OffsetMilliseconds,
    bool AutoScroll,
    LyricsLoadStatus Status,
    string Message)
{
    public static LyricsState Empty { get; } = new(
        null,
        Array.Empty<LyricsLineState>(),
        -1,
        0,
        true,
        LyricsLoadStatus.Empty,
        "暂无歌词");
}

/// <summary>
/// Owns the complete lyrics workflow: generation-safe loading, playback tracking,
/// position mapping, per-song offsets, display state, and accessible announcements.
/// </summary>
public sealed class LyricsWorkflow : IDisposable
{
    private const string LoadSlot = "lyrics.load";
    private const string PositionSlot = "lyrics.position";
    private const string PersistSlot = "lyrics.persist-offsets";
    private static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SeekAnnouncementDelay = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly ILogger<LyricsWorkflow> _logger;
    private readonly ILyricService _lyrics;
    private readonly IPlaybackCoordinator _playback;
    private readonly IAudioPlaybackControl _audio;
    private readonly BackgroundWorkHost _backgroundWork;
    private readonly IAccessibleAnnouncer _announcer;
    private readonly Func<IReadOnlyDictionary<string, int>, CancellationToken, Task> _persistOffsets;
    private readonly Dictionary<string, int> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private LyricsState _state = LyricsState.Empty;
    private string? _activeMusicKey;
    private bool _autoAnnounce = true;
    private bool _speechSuppressed;
    private bool _announceAfterSeek;
    private long _announceBlockedUntilTick;
    private volatile bool _disposed;

    public LyricsWorkflow(
        ILogger<LyricsWorkflow> logger,
        ILyricService lyrics,
        IPlaybackCoordinator playback,
        IAudioPlaybackControl audio,
        BackgroundWorkHost backgroundWork,
        IAccessibleAnnouncer announcer,
        SettingsWorkflow settingsWorkflow)
        : this(
            logger,
            lyrics,
            playback,
            audio,
            backgroundWork,
            announcer,
            (offsets, cancellationToken) => PersistOffsetsAsync(settingsWorkflow, offsets, cancellationToken),
            startPositionTracking: true)
    {
    }

    internal LyricsWorkflow(
        ILogger<LyricsWorkflow> logger,
        ILyricService lyrics,
        IPlaybackCoordinator playback,
        IAudioPlaybackControl audio,
        BackgroundWorkHost backgroundWork,
        IAccessibleAnnouncer announcer,
        Func<IReadOnlyDictionary<string, int>, CancellationToken, Task>? persistOffsets = null,
        bool startPositionTracking = false)
    {
        _logger = logger;
        _lyrics = lyrics;
        _playback = playback;
        _audio = audio;
        _backgroundWork = backgroundWork;
        _announcer = announcer;
        _persistOffsets = persistOffsets ?? ((_, _) => Task.CompletedTask);
        _playback.StateChanged += Playback_StateChanged;
        if (startPositionTracking)
            _backgroundWork.StartLatest(PositionSlot, TrackPositionAsync);
    }

    public event EventHandler<LyricsState>? StateChanged;

    public LyricsState Current
    {
        get { lock (_gate) return _state; }
    }

    public bool AutoAnnounce
    {
        get { lock (_gate) return _autoAnnounce; }
        set { lock (_gate) _autoAnnounce = value; }
    }

    public bool AutoScroll
    {
        get { lock (_gate) return _state.AutoScroll; }
        set
        {
            LyricsState next;
            lock (_gate)
            {
                if (_state.AutoScroll == value) return;
                _state = _state with { AutoScroll = value };
                next = _state;
            }
            Publish(next);
        }
    }

    /// <summary>Temporarily keeps lyric speech from interrupting a queued device announcement.</summary>
    public bool SpeechSuppressed
    {
        get { lock (_gate) return _speechSuppressed; }
        set { lock (_gate) _speechSuppressed = value; }
    }

    public void ApplySettings(
        bool autoAnnounce,
        bool autoScroll,
        IReadOnlyDictionary<string, int>? offsets)
    {
        LyricsState next;
        lock (_gate)
        {
            _autoAnnounce = autoAnnounce;
            _offsets.Clear();
            if (offsets is not null)
            {
                foreach (var (key, value) in offsets)
                    _offsets[key] = Math.Clamp(value, -30000, 30000);
            }

            var offset = _activeMusicKey is not null && _offsets.TryGetValue(_activeMusicKey, out var saved)
                ? saved
                : 0;
            var position = SameMusic(_state.Music, _playback.CurrentMusic)
                ? _audio.Position
                : TimeSpan.Zero;
            _state = _state with
            {
                AutoScroll = autoScroll,
                OffsetMilliseconds = offset,
                CurrentIndex = FindIndex(_state.Lines, position, offset)
            };
            next = _state;
        }
        Publish(next);
    }

    public IReadOnlyDictionary<string, int> GetOffsetsSnapshot()
    {
        lock (_gate) return new Dictionary<string, int>(_offsets, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> LoadAsync(MusicInfo music)
    {
        ArgumentNullException.ThrowIfNull(music);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = MusicKey(music);
        LyricsState loading;
        lock (_gate)
        {
            _activeMusicKey = key;
            _announceAfterSeek = false;
            _announceBlockedUntilTick = 0;
            var offset = _offsets.TryGetValue(key, out var saved) ? saved : 0;
            _state = new LyricsState(
                music,
                Array.Empty<LyricsLineState>(),
                -1,
                offset,
                _state.AutoScroll,
                LyricsLoadStatus.Loading,
                "歌词加载中...");
            loading = _state;
        }
        Publish(loading);

        var applied = false;
        await _backgroundWork.RunLatestAsync(LoadSlot, async context =>
        {
            LyricResult? result = null;
            Exception? failure = null;
            try
            {
                result = await _lyrics.GetLyricAsync(music, context.CancellationToken);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            LyricsState? committed = null;
            context.TryCommit(() =>
            {
                lock (_gate)
                {
                    if (!string.Equals(_activeMusicKey, key, StringComparison.OrdinalIgnoreCase)) return;
                    if (failure is not null)
                    {
                        _logger.LogWarning(
                            failure,
                            "Lyric loading failed for {Source}:{Id}",
                            music.Source,
                            music.Id);
                        _state = _state with
                        {
                            Status = LyricsLoadStatus.Failed,
                            Message = "歌词加载失败"
                        };
                    }
                    else if (result?.Lyric is not { Lines.Count: > 0 } lyric)
                    {
                        _state = _state with
                        {
                            Status = LyricsLoadStatus.Missing,
                            Message = IsLocal(music) ? "本地歌曲暂不支持歌词" : "未找到歌词"
                        };
                    }
                    else
                    {
                        var lines = lyric.Lines
                            .OrderBy(line => line.Time)
                            .Select(line => new LyricsLineState(line.Time, line.Text, line.Translation))
                            .ToArray();
                        var position = SameMusic(music, _playback.CurrentMusic)
                            ? _audio.Position
                            : TimeSpan.Zero;
                        var index = FindIndex(lines, position, _state.OffsetMilliseconds);
                        _state = _state with
                        {
                            Lines = lines,
                            CurrentIndex = index,
                            Status = LyricsLoadStatus.Ready,
                            Message = result.FromSearch
                                ? $"已从{SourceDisplayName(result.SourceId)}搜索到歌词，共 {lines.Length} 行"
                                : $"已加载歌词，共 {lines.Length} 行"
                        };
                        WarnIfDurationMismatch(music, lines);
                    }
                    committed = _state;
                    applied = true;
                }
            });

            if (committed is not null) Publish(committed);
        });
        return applied;
    }

    public void Seek(TimeSpan position)
    {
        LyricsState? changed;
        lock (_gate)
        {
            _announceBlockedUntilTick = Environment.TickCount64 + (long)SeekAnnouncementDelay.TotalMilliseconds;
            _announceAfterSeek = true;
            changed = SynchronizePositionLocked(position);
        }
        if (changed is not null) Publish(changed);
    }

    public void AdjustOffset(int deltaMilliseconds)
    {
        LyricsState next;
        IReadOnlyDictionary<string, int> snapshot;
        lock (_gate)
        {
            var offset = Math.Clamp(_state.OffsetMilliseconds + deltaMilliseconds, -30000, 30000);
            if (_activeMusicKey is not null) _offsets[_activeMusicKey] = offset;
            var position = SameMusic(_state.Music, _playback.CurrentMusic)
                ? _audio.Position
                : TimeSpan.Zero;
            _state = _state with
            {
                OffsetMilliseconds = offset,
                CurrentIndex = FindIndex(_state.Lines, position, offset)
            };
            next = _state;
            snapshot = new Dictionary<string, int>(_offsets, StringComparer.OrdinalIgnoreCase);
        }
        Publish(next);
        _backgroundWork.StartLatest(PersistSlot, context =>
            _persistOffsets(snapshot, context.CancellationToken));

        var direction = deltaMilliseconds > 0 ? "延后" : "提前";
        SafeAnnounce(new(
            $"歌词已{direction}，当前偏移 {next.OffsetMilliseconds / 1000.0:+#.##;-#.##;0} 秒",
            AnnouncementKind.Status,
            "lyrics-offset"));
    }

    public void AnnounceCurrentLine()
    {
        LyricsState state;
        lock (_gate)
        {
            if (_speechSuppressed) return;
            state = _state;
        }
        var text = state.CurrentIndex >= 0 && state.CurrentIndex < state.Lines.Count
            ? state.Lines[state.CurrentIndex].AccessibleText
            : state.Lines.FirstOrDefault()?.AccessibleText ?? state.Message;
        SafeAnnounce(new(
            text,
            AnnouncementKind.Progress,
            "lyrics",
            AnnouncementSpeech.Always));
    }

    internal void SynchronizePosition(TimeSpan position)
    {
        LyricsState? changed;
        string? announcement;
        lock (_gate)
        {
            changed = SynchronizePositionLocked(position);
            announcement = SelectPositionAnnouncementLocked(changed is not null);
        }
        if (changed is not null) Publish(changed);
        if (announcement is not null)
        {
            SafeAnnounce(new(
                announcement,
                AnnouncementKind.Progress,
                "lyrics",
                AnnouncementSpeech.Always));
        }
    }

    internal void SynchronizePlaybackPosition()
    {
        MusicInfo? playing = _playback.CurrentMusic;
        bool shouldSynchronize;
        lock (_gate)
        {
            shouldSynchronize = _playback.IsPlaying
                && playing is not null
                && SameMusic(_state.Music, playing)
                && _state.Status == LyricsLoadStatus.Ready;
        }
        if (shouldSynchronize) SynchronizePosition(_audio.Position);
    }

    private async Task TrackPositionAsync(BackgroundWorkContext context)
    {
        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            SynchronizePlaybackPosition();
            await Task.Delay(PositionInterval, context.CancellationToken);
        }
    }

    private void Playback_StateChanged(object? sender, PlayerStateChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.NewState == PlayerState.Playing && e.CurrentMusic is not null)
        {
            bool needsLoad;
            lock (_gate) needsLoad = !SameMusic(_state.Music, e.CurrentMusic);
            if (needsLoad) _ = LoadAsync(e.CurrentMusic);
            return;
        }

        if (e.NewState is PlayerState.Stopped or PlayerState.Error)
        {
            _backgroundWork.Cancel(LoadSlot);
            LyricsState? changed = null;
            lock (_gate)
            {
                _announceAfterSeek = false;
                if (_state.Status == LyricsLoadStatus.Loading)
                {
                    _state = _state with
                    {
                        Status = LyricsLoadStatus.Empty,
                        Message = "暂无歌词"
                    };
                    changed = _state;
                }
            }
            if (changed is not null) Publish(changed);
        }
    }

    private LyricsState? SynchronizePositionLocked(TimeSpan position)
    {
        if (_state.Status != LyricsLoadStatus.Ready || _state.Lines.Count == 0) return null;
        var index = FindIndex(_state.Lines, position, _state.OffsetMilliseconds);
        if (index == _state.CurrentIndex) return null;
        _state = _state with { CurrentIndex = index };
        return _state;
    }

    private string? SelectPositionAnnouncementLocked(bool indexChanged)
    {
        if (_speechSuppressed || !_playback.IsPlaying) return null;
        var blockOver = _announceBlockedUntilTick <= 0
            || Environment.TickCount64 >= _announceBlockedUntilTick;
        if (_announceAfterSeek)
        {
            if (!blockOver) return null;
            _announceAfterSeek = false;
            if (!_autoAnnounce) return null;
        }
        else if (!_autoAnnounce || !indexChanged || !blockOver)
        {
            return null;
        }

        return _state.CurrentIndex >= 0 && _state.CurrentIndex < _state.Lines.Count
            ? _state.Lines[_state.CurrentIndex].AccessibleText
            : null;
    }

    private void Publish(LyricsState state)
    {
        foreach (EventHandler<LyricsState> handler in StateChanged?.GetInvocationList() ?? [])
        {
            try { handler(this, state); }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Lyrics state observer failed: {ErrorType}",
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
                "Lyrics announcement failed: {ErrorType}",
                exception.GetType().Name);
        }
    }

    private void WarnIfDurationMismatch(MusicInfo music, IReadOnlyList<LyricsLineState> lines)
    {
        var duration = _audio.Duration;
        if (duration > TimeSpan.Zero && lines[^1].Time > duration + TimeSpan.FromSeconds(10))
        {
            _logger.LogWarning(
                "Lyric duration exceeds audio duration for {Source}:{Id}",
                music.Source,
                music.Id);
        }
    }

    private static int FindIndex(
        IReadOnlyList<LyricsLineState> lines,
        TimeSpan position,
        int offsetMilliseconds)
    {
        var adjusted = position - TimeSpan.FromMilliseconds(offsetMilliseconds);
        var index = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (adjusted < lines[i].Time) break;
            index = i;
        }
        return index;
    }

    private static string MusicKey(MusicInfo music) => $"{music.Source}|{music.Id}";
    private static bool SameMusic(MusicInfo? left, MusicInfo? right) =>
        left is not null
        && right is not null
        && string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
    private static bool IsLocal(MusicInfo music) =>
        string.Equals(music.Source, "Local", StringComparison.OrdinalIgnoreCase);

    private static string SourceDisplayName(string sourceId) => sourceId.ToLowerInvariant() switch
    {
        "netease" => "网易云音乐",
        "qq" => "QQ音乐",
        "kg" => "酷狗音乐",
        "kw" => "酷我音乐",
        "migu" => "咪咕音乐",
        "script" => "脚本源",
        "local" => "本地",
        _ => sourceId
    };

    private static async Task PersistOffsetsAsync(
        SettingsWorkflow settingsWorkflow,
        IReadOnlyDictionary<string, int> offsets,
        CancellationToken cancellationToken)
    {
        var edit = settingsWorkflow.Edit(settings =>
            settings.LyricOffsets = new Dictionary<string, int>(offsets, StringComparer.OrdinalIgnoreCase));
        if (!edit.Accepted)
            throw new InvalidOperationException(string.Join(" ", edit.Errors));
        var saved = await settingsWorkflow.SaveAsync(edit.State, cancellationToken);
        if (!saved.Succeeded)
            throw new InvalidOperationException($"Lyric offset persistence failed: {saved.ErrorType}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _playback.StateChanged -= Playback_StateChanged;
        _backgroundWork.Cancel(LoadSlot);
        _backgroundWork.Cancel(PositionSlot);
        _backgroundWork.Cancel(PersistSlot);
    }
}
