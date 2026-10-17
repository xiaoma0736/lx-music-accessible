using System.Collections.Concurrent;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Services;

public enum ManualSourceDiscoveryStatus
{
    Success,
    Timeout,
    Failed
}

/// <summary>一个已经实际解析并通过可播放性校验的换源选项；不缓存播放 URL。</summary>
public sealed record ManualSourceOption(
    string SourceId,
    string SourceName,
    string Platform,
    MusicInfo Candidate,
    string Quality,
    string DisplayLabel)
{
    public string AccessibleLabel => DisplayLabel;

    public bool IsPlayable { get; init; } = true;

    public string? StateMessage { get; init; }

    public static ManualSourceOption Create(
        string sourceId,
        string sourceName,
        string platform,
        MusicInfo candidate,
        string quality,
        string displayLabel)
    {
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(sourceId);
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(sourceName);
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(platform);
        FrameworkCompatibility.ThrowIfNull(candidate);
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(quality);
        var label = string.IsNullOrWhiteSpace(displayLabel)
            ? $"{sourceName}，{QualityCatalog.Label(quality)}，{candidate.Title}，{candidate.Artist}"
            : displayLabel.Trim();
        return new(sourceId, sourceName, platform, candidate, quality, label);
    }

    public static ManualSourceOption Unavailable(
        string sourceId,
        string sourceName,
        string platform,
        MusicInfo candidate,
        string quality,
        string stateMessage)
    {
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(sourceId);
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(sourceName);
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(platform);
        FrameworkCompatibility.ThrowIfNull(candidate);
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(quality);
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(stateMessage);
        var label = $"{sourceName}，{QualityCatalog.Label(quality)}，不可用，{stateMessage}，{candidate.Title}，{candidate.Artist}";
        return new(sourceId, sourceName, platform, candidate, quality, label)
        {
            IsPlayable = false,
            StateMessage = stateMessage
        };
    }

    internal string DeduplicationKey =>
        $"{SourceId}|{Platform}|{Candidate.Source}|{Candidate.Id}|{Candidate.Hash}|{Candidate.Songmid}|{Quality}";
}

/// <summary>换源可用的播放接口描述。</summary>
public sealed record ManualSourceInterface(
    string Id,
    string DisplayName,
    string Platform,
    IReadOnlyList<string> Qualities,
    bool IsScript,
    bool IsEnabled)
{
    public static ManualSourceInterface BuiltIn(
        string id,
        string displayName,
        string platform,
        IReadOnlyList<string> qualities) => new(id, displayName, platform, qualities, IsScript: false, IsEnabled: true);

    public static ManualSourceInterface Script(
        string id,
        string displayName,
        string platform,
        IReadOnlyList<string> qualities,
        bool isEnabled = true) => new(id, displayName, platform, qualities, IsScript: true, isEnabled);
}

public sealed record ManualSourceDiscoveryResult(
    ManualSourceDiscoveryStatus Status,
    IReadOnlyList<ManualSourceOption> Options,
    string Message = "")
{
    public static ManualSourceDiscoveryResult Success(IReadOnlyList<ManualSourceOption> options)
    {
        var playable = options.Count(option => option.IsPlayable);
        var hidden = options.Count - playable;
        var message = playable == 0
            ? "没有找到可播放音源"
            : hidden == 0
                ? $"找到 {playable} 个可播放音源"
                : $"找到 {playable} 个可播放音源，已隐藏 {hidden} 个不可用项";
        return new(ManualSourceDiscoveryStatus.Success, options, message);
    }

    public static ManualSourceDiscoveryResult Partial(IReadOnlyList<ManualSourceOption> options) => new(
        ManualSourceDiscoveryStatus.Success,
        options,
        $"换源查询超时，仅列出已完成的可播放项（{options.Count} 个）");

    public static ManualSourceDiscoveryResult Timeout() => new(
        ManualSourceDiscoveryStatus.Timeout,
        [],
        "换源查询超时");

    public static ManualSourceDiscoveryResult Failed(string message) => new(
        ManualSourceDiscoveryStatus.Failed,
        [],
        message);
}

public sealed record ManualSourcePlaybackRequest(
    IReadOnlyList<MusicInfo> Queue,
    MusicInfo Target,
    ManualSourceOption Option,
    PlaybackQueueOrigin Origin);

