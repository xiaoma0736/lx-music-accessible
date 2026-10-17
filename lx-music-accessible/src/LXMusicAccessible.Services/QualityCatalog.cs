namespace LXMusicAccessible.Services;

/// <summary>
/// The single definition of every quality id the application can show: Chinese label and fidelity
/// rank. Menus, the source-switch list, playback status text and the settings combos all derive
/// their text from here, so the same id can never be worded differently on two surfaces.
/// </summary>
public static class QualityCatalog
{
    /// <summary>Canonical ids in menu order, highest fidelity first.</summary>
    public static IReadOnlyList<string> KnownQualities { get; } =
    [
        "jymaster", "master", "atmos_plus", "atmos", "spatial", "dolby", "sky",
        "jyeffect", "hires", "flac24bit", "hifi", "flac", "320k", "192k", "128k"
    ];

    /// <summary>
    /// Requested qualities and the media tokens reported by the player/download inspector. Ranks are
    /// kept stable: callers use them to decide whether a stream is at least as good as requested.
    /// </summary>
    private static readonly Dictionary<string, (string Label, int Rank)> Definitions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["viper"] = ("蝰蛇音质", 14),
            ["jymaster"] = ("珍品/超清母带", 13),
            ["master"] = ("母带", 12),
            ["atmos_plus"] = ("杜比全景声+", 11),
            ["atmos"] = ("杜比全景声", 10),
            ["spatial"] = ("空间音频", 10),
            ["dolby"] = ("杜比", 9),
            ["sky"] = ("沉浸环绕声", 8),
            ["jyeffect"] = ("高清环绕声", 7),
            ["hires"] = ("Hi-Res", 6),
            ["flac24bit"] = ("Hi-Res 无损", 5),
            ["hifi"] = ("Hi-Fi 无损", 5),
            ["flac"] = ("无损 FLAC", 4),
            ["ape"] = ("无损 APE", 4),
            ["alac"] = ("无损 ALAC", 4),
            ["wav"] = ("无损 WAV", 4),
            ["opus"] = ("Opus", 4),
            ["320k"] = ("极高", 3),
            ["192k"] = ("较高", 2),
            ["128k"] = ("标准", 1),
            ["mp3"] = ("MP3", 1),
            ["aac"] = ("AAC", 1),
            ["ogg"] = ("OGG", 1),
            ["m4a"] = ("AAC/M4A", 1),
            ["wma"] = ("WMA", 1)
        };

    public static string Label(string? quality)
    {
        var id = Normalize(quality);
        if (id.Length == 0) return "未知";
        return Definitions.TryGetValue(id, out var definition) ? definition.Label : id;
    }

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

    public static int Rank(string? quality)
    {
        var id = Normalize(quality);
        return id.Length > 0 && Definitions.TryGetValue(id, out var definition)
            ? definition.Rank
            : 0;
    }

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
