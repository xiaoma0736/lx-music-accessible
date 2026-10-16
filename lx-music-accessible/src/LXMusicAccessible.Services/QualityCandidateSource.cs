using LXMusicAccessible.Models;

namespace LXMusicAccessible.Services;

public sealed record SourceQualityCandidate(
    string InterfaceId,
    string InterfaceName,
    int Priority,
    string Quality);

public interface IQualityCandidateSource
{
    Task<IReadOnlyList<SourceQualityCandidate>> GetCandidatesAsync(
        MusicInfo music,
        CancellationToken cancellationToken = default);

    Task<string?> ResolveAsync(
        MusicInfo music,
        SourceQualityCandidate candidate,
        CancellationToken cancellationToken = default);
}
