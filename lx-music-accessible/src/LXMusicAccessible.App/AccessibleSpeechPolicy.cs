using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>Restricts optional TTS to high-value playback, download, device, lyrics, settings, and hotkey-recording announcements.</summary>
internal static class AccessibleSpeechPolicy
{
    /// <summary>Live-region activity used by settings pages and settings toggle feedback.</summary>
    private const string SettingsActivityId = "settings";

    /// <summary>Live-region activity used while recording and confirming hotkey assignments.</summary>
    private const string HotkeyRecordingActivityId = "hotkey-recording";

    public static bool ShouldSpeak(AccessibleAnnouncement announcement)
    {
        return announcement.Speech != AnnouncementSpeech.None && IsAllowed(announcement);
    }

    private static bool IsAllowed(AccessibleAnnouncement announcement)
    {
        if (announcement.ActivityId is not { Length: > 0 } activityId)
            return false;
        return activityId.Equals(SettingsActivityId, StringComparison.Ordinal)
            || activityId.Equals(HotkeyRecordingActivityId, StringComparison.Ordinal)
            || activityId.Equals("play-mode", StringComparison.Ordinal)
            || activityId.Equals("audio-device", StringComparison.Ordinal)
            || activityId.Equals("playback-song", StringComparison.Ordinal)
            || activityId.Equals("lyrics", StringComparison.Ordinal)
            || activityId.Equals("lyrics-offset", StringComparison.Ordinal)
            || activityId.StartsWith("download:", StringComparison.Ordinal)
            || activityId.StartsWith("download-list:", StringComparison.Ordinal);
    }
}
