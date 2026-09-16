
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class Issue49HotkeyTests
{
    [Fact]
    public async Task Voice_announcement_hotkeys_have_local_shift_f5_and_f6_defaults()
    {
        Assert.Contains(HotkeyActions.ToggleVoiceAnnouncement, HotkeyActions.All);
        Assert.Contains(HotkeyActions.ToggleLyricAnnouncement, HotkeyActions.All);

        var loaded = await LoadAsync(new UserSettings());
        var voice = Hotkey(loaded, HotkeyActions.ToggleVoiceAnnouncement);
        var lyrics = Hotkey(loaded, HotkeyActions.ToggleLyricAnnouncement);

        Assert.False(voice.IsGlobal);
        Assert.True(voice.IsEnabled);
        Assert.Equal(PlatformKey.F5, voice.Key);
        Assert.Equal(PlatformModifiers.Shift, voice.Modifiers);

        Assert.False(lyrics.IsGlobal);
        Assert.True(lyrics.IsEnabled);
        Assert.Equal(PlatformKey.F6, lyrics.Key);
        Assert.Equal(PlatformModifiers.Shift, lyrics.Modifiers);
    }

    [Fact]
    public async Task Existing_user_bindings_keep_precedence_and_conflicting_new_defaults_stay_unbound()
    {
        var loaded = await LoadAsync(new UserSettings
        {
            CustomHotkeys =
            [
                new Hotkey
                {
                    ActionId = HotkeyActions.PlayPause,
                    Name = "播放/暂停",
                    Key = PlatformKey.F5,
                    Modifiers = PlatformModifiers.Shift,
                    IsGlobal = false
                },
                new Hotkey
                {
                    ActionId = HotkeyActions.Stop,
                    Name = "停止",
                    Key = PlatformKey.F6,
                    Modifiers = PlatformModifiers.Shift,
                    IsGlobal = false
                }
            ]
        });

        var voice = Hotkey(loaded, HotkeyActions.ToggleVoiceAnnouncement);
        var lyrics = Hotkey(loaded, HotkeyActions.ToggleLyricAnnouncement);
        Assert.Equal(PlatformKey.None, voice.Key);
        Assert.Equal(PlatformModifiers.None, voice.Modifiers);
        Assert.False(voice.IsEnabled);
        Assert.Equal(PlatformKey.None, lyrics.Key);
        Assert.Equal(PlatformModifiers.None, lyrics.Modifiers);
        Assert.False(lyrics.IsEnabled);
    }

    [Fact]
    public async Task Changed_voice_and_lyric_shortcuts_dispatch_and_persist()
    {
        var store = new RecordingSettingsStore();
        var workflow = CreateWorkflow(store);
        await workflow.LoadAsync(IntPtr.Zero);

        var voiceResult = await workflow.ReplaceHotkeyAsync(
            HotkeyActions.ToggleVoiceAnnouncement,
            isGlobal: false,
            PlatformKey.F5,
            PlatformModifiers.Control | PlatformModifiers.Alt);
        var lyricResult = await workflow.ReplaceHotkeyAsync(
            HotkeyActions.ToggleLyricAnnouncement,
            isGlobal: false,
            PlatformKey.F6,
            PlatformModifiers.Control | PlatformModifiers.Alt);
        Assert.Equal(HotkeyEditStatus.Applied, voiceResult.Status);
        Assert.Equal(HotkeyEditStatus.Applied, lyricResult.Status);
        Assert.True((await workflow.SaveAsync(workflow.Current)).Succeeded);

        var restarted = await LoadAsync(store.Saved[^1]);
        var restoredVoice = Hotkey(restarted, HotkeyActions.ToggleVoiceAnnouncement);
        var restoredLyrics = Hotkey(restarted, HotkeyActions.ToggleLyricAnnouncement);
        Assert.Equal(PlatformKey.F5, restoredVoice.Key);
        Assert.Equal(PlatformModifiers.Control | PlatformModifiers.Alt, restoredVoice.Modifiers);
        Assert.Equal(PlatformKey.F6, restoredLyrics.Key);
        Assert.Equal(PlatformModifiers.Control | PlatformModifiers.Alt, restoredLyrics.Modifiers);

    }

    [Fact]
    public async Task Announcement_toggle_actions_update_settings_and_dispatch_accessible_speech()
    {
        var workflow = CreateWorkflow(new RecordingSettingsStore());
        await workflow.LoadAsync(IntPtr.Zero);
        var announcer = new RecordingAnnouncer();
        var service = new AnnouncementToggleService(announcer, workflow);

        service.ToggleVoiceAnnouncement();

        Assert.False(workflow.Current.Settings.EnableVoiceAnnouncement);
        var voiceOff = Assert.Single(announcer.Announcements);
        Assert.Equal("已关闭语音朗读提示", voiceOff.Text);
        Assert.Equal("settings", voiceOff.ActivityId);
        Assert.Equal(AnnouncementSpeech.Always, voiceOff.Speech);

        service.ToggleVoiceAnnouncement();
        Assert.True(workflow.Current.Settings.EnableVoiceAnnouncement);
        Assert.Equal(2, announcer.Announcements.Count);
        var voiceOn = announcer.Announcements[^1];
        Assert.Equal("已开启语音朗读提示", voiceOn.Text);
        Assert.Equal("settings", voiceOn.ActivityId);
        Assert.Equal(AnnouncementSpeech.Always, voiceOn.Speech);

        service.ToggleLyricAnnouncement();
        Assert.False(workflow.Current.Settings.LyricAutoAnnounce);
        var lyricOff = announcer.Announcements[^1];
        Assert.Equal("已关闭语音朗读歌词", lyricOff.Text);
        Assert.Equal("settings", lyricOff.ActivityId);
    }

    private static Hotkey Hotkey(UserSettings settings, string actionId) => Assert.Single(
        settings.CustomHotkeys,
        item => item.ActionId == actionId && !item.IsGlobal);

    private static async Task<UserSettings> LoadAsync(UserSettings saved)
    {
        var workflow = CreateWorkflow(new RecordingSettingsStore { Loaded = saved });
        return (await workflow.LoadAsync(IntPtr.Zero)).Settings;
    }

    private static SettingsWorkflow CreateWorkflow(ISettingsStore store) => new(
        store,
        new HotkeyManagerService(
            NullLogger<HotkeyManagerService>.Instance,
            new RecordingHotkeyAdapter()),
        NullLogger<SettingsWorkflow>.Instance);

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public UserSettings? Loaded { get; init; }
        public List<UserSettings> Saved { get; } = [];

        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Loaded);

        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken)
        {
            Saved.Add(settings);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHotkeyAdapter : IGlobalHotkeyAdapter
    {
        public int LastRegisterError { get; private set; }
        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers) => true;
        public bool Unregister(IntPtr windowHandle, int id) => true;
    }

    private sealed class RecordingAnnouncer : IAccessibleAnnouncer
    {
        public bool SpeechEnabled { get; set; }
        public List<AccessibleAnnouncement> Announcements { get; } = [];
        public void Announce(AccessibleAnnouncement announcement) => Announcements.Add(announcement);
    }
}
