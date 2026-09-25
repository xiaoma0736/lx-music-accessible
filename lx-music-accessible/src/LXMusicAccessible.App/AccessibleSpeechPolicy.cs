using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>Sends only high-value feedback to TTS; routine browse state remains visual only.</summary>
internal static class AccessibleSpeechPolicy
{
    public static bool ShouldSpeak(AccessibleAnnouncement announcement)
    {
        if (announcement.Speech == AnnouncementSpeech.None) return false;
        if (announcement.Kind == AnnouncementKind.Error) return true;

        var activity = announcement.ActivityId;
        if (activity is null) return false;
        if (activity.Equals("welcome", StringComparison.Ordinal)) return false;
        if (activity.StartsWith("music-browse:", StringComparison.Ordinal)) return false;

        return !activity.Equals("categories", StringComparison.Ordinal)
            && !activity.Equals("rankings", StringComparison.Ordinal)
            && !activity.Equals("search-results", StringComparison.Ordinal)
            && !activity.Equals("browse-status", StringComparison.Ordinal)
            && !activity.Equals("saved-playlists", StringComparison.Ordinal);
    }
}
