namespace LXMusicAccessible.Services;

internal static class MusicQuality
{
    internal static bool AcceptsDownload(string requested, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        var requestedRank = Rank(requested);
        var actualRank = Rank(actual);
        if (requestedRank == 0 || actualRank == 0)
            return string.Equals(requested, actual, StringComparison.OrdinalIgnoreCase);
        return actualRank >= requestedRank;
    }

    internal static bool AcceptsPlayback(string requested, string? actual)
    {
        // BASS exposes encoded bitrate for lossy streams, but only the lossless
        // container for FLAC/APE/ALAC. A lossless container therefore disproves
        // the reported lowest-quality regression even when its source bit depth
        // cannot be recovered at this seam.
        if (string.IsNullOrWhiteSpace(actual)) return true;
        if (AcceptsDownload(requested, actual)) return true;
        return Rank(requested) >= 4 && Rank(actual) >= 4;
    }

    internal static int Rank(string? quality) => QualityCatalog.Rank(quality);
}
