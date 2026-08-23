using Microsoft.Extensions.Logging;
using LXMusicAccessible.Models;
using System.Collections.ObjectModel;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace LXMusicAccessible.Services;

/// <summary>
/// 下载服务接口
/// </summary>
public interface IDownloadService
{
    /// <summary>下载任务集合（供 UI 绑定）</summary>
    ObservableCollection<DownloadTask> Tasks { get; }

    /// <summary>下载完成事件（成功/失败/取消都触发）</summary>
    event EventHandler<DownloadTask>? TaskCompleted;

    /// <summary>开始下载歌曲（isBatch=true 表示批量任务：串行 + 每首最小间隔，避免并发被限流）</summary>
    Task<DownloadTask> DownloadAsync(MusicInfo music, string quality, string? downloadDir = null, bool isBatch = false);

    /// <summary>重试下载任务</summary>
    Task<bool> RetryAsync(DownloadTask task, string? downloadDir = null);

    /// <summary>取消下载</summary>
    void Cancel(DownloadTask task);

    /// <summary>清理已完成/失败/取消的任务</summary>
    void ClearCompleted();

    /// <summary>获取默认下载目录（音乐库\落雪音乐下载，不可用时自动回退）</summary>
    string GetDefaultDownloadDir();

    /// <summary>下载时是否同时下载歌词（.lrc 文件）</summary>
    bool DownloadLyric { get; set; }

    /// <summary>下载文件命名顺序（ArtistTitle=艺术家-歌名，TitleArtist=歌名-艺术家）</summary>
    DownloadNameOrder NameOrder { get; set; }

    /// <summary>下载文件名是否包含音质标签（如 [mp3]）</summary>
    bool IncludeQualityTag { get; set; }
}

/// <summary>
/// 下载服务 - 负责歌曲下载到本地目录
/// </summary>
public class DownloadService : IDownloadService, IAsyncDisposable
{
    private readonly ILogger<DownloadService> _logger;
    private readonly IMusicSourceService _musicSourceService;
    private readonly ILyricService _lyricService;
    private readonly HttpClient _httpClient;
    private readonly BackgroundWorkHost _backgroundWork;
    private readonly bool _ownsHttpClient;
    private readonly bool _ownsBackgroundWork;

