using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Services;

public sealed record DownloadPreferences(
    bool DownloadLyric,
    DownloadNameOrder NameOrder,
    bool IncludeQualityTag,
    int BatchDownloadDelaySeconds = 2,
    int DownloadSpeedLimitKbps = 0,
    string? TargetDirectory = null);

internal class DownloadThrottle
{
    public virtual Task DelayBetweenSongsAsync(
        DownloadPreferences preferences,
        bool hasMoreSongs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!hasMoreSongs)
            return Task.CompletedTask;
        var seconds = Math.Max(0, preferences.BatchDownloadDelaySeconds);
        return seconds == 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
    }

    public virtual async Task PaceTransferAsync(
        DownloadPreferences preferences,
        TimeSpan elapsed,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var delay = TransferDelay(preferences.DownloadSpeedLimitKbps, elapsed, totalBytes);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);
    }

    internal static TimeSpan TransferDelay(int limitKbps, TimeSpan elapsed, long totalBytes)
    {
        if (limitKbps <= 0 || totalBytes <= 0)
            return TimeSpan.Zero;
        var target = TimeSpan.FromSeconds(totalBytes / (limitKbps * 1024d));
        return target > elapsed ? target - elapsed : TimeSpan.Zero;
    }
}

public sealed record DownloadRequest(
    MusicInfo Music,
    string Quality = "128k",
    string? TargetDirectory = null);

public sealed record DownloadListRequest(
    IReadOnlyList<MusicInfo> Songs,
    string Label,
    string Quality = "128k",
    string? TargetDirectory = null,
    string? Subdirectory = null);

public sealed record DownloadListResult(
    int Requested,
    int Completed,
    int Failed,
    int Cancelled,
    int Skipped,
    IReadOnlyList<DownloadTask> Tasks,
    string Message)
{
    public bool Succeeded => Requested > 0 && Completed == Requested;
    public bool PartiallySucceeded => Completed > 0 && !Succeeded;
}

public sealed record ExternalPlaybackTarget(string Value);

public sealed record ExternalPlaybackRequest(string PlayerPath, MusicInfo Music, string Quality);

public sealed record ExternalPlaybackResult(bool Succeeded, string Message);

public sealed record DownloadCacheCleanupResult(int DeletedFiles, long FreedBytes, int SkippedFiles = 0);

/// <summary>
/// Deep download module. Its interface accepts complete single/list intents and owns
/// resolution, HTTP, cancellation, naming, conflict handling, progress, temporary
/// files, atomic commit, cleanup, and accessible summaries.
/// </summary>
public interface IDownloadWorkflow
{
    ReadOnlyObservableCollection<DownloadTask> Tasks { get; }
    void ApplyPreferences(DownloadPreferences preferences);
    Task<DownloadTask> DownloadAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default);
    Task<DownloadListResult> DownloadListAsync(
        DownloadListRequest request,
        CancellationToken cancellationToken = default);
    Task<bool> RetryAsync(
        DownloadTask task,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default);
    Task<bool> RedownloadAsync(
        DownloadTask task,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default);
    void Cancel(DownloadTask task);
    void Pause(DownloadTask task);
    void Resume(DownloadTask task);
    bool Delete(DownloadTask task, bool deleteSourceFile);
    Task<bool> DeleteAsync(DownloadTask task, bool deleteSourceFile);
    void ClearCompleted();
    string ResolveDownloadDirectory(string? requested = null);
    ExternalPlaybackResult OpenDownloadDirectory(string? requested = null);
    Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(
        MusicInfo music,
        string quality,
        CancellationToken cancellationToken = default);
    Task<ExternalPlaybackResult> PlayExternallyAsync(
        ExternalPlaybackRequest request,
        CancellationToken cancellationToken = default);
    DownloadCacheCleanupResult ClearTemporaryFiles();
}

public interface IDownloadSource
{
    Task<IReadOnlyList<string>> GetQualitiesAsync(
        MusicInfo music,
        CancellationToken cancellationToken);
    Task<string?> GetUrlAsync(
        MusicInfo music,
        string quality,
        CancellationToken cancellationToken);
    Task<LyricResult?> GetLyricAsync(
        MusicInfo music,
        CancellationToken cancellationToken);
}

internal sealed class DownloadSourceAdapter(
    IMusicSourceService musicSources,
    ILyricService lyrics) : IDownloadSource
{
    public async Task<IReadOnlyList<string>> GetQualitiesAsync(
        MusicInfo music,
        CancellationToken cancellationToken)
    {
        // Download tasks and quality menus must use the same declarations, including
        // default-source preference and platform fallback implemented by the candidate source.
        if (musicSources is IQualityCandidateSource candidateSource)
        {
            var candidates = await candidateSource
                .GetCandidatesAsync(music, cancellationToken)
                .WaitAsync(cancellationToken);
            return QualityCatalog.Order(candidates.Select(candidate => candidate.Quality));
        }

        return await musicSources
            .GetQualitiesForSourceAsync(music.Source)
            .WaitAsync(cancellationToken);
    }

    public Task<string?> GetUrlAsync(
        MusicInfo music,
        string quality,
        CancellationToken cancellationToken) =>
        musicSources.GetPlayUrlAsync(music, music.Source, quality, cancellationToken);

    public Task<LyricResult?> GetLyricAsync(
        MusicInfo music,
        CancellationToken cancellationToken) =>
        lyrics.GetLyricAsync(music, cancellationToken);
}

