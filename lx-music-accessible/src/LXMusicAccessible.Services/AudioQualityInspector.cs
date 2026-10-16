namespace LXMusicAccessible.Services;

internal static class AudioQualityInspector
{
    internal static string? Identify(byte[] bytes)
    {
        if (bytes.Length < 64) return null;
        if (StartsWith(bytes, "fLaC"u8)) return IdentifyFlac(bytes);
        if (StartsWith(bytes, "ID3"u8)) return IdentifyMp3(bytes);
        if (bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0) return IdentifyMp3(bytes);
        if (StartsWith(bytes, "RIFF"u8) && StartsWith(bytes.AsSpan(8), "WAVE"u8)) return "wav";
        if (StartsWith(bytes, "OggS"u8)) return "ogg";
        if (StartsWith(bytes, "MAC "u8)) return "ape";
        return null;
    }

    private static string? IdentifyFlac(byte[] bytes)
    {
        if (bytes.Length < 26 || !StartsWith(bytes, "fLaC"u8)) return "flac";
        if ((bytes[4] & 0x7F) != 0) return "flac";
        var blockLength = (bytes[5] << 16) | (bytes[6] << 8) | bytes[7];
        if (blockLength < 18) return "flac";
        ulong packed = 0;
        for (var index = 18; index < 26; index++) packed = (packed << 8) | bytes[index];
        var sampleRate = (int)((packed >> 44) & 0xFFFFF);
        var bitsPerSample = (int)((packed >> 36) & 0x1F) + 1;
        if (sampleRate <= 0) return "flac";
        if (bitsPerSample >= 24 && sampleRate > 48_000)
            return "hires";
        return bitsPerSample >= 24 ? "flac24bit" : "flac";
    }

    private static string? IdentifyMp3(byte[] bytes)
    {
        var start = 0;
        if (StartsWith(bytes, "ID3"u8) && bytes.Length >= 10)
            start = Math.Min(bytes.Length, 10 + ((bytes[6] & 0x7F) << 21)
                + ((bytes[7] & 0x7F) << 14) + ((bytes[8] & 0x7F) << 7) + (bytes[9] & 0x7F));

        for (var index = start; index + 3 < bytes.Length; index++)
        {
            if (bytes[index] != 0xFF || (bytes[index + 1] & 0xE0) != 0xE0) continue;
            var version = (bytes[index + 1] >> 3) & 0x03;
            var layer = (bytes[index + 1] >> 1) & 0x03;
            var bitrateIndex = (bytes[index + 2] >> 4) & 0x0F;
            if (layer != 1 || bitrateIndex is 0 or 15) continue;
            var table = version == 3
                ? new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }
                : new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
            var bitrate = table[bitrateIndex];
            return bitrate <= 160 ? "128k" : bitrate <= 256 ? "192k" : "320k";
        }

        return "128k";
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix) =>
        value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);

    private static bool StartsWith(byte[] value, ReadOnlySpan<byte> prefix) =>
        StartsWith((ReadOnlySpan<byte>)value, prefix);
}