public interface IManualSourceGateway
{
    IReadOnlyList<ManualSourceInterface> GetPlaybackInterfaces(MusicInfo target);

    Task<IReadOnlyList<MusicInfo>> SearchCandidatesAsync(
        MusicInfo target,
        string sourceId,
        CancellationToken cancellationToken = default);

    Task<string?> ResolveExactAsync(
        MusicInfo candidate,
        string sourceId,
        string quality,
        CancellationToken cancellationToken = default);
}

public interface IExactPlaybackCoordinator
{
    Task<PlaybackResult> PlayExactAsync(
        PlaybackRequest request,
        ManualSourceOption option);
}

public interface IManualSourceDiscoveryWorkflow : IDisposable
{
    Task<ManualSourceDiscoveryResult> DiscoverAsync(
        MusicInfo target,
        CancellationToken cancellationToken = default);

    Task<PlaybackResult> PlayAsync(
        ManualSourcePlaybackRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>后台发现所有接口的严格同曲可播放选项，并提交显式精确播放。</summary>
public sealed class ManualSourceDiscoveryWorkflow(
    IManualSourceGateway gateway,
    IExactPlaybackCoordinator playback,
    ILogger<ManualSourceDiscoveryWorkflow> logger,
    TimeSpan? budget = null) : IManualSourceDiscoveryWorkflow
{
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CancellationPropagationGrace = TimeSpan.FromMilliseconds(250);
    // Every script candidate can create its own ClearScript V8 engine and block a thread-pool
    // thread. Sixteen enabled scripts with eight declared qualities each used to start well over a
    // hundred of them at once, which starved the whole process. Eight keeps one interface's whole
    // quality list in flight while bounding the engines globally.
    private const int MaxConcurrentQualityProbes = 8;
    private readonly SemaphoreSlim _probeGate = new(MaxConcurrentQualityProbes, MaxConcurrentQualityProbes);

    public async Task<ManualSourceDiscoveryResult> DiscoverAsync(
        MusicInfo target,
        CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(target);
        using var timeout = new CancellationTokenSource(
            budget ?? DefaultBudget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var completed = new ConcurrentBag<ManualSourceOption>();
        var discovery = DiscoverCoreAsync(target, linked.Token, completed);

        try
        {
            var options = await discovery.WaitAsync(timeout.Token);
            return ManualSourceDiscoveryResult.Success(options);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            await WaitForCancellationPropagationAsync(discovery);
            var partial = OrderOptions(completed.ToArray(), includeUnavailable: false);
            return partial.Count > 0
                ? ManualSourceDiscoveryResult.Partial(partial)
                : ManualSourceDiscoveryResult.Timeout();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Manual source discovery failed for {Source}|{MusicId}",
                target.Source,
                target.Id);
            return ManualSourceDiscoveryResult.Failed("换源查询失败");
        }
    }

    public async Task<PlaybackResult> PlayAsync(
        ManualSourcePlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(request);
        FrameworkCompatibility.ThrowIfNull(request.Target);
        FrameworkCompatibility.ThrowIfNull(request.Option);
        if (!request.Option.IsPlayable)
        {
            return new(
                PlaybackResultStatus.Failed,
                null,
                request.Origin,
                request.Option.StateMessage ?? "所选音源音质不可用",
                PlaybackTrigger.UserInitiated);
        }

        if (!MusicSearchMatcher.IsSameSong(request.Option.Candidate, request.Target))
        {
            return new(
                PlaybackResultStatus.Failed,
                null,
                request.Origin,
                "换源候选不是严格同曲",
                PlaybackTrigger.UserInitiated);
        }

        var queue = request.Queue?.Where(item => item is not null).ToList() ?? [];
        return await playback.PlayExactAsync(
            new(
                queue,
                request.Target,
                request.Option.Quality,
                request.Origin,
                ForceResolve: true,
                PlaybackTrigger.UserInitiated),
            request.Option).WaitAsync(cancellationToken);
    }

    public void Dispose() => GC.SuppressFinalize(this);

    /// <summary>
    /// Gives a timed-out discovery task a bounded chance to observe cancellation.
    /// </summary>
    private async Task WaitForCancellationPropagationAsync(
        Task<IReadOnlyList<ManualSourceOption>> discovery)
    {
        try
        {
            await discovery.WaitAsync(CancellationPropagationGrace);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Manual source discovery cancellation propagated after the budget expired");
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Manual source discovery completed exceptionally while propagating cancellation");
        }
    }

    private async Task<IReadOnlyList<ManualSourceOption>> DiscoverCoreAsync(
        MusicInfo target,
        CancellationToken cancellationToken,
        ConcurrentBag<ManualSourceOption> completed)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var interfaces = gateway.GetPlaybackInterfaces(target)
            .Where(source => source.IsEnabled)
            .ToArray();

        var searchTasks = interfaces.Select(source => SearchBestCandidateAsync(
            target,
            source,
            cancellationToken)).ToArray();
        var candidates = await Task.WhenAll(searchTasks);

        var optionTasks = interfaces
            .Select((source, index) => DiscoverInterfaceOptionsAsync(
                target,
                source,
                candidates[index],
                completed,
                cancellationToken))
            .ToArray();
        var discovered = await Task.WhenAll(optionTasks);
        return OrderOptions(discovered.SelectMany(options => options), includeUnavailable: true);
    }

    private static IReadOnlyList<ManualSourceOption> OrderOptions(
        IEnumerable<ManualSourceOption> options,
        bool includeUnavailable) =>
        options
            .Where(option => includeUnavailable || option.IsPlayable)
            .GroupBy(option => option.DeduplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(option => option.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(option => QualityCatalog.Rank(option.Quality))
            .ThenBy(option => option.Quality, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private async Task<MusicInfo?> SearchBestCandidateAsync(
        MusicInfo target,
        ManualSourceInterface source,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await gateway.SearchCandidatesAsync(
                target,
                source.Id,
                cancellationToken);
            var candidate = candidates.FirstOrDefault(item => MusicSearchMatcher.IsSameSong(item, target));
            if (candidate is not null) return candidate;
            return source.IsScript
                && string.Equals(source.Platform, target.Source, StringComparison.OrdinalIgnoreCase)
                && MusicSearchMatcher.IsSameSong(target, target)
                ? target
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Manual source candidate search failed ({SourceId})",
                source.Id);
            return null;
        }
    }

    private async Task<IReadOnlyList<ManualSourceOption>> DiscoverInterfaceOptionsAsync(
        MusicInfo target,
        ManualSourceInterface source,
        MusicInfo? candidate,
        ConcurrentBag<ManualSourceOption> completed,
        CancellationToken cancellationToken)
    {
        if (candidate is null || !MusicSearchMatcher.IsSameSong(candidate, target))
            return [];

        var qualities = source.Qualities
            .Where(quality => !string.IsNullOrWhiteSpace(quality))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var probes = qualities.Select(quality => ProbeQualityAsync(
            candidate,
            source,
            quality,
            completed,
            cancellationToken)).ToArray();
        var options = await Task.WhenAll(probes);
        return options;
    }

    private async Task<ManualSourceOption> ProbeQualityAsync(
        MusicInfo candidate,
        ManualSourceInterface source,
        string quality,
        ConcurrentBag<ManualSourceOption> completed,
        CancellationToken cancellationToken)
    {
        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = await gateway.ResolveExactAsync(
                candidate,
                source.Id,
                quality,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(url))
            {
                var playable = ManualSourceOption.Create(
                    source.Id,
                    source.DisplayName,
                    source.Platform,
                    candidate,
                    quality,
                    $"{source.DisplayName}，{QualityCatalog.Label(quality)}，{candidate.Title}，{candidate.Artist}");
                completed.Add(playable);
                return playable;
            }

            return ManualSourceOption.Unavailable(
                source.Id,
                source.DisplayName,
                source.Platform,
                candidate,
                quality,
                "未解析到播放地址");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Manual source URL probe failed ({SourceId}, {Quality})",
                source.Id,
                quality);
            return ManualSourceOption.Unavailable(
                source.Id,
                source.DisplayName,
                source.Platform,
                candidate,
                quality,
                string.IsNullOrWhiteSpace(exception.Message) ? "解析失败" : exception.Message);
        }
        finally
        {
            _probeGate.Release();
        }
    }

}
