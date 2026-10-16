namespace LXMusicAccessible.Services;

public static class QualityCatalog
{
    public static IReadOnlyList<string> KnownQualities { get; } =
    [
        "jymaster", "master", "atmos_plus", "atmos", "dolby", "sky", "jyeffect",
        "hires", "flac24bit", "flac", "320k", "192k", "128k"
    ];

    public static string Label(string? quality) => Normalize(quality) switch
    {
        "128k" => "标准",
        "192k" => "较高",
        "320k" => "极高",
        "flac" => "无损 FLAC",
        "flac24bit" => "Hi-Res 无损",
        "hires" => "Hi-Res",
        "atmos" => "杜比全景声",
        "atmos_plus" => "杜比全景声+",
        "master" => "母带",
        "jyeffect" => "高清环绕声",
        "sky" => "沉浸环绕声",
        "dolby" => "杜比",
        "jymaster" => "珍品/超清母带",
        "viper" => "蝰蛇音质",
        var id => string.IsNullOrWhiteSpace(id) ? "未知" : id
    };

    public static string Normalize(string? quality)
    {
        var id = quality?.Trim().ToLowerInvariant() ?? "";
        return id switch
        {
            "standard" => "128k",
            "high" => "320k",
            "lossless" => "flac24bit",
            _ => id
        };
    }

    public static int Rank(string? quality) => Normalize(quality) switch
    {
        "128k" or "mp3" or "aac" or "ogg" or "m4a" or "wma" => 1,
        "192k" => 2,
        "320k" => 3,
        "flac" or "ape" or "alac" or "wav" or "opus" => 4,
        "flac24bit" => 5,
        "hires" => 6,
        "jyeffect" => 7,
        "sky" => 8,
        "dolby" => 9,
        "atmos" => 10,
        "atmos_plus" => 11,
        "master" => 12,
        "jymaster" => 13,
        "viper" => 14,
        _ => 0
    };

    public static IReadOnlyList<string> Order(IEnumerable<string?> qualities)
    {
        return qualities
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(Rank)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