public sealed class DownloadWorkflow : IDownloadWorkflow, IAsyncDisposable
{
    private static readonly HashSet<string> KnownAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".aac", ".ape", ".wma", ".opus", ".webm"
    };

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly ILogger<DownloadWorkflow> _logger;
    private readonly IDownloadSource _source;
    private readonly HttpClient _httpClient;
    private readonly BackgroundWorkHost _backgroundWork;
    private readonly ConcurrentDictionary<string, DownloadOperation> _operations = new(StringComparer.Ordinal);
    private readonly IAccessibleAnnouncer _announcer;
    private readonly IProcessLauncher _processLauncher;
    private readonly string _temporaryRoot;
    private readonly Dictionary<string, DownloadPlan> _plans = new(StringComparer.Ordinal);
    private readonly HashSet<string> _externalTemporaryFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly ObservableCollection<DownloadTask> _tasks = [];
    private DownloadPreferences _preferences = new(true, DownloadNameOrder.ArtistTitle, true);
    private readonly DownloadThrottle _throttle = new();
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly TimeSpan _resolutionTimeout = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _responseHeaderTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _readStallTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _lyricTimeout = TimeSpan.FromSeconds(20);
    private bool _disposed;

    public DownloadWorkflow(
        ILogger<DownloadWorkflow> logger,
        IMusicSourceService musicSources,
        ILyricService lyrics,
        HttpClient httpClient,
        BackgroundWorkHost backgroundWork,
        IAccessibleAnnouncer announcer,
        IProcessLauncher processLauncher)
        : this(
            logger,
            new DownloadSourceAdapter(musicSources, lyrics),
            httpClient,
            backgroundWork,
            announcer,
            temporaryRoot: null,
            processLauncher)
    {
    }

    internal DownloadWorkflow(
        ILogger<DownloadWorkflow> logger,
        IDownloadSource source,
        HttpClient httpClient,
        BackgroundWorkHost backgroundWork,
        IAccessibleAnnouncer announcer,
        string? temporaryRoot,
        IProcessLauncher processLauncher,
        DownloadThrottle throttle,
        TimeSpan? resolutionTimeout = null,
        TimeSpan? responseHeaderTimeout = null,
        TimeSpan? readStallTimeout = null,
        TimeSpan? lyricTimeout = null)
        : this(logger, source, httpClient, backgroundWork, announcer, temporaryRoot, processLauncher)
    {
        _throttle = throttle;
        if (resolutionTimeout.HasValue) _resolutionTimeout = resolutionTimeout.Value;
        if (responseHeaderTimeout.HasValue) _responseHeaderTimeout = responseHeaderTimeout.Value;
        if (readStallTimeout.HasValue) _readStallTimeout = readStallTimeout.Value;
        if (lyricTimeout.HasValue) _lyricTimeout = lyricTimeout.Value;
    }

    public DownloadWorkflow(
        ILogger<DownloadWorkflow> logger,
        IDownloadSource source,
        HttpClient httpClient,
        BackgroundWorkHost backgroundWork,
        IAccessibleAnnouncer announcer,
        string? temporaryRoot,
        IProcessLauncher processLauncher)
    {
        _logger = logger;
        _source = source;
        _httpClient = httpClient;
        _backgroundWork = backgroundWork;
        _announcer = announcer;
        _processLauncher = processLauncher;
        _temporaryRoot = Path.GetFullPath(temporaryRoot ?? Path.GetTempPath());
        Tasks = new ReadOnlyObservableCollection<DownloadTask>(_tasks);
    }

    public ReadOnlyObservableCollection<DownloadTask> Tasks { get; }

    public void ApplyPreferences(DownloadPreferences preferences)
    {
        FrameworkCompatibility.ThrowIfNull(preferences);
        preferences = preferences with
        {
            BatchDownloadDelaySeconds = preferences.BatchDownloadDelaySeconds < 0
                ? 0
                : preferences.BatchDownloadDelaySeconds > 60 ? 60 : preferences.BatchDownloadDelaySeconds,
            DownloadSpeedLimitKbps = preferences.DownloadSpeedLimitKbps switch
            {
                <= 0 => 0,
                < 32 => 32,
                > 100000 => 100000,
                _ => preferences.DownloadSpeedLimitKbps
            }
        };
        lock (_gate) _preferences = preferences;
    }

    public async Task<DownloadTask> DownloadAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(request);
        FrameworkCompatibility.ThrowIfNull(request.Music);
        if (_disposed) throw new ObjectDisposedException(nameof(DownloadWorkflow));

        var task = new DownloadTask
        {
            Music = request.Music,
            Quality = string.IsNullOrWhiteSpace(request.Quality) ? "128k" : request.Quality
        };
        DownloadPreferences preferences;
        DownloadPlan plan;
        lock (_gate)
        {
            preferences = _preferences;
            plan = new DownloadPlan(
                request.TargetDirectory ?? preferences.TargetDirectory,
                preferences,
                task.Quality);
            _plans[task.Id] = plan;
        }
        _tasks.Add(task);
        await ExecuteManagedAsync(task, plan.TargetDirectory, preferences, announce: true, cancellationToken);
        return task;
    }

    public async Task<DownloadListResult> DownloadListAsync(
        DownloadListRequest request,
        CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(request);
        FrameworkCompatibility.ThrowIfNull(request.Songs);
        if (_disposed) throw new ObjectDisposedException(nameof(DownloadWorkflow));

        var songs = request.Songs.Where(song => song is not null).ToArray();
        var requested = songs.Length;
        var label = string.IsNullOrWhiteSpace(request.Label) ? "列表" : request.Label.Trim();
        var activityId = $"download-list:{Guid.NewGuid():N}";
        if (requested == 0)
        {
            const string empty = "没有可下载的歌曲";
            SafeAnnounce(new(empty, AnnouncementKind.Error, activityId));
            return new(0, 0, 0, 0, 0, [], empty);
        }

        DownloadListResult? result = null;
        await _backgroundWork.RunLatestAsync("download.list", async context =>
        {
            using var registration = cancellationToken.Register(() =>
                _backgroundWork.Cancel("download.list"));
            string targetDirectory;
            string? requestedTargetDirectory;
            lock (_gate) requestedTargetDirectory = request.TargetDirectory ?? _preferences.TargetDirectory;
            try
            {
                targetDirectory = ResolveTargetDirectory(requestedTargetDirectory);
                if (!string.IsNullOrWhiteSpace(request.Subdirectory))
                {
                    targetDirectory = EnsureSafeDirectory(
                        Path.Combine(targetDirectory, SanitizeFileName(request.Subdirectory)));
                }
            }
            catch (DownloadSafetyException exception)
            {
                var refusalMessage = $"批量下载{label}失败：{exception.Message}";
                SafeAnnounce(new(refusalMessage, AnnouncementKind.Error, activityId));
                result = new(requested, 0, requested, 0, 0, [], refusalMessage);
                return;
            }

            SafeAnnounce(new(
                $"已开始批量下载{label}：{requested} 首歌曲",
                AnnouncementKind.Busy,
                activityId));

            DownloadPreferences preferences;
            lock (_gate) preferences = _preferences;
            var tasks = new List<DownloadTask>(requested);
            var completed = 0;
            var failed = 0;
            var cancelled = 0;
            var skipped = 0;

            for (var index = 0; index < songs.Length; index++)
            {
                if (context.CancellationToken.IsCancellationRequested)
                {
                    skipped = songs.Length - index;
                    break;
                }

                var task = new DownloadTask
                {
                    Music = songs[index],
                    Quality = string.IsNullOrWhiteSpace(request.Quality) ? "128k" : request.Quality,
                    IsBatch = true
                };
                lock (_gate) _plans[task.Id] = new DownloadPlan(
                    targetDirectory,
                    preferences,
                    task.Quality);
                _tasks.Add(task);
                tasks.Add(task);
                await ExecuteManagedAsync(
                    task,
                    targetDirectory,
                    preferences,
                    announce: false,
                    context.CancellationToken);

                switch (task.Status)
                {
                    case DownloadTaskStatus.Completed: completed++; break;
                    case DownloadTaskStatus.Cancelled: cancelled++; break;
                    default: failed++; break;
                }
                if (context.CancellationToken.IsCancellationRequested)
                {
                    skipped = songs.Length - index - 1;
                    break;
                }

                if (index < songs.Length - 1)
                {
                    await _throttle.DelayBetweenSongsAsync(
                        preferences,
                        hasMoreSongs: true,
                        context.CancellationToken);
                }
            }

            var message = $"批量下载{label}结束：成功 {completed} 首，失败 {failed} 首，取消 {cancelled} 首，跳过 {skipped} 首";
            SafeAnnounce(new(
                message,
                failed > 0 || cancelled > 0 || skipped > 0
                    ? AnnouncementKind.Error
                    : AnnouncementKind.Success,
                activityId));
            result = new(requested, completed, failed, cancelled, skipped, tasks, message);
        });

        return result ?? new(
            requested,
            0,
            0,
            0,
            requested,
            [],
            $"批量下载{label}已取消：跳过 {requested} 首");
    }

    private async Task<bool> RestartDownloadAsync(
        DownloadTask task,
        bool manualRedownload,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(task);
        if (_disposed) throw new ObjectDisposedException(nameof(DownloadWorkflow));
        DownloadPlan plan;
        lock (_gate)
        {
            if (!_plans.TryGetValue(task.Id, out plan!)) return false;
            if (_operations.ContainsKey(task.Id)
                || task.Status is DownloadTaskStatus.Pending
                    or DownloadTaskStatus.Downloading
                    or DownloadTaskStatus.Paused)
                return false;
            if (!string.IsNullOrWhiteSpace(targetDirectory))
                plan = plan with { TargetDirectory = targetDirectory };
            plan = plan with
            {
                Preferences = plan.Preferences with { DownloadLyric = true },
                ReplaceAudioPath = task.SavePath,
                ReplaceLyricPath = task.LyricPath,
            };
            _plans[task.Id] = plan;
        }

        task.Quality = plan.RequestedQuality;
        task.ActualQuality = null;
        task.QualityFallback = null;
        task.Progress = 0;
        task.DownloadedBytes = 0;
        task.TotalBytes = 0;
        task.ErrorMessage = null;
        task.IsBatch = false;
        task.Status = DownloadTaskStatus.Pending;
        await ExecuteManagedAsync(
            task,
            plan.TargetDirectory,
            plan.Preferences,
            announce: true,
            cancellationToken,
            plan.ReplaceAudioPath,
            plan.ReplaceLyricPath);
        return true;
    }
    public async Task<bool> RetryAsync(
        DownloadTask task,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default)
    {
        return await RestartDownloadAsync(task, manualRedownload: false, targetDirectory, cancellationToken);
    }

    public async Task<bool> RedownloadAsync(
        DownloadTask task,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default)
    {
        return await RestartDownloadAsync(task, manualRedownload: true, targetDirectory, cancellationToken);
    }

    public void Cancel(DownloadTask task)
    {
        if (task is null) return;
        if (_operations.TryGetValue(task.Id, out var operation)) operation.Cancel();
        _backgroundWork.Cancel(DownloadSlot(task));
    }

    public void Pause(DownloadTask task)
    {
        if (task is null || task.Status != DownloadTaskStatus.Downloading) return;
        if (_operations.TryGetValue(task.Id, out var operation) && operation.Pause())
            task.Status = DownloadTaskStatus.Paused;
    }

    public void Resume(DownloadTask task)
    {
        if (task is null || task.Status != DownloadTaskStatus.Paused) return;
        if (_operations.TryGetValue(task.Id, out var operation) && operation.Resume())
            task.Status = DownloadTaskStatus.Downloading;
    }

    public bool Delete(DownloadTask task, bool deleteSourceFile) => DeleteAsyncCore(task, deleteSourceFile).GetAwaiter().GetResult();
    public Task<bool> DeleteAsync(DownloadTask task, bool deleteSourceFile) => DeleteAsyncCore(task, deleteSourceFile);

    private async Task<bool> DeleteAsyncCore(DownloadTask task, bool deleteSourceFile)
    {
        if (task is null) return false;
        var wasActive = task.Status is DownloadTaskStatus.Pending
            or DownloadTaskStatus.Downloading
            or DownloadTaskStatus.Paused;
        if (wasActive) Cancel(task);

        if (wasActive)
        {
            using var waitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _backgroundWork.WaitAsync(DownloadSlot(task), waitTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }


        _tasks.Remove(task);
        lock (_gate) _plans.Remove(task.Id);

        if (deleteSourceFile)
        {
            DeleteTemporaryFile(task.SavePath, task.Id);
            DeleteTemporaryFile(task.LyricPath, task.Id);
        }

        return true;
    }

    public void ClearCompleted()
    {
        var done = Tasks
            .Where(task => task.Status is DownloadTaskStatus.Completed
                or DownloadTaskStatus.Failed
                or DownloadTaskStatus.Cancelled)
            .ToArray();
        foreach (var task in done)
        {
            _tasks.Remove(task);
            lock (_gate) _plans.Remove(task.Id);
        }
    }

    public string ResolveDownloadDirectory(string? requested = null)
    {
        string? preferredDirectory;
        lock (_gate) preferredDirectory = _preferences.TargetDirectory;
        var effectiveDirectory = string.IsNullOrWhiteSpace(requested) ? preferredDirectory : requested;
        try
        {
            return ResolveTargetDirectory(effectiveDirectory);
        }
        catch (DownloadSafetyException) when (!string.IsNullOrWhiteSpace(effectiveDirectory))
        {
            return ResolveTargetDirectory(null);
        }
    }

    public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null)
    {
        try
        {
            var directory = ResolveDownloadDirectory(requested);
            _processLauncher.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"")
            {
                UseShellExecute = true
            });
            return new(true, $"已打开下载文件夹 {directory}");
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Opening download directory failed: {ErrorType}", exception.GetType().Name);
            return new(false, "打开下载文件夹失败");
        }
    }

    private static string GetDefaultDownloadDirectory()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrWhiteSpace(music)) return Path.Combine(music, "落雪音乐下载");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local)) return Path.Combine(local, "lxmusic", "downloads");
        return Path.Combine(Path.GetTempPath(), "LXMusicAccessible.Downloads");
    }

    public async Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(
        MusicInfo music,
        string quality,
        CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(music);
        if (_disposed) throw new ObjectDisposedException(nameof(DownloadWorkflow));
        if (string.Equals(music.Source, "Local", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(music.PlayUrl) && File.Exists(music.PlayUrl)
                ? new ExternalPlaybackTarget(Path.GetFullPath(music.PlayUrl))
                : null;
        }

        var activityId = $"download-external:{Guid.NewGuid():N}";
        SafeAnnounce(new($"正在准备外部播放：{music.Title}", AnnouncementKind.Busy, activityId));
        ExternalPlaybackTarget? result = null;
        await _backgroundWork.RunLatestAsync("download.external", async context =>
        {
            using var registration = cancellationToken.Register(() =>
                _backgroundWork.Cancel("download.external"));
            var declared = await _source.GetQualitiesAsync(music, context.CancellationToken);
            if (!declared.Contains(QualityCatalog.Normalize(quality), StringComparer.OrdinalIgnoreCase))
            {
                SafeAnnounce(new(
                    "音源未声明所选音质：" + QualityCatalog.Label(quality),
                    AnnouncementKind.Error,
                    activityId));
                return;
            }

            var url = await _source.GetUrlAsync(music, quality, context.CancellationToken);
            if (string.IsNullOrWhiteSpace(url)) return;

            DownloadPreferences preferences;
            lock (_gate) preferences = _preferences;

            try
            {
                var directory = EnsureSafeDirectory(Path.Combine(
                    _temporaryRoot,
                    "LXMusicAccessible.External"));
                string? temporary = null;
                try
                {
                    var download = await DownloadHttpToTemporaryFileAsync(
                        url,
                        directory,
                        preferences,
                        context.CancellationToken,
                        progress: null);
                    temporary = download.Path;
                    var final = Path.Combine(
                        directory,
                        $"lxmusic_ext_{Guid.NewGuid():N}{NormalizeAudioExtension(download.Extension)}");
                    EnsureContained(directory, final);
                    File.Move(download.Path, final);
                    temporary = null;
                    lock (_gate) _externalTemporaryFiles.Add(final);
                    result = new ExternalPlaybackTarget(final);
                }
                finally
                {
                    DeleteTemporaryFile(temporary, "external");
                }
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
catch (Exception exception)
            {
                _logger.LogWarning(
                    "External playback materialization failed: {ErrorType}",
                    exception.GetType().Name);
                result = new ExternalPlaybackTarget(url);
            }
        });

        if (result is null)
            SafeAnnounce(new("未能准备外部播放链接", AnnouncementKind.Error, activityId));
        return result;
    }

    public async Task<ExternalPlaybackResult> PlayExternallyAsync(
        ExternalPlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(request);
        var playerPath = Environment.ExpandEnvironmentVariables(
            request.PlayerPath?.Trim().Trim('"').Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(playerPath))
            return new(false, "请先在设置中配置第三方播放器");
        if (!File.Exists(playerPath))
            return new(false, "未找到第三方播放器程序，请检查设置中的播放器路径");

        try
        {
            var target = await PrepareExternalPlaybackAsync(
                request.Music,
                request.Quality,
                cancellationToken);
            if (target is null) return new(false, "未能准备外部播放内容");
            _processLauncher.Start(new ProcessStartInfo
            {
                FileName = playerPath,
                Arguments = $"\"{target.Value}\"",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(playerPath) ?? string.Empty
            });
            return new(true, $"已调用第三方播放器播放：{request.Music.Title}，{request.Music.Artist}");
        }
        catch (Exception exception)
        {
            _logger.LogWarning("External player launch failed: {ErrorType}", exception.GetType().Name);
            return new(false, "调用第三方播放器失败");
        }
    }

    public DownloadCacheCleanupResult ClearTemporaryFiles()
    {
        string directory;
        try { directory = EnsureSafeDirectory(Path.Combine(_temporaryRoot, "LXMusicAccessible.External")); }
        catch (DownloadSafetyException) { return new(0, 0); }
        var deleted = 0;
        var skipped = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory)
                .Where(path => Path.GetFileName(path).StartsWith("lxmusic_ext_", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).EndsWith(".download", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    EnsureContained(directory, file);
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    lock (_gate) _externalTemporaryFiles.Remove(file);
                    deleted++;
                    bytes += length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    _logger.LogDebug(
                        "Temporary download cleanup skipped {ErrorType}",
                        exception.GetType().Name);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(
                "Temporary download cleanup enumeration failed {ErrorType}",
                exception.GetType().Name);
        }
        return new(deleted, bytes, skipped);
    }

    private async Task ExecuteManagedAsync(
        DownloadTask task,
        string? targetDirectory,
        DownloadPreferences preferences,
        bool announce,
        CancellationToken cancellationToken,
        string? replaceAudioPath = null,
        string? replaceLyricPath = null)
    {
        var activityId = $"download:{task.Id}";
        if (announce)
            SafeAnnounce(new($"已开始下载：{task.DisplayName}", AnnouncementKind.Busy, activityId));

        var operation = new DownloadOperation();
        _operations[task.Id] = operation;
        try
        {
            using var registration = cancellationToken.Register(operation.Cancel);
            await _backgroundWork.RunLatestAsync(DownloadSlot(task), async context =>
            {
                using var contextRegistration = context.CancellationToken.Register(operation.Cancel);
                var acquired = false;
                try
                {
                    // Singles, albums, batch lists and retries share one process-wide gate, so an
                    // album started after a single never issues concurrent anti-ban-sensitive requests.
                    await _downloadGate.WaitAsync(context.CancellationToken);
                    acquired = true;
                    await RunTaskAsync(
                        task,
                        targetDirectory,
                        preferences,
                        announce,
                        activityId,
                        operation,
                        context.CancellationToken);
                }
                catch (OperationCanceledException) when (
                    context.CancellationToken.IsCancellationRequested
                    && task.Status == DownloadTaskStatus.Pending)
                {
                    task.Status = DownloadTaskStatus.Cancelled;
                    task.ErrorMessage = "下载已取消";
                }
                finally
                {
                    if (acquired) _downloadGate.Release();
                }
            });
        }
        finally
        {
            _operations.TryRemove(task.Id, out _);
            operation.Dispose();
        }
    }

    private async Task RunTaskAsync(
        DownloadTask task,
        string? requestedDirectory,
        DownloadPreferences preferences,
        bool announce,
        string activityId,
        DownloadOperation operation,
        CancellationToken cancellationToken,
        string? replaceAudioPath = null,
        string? replaceLyricPath = null)
    {
        string? audioTemporary = null;
        string? lyricTemporary = null;
        string? committedReplacementAudio = null;
        string? committedReplacementLyric = null;
        try
        {
            task.Status = DownloadTaskStatus.Downloading;
            var directory = ResolveTargetDirectory(requestedDirectory);
            var lastAnnouncedProgress = 0;
            string extension;
            if (string.Equals(task.Music.Source, "Local", StringComparison.OrdinalIgnoreCase))
            {
                var sourcePath = task.Music.PlayUrl;
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    throw new FileNotFoundException("本地文件不存在");
                extension = NormalizeAudioExtension(Path.GetExtension(sourcePath));
                audioTemporary = CreateTemporaryPath(directory);
                await CopyFileAsync(sourcePath, audioTemporary, cancellationToken);
                task.DownloadedBytes = task.TotalBytes = new FileInfo(audioTemporary).Length;
                task.Progress = 100;
                task.ActualQuality = extension.TrimStart('.');
            }
            else
            {
                var download = await DownloadRemoteAsync(
                    task,
                    directory,
                    preferences,
                    announce ? progress =>
                    {
                        var bucket = FrameworkCompatibility.Clamp(progress / 25 * 25, 0, 75);
                        if (bucket <= 0 || lastAnnouncedProgress >= bucket) return;
                        lastAnnouncedProgress = bucket;
                        SafeAnnounce(new(
                            $"下载进度：{task.Music.Title} {bucket}%",
                            AnnouncementKind.Progress,
                            activityId,
                            AnnouncementSpeech.None));
                    } : null,
                    operation,
                    cancellationToken);
                audioTemporary = download.Path;
                extension = download.Actual.Extension;
                task.ActualQuality = download.Actual.QualityToken ?? download.Actual.Label;
                task.Quality = download.Quality;
            }

            var name = BuildNamePart(task.Music.Artist, task.Music.Title, preferences.NameOrder);
            if (preferences.IncludeQualityTag && !string.Equals(task.Music.Source, "Local", StringComparison.OrdinalIgnoreCase))
                name += $" [{task.ActualQuality}]";
            name = SanitizeFileName(name);

            if (preferences.DownloadLyric
                && !string.Equals(task.Music.Source, "Local", StringComparison.OrdinalIgnoreCase))
            {
                lyricTemporary = await TryWriteLyricTemporaryAsync(
                    task.Music,
                    directory,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string finalPath;
            string? finalLyricPath = null;
            if (replaceAudioPath is null)
            {
                finalPath = CommitUnique(audioTemporary, directory, name, extension);
                audioTemporary = null;
                committedReplacementAudio = finalPath;
                task.SavePath = finalPath;
            }
            else
            {
                finalPath = CommitUnique(audioTemporary, directory, name, extension);
                audioTemporary = null;
                if (lyricTemporary is not null)
                {
                    finalLyricPath = CommitUnique(lyricTemporary, directory, name, ".lrc");
                    lyricTemporary = null;
                    committedReplacementLyric = finalLyricPath;
                }

                PromoteReplacement(finalPath, finalLyricPath, replaceAudioPath, replaceLyricPath);
                finalPath = replaceAudioPath;
                finalLyricPath = File.Exists(replaceLyricPath) ? replaceLyricPath : null;
                task.SavePath = finalPath;
                task.LyricPath = finalLyricPath;
            }

            if (lyricTemporary is not null)
            {
                var lyricPath = Path.ChangeExtension(finalPath, ".lrc");
                try
                {
                    directory = EnsureSafeDirectory(directory);
                    EnsureContained(directory, lyricPath);
                    File.Move(lyricTemporary, lyricPath);
                    lyricTemporary = null;
                    task.LyricPath = lyricPath;
                }
                catch (Exception exception)
                {
                    // Existing lyrics are user content and are never overwritten.
                    _logger.LogWarning(
                        "Lyric commit skipped ({TaskId}): {ErrorType}",
                        task.Id,
                        exception.GetType().Name);
                }
            }

            task.Progress = 100;
            task.Status = DownloadTaskStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            task.Status = DownloadTaskStatus.Cancelled;
            task.ErrorMessage = "下载已取消";
            DeleteTemporaryFile(committedReplacementAudio, task.Id);
            DeleteTemporaryFile(committedReplacementLyric, task.Id);
            if (!string.IsNullOrWhiteSpace(replaceAudioPath))
            {
                task.SavePath = replaceAudioPath;
                task.LyricPath = File.Exists(replaceLyricPath) ? replaceLyricPath : null;
            }
        }
        catch (DownloadSafetyException exception)
        {
            task.Status = DownloadTaskStatus.Failed;
            DeleteTemporaryFile(committedReplacementAudio, task.Id);
            DeleteTemporaryFile(committedReplacementLyric, task.Id);
            task.ErrorMessage = exception.Message;
            _logger.LogWarning("Download target was refused ({TaskId})", task.Id);
        }
        catch (FileNotFoundException exception)
        {
            task.Status = DownloadTaskStatus.Failed;
            task.ErrorMessage = exception.Message;
        }
        catch (QualityUnavailableException exception)
        {
            task.Status = DownloadTaskStatus.Failed;
            task.ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            task.Status = DownloadTaskStatus.Failed;
            DeleteTemporaryFile(committedReplacementAudio, task.Id);
            DeleteTemporaryFile(committedReplacementLyric, task.Id);
            if (!string.IsNullOrWhiteSpace(replaceAudioPath))
            {
                task.SavePath = replaceAudioPath;
                task.LyricPath = File.Exists(replaceLyricPath) ? replaceLyricPath : null;
            }
            task.ErrorMessage = "下载失败：" + exception.GetType().Name + "：" + exception.Message;
            _logger.LogError(
                "Download failed ({TaskId}): {ErrorType}",
                task.Id,
                exception.GetType().Name);
        }
        finally
        {
            DeleteTemporaryFile(audioTemporary, task.Id);
            DeleteTemporaryFile(lyricTemporary, task.Id);
            if (announce) AnnounceTerminal(task, activityId);
        }
    }

    private async Task<RemoteDownload> DownloadRemoteAsync(
        DownloadTask task,
        string directory,
        DownloadPreferences preferences,
        Action<int>? progress,
        DownloadOperation operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var declared = await _source.GetQualitiesAsync(task.Music, cancellationToken);
        var quality = QualityCatalog.Normalize(task.Quality);
        if (!declared.Contains(quality, StringComparer.OrdinalIgnoreCase))
        {
            throw new QualityUnavailableException(
                "音源未声明所选音质：" + QualityCatalog.Label(quality));
        }

        string? url;
        using (var resolutionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            resolutionTimeout.CancelAfter(_resolutionTimeout);
            try
            {
                url = await _source.GetUrlAsync(task.Music, quality, resolutionTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("获取下载链接超时。");
            }
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new QualityUnavailableException(
                "音源未提供 " + QualityCatalog.Label(quality) + " 下载链接");
        }

        var download = await DownloadHttpToTemporaryFileAsync(
            url,
            directory,
            preferences,
            cancellationToken,
            (read, total) =>
            {
                task.DownloadedBytes = read;
                task.TotalBytes = total;
                if (total <= 0) return;
                task.Progress = Math.Min(99, read * 100d / total);
                progress?.Invoke((int)task.Progress);
            });

        // The source declaration is the quality contract. Byte sniffing cannot prove transcoding
        // and previously discarded useful payloads after a complete transfer.
        var actual = new AudioFileInfo(
            QualityCatalog.Label(quality),
            download.Extension,
            quality);
        return new(download.Path, quality, actual);
    }

    private async Task<HttpDownload> DownloadHttpToTemporaryFileAsync(
        string url,
        string directory,
        DownloadPreferences preferences,
        CancellationToken cancellationToken,
        Action<long, long>? progress,
        DownloadOperation? operation = null)
    {
        HttpResponseMessage response;
        using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            headerTimeout.CancelAfter(_responseHeaderTimeout);
            try
            {
                response = await _httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    headerTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("连接下载地址超时。");
            }
        }
        response.EnsureSuccessStatusCode();

        var temporary = CreateTemporaryPath(directory);
        try
        {
            var total = response.Content.Headers.ContentLength ?? 0;
            using var input = await response.Content.ReadAsStreamAsync();
            using var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            var clock = Stopwatch.StartNew();
            long read = 0;
            int count;
            while (true)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(_readStallTimeout);
                try
                {
                    count = await input.ReadAsync(buffer, 0, buffer.Length, readTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("下载网络传输超时。");
                }

                if (count == 0) break;

                await output.WriteAsync(buffer, 0, count, cancellationToken);
                read += count;
                if (operation is not null)
                    await operation.WaitIfPausedAsync(cancellationToken);
                await _throttle.PaceTransferAsync(preferences, clock.Elapsed, read, cancellationToken);
                progress?.Invoke(read, total);
            }

            // On .NET Framework, HttpClient can turn a canceled content-stream read into a zero-byte
            // EOF. A request that was cancelled during transfer must surface as cancelled, not fall
            // through to audio sniffing and quality-fallback errors.
            cancellationToken.ThrowIfCancellationRequested();
            await output.FlushAsync(cancellationToken);
            return new(temporary, GetExtension(url, response.Content.Headers.ContentType));
        }
        catch
        {
            DeleteTemporaryFile(temporary, "http");
            throw;
        }
    }

    private async Task<string?> TryWriteLyricTemporaryAsync(
        MusicInfo music,
        string directory,
        CancellationToken cancellationToken)
    {
        try
        {
            LyricResult? result;
            using (var lyricTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                lyricTimeout.CancelAfter(_lyricTimeout);
                try
                {
                    result = await _source.GetLyricAsync(music, lyricTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Lyric download timed out ({Source}:{Id})",
                        music.Source,
                        music.Id);
                    return null;
                }
            }

            if (result?.Lyric is not { Lines.Count: > 0 } lyric) return null;
            var temporary = CreateTemporaryPath(directory);
            await Task.Run(() => File.WriteAllText(
                temporary,
                BuildLrc(lyric),
                new UTF8Encoding(false)), cancellationToken);
            return temporary;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Lyric download failed ({Source}:{Id}): {ErrorType}",
                music.Source,
                music.Id,
                exception.GetType().Name);
            return null;
        }
    }

    private string ResolveTargetDirectory(string? requested) =>
        EnsureSafeDirectory(string.IsNullOrWhiteSpace(requested)
            ? GetDefaultDownloadDirectory()
            : requested);

    private static string EnsureSafeDirectory(string path)
    {
        bool fullyQualified;
        try
        {
            fullyQualified = FrameworkCompatibility.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            throw new DownloadSafetyException("下载目录无效");
        }

        if (!fullyQualified)
            throw new DownloadSafetyException("下载目录必须是绝对路径");

        string full;
        try { full = FrameworkCompatibility.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DownloadSafetyException("下载目录无效");
        }

        var root = FrameworkCompatibility.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            throw new DownloadSafetyException("不能将磁盘根目录用作下载目录");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile)
            && SamePath(full, userProfile))
            throw new DownloadSafetyException("不能将用户根目录用作下载目录");

        foreach (var protectedDirectory in GetProtectedDirectories())
        {
            if (IsWithin(protectedDirectory, full))
                throw new DownloadSafetyException("不能将系统目录用作下载目录");
        }

        RejectReparsePoints(full);
        try { Directory.CreateDirectory(full); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DownloadSafetyException("无法创建下载目录");
        }
        RejectReparsePoints(full);
        return full;
    }

    private static IEnumerable<string> GetProtectedDirectories()
    {
        var values = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)
        };
        return values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void RejectReparsePoints(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new DownloadSafetyException("下载目录不能包含重解析点");
            current = current.Parent;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = FrameworkCompatibility.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            FrameworkCompatibility.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            FrameworkCompatibility.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static void EnsureContained(string directory, string path)
    {
        if (!IsWithin(directory, path) || SamePath(directory, path))
            throw new DownloadSafetyException("下载文件不能逃逸目标目录");
    }

    private static string CreateTemporaryPath(string directory)
    {
        var path = Path.Combine(directory, $".lxmusic-{Guid.NewGuid():N}.download");
        EnsureContained(directory, path);
        return path;
    }

    private static void PromoteReplacement(
        string newAudio,
        string? newLyric,
        string replaceAudio,
        string? replaceLyric)
    {
        var audioBackup = File.Exists(replaceAudio)
            ? Path.Combine(Path.GetDirectoryName(replaceAudio)!, ".lxmusic-backup-" + Guid.NewGuid().ToString("N") + Path.GetExtension(replaceAudio))
            : null;
        var lyricBackup = replaceLyric is not null && File.Exists(replaceLyric)
            ? Path.Combine(Path.GetDirectoryName(replaceLyric)!, ".lxmusic-backup-" + Guid.NewGuid().ToString("N") + ".lrc")
            : null;
        var rollbackSucceeded = false;
        try
        {
            if (audioBackup is not null) File.Move(replaceAudio, audioBackup);
            if (lyricBackup is not null) File.Move(replaceLyric!, lyricBackup);
            if (newLyric is not null) File.Move(newLyric, replaceLyric!);
            else if (File.Exists(replaceLyric)) File.Delete(replaceLyric);
            File.Move(newAudio, replaceAudio);
            rollbackSucceeded = true;
        }
        catch
        {
            try
            {
                if (audioBackup is not null)
                {
                    if (File.Exists(replaceAudio)) File.Delete(replaceAudio);
                    File.Move(audioBackup, replaceAudio);
                }
                if (lyricBackup is not null)
                {
                    if (File.Exists(replaceLyric)) File.Delete(replaceLyric);
                    File.Move(lyricBackup, replaceLyric!);
                }
                rollbackSucceeded = true;
            }
            catch (Exception rollbackException)
            {
                // Preserve any remaining backup so the prior version remains recoverable.
                Debug.WriteLine("Replacement rollback failed: " + rollbackException.GetType().Name);
            }
            throw;
        }
        finally
        {
            if (File.Exists(newAudio)) File.Delete(newAudio);
            if (newLyric is not null && File.Exists(newLyric)) File.Delete(newLyric);
            if (rollbackSucceeded)
            {
                if (audioBackup is not null && File.Exists(audioBackup)) File.Delete(audioBackup);
                if (lyricBackup is not null && File.Exists(lyricBackup)) File.Delete(lyricBackup);
            }
        }
    }
    private static string CommitUnique(string temporary, string directory, string name, string extension)
    {
        directory = EnsureSafeDirectory(directory);
        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var fileName = suffix == 0
                ? $"{name}{extension}"
                : $"{name}({suffix}){extension}";
            var candidate = Path.Combine(directory, fileName);
            EnsureContained(directory, candidate);
            try
            {
                File.Move(temporary, candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Another file claimed this suffix; continue to the next bounded candidate.
            }
        }
        throw new IOException("Too many conflicting download files.");
    }

    private static async Task CopyFileAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new FileStream(
            target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 81920, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static string SanitizeFileName(string? value)
    {
        var input = value?.Trim() ?? string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(Math.Min(input.Length, 160));
        foreach (var character in input)
        {
            if (builder.Length >= 160) break;
            builder.Append(character < ' ' || invalid.Contains(character) ? '_' : character);
        }
        var result = builder.ToString().Trim().TrimEnd(' ', '.');
        while (result.Contains("..", StringComparison.Ordinal))
            result = result.Replace("..", "_", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(result) || result is "." or "..") result = "未命名";
        var stem = result.Split('.')[0];
        if (ReservedFileNames.Contains(stem)) result = $"_{result}";
        return result;
    }

    private static string BuildNamePart(string? artist, string? title, DownloadNameOrder order)
    {
        var cleanArtist = artist?.Trim() ?? string.Empty;
        var cleanTitle = title?.Trim() ?? string.Empty;
        if (cleanArtist.Length == 0) return cleanTitle;
        if (cleanTitle.Length == 0) return cleanArtist;
        return order == DownloadNameOrder.TitleArtist
            ? $"{cleanTitle} - {cleanArtist}"
            : $"{cleanArtist} - {cleanTitle}";
    }

    private static byte[] GetBytes(byte[] source, int start, int end)
    {
        var result = new byte[end - start];
        Buffer.BlockCopy(source, start, result, 0, result.Length);
        return result;
    }

    private static string GetExtension(string url, MediaTypeHeaderValue? contentType)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (KnownAudioExtensions.Contains(extension)) return extension.ToLowerInvariant();
        }

        var mediaType = contentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
        if (mediaType.Contains("flac")) return ".flac";
        if (mediaType.Contains("mpeg") || mediaType.Contains("mp3")) return ".mp3";
        if (mediaType.Contains("mp4") || mediaType.Contains("m4a")) return ".m4a";
        if (mediaType.Contains("aac")) return ".aac";
        if (mediaType.Contains("wav") || mediaType.Contains("wave")) return ".wav";
        if (mediaType.Contains("opus")) return ".opus";
        if (mediaType.Contains("ogg")) return ".ogg";
        if (mediaType.Contains("ape")) return ".ape";
        if (mediaType.Contains("wma")) return ".wma";
        return ".mp3";
    }

    private static string NormalizeAudioExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && KnownAudioExtensions.Contains(extension)
            ? extension.ToLowerInvariant()
            : ".mp3";

    private static string BuildLrc(Lyric lyric)
    {
        var builder = new StringBuilder();
        foreach (var line in lyric.Lines.OrderBy(line => line.Time))
        {
            builder.Append('[')
                .Append(line.Time.Minutes.ToString("00"))
                .Append(':')
                .Append(line.Time.Seconds.ToString("00"))
                .Append('.')
                .Append((line.Time.Milliseconds / 10).ToString("00"))
                .Append(']')
                .Append(line.Text ?? string.Empty)
                .Append('\n');
        }
        return builder.ToString();
    }

    private void AnnounceTerminal(DownloadTask task, string activityId)
    {
        var announcement = task.Status switch
        {
            DownloadTaskStatus.Completed => new AccessibleAnnouncement(
                $"下载完成：{task.DisplayName}"
                + (string.IsNullOrEmpty(task.QualityFallback) ? "" : $"，{task.QualityFallback}")
                + (string.IsNullOrEmpty(task.LyricPath) ? "" : "，已同步下载歌词"),
                AnnouncementKind.Success,
                activityId),
            DownloadTaskStatus.Cancelled => new(
                $"下载已取消：{task.DisplayName}",
                AnnouncementKind.Error,
                activityId),
            _ => new(
                $"下载失败：{task.DisplayName}，{task.ErrorMessage}",
                AnnouncementKind.Error,
                activityId)
        };
        SafeAnnounce(announcement);
    }

    private void SafeAnnounce(AccessibleAnnouncement announcement)
    {
        try { _announcer.Announce(announcement); }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Download announcement failed: {ErrorType}",
                exception.GetType().Name);
        }
    }

    private void DeleteTemporaryFile(string? path, string taskId)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Download temporary file release failed ({TaskId}): {ErrorType}",
                taskId,
                exception.GetType().Name);
        }
    }

    private sealed class DownloadOperation : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<object?> _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _paused;
        private bool _disposed;

        public CancellationToken Token => _cancellation.Token;

        public void Cancel()
        {
            if (_disposed) return;
            _paused = false;
            _resume.TrySetResult(null);
            _cancellation.Cancel();
        }

        public bool Pause()
        {
            if (_disposed || _paused) return false;
            _paused = true;
            return true;
        }

        public bool Resume()
        {
            if (_disposed || !_paused) return false;
            _paused = false;
            _resume.TrySetResult(null);
            return true;
        }

        public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
        {
            if (!_paused) return;
            using var registration = cancellationToken.Register(() => _resume.TrySetCanceled(cancellationToken));
            await _resume.Task;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _resume.TrySetResult(null);
            _cancellation.Dispose();
        }
    }

    private static string DownloadSlot(DownloadTask task) => $"download.{task.Id}";

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var slots = _tasks.Select(DownloadSlot).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var slot in slots) _backgroundWork.Cancel(slot);
        foreach (var slot in slots) await _backgroundWork.WaitAsync(slot);
        string[] temporaryFiles;
        lock (_gate) temporaryFiles = _externalTemporaryFiles.ToArray();
        foreach (var file in temporaryFiles) DeleteExternalTemporaryFile(file);
        _downloadGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record DownloadPlan(
        string? TargetDirectory,
        DownloadPreferences Preferences,
        string RequestedQuality,
        string? ReplaceAudioPath = null,
        string? ReplaceLyricPath = null);

    private sealed record HttpDownload(string Path, string Extension);
    private sealed record RemoteDownload(string Path, string Quality, AudioFileInfo Actual);
    private sealed record AudioFileInfo(string Label, string Extension, string? QualityToken);

    private sealed class DownloadSafetyException(string message) : IOException(message);
private sealed class QualityUnavailableException(string message) : IOException(message);

    private void DeleteExternalTemporaryFile(string path)
    {
        try
        {
            var directory = EnsureSafeDirectory(Path.Combine(
                _temporaryRoot,
                "LXMusicAccessible.External"));
            EnsureContained(directory, path);
            File.Delete(path);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "External temporary file release failed: {ErrorType}",
                exception.GetType().Name);
        }
    }
}




