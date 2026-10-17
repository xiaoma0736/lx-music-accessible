using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Services;

public enum QualityAvailability
{
    Unknown,
    Probing,
    Playable,
    Unavailable,
    Failed
}

public sealed record QualityOption(
    string Quality,
    string Label,
    QualityAvailability Status,
    string? InterfaceName,
    string? ActualQuality,
    bool Downloadable = false);

public sealed record QualityProbeResult(
    string CacheKey,
    IReadOnlyList<QualityOption> Options,
    string? Message);

public interface IQualityProbeWorkflow
{
    Task<QualityProbeResult> ProbeAsync(
        MusicInfo music,
        CancellationToken cancellationToken = default);

    QualityProbeResult GetCached(MusicInfo music);

    void Invalidate(MusicInfo music);
}

public sealed class QualityProbeWorkflow(
    IQualityCandidateSource candidateSource,
    HttpClient httpClient) : IQualityProbeWorkflow
{
    // The quality submenu must answer in seconds while still never claiming a quality the source
    // cannot serve. Groups are probed in parallel, candidates inside one group stay ordered by
    // interface priority so the lowest-priority playable URL wins deterministically.
    private const int MaxConcurrentQualityProbes = 4;
    private const int MaxProbeBytes = 262_144;
    private const int RecognizedPayloadThreshold = 65_536;
    private const string ProbeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CandidateTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(10);

    private sealed class CacheEntry
    {
        public CacheEntry()
        {
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Cancellation = new();
        }

        public TaskCompletionSource<QualityProbeResult> Completion { get; }
        public DateTimeOffset ExpiresAt { get; set; }
        public bool IsSuccess { get; set; }
        public bool Started { get; set; }
        public CancellationTokenSource Cancellation { get; }
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public async Task<QualityProbeResult> ProbeAsync(
        MusicInfo music,
        CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(music);
        cancellationToken.ThrowIfCancellationRequested();
        var key = CacheKey(music);
        var entry = _cache.GetOrAdd(key, _ => new CacheEntry());
        var entryToken = entry.Cancellation.Token;
        if (entry.IsSuccess && entry.Completion.Task.IsCompleted && entry.ExpiresAt > DateTimeOffset.UtcNow)
            return await entry.Completion.Task;

        lock (entry)
        {
            if (!entry.Started)
            {
                entry.Started = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await ProbeCoreAsync(music, entryToken);
                        // Incomplete rounds are handed to the caller that asked for them and then
                        // dropped, so reopening the submenu retries instead of replaying the gap.
                        var cacheable = result.Options.Count > 0
                            && result.Options.All(option => option.Status != QualityAvailability.Failed);
                        if (cacheable)
                        {
                            entry.ExpiresAt = DateTimeOffset.UtcNow.Add(ResultLifetime);
                            entry.IsSuccess = true;
                        }
                        else
                        {
                            Discard(key, entry);
                        }
                        entry.Completion.TrySetResult(result);
                    }
                    catch (OperationCanceledException)
                    {
                        Discard(key, entry);
                        entry.Completion.TrySetCanceled(entryToken);
                    }
                    catch (Exception exception)
                    {
                        // A transient failure is reported once and then forgotten. Keeping it in the
                        // dictionary would pin the submenu to "探测失败" for the rest of the session,
                        // because a completed entry is handed to every later caller.
                        Discard(key, entry);
                        var failure = new QualityProbeResult(
                            CacheKey(music),
                            [],
                            $"音质探测失败：{exception.GetType().Name}");
                        entry.Completion.TrySetResult(failure);
                    }
                }, entryToken);
            }
        }

        try
        {
            return await entry.Completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Invalidate(music);
            // The queued probe may never run when its token is already cancelled; completing the
            // shared entry keeps other callers from waiting on a task that can no longer finish.
            entry.Completion.TrySetCanceled(entryToken);
            throw new OperationCanceledException("音质探测已取消", entryToken);
        }
    }

    public QualityProbeResult GetCached(MusicInfo music)
    {
        if (!_cache.TryGetValue(CacheKey(music), out var entry)
            || !entry.IsSuccess
            || !entry.Completion.Task.IsCompleted
            || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            return new(CacheKey(music), [], null);
        return entry.Completion.Task.Status == TaskStatus.RanToCompletion
            ? entry.Completion.Task.Result
            : new(CacheKey(music), [], "音质探测未完成");
    }

    public void Invalidate(MusicInfo music)
    {
        if (_cache.TryRemove(CacheKey(music), out var entry))
        {
            entry.Cancellation.Cancel();
            entry.Cancellation.Dispose();
        }
    }

    private void Discard(string key, CacheEntry entry) =>
        ((ICollection<KeyValuePair<string, CacheEntry>>)_cache)
            .Remove(new KeyValuePair<string, CacheEntry>(key, entry));

    private async Task<QualityProbeResult> ProbeCoreAsync(
        MusicInfo music,
        CancellationToken cancellationToken)
    {
        var candidates = await candidateSource.GetCandidatesAsync(music, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var groups = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Quality))
            .GroupBy(candidate => QualityCatalog.Normalize(candidate.Quality))
            .OrderByDescending(group => QualityCatalog.Rank(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ProbeBudget);
        var probed = new QualityOption?[groups.Length];
        using var gate = new SemaphoreSlim(MaxConcurrentQualityProbes, MaxConcurrentQualityProbes);
        var work = groups
            .Select((group, index) => ProbeGroupWithGateAsync(
                music,
                group.Key,
                group,
                index,
                probed,
                gate,
                budget.Token,
                cancellationToken))
            .ToArray();
        await Task.WhenAll(work);

        var options = new List<QualityOption>(groups.Length);
        for (var index = 0; index < groups.Length; index++)
            options.Add(probed[index] ?? Incomplete(groups[index].Key));

        var playable = options.Any(option => option.Status == QualityAvailability.Playable);
        var incomplete = options.Any(option => option.Status == QualityAvailability.Failed);
        var message = incomplete
            ? playable
                ? "部分音质未探测完成，可重新打开菜单重试"
                : "音质探测未完成，请重新打开菜单重试"
            : playable
                ? "音质探测完成"
                : "没有探测到可播放音质";
        return new QualityProbeResult(CacheKey(music), options, message);
    }

    private async Task ProbeGroupWithGateAsync(
        MusicInfo music,
        string quality,
        IEnumerable<SourceQualityCandidate> candidates,
        int index,
        QualityOption?[] probed,
        SemaphoreSlim gate,
        CancellationToken probeToken,
        CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(probeToken);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return;
        }

        try
        {
            probed[index] = await ProbeQualityGroupAsync(
                music,
                quality,
                candidates,
                probeToken,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            probed[index] = Incomplete(quality);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<QualityOption> ProbeQualityGroupAsync(
        MusicInfo music,
        string quality,
        IEnumerable<SourceQualityCandidate> candidates,
        CancellationToken probeToken,
        CancellationToken cancellationToken)
    {
        var ordered = candidates
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var attempted = false;
        var everyAttemptFailed = true;
        foreach (var candidate in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (probeToken.IsCancellationRequested) return Incomplete(quality);
            attempted = true;

            string? url;
            try
            {
                url = await candidateSource.ResolveAsync(music, candidate, probeToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Incomplete(quality);
            }
            catch
            {
                // One broken interface must not take the whole submenu down with it.
                continue;
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                everyAttemptFailed = false;
                continue;
            }

            string? actual;
            try
            {
                actual = await ProbeMediaAsync(url, quality, probeToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Incomplete(quality);
            }
            catch
            {
                continue;
            }

            if (actual is null)
            {
                everyAttemptFailed = false;
                continue;
            }

            return new QualityOption(
                quality,
                QualityCatalog.Label(quality),
                QualityAvailability.Playable,
                candidate.InterfaceName,
                actual,
                MusicQuality.AcceptsDownload(quality, actual));
        }

        if (!attempted) return Incomplete(quality);
        return new QualityOption(
            quality,
            QualityCatalog.Label(quality),
            everyAttemptFailed ? QualityAvailability.Failed : QualityAvailability.Unavailable,
            null,
            null);
    }

    private async Task<string?> ProbeMediaAsync(
        string url,
        string requestedQuality,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return null;

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(CandidateTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // The playback probe in MusicSourceService sends a browser user agent; CDNs reject the bare
        // request while serving the very same stream to the player, which made every quality look dead.
        request.Headers.TryAddWithoutValidation("User-Agent", ProbeUserAgent);
        request.Headers.Range = new RangeHeaderValue(0, MaxProbeBytes - 1);
        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                attempt.Token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                return null;

            var bytes = await ReadProbeBytesAsync(response.Content, attempt.Token);
            if (bytes.Length == 0) return null;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var actual = AudioQualityInspector.Identify(bytes);
            if (actual is null
                && !LooksLikeTextualPayload(bytes, contentType)
                && (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                    || bytes.Length > RecognizedPayloadThreshold))
                actual = requestedQuality;
            if (actual is null) return null;
            // The submenu must answer the same question the player answers: would this stream play
            // when the user picks this quality? Downloads keep their stricter rule via Downloadable.
            return MusicQuality.AcceptsPlayback(requestedQuality, actual) ? actual : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A candidate timeout or transport error only rules out this candidate.
            return null;
        }
    }

    private static async Task<byte[]> ReadProbeBytesAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync();
        var buffer = new byte[MaxProbeBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, total, buffer.Length - total, cancellationToken);
            if (read <= 0) break;
            total += read;
        }

        if (total == buffer.Length) return buffer;
        Array.Resize(ref buffer, total);
        return buffer;
    }

    /// <summary>
    /// Error pages and JSON error bodies are frequently larger than 64 KB; accepting them as the
    /// requested quality produced menu entries that could never play.
    /// </summary>
    private static bool LooksLikeTextualPayload(ReadOnlySpan<byte> bytes, string contentType)
    {
        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            return true;

        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        while (offset < bytes.Length && bytes[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            offset++;
        return offset < bytes.Length && bytes[offset] is (byte)'<' or (byte)'{' or (byte)'[';
    }

    private static QualityOption Incomplete(string quality) => new(
        quality,
        QualityCatalog.Label(quality),
        QualityAvailability.Failed,
        null,
        null);

    private static string CacheKey(MusicInfo music) =>
        $"{music.Source.Trim().ToLowerInvariant()}|{music.Id}";
}
