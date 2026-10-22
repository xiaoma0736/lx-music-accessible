using System.Collections.Concurrent;
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
    string? DownloadQuality = null);

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

/// <summary>
/// Builds quality menus from what an enabled source interface declares for the song's platform.
/// It intentionally does not download media:真实性 cannot be inferred from a short probe, and the
/// source declaration is the only contract the app can show before playback or download begins.
/// </summary>
public sealed class QualityProbeWorkflow(IQualityCandidateSource candidateSource) : IQualityProbeWorkflow
{
    private readonly ConcurrentDictionary<string, QualityProbeResult> _cache = new(StringComparer.Ordinal);

    public async Task<QualityProbeResult> ProbeAsync(
        MusicInfo music,
        CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(music);
        cancellationToken.ThrowIfCancellationRequested();
        var key = CacheKey(music);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var result = await ProbeCoreAsync(music, cancellationToken);
        return _cache.GetOrAdd(key, result);
    }

    public QualityProbeResult GetCached(MusicInfo music)
    {
        FrameworkCompatibility.ThrowIfNull(music);
        return _cache.TryGetValue(CacheKey(music), out var result)
            ? result
            : new(CacheKey(music), [], null);
    }

    public void Invalidate(MusicInfo music)
    {
        FrameworkCompatibility.ThrowIfNull(music);
        _cache.TryRemove(CacheKey(music), out _);
    }

    /// <summary>
    /// Source changes (default source, enable/disable, import, delete) change the declarations,
    /// so every cached round must be dropped together.
    /// </summary>
    public void InvalidateAll() => _cache.Clear();

    private async Task<QualityProbeResult> ProbeCoreAsync(
        MusicInfo music,
        CancellationToken cancellationToken)
    {
        var candidates = await candidateSource.GetCandidatesAsync(music, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var options = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Quality))
            .GroupBy(candidate => QualityCatalog.Normalize(candidate.Quality))
            .OrderByDescending(group => QualityCatalog.Rank(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var selected = group
                    .OrderBy(candidate => candidate.Priority)
                    .ThenBy(candidate => candidate.InterfaceName, StringComparer.OrdinalIgnoreCase)
                    .First();
                return new QualityOption(
                    group.Key,
                    QualityCatalog.Label(group.Key),
                    QualityAvailability.Playable,
                    selected.InterfaceName,
                    null);
            })
            .ToArray();

        return new(
            CacheKey(music),
            options,
            options.Length == 0
                ? "当前音源没有声明可用音质"
                : "音源声明音质已加载");
    }

    private static string CacheKey(MusicInfo music) =>
        $"{music.Source.Trim().ToLowerInvariant()}|{music.Id}";
}
