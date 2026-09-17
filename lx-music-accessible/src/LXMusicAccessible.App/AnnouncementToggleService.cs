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
        var edit = _settingsWorkflow.Edit(settings => settings.EnableVoiceAnnouncement = enabled);
        _announcer.Announce(new(
            enabled ? "已开启语音朗读提示" : "已关闭语音朗读提示",
            AnnouncementKind.Status,
            "settings",
            AnnouncementSpeech.Always));
        _ = SaveAsync(edit);
    }

    public void ToggleLyricAnnouncement()
    {
        var enabled = !_settingsWorkflow.Current.Settings.LyricAutoAnnounce;
        var edit = _settingsWorkflow.Edit(settings => settings.LyricAutoAnnounce = enabled);
        _announcer.Announce(new(
            enabled ? "已开启语音朗读歌词" : "已关闭语音朗读歌词",
            AnnouncementKind.Status,
            "settings",
            AnnouncementSpeech.FollowPreference));
        _ = SaveAsync(edit);
    }

    /// <summary>
    /// Writes the toggle immediately so the change survives a crash, and reports a failed write
    /// instead of leaving the user believing the setting was stored. The next exit attempt retries.
    /// </summary>
    private async Task SaveAsync(SettingsEditResult edit)
    {
        if (!edit.Accepted) return;
        var saved = false;
        try
        {
            saved = (await _settingsWorkflow.SaveAsync(edit.State)).Succeeded;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine("Settings toggle persistence failed: " + exception.GetType().Name);
        }
        if (saved) return;
        _announcer.Announce(new AccessibleAnnouncement(
            "设置保存失败，将在退出时重试。",
            AnnouncementKind.Error,
            "settings",
            AnnouncementSpeech.Always));
    }
}
