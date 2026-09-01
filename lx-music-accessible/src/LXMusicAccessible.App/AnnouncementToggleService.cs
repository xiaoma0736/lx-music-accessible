using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>Applies local announcement toggles and emits state-specific accessible feedback.</summary>
internal sealed class AnnouncementToggleService(
    IAccessibleAnnouncer announcer,
    SettingsWorkflow settingsWorkflow)
{
    private readonly IAccessibleAnnouncer _announcer = announcer ?? throw new ArgumentNullException(nameof(announcer));
    private readonly SettingsWorkflow _settingsWorkflow = settingsWorkflow ?? throw new ArgumentNullException(nameof(settingsWorkflow));

    public void ToggleVoiceAnnouncement()
    {
        var enabled = !_settingsWorkflow.Current.Settings.EnableVoiceAnnouncement;
        _settingsWorkflow.Edit(settings => settings.EnableVoiceAnnouncement = enabled);
        _announcer.Announce(new(
            enabled ? "已开启语音朗读提示" : "已关闭语音朗读提示",
            AnnouncementKind.Status,
            "settings",
            enabled ? AnnouncementSpeech.FollowPreference : AnnouncementSpeech.Always));
    }

    public void ToggleLyricAnnouncement()
    {
        var enabled = !_settingsWorkflow.Current.Settings.LyricAutoAnnounce;
        _settingsWorkflow.Edit(settings => settings.LyricAutoAnnounce = enabled);
        _announcer.Announce(new(
            enabled ? "已开启语音朗读歌词" : "已关闭语音朗读歌词",
            AnnouncementKind.Status,
            "settings",
            AnnouncementSpeech.FollowPreference));
    }
}