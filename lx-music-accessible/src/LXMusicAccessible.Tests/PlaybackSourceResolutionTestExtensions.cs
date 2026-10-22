using LXMusicAccessible.Services;

namespace LXMusicAccessible.Tests;

internal static class PlaybackSourceResolutionTestExtensions
{
    public static PlaybackSourceResolution? ToResolution(
        this string? url,
        string interfaceId,
        string quality,
        string? interfaceName = null) => string.IsNullOrWhiteSpace(url)
            ? null
            : new(url, interfaceId, interfaceName ?? interfaceId, quality);
}
