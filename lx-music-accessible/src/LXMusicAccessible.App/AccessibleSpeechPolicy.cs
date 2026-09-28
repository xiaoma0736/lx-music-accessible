using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>
/// Speech is opt-in and scope-aware: only the activities below can reach a TTS back end, every other
/// announcement still mirrors into the visible status text and the live region. Routine browse and focus
/// echoes stay out of the list because screen readers already announce them. User-facing playback
/// categories are now listed here and can be narrowed further in the 无障碍 settings scope.
/// </summary>
internal static class AccessibleSpeechPolicy
{
    /// <summary>Activities whose result cannot be perceived any other way.</summary>
    private static readonly string[] SpokenActivities =
    {
        "album",
        "application-shutdown",
        "audio-device",
        "desktop-lifecycle",
        "download-manager",
        "external-playback",
        "favorite",
        "hotkey-recording",
        "hotkey-registration",
        "lyrics",
        "lyrics-offset",
        "play-mode",
        "playback-queue",
        "playback-song",
        "playback-state",
        "seek",
        "playlist-import",
        "playlist-login",
        "playlist-preview",
        "qr-login",
        "remote-favorite",
        "search-history-clear",
        "search-history-delete",
        "settings",
        "source-default",
        "source-delete",
        "source-disable",
        "source-enable",
        "source-import",
        "source-list",
        "source-test",
        "source-update",
        "sponsor",
        "web-login",
        "volume"
    };

    /// <summary>Activities that carry a dynamic identifier, GUID or revision suffix.</summary>
    private static readonly string[] SpokenActivityPrefixes =
    {
        "download:",
        "download-external:",
        "download-list:",
        "manual-source-playback:",
        "manual-source-query:",
        "playback-track:"
    };

    public static bool ShouldSpeak(AccessibleAnnouncement announcement)
    {
        if (announcement.Speech == AnnouncementSpeech.None) return false;
        if (announcement.Kind == AnnouncementKind.Error) return true;

        var activity = announcement.ActivityId;
        if (string.IsNullOrEmpty(activity)) return false;
        if (Array.IndexOf(SpokenActivities, activity) >= 0) return true;
        foreach (var prefix in SpokenActivityPrefixes)
        {
            if (activity.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// Narrows the global whitelist by user-selected speech categories. Errors stay audible so turning
    /// off routine echoes cannot also hide the reason an action failed.
    /// </summary>
    public static bool IsScopeAllowed(AccessibleAnnouncement announcement, AccessibilitySettings settings)
    {
        if (announcement.Kind == AnnouncementKind.Error) return true;
        return announcement.ActivityId switch
        {
            "playback-state" => settings.AnnouncePlaybackState,
            "playback-song" => settings.AnnounceTrackChange,
            "volume" => settings.AnnounceVolume,
            "seek" => settings.AnnounceSeek,
            "desktop-lifecycle" => settings.AnnounceWindowVisibility,
            "play-mode" => settings.AnnouncePlaybackMode,
            "audio-device" => settings.AnnounceAudioDevice,
            _ => true
        };
    }

    public static bool ShouldSpeak(AccessibleAnnouncement announcement, AccessibilitySettings settings)
        => ShouldSpeak(announcement) && IsScopeAllowed(announcement, settings);
}
