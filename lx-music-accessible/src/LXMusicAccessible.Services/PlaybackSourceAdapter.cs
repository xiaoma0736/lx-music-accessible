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
}

public sealed class MusicPlaybackSourceAdapter(
    IMusicSourceService sources) : IPlaybackSourceAdapter
{
    public IReadOnlyList<string> GetAlternativeSourceIds(string currentSource) =>
        sources.GetEnabledSources()
            .Where(source => source.IsEnabled
                && source.Type == SourceType.BuiltIn
                && !string.Equals(source.Id, currentSource, StringComparison.OrdinalIgnoreCase))
            .Select(source => source.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public Task<string?> ResolveAsync(
        MusicInfo music,
        string quality,
        CancellationToken cancellationToken) =>
        sources.GetPlayUrlAsync(
            music,
            music.Source,
            quality,
            cancellationToken);

    public async Task<IReadOnlyList<MusicInfo>> FindAlternativesAsync(
        MusicInfo music,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var result = await sources.SearchAsync(
            MusicSearchMatcher.BuildSearchKeyword(music),
            1,
            20,
            "song",
            sourceId,
            cancellationToken);
        return MusicSearchMatcher.FindBestMatches(result.Items, music);
    }
}