    private static readonly HashSet<string> KnownAudioExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".aac", ".ape", ".wma", ".opus"
    };

    // 批量下载保护：批量任务串行执行，且每首之间至少间隔若干秒，避免并发请求被服务商限流
    private static readonly SemaphoreSlim _batchGate = new(1, 1);
    private static DateTime _lastBatchFinish = DateTime.MinValue;
    private static readonly object _batchLock = new();
    private const int BatchDownloadIntervalSeconds = 3;

    public ObservableCollection<DownloadTask> Tasks { get; } = new();
    public event EventHandler<DownloadTask>? TaskCompleted;

    public DownloadService(
        ILogger<DownloadService> logger,
        IMusicSourceService musicSourceService,
        ILyricService lyricService,
        BackgroundWorkHost? backgroundWork = null,
        HttpClient? httpClient = null)
    {
        _logger = logger;
        _musicSourceService = musicSourceService;
        _lyricService = lyricService;
        _backgroundWork = backgroundWork
            ?? new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsBackgroundWork = backgroundWork is null;
        _ownsHttpClient = httpClient is null;
    }

    /// <summary>下载时是否同时下载歌词（.lrc 文件）</summary>
    public bool DownloadLyric { get; set; } = true;

    /// <summary>下载文件命名顺序（ArtistTitle=艺术家-歌名，TitleArtist=歌名-艺术家）</summary>
    public DownloadNameOrder NameOrder { get; set; } = DownloadNameOrder.ArtistTitle;

    /// <summary>下载文件名是否包含音质标签（如 [mp3]）</summary>
    public bool IncludeQualityTag { get; set; } = true;

    /// <summary>单次下载偏好快照（发起时捕获，避免中途改设置影响本次任务）</summary>
    private readonly record struct DownloadPrefs(bool Lyric, DownloadNameOrder Order, bool IncludeQuality);

    public Task<DownloadTask> DownloadAsync(MusicInfo music, string quality, string? downloadDir = null, bool isBatch = false)
    {
        var task = new DownloadTask
        {
            Music = music,
            Quality = string.IsNullOrEmpty(quality) ? "128k" : quality,
            IsBatch = isBatch
        };
        Tasks.Add(task);
        var dir = string.IsNullOrEmpty(downloadDir) ? GetDefaultDownloadDir() : downloadDir;
        var prefs = new DownloadPrefs(DownloadLyric, NameOrder, IncludeQualityTag);
        _backgroundWork.StartLatest(
            DownloadSlot(task),
            context => RunTaskAsync(task, dir, prefs, context.CancellationToken));
        return Task.FromResult(task);
    }

    public async Task<bool> RetryAsync(DownloadTask task, string? downloadDir = null)
    {
        if (task == null) return false;
        task.Status = DownloadTaskStatus.Pending;
        task.Progress = 0;
        task.DownloadedBytes = 0;
        task.TotalBytes = 0;
        task.SavePath = null;
        task.ErrorMessage = null;
        var dir = string.IsNullOrEmpty(downloadDir) ? GetDefaultDownloadDir() : downloadDir;
        var prefs = new DownloadPrefs(DownloadLyric, NameOrder, IncludeQualityTag);
        await _backgroundWork.RunLatestAsync(
            DownloadSlot(task),
            context => RunTaskAsync(task, dir, prefs, context.CancellationToken));
        return true;
    }

    public void Cancel(DownloadTask task)
    {
        if (task == null) return;
        _backgroundWork.Cancel(DownloadSlot(task));
    }

    public void ClearCompleted()
    {
        var done = Tasks.Where(t => t.Status is DownloadTaskStatus.Completed or DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled).ToList();
        foreach (var t in done)
        {
            Tasks.Remove(t);
        }
    }

    /// <summary>默认下载目录：音乐库\落雪音乐下载；音乐库/文档目录不可用时回退到 %LocalAppData%\lxmusic\downloads。</summary>
    public string GetDefaultDownloadDir()
    {
        foreach (var baseDir in GetCandidateMusicRoots())
        {
            var candidate = Path.Combine(baseDir, "落雪音乐下载");
            if (IsSafeDirectory(candidate)) return candidate;
        }
        return GetFallbackDownloadDir();
    }

    private static IEnumerable<string> GetCandidateMusicRoots()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrWhiteSpace(music)) yield return music;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(docs)) yield return docs;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(appData)) yield return appData;
    }

    private static string GetFallbackDownloadDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) appData = Path.GetTempPath();
        return Path.Combine(appData, "lxmusic", "downloads");
    }

    /// <summary>判断是否为合法的绝对目录路径（含根目录、不含非法字符，?/* 一并拒绝）</summary>
    private static bool IsSafeDirectory(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            var root = Path.GetPathRoot(dir);
            if (string.IsNullOrEmpty(root)) return false; // 非绝对路径
            if (dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            if (dir.IndexOfAny(new[] { '?', '*' }) >= 0) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>创建下载目录；指定目录不可用（非法/权限/磁盘异常）时回退到默认安全目录，返回实际使用目录。</summary>
    private string EnsureDownloadDirectory(string requested)
    {
        try
        {
            Directory.CreateDirectory(requested);
            return requested;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "下载目录创建失败，回退到默认目录: {Requested}", requested);
            var fallback = GetFallbackDownloadDir();
            try
            {
                Directory.CreateDirectory(fallback);
                return fallback;
            }
            catch (Exception ex2)
            {
                _logger.LogError(ex2, "回退下载目录创建失败: {Fallback}", fallback);
                throw;
            }
        }
    }

    // ================================================================
    //  下载核心
    // ================================================================

    private async Task RunTaskAsync(DownloadTask task, string saveDir, DownloadPrefs prefs, CancellationToken ct)
    {
        bool batchAcquired = false;
        if (task.IsBatch)
        {
            // 批量下载保护：串行执行 + 每首最小间隔（每隔几秒下载一个），避免并发被限流
            try { await _batchGate.WaitAsync(ct); batchAcquired = true; }
            catch (OperationCanceledException) { throw; }
            try
            {
                DateTime last;
                lock (_batchLock) last = _lastBatchFinish;
                var waitMs = (int)(BatchDownloadIntervalSeconds * 1000 - (DateTime.Now - last).TotalMilliseconds);
                if (waitMs > 0) await Task.Delay(waitMs, ct);
            }
            catch (OperationCanceledException) { throw; }
        }
        try
        {
            task.Status = DownloadTaskStatus.Downloading;
            if (string.Equals(task.Music.Source, "Local", StringComparison.OrdinalIgnoreCase))
            {
                CopyLocalFile(task, saveDir, prefs);
            }
            else
            {
                await DownloadWithFallbackAsync(task, saveDir, prefs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = DownloadTaskStatus.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "下载失败 ({TaskId}): {ErrorType}",
                task.Id,
                ex.GetType().Name);
            task.Status = DownloadTaskStatus.Failed;
            task.ErrorMessage = "下载失败，请查看日志";
        }
        finally
        {
            if (batchAcquired)
            {
                lock (_batchLock) _lastBatchFinish = DateTime.Now;
                _batchGate.Release();
            }
            try { TaskCompleted?.Invoke(this, task); }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Download completion notification failed ({TaskId}): {ErrorType}",
                    task.Id,
                    exception.GetType().Name);
            }
        }
    }
    private void CopyLocalFile(DownloadTask task, string saveDir, DownloadPrefs prefs)
    {
        saveDir = EnsureDownloadDirectory(saveDir);
        var src = task.Music.PlayUrl;
        if (string.IsNullOrEmpty(src) || !File.Exists(src))
        {
            task.Status = DownloadTaskStatus.Failed;
            task.ErrorMessage = "本地文件不存在";
            return;
        }
        var ext = Path.GetExtension(src);
        if (string.IsNullOrEmpty(ext)) ext = ".mp3";
        var namePart = BuildNamePart(task.Music.Artist, task.Music.Title, prefs.Order);
        var finalPath = GetUniquePath(Path.Combine(saveDir, SanitizeFileName(namePart) + ext));
        File.Copy(src, finalPath, false);
        task.SavePath = finalPath;
        task.Progress = 100;
        task.Status = DownloadTaskStatus.Completed;
    }

    /// <summary>
    /// 带音质回退的下载：先尝试任务请求的音质；下载不了（取不到 URL 或下载失败）时，
    /// 按“最高可用音质优先”的顺序依次回退，直到成功或全部失败。
    /// </summary>
    private async Task DownloadWithFallbackAsync(DownloadTask task, string saveDir, DownloadPrefs prefs, CancellationToken ct)
    {
        var requested = task.Quality;
        var qualities = await _musicSourceService
            .GetQualitiesForSourceAsync(task.Music.Source)
            .WaitAsync(ct);
        var ordered = BuildQualityOrder(requested, qualities);
        string? lastError = null;
        foreach (var q in ordered)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var url = await _musicSourceService.GetPlayUrlAsync(
                    task.Music,
                    task.Music.Source,
                    q,
                    ct);
                if (string.IsNullOrEmpty(url)) continue;
                await DownloadFromUrlAsync(task, url, saveDir, prefs, ct);
                if (task.Status == DownloadTaskStatus.Completed)
                {
                    task.Quality = q;
                    if (!string.Equals(q, requested, StringComparison.OrdinalIgnoreCase))
                        _logger.LogInformation("下载音质回退: {Requested} → {Actual}", requested, q);
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = "下载请求失败";
                _logger.LogWarning(
                    "音质 {Quality} 下载失败，尝试下一音质 ({TaskId}): {ErrorType}",
                    q,
                    task.Id,
                    ex.GetType().Name);
            }
        }
        if (task.Status != DownloadTaskStatus.Cancelled)
        {
            task.Status = DownloadTaskStatus.Failed;
            task.ErrorMessage = string.IsNullOrEmpty(lastError) ? "获取播放地址失败" : lastError;
        }
    }

    /// <summary>音质档位排序（数值越大越优）：flac24bit &gt; flac &gt; 320k &gt; 192k &gt; 128k</summary>
    private static int QualityRank(string q)
    {
        var key = (q ?? "").ToLowerInvariant().Replace(" ", "");
        return key switch
        {
            "flac24bit" => 6,
            "flac" or "无损" => 5,
            "320k" => 4,
            "192k" => 3,
            "128k" => 2,
            _ => 1
        };
    }

    /// <summary>构造下载音质尝试顺序：请求的音质优先，其余按“最高→最低”排列（下载不了时优先最高音质）</summary>
    private static List<string> BuildQualityOrder(string requested, List<string> available)
    {
        var result = new List<string>();
        if (!string.IsNullOrEmpty(requested) && !result.Contains(requested, StringComparer.OrdinalIgnoreCase))
            result.Add(requested);
        foreach (var q in (available ?? new List<string>()).OrderByDescending(QualityRank))
            if (!result.Contains(q, StringComparer.OrdinalIgnoreCase))
                result.Add(q);
        return result;
    }

    private async Task DownloadFromUrlAsync(DownloadTask task, string url, string saveDir, DownloadPrefs prefs, CancellationToken ct)
    {
        saveDir = EnsureDownloadDirectory(saveDir);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        task.TotalBytes = total;
        task.Status = DownloadTaskStatus.Downloading;

        var ext = GetExtension(url, response.Content.Headers.ContentType?.MediaType ?? "");
        var tempPath = Path.Combine(saveDir, Guid.NewGuid().ToString("N") + ".download");

        try
        {
            await using (var fs = File.Create(tempPath))
            {
                await using var src = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n), timeout.Token);
                    read += n;
                    task.DownloadedBytes = read;
                    if (total > 0) task.Progress = Math.Min(100, read * 100.0 / total);
                }
            }

            // 依据真实文件头识别实际音频格式并以此命名，避免“请求无损却返回 mp3”时命名失真。
            var (actualLabel, actualExt) = ResolveActualFormat(tempPath, ext);
            task.ActualQuality = actualLabel;
            var namePart = BuildNamePart(task.Music.Artist, task.Music.Title, prefs.Order);
            var qualityTag = prefs.IncludeQuality ? $" [{task.ActualQuality}]" : "";
            var finalBase = SanitizeFileName(namePart + qualityTag);
            var finalPath = GetUniquePath(Path.Combine(saveDir, finalBase + actualExt));
            File.Move(tempPath, finalPath);
            task.SavePath = finalPath;
            task.Progress = 100;
            task.Status = DownloadTaskStatus.Completed;

            // 同步下载歌词（失败不影响歌曲下载）
            if (prefs.Lyric && !string.Equals(task.Music.Source, "Local", StringComparison.OrdinalIgnoreCase))
                await DownloadLyricFileAsync(task, saveDir, Path.GetFileNameWithoutExtension(finalPath), ct);
        }
        catch
        {
            // 下载被取消或失败：删除未转正的临时 .download 文件，避免在下载目录残留缓存
            DeleteTemporaryFile(tempPath, task.Id);
            throw;
        }
    }

    /// <summary>Resolve the real format of a finished download (magic bytes first, then URL/content-type).</summary>
    private static (string Label, string Ext) ResolveActualFormat(string filePath, string fallbackExt)
    {
        var fmt = SniffAudioFormat(filePath);
        if (!string.IsNullOrEmpty(fmt))
        {
            return fmt switch
            {
                "flac" => ("flac", ".flac"),
                "mp3" => ("mp3", ".mp3"),
                "m4a" => ("m4a", ".m4a"),
                "aac" => ("aac", ".aac"),
                "ogg" => ("ogg", ".ogg"),
                "wav" => ("wav", ".wav"),
                "ape" => ("ape", ".ape"),
                "wma" => ("wma", ".wma"),
                _ => (fmt, fallbackExt)
            };
        }
        var label = fallbackExt.TrimStart('.');
        return (string.IsNullOrEmpty(label) ? "audio" : label, fallbackExt);
    }

    /// <summary>Sniff the audio container from the file header (magic bytes).</summary>
    private static string? SniffAudioFormat(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            var head = new byte[16];
            int read = fs.Read(head, 0, head.Length);
            if (read < 4) return null;
            if (head[0] == 0x66 && head[1] == 0x4C && head[2] == 0x61 && head[3] == 0x43) return "flac"; // fLaC
            if (head[0] == 0x4F && head[1] == 0x67 && head[2] == 0x67 && head[3] == 0x53) return "ogg";   // OggS
            if (head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 &&
                read >= 12 && head[8] == 0x57 && head[9] == 0x41 && head[10] == 0x56 && head[11] == 0x45) return "wav"; // RIFF..WAVE
            if (head[0] == 0x4D && head[1] == 0x41 && head[2] == 0x43 && head[3] == 0x20) return "ape";   // MAC 
            if (read >= 8 && head[4] == 0x66 && head[5] == 0x74 && head[6] == 0x79 && head[7] == 0x70) return "m4a"; // ftyp
            if (head[0] == 0x49 && head[1] == 0x44 && head[2] == 0x33) return "mp3";  // ID3 tag
            if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && (head[1] & 0x06) != 0) return "mp3"; // MPEG frame sync
            if (read >= 8 && head[0] == 0x30 && head[1] == 0x26 &&
                head[4] == 0xB2 && head[5] == 0x75 && head[6] == 0x8E && head[7] == 0x66) return "wma"; // ASF GUID
            return null;
        }
        catch { return null; }
    }

    private static string GetExtension(string url, string contentType)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (KnownAudioExt.Contains(ext)) return ext.ToLowerInvariant();
        }
        catch { }
        if (!string.IsNullOrEmpty(contentType))
        {
            var ct = contentType.ToLowerInvariant();
            if (ct.Contains("flac")) return ".flac";
            if (ct.Contains("mpeg") || ct.Contains("mp3")) return ".mp3";
            if (ct.Contains("mp4") || ct.Contains("m4a") || ct.Contains("aac")) return ct.Contains("aac") ? ".aac" : ".m4a";
            if (ct.Contains("wav") || ct.Contains("wave")) return ".wav";
            if (ct.Contains("opus")) return ".opus";
            if (ct.Contains("ogg")) return ".ogg";
            if (ct.Contains("ape")) return ".ape";
            if (ct.Contains("wma")) return ".wma";
        }
        return ".mp3";
    }

    /// <summary>按命名设置拼接“艺术家-歌名”或“歌名-艺术家”（空段自动忽略分隔符）</summary>
    private static string BuildNamePart(string artist, string title, DownloadNameOrder order)
    {
        var a = (artist ?? "").Trim();
        var t = (title ?? "").Trim();
        if (string.IsNullOrEmpty(a)) return t;
        if (string.IsNullOrEmpty(t)) return a;
        return order == DownloadNameOrder.TitleArtist ? $"{t} - {a}" : $"{a} - {t}";
    }

    /// <summary>将解析后的歌词行转为标准 LRC 文本（[mm:ss.xx] 文本）</summary>
    private static string BuildLrc(Lyric lyric)
    {
        var sb = new StringBuilder();
        foreach (var line in lyric.Lines.OrderBy(l => l.Time))
        {
            var t = line.Time;
            sb.Append('[')
              .Append(t.Minutes.ToString("00")).Append(':')
              .Append(t.Seconds.ToString("00")).Append('.')
              .Append((t.Milliseconds / 10).ToString("00")).Append(']')
              .Append(line.Text ?? "").Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>下载歌词：获取并生成与音频同名的 .lrc 文件，失败静默不影响歌曲下载</summary>
    private async Task DownloadLyricFileAsync(DownloadTask task, string saveDir, string baseName, CancellationToken ct)
    {
        try
        {
            var result = await _lyricService.GetLyricAsync(task.Music, ct);
            if (result?.Lyric == null || result.Lyric.Lines.Count == 0)
            {
                _logger.LogDebug("无歌词可下载: {Title}", task.Music.Title);
                return;
            }
            var lrcPath = Path.Combine(saveDir, baseName + ".lrc");
            var lrc = BuildLrc(result.Lyric);
            await File.WriteAllTextAsync(lrcPath, lrc, new System.Text.UTF8Encoding(false), ct);
            task.LyricPath = lrcPath;
            _logger.LogInformation("歌词下载完成: {Path}", lrcPath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "歌词下载失败（不影响歌曲）: {Title}", task.Music.Title);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var result = sb.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "未命名" : result;
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string DownloadSlot(DownloadTask task) => $"download.{task.Id}";

    private void DeleteTemporaryFile(string path, string taskId)
    {
        try { File.Delete(path); }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Download temporary file release failed ({TaskId}): {ErrorType}",
                taskId,
                exception.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var slots = Tasks.Select(DownloadSlot).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var slot in slots) _backgroundWork.Cancel(slot);
        foreach (var slot in slots) await _backgroundWork.WaitAsync(slot);
        if (_ownsBackgroundWork) await _backgroundWork.DisposeAsync();
        if (_ownsHttpClient) _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
