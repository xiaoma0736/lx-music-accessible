using LXMusicAccessible.Models;

namespace LXMusicAccessible.Services;

/// <summary>播放协调模块内部的来源接缝；生产和测试适配器遵守同一顺序与取消语义。</summary>
public interface IPlaybackSourceAdapter
{
    IReadOnlyList<string> GetAlternativeSourceIds(string currentSource);

    Task<string?> ResolveAsync(
        MusicInfo music,
        string quality,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MusicInfo>> FindAlternativesAsync(
        MusicInfo music,
        string sourceId,
        CancellationToken cancellationToken);

    Task<string?> ResolveExactAsync(
        MusicInfo music,
        string sourceId,
        string quality,
        CancellationToken cancellationToken);
}

public sealed class MusicPlaybackSourceAdapter(
    MusicSourceService sources) : IPlaybackSourceAdapter
{
    private static readonly TimeSpan ResolutionBudget = TimeSpan.FromSeconds(10);

    public IReadOnlyList<string> GetAlternativeSourceIds(string currentSource) =>
        sources.GetEnabledSources()
            .Where(source => source.IsEnabled
                && source.Type == SourceType.BuiltIn
                && !string.Equals(source.Id, currentSource, StringComparison.OrdinalIgnoreCase))
            .Select(source => source.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<string?> ResolveAsync(
        MusicInfo music,
        string quality,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(ResolutionBudget);
        try
        {
            return await sources.GetPlayUrlAsync(
                music,
                music.Source,
                quality,
                attempt.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<string?> ResolveExactAsync(
        MusicInfo music,
        string sourceId,
        string quality,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(ResolutionBudget);
        try
        {
            return await sources.ResolveExactAsync(
                music,
                sourceId,
                quality,
                attempt.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<MusicInfo>> FindAlternativesAsync(
        MusicInfo music,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var api = sources.GetApi(sourceId);
        if (api is null) return [];
        var result = await api.SearchAsync(
            MusicSearchMatcher.BuildSearchKeyword(music),
            1,
            20,
            "song",
            cancellationToken);
        return MusicSearchMatcher.FindBestMatches(result.Items, music);
    }
}
