using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>Restricts optional TTS to high-value playback/download/device/lyrics announcements.</summary>
internal static class AccessibleSpeechPolicy
{
    public static bool ShouldSpeak(AccessibleAnnouncement announcement)
    {
        return announcement.Speech != AnnouncementSpeech.None && IsAllowed(announcement);
    }

    private static bool IsAllowed(AccessibleAnnouncement announcement)
    {
        if (announcement.ActivityId is not { Length: > 0 } activityId)
            return false;
        return activityId.Equals("play-mode", StringComparison.Ordinal)
            || activityId.Equals("audio-device", StringComparison.Ordinal)
            || activityId.Equals("playback-song", StringComparison.Ordinal)
            || activityId.Equals("lyrics", StringComparison.Ordinal)
            || activityId.Equals("lyrics-offset", StringComparison.Ordinal)
            || activityId.StartsWith("download:", StringComparison.Ordinal)
            || activityId.StartsWith("download-list:", StringComparison.Ordinal);
    }
}
