using System.IO;
using System.Windows.Input;
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
        Assert.Equal(Key.F5, voice.Key);
        Assert.Equal(ModifierKeys.Shift, voice.Modifiers);

        Assert.False(lyrics.IsGlobal);
        Assert.True(lyrics.IsEnabled);
        Assert.Equal(Key.F6, lyrics.Key);
        Assert.Equal(ModifierKeys.Shift, lyrics.Modifiers);
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
                    Key = Key.F5,
                    Modifiers = ModifierKeys.Shift,
                    IsGlobal = false
                },
                new Hotkey
                {
                    ActionId = HotkeyActions.Stop,
                    Name = "停止",
                    Key = Key.F6,
                    Modifiers = ModifierKeys.Shift,
                    IsGlobal = false
                }
            ]
        });

        var voice = Hotkey(loaded, HotkeyActions.ToggleVoiceAnnouncement);
        var lyrics = Hotkey(loaded, HotkeyActions.ToggleLyricAnnouncement);
        Assert.Equal(Key.None, voice.Key);
        Assert.Equal(ModifierKeys.None, voice.Modifiers);
        Assert.False(voice.IsEnabled);
        Assert.Equal(Key.None, lyrics.Key);
        Assert.Equal(ModifierKeys.None, lyrics.Modifiers);
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
            Key.F5,
            ModifierKeys.Control | ModifierKeys.Alt);
        var lyricResult = await workflow.ReplaceHotkeyAsync(
            HotkeyActions.ToggleLyricAnnouncement,
            isGlobal: false,
            Key.F6,
            ModifierKeys.Control | ModifierKeys.Alt);
        Assert.Equal(HotkeyEditStatus.Applied, voiceResult.Status);
        Assert.Equal(HotkeyEditStatus.Applied, lyricResult.Status);
        Assert.True((await workflow.SaveAsync(workflow.Current)).Succeeded);

        var restarted = await LoadAsync(store.Saved[^1]);
        var restoredVoice = Hotkey(restarted, HotkeyActions.ToggleVoiceAnnouncement);
        var restoredLyrics = Hotkey(restarted, HotkeyActions.ToggleLyricAnnouncement);
        Assert.Equal(Key.F5, restoredVoice.Key);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, restoredVoice.Modifiers);
        Assert.Equal(Key.F6, restoredLyrics.Key);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, restoredLyrics.Modifiers);

        var mainCode = await File.ReadAllTextAsync(FindRepositoryFile(
            Path.Combine("src", "LXMusicAccessible.App", "MainWindow.xaml.cs")));
        Assert.Contains("case HotkeyActions.ToggleVoiceAnnouncement:", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.EnableVoiceAnnouncement = !settings.EnableVoiceAnnouncement", mainCode, StringComparison.Ordinal);
        Assert.Contains("case HotkeyActions.ToggleLyricAnnouncement:", mainCode, StringComparison.Ordinal);
        Assert.Contains("settings.LyricAutoAnnounce = !settings.LyricAutoAnnounce", mainCode, StringComparison.Ordinal);
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

    private static string FindRepositoryFile(string relativeProbe)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativeProbe);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository file was not found.");
    }

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
        public bool Register(IntPtr windowHandle, int id, Key key, ModifierKeys modifiers) => true;
        public bool Unregister(IntPtr windowHandle, int id) => true;
    }
}
