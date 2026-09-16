using LXMusicAccessible.Models;

namespace LXMusicAccessible.Services;

internal sealed class SourceSnapshot
{
    internal SourceSnapshot(
        string name,
        string version,
        string scriptUrl,
        string? scriptContent,
        bool isEnabled,
        SourceType type,
        ScriptTrustState trustState,
        string? contentSha256,
        DateTime lastUpdateDate)
    {
        Name = name; Version = version; ScriptUrl = scriptUrl; ScriptContent = scriptContent;
        IsEnabled = isEnabled; Type = type; TrustState = trustState;
        ContentSha256 = contentSha256; LastUpdateDate = lastUpdateDate;
    }

    internal string Name { get; }
    internal string Version { get; }
    internal string ScriptUrl { get; }
    internal string? ScriptContent { get; }
    internal bool IsEnabled { get; }
    internal SourceType Type { get; }
    internal ScriptTrustState TrustState { get; }
    internal string? ContentSha256 { get; }
    internal DateTime LastUpdateDate { get; }

    internal static SourceSnapshot Capture(MusicSource source) => new(
        source.Name, source.Version, source.ScriptUrl, source.ScriptContent, source.IsEnabled,
        source.Type, source.TrustState, source.ContentSha256, source.LastUpdateDate);

    internal void Restore(MusicSource source)
    {
        source.Name = Name; source.Version = Version; source.ScriptUrl = ScriptUrl;
        source.ScriptContent = ScriptContent; source.IsEnabled = IsEnabled; source.Type = Type;
        source.TrustState = TrustState; source.ContentSha256 = ContentSha256;
        source.LastUpdateDate = LastUpdateDate;
    }
}
