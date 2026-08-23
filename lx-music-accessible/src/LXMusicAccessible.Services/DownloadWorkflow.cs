using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Services;

public sealed record DownloadPreferences(
    bool DownloadLyric,
    DownloadNameOrder NameOrder,
    bool IncludeQualityTag);

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

public sealed record DownloadCacheCleanupResult(int DeletedFiles, long FreedBytes);

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
    void Cancel(DownloadTask task);
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
        string source,
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
        string source,
        CancellationToken cancellationToken) =>
        await musicSources.GetQualitiesForSourceAsync(source).WaitAsync(cancellationToken);

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
    private readonly IAccessibleAnnouncer _announcer;
    private readonly IProcessLauncher _processLauncher;
    private readonly string _temporaryRoot;
    private readonly Dictionary<string, DownloadPlan> _plans = new(StringComparer.Ordinal);
    private readonly HashSet<string> _externalTemporaryFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly ObservableCollection<DownloadTask> _tasks = [];
    private DownloadPreferences _preferences = new(true, DownloadNameOrder.ArtistTitle, true);
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
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate) _preferences = preferences;
    }

    public async Task<DownloadTask> DownloadAsync(
        DownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Music);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var task = new DownloadTask
        {
            Music = request.Music,
            Quality = string.IsNullOrWhiteSpace(request.Quality) ? "128k" : request.Quality
        };
        DownloadPreferences preferences;
        lock (_gate)
        {
            preferences = _preferences;
            _plans[task.Id] = new DownloadPlan(request.TargetDirectory, preferences);
        }
        _tasks.Add(task);
        await ExecuteManagedAsync(task, request.TargetDirectory, preferences, announce: true, cancellationToken);
        return task;
    }

    public async Task<DownloadListResult> DownloadListAsync(
        DownloadListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Songs);
        ObjectDisposedException.ThrowIf(_disposed, this);

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
            try
            {
                targetDirectory = ResolveTargetDirectory(request.TargetDirectory);
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
                lock (_gate) _plans[task.Id] = new DownloadPlan(targetDirectory, preferences);
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

    public async Task<bool> RetryAsync(
        DownloadTask task,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObjectDisposedException.ThrowIf(_disposed, this);
        DownloadPlan plan;
        lock (_gate)
        {
            if (!_plans.TryGetValue(task.Id, out plan!)) return false;
            if (!string.IsNullOrWhiteSpace(targetDirectory))
                plan = plan with { TargetDirectory = targetDirectory };
            _plans[task.Id] = plan;
        }

        task.Status = DownloadTaskStatus.Pending;
        task.Progress = 0;
        task.DownloadedBytes = 0;
        task.TotalBytes = 0;
        task.SavePath = null;
        task.LyricPath = null;
        task.ErrorMessage = null;
        task.ActualQuality = null;
        task.IsBatch = false;
        await ExecuteManagedAsync(task, plan.TargetDirectory, plan.Preferences, announce: true, cancellationToken);
        return true;
    }

    public void Cancel(DownloadTask task)
    {
        if (task is null) return;
        _backgroundWork.Cancel(DownloadSlot(task));
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
        try { return ResolveTargetDirectory(requested); }
        catch (DownloadSafetyException) when (!string.IsNullOrWhiteSpace(requested))
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
        ArgumentNullException.ThrowIfNull(music);
        ObjectDisposedException.ThrowIf(_disposed, this);
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
            var url = await _source.GetUrlAsync(music, quality, context.CancellationToken);
            if (string.IsNullOrWhiteSpace(url)) return;

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
                        context.CancellationToken,
                        progress: null);
                    temporary = download.Path;
                    var actual = ResolveActualFormat(download.Path, download.Extension);
                    var final = Path.Combine(directory, $"lxmusic_ext_{Guid.NewGuid():N}{actual.Extension}");
                    EnsureContained(directory, final);
                    File.Move(download.Path, final, overwrite: false);
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
        ArgumentNullException.ThrowIfNull(request);
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
        long bytes = 0;
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
            catch (Exception exception)
            {
                _logger.LogDebug(
                    "Temporary download cleanup skipped {ErrorType}",
                    exception.GetType().Name);
            }
        }
        return new(deleted, bytes);
    }

    private async Task ExecuteManagedAsync(
        DownloadTask task,
        string? targetDirectory,
        DownloadPreferences preferences,
        bool announce,
        CancellationToken cancellationToken)
    {
        var activityId = $"download:{task.Id}";
        if (announce)
            SafeAnnounce(new($"已开始下载：{task.DisplayName}", AnnouncementKind.Busy, activityId));

        await _backgroundWork.RunLatestAsync(DownloadSlot(task), async context =>
        {
            using var registration = cancellationToken.Register(() =>
                _backgroundWork.Cancel(DownloadSlot(task)));
            await RunTaskAsync(task, targetDirectory, preferences, announce, activityId, context.CancellationToken);
        });
    }

    private async Task RunTaskAsync(
        DownloadTask task,
        string? requestedDirectory,
        DownloadPreferences preferences,
        bool announce,
        string activityId,
        CancellationToken cancellationToken)
    {
        string? audioTemporary = null;
        string? lyricTemporary = null;
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
                    announce ? progress =>
                    {
                        var bucket = Math.Clamp(progress / 25 * 25, 0, 75);
                        if (bucket <= 0 || lastAnnouncedProgress >= bucket) return;
                        lastAnnouncedProgress = bucket;
                        SafeAnnounce(new(
                            $"下载进度：{task.Music.Title} {bucket}%",
                            AnnouncementKind.Progress,
                            activityId,
                            AnnouncementSpeech.None));
                    } : null,
                    cancellationToken);
                audioTemporary = download.Path;
                var actual = ResolveActualFormat(audioTemporary, download.Extension);
                extension = actual.Extension;
                task.ActualQuality = actual.Label;
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
            var finalPath = CommitUnique(audioTemporary, directory, name, extension);
            audioTemporary = null;
            task.SavePath = finalPath;

            if (lyricTemporary is not null)
            {
                var lyricPath = Path.ChangeExtension(finalPath, ".lrc");
                try
                {
                    directory = EnsureSafeDirectory(directory);
                    EnsureContained(directory, lyricPath);
                    File.Move(lyricTemporary, lyricPath, overwrite: false);
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
        }
        catch (DownloadSafetyException exception)
        {
            task.Status = DownloadTaskStatus.Failed;
            task.ErrorMessage = exception.Message;
            _logger.LogWarning("Download target was refused ({TaskId})", task.Id);
        }
        catch (FileNotFoundException exception)
        {
            task.Status = DownloadTaskStatus.Failed;
            task.ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            task.Status = DownloadTaskStatus.Failed;
            task.ErrorMessage = "下载失败，请查看日志";
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
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        var qualities = await _source.GetQualitiesAsync(task.Music.Source, cancellationToken);
        var ordered = BuildQualityOrder(task.Quality, qualities);
        foreach (var quality in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var url = await _source.GetUrlAsync(task.Music, quality, cancellationToken);
                if (string.IsNullOrWhiteSpace(url)) continue;
                var download = await DownloadHttpToTemporaryFileAsync(
                    url,
                    directory,
                    cancellationToken,
                    (read, total) =>
                    {
                        task.DownloadedBytes = read;
                        task.TotalBytes = total;
                        if (total <= 0) return;
                        task.Progress = Math.Min(99, read * 100d / total);
                        progress?.Invoke((int)task.Progress);
                    });
                return new(download.Path, download.Extension, quality);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Download quality failed ({TaskId}, {Quality}): {ErrorType}",
                    task.Id,
                    quality,
                    exception.GetType().Name);
            }
        }
        throw new HttpRequestException("No download quality succeeded.");
    }

    private async Task<HttpDownload> DownloadHttpToTemporaryFileAsync(
        string url,
        string directory,
        CancellationToken cancellationToken,
        Action<long, long>? progress)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        response.EnsureSuccessStatusCode();

        var temporary = CreateTemporaryPath(directory);
        try
        {
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            long read = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                read += count;
                progress?.Invoke(read, total);
            }
            await output.FlushAsync(timeout.Token);
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
            var result = await _source.GetLyricAsync(music, cancellationToken);
            if (result?.Lyric is not { Lines.Count: > 0 } lyric) return null;
            var temporary = CreateTemporaryPath(directory);
            await File.WriteAllTextAsync(
                temporary,
                BuildLrc(lyric),
                new UTF8Encoding(false),
                cancellationToken);
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
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DownloadSafetyException("下载目录无效");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty);
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
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
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
                File.Move(temporary, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
            }
        }
        throw new IOException("Too many conflicting download files.");
    }

    private static async Task CopyFileAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken);
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

    private static IReadOnlyList<string> BuildQualityOrder(
        string requested,
        IReadOnlyList<string> available)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(requested)) result.Add(requested);
        foreach (var quality in available.OrderByDescending(QualityRank))
        {
            if (!result.Contains(quality, StringComparer.OrdinalIgnoreCase)) result.Add(quality);
        }
        return result;
    }

    private static int QualityRank(string quality) =>
        quality.ToLowerInvariant().Replace(" ", string.Empty) switch
        {
            "flac24bit" => 6,
            "flac" or "无损" => 5,
            "320k" => 4,
            "192k" => 3,
            "128k" => 2,
            _ => 1
        };

    private static (string Label, string Extension) ResolveActualFormat(
        string filePath,
        string fallbackExtension)
    {
        var format = SniffAudioFormat(filePath);
        return format switch
        {
            "flac" => ("flac", ".flac"),
            "mp3" => ("mp3", ".mp3"),
            "m4a" => ("m4a", ".m4a"),
            "aac" => ("aac", ".aac"),
            "ogg" => ("ogg", ".ogg"),
            "wav" => ("wav", ".wav"),
            "ape" => ("ape", ".ape"),
            "wma" => ("wma", ".wma"),
            _ => (fallbackExtension.TrimStart('.'), fallbackExtension)
        };
    }

    private static string? SniffAudioFormat(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        Span<byte> head = stackalloc byte[16];
        var read = stream.Read(head);
        if (read < 4) return null;
        if (head[..4].SequenceEqual("fLaC"u8)) return "flac";
        if (head[..4].SequenceEqual("OggS"u8)) return "ogg";
        if (head[..4].SequenceEqual("RIFF"u8) && read >= 12 && head[8..12].SequenceEqual("WAVE"u8)) return "wav";
        if (head[..4].SequenceEqual("MAC "u8)) return "ape";
        if (read >= 8 && head[4..8].SequenceEqual("ftyp"u8)) return "m4a";
        if (head[..3].SequenceEqual("ID3"u8)) return "mp3";
        if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && (head[1] & 0x06) != 0) return "mp3";
        if (read >= 8 && head[0] == 0x30 && head[1] == 0x26
            && head[4] == 0xB2 && head[5] == 0x75 && head[6] == 0x8E && head[7] == 0x66) return "wma";
        return null;
    }

    private static string GetExtension(string url, MediaTypeHeaderValue? contentType)
    {
        try
        {
            var extension = Path.GetExtension(new Uri(url).AbsolutePath);
            if (KnownAudioExtensions.Contains(extension)) return extension.ToLowerInvariant();
        }
        catch (UriFormatException)
        {
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
                string.IsNullOrEmpty(task.LyricPath)
                    ? $"下载完成：{task.DisplayName}"
                    : $"下载完成：{task.DisplayName}，已同步下载歌词",
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
        GC.SuppressFinalize(this);
    }

    private sealed record DownloadPlan(
        string? TargetDirectory,
        DownloadPreferences Preferences);

    private sealed record HttpDownload(string Path, string Extension);
    private sealed record RemoteDownload(string Path, string Extension, string Quality);

    private sealed class DownloadSafetyException(string message) : IOException(message);

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
