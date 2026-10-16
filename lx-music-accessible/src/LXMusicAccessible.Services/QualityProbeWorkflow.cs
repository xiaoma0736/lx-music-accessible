using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
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
    string? ActualQuality);

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
        var entry = _cache.GetOrAdd(key, _ => new());
        if (entry.Completion.Task.IsCompleted && entry.ExpiresAt > DateTimeOffset.UtcNow)
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
                        var result = await ProbeCoreAsync(music, entry.Cancellation.Token);
                        entry.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
                        entry.IsSuccess = true;
                        entry.Completion.TrySetResult(result);
                    }
                    catch (OperationCanceledException)
                    {
                        entry.Completion.TrySetCanceled(entry.Cancellation.Token);
                    }
                    catch (Exception exception)
                    {
                        var failure = new QualityProbeResult(
                            CacheKey(music),
                            [],
                            $"音质探测失败：{exception.GetType().Name}");
                        entry.Completion.TrySetResult(failure);
                    }
                }, entry.Cancellation.Token);
            }
        }

        try
        {
            return await entry.Completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Invalidate(music);
            throw new OperationCanceledException("音质探测已取消", entry.Cancellation.Token);
        }
    }

    public QualityProbeResult GetCached(MusicInfo music)
    {
        if (!_cache.TryGetValue(CacheKey(music), out var entry)
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

    private async Task<QualityProbeResult> ProbeCoreAsync(
        MusicInfo music,
        CancellationToken cancellationToken)
    {
        var candidates = await candidateSource.GetCandidatesAsync(music, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var groups = candidates
            .GroupBy(candidate => QualityCatalog.Normalize(candidate.Quality))
            .OrderByDescending(group => QualityCatalog.Rank(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var options = new List<QualityOption>(groups.Length);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quality = group.Key;
            var ordered = group
                .OrderBy(candidate => candidate.Priority)
                .ThenBy(candidate => candidate.InterfaceName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var playable = false;
            string? actualQuality = null;
            string? interfaceName = null;

            foreach (var candidate in ordered)
            {
                string? url;
                try
                {
                    url = await candidateSource.ResolveAsync(music, candidate, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(url)) continue;
                var probe = await ProbeMediaAsync(url, quality, cancellationToken);
                if (probe is null) continue;
                playable = true;
                actualQuality = probe;
                interfaceName = candidate.InterfaceName;
                break;
            }

            options.Add(new(
                quality,
                QualityCatalog.Label(quality),
                playable ? QualityAvailability.Playable : QualityAvailability.Unavailable,
                playable ? interfaceName : null,
                actualQuality));
        }

        return new(CacheKey(music), options, options.Any(option => option.Status == QualityAvailability.Playable)
            ? "音质探测完成"
            : "没有探测到可播放音质");
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
        attempt.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 262_143);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            attempt.Token);
        var status = response.StatusCode;
        if (status is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)) return null;

        var bytes = await response.Content.ReadAsByteArrayAsync();
        attempt.Token.ThrowIfCancellationRequested();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var actual = AudioQualityInspector.Identify(bytes);
        if (actual is null
            && (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || bytes.Length > 65_536))
            actual = requestedQuality;
        if (actual is null) return null;
        return MusicQuality.AcceptsDownload(requestedQuality, actual) ? actual : null;
    }

    private static string CacheKey(MusicInfo music) =>
        $"{music.Source.Trim().ToLowerInvariant()}|{music.Id}";

}
