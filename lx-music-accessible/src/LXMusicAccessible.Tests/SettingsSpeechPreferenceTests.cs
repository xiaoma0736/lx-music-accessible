using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>
/// The 无障碍 page is the only place a reader user can tune the voice that speaks the rest of the
/// application. Every control there has to do three things: show the value that is actually stored,
/// keep that value when the window is opened, and hand it to the speech backend that makes the sound.
/// A row that only displays a friendly label, or a preference that nothing ever reads, is a control
/// that reads as working and does nothing.
/// </summary>
public sealed class SettingsSpeechPreferenceTests
{
    [Fact]
    public void Quality_combos_show_the_catalog_labels()
    {
        WinFormsTestRunner.Run(() =>
        {
            var settings = LoadSettings(new UserSettings());
            using var form = new SettingsForm(
                new RecordingAnnouncer(), settings, new ListVoicesTts(), new IdleDownloads(), _ => false);
            form.Show();
            Application.DoEvents();

            var expected = new[] { "标准", "较高", "极高", "无损 FLAC", "Hi-Res" };
            var play = Assert.IsType<ComboBox>(form.Controls.Find("默认播放音质", true).Single());
            var download = Assert.IsType<ComboBox>(form.Controls.Find("默认下载音质", true).Single());

            Assert.Equal(expected, play.Items.Cast<object>().Select(item => item.ToString()).ToArray());
            Assert.Equal(expected, download.Items.Cast<object>().Select(item => item.ToString()).ToArray());
        });
    }

    [Fact]
    public void Voice_row_matches_the_stored_identifier_and_survives_reopening_settings()
    {
        WinFormsTestRunner.Run(() =>
        {
            var stored = new UserSettings();
            stored.AccessibilitySettings.AnnouncementVoiceId = "sapi:XiaoYi";
            stored.AccessibilitySettings.AnnouncementRate = 1.25;
            var settings = LoadSettings(stored);
            var tts = new ListVoicesTts();

            using (var form = new SettingsForm(new RecordingAnnouncer(), settings, tts, new IdleDownloads(), _ => false))
            {
                form.Show();
                Application.DoEvents();

                Assert.Equal(2, VoiceBox(form).SelectedIndex);
                Assert.Equal("1.25", RateBox(form).Text);
                Assert.Equal("sapi:XiaoYi", settings.Current.Settings.AccessibilitySettings.AnnouncementVoiceId);
            }

            // Reading the value back is the only verification a screen reader user has, so a second
            // window must select the same row instead of falling back to the default voice.
            using (var again = new SettingsForm(new RecordingAnnouncer(), settings, tts, new IdleDownloads(), _ => false))
            {
                again.Show();
                Application.DoEvents();
                Assert.Equal(2, VoiceBox(again).SelectedIndex);
            }

            Assert.Equal("sapi:XiaoYi", settings.Current.Settings.AccessibilitySettings.AnnouncementVoiceId);
        });
    }

    [Theory]
    [InlineData(PlayMode.Sequential, 0, "\u987a\u5e8f\u64ad\u653e")]
    [InlineData(PlayMode.Loop, 1, "\u5217\u8868\u5faa\u73af")]
    [InlineData(PlayMode.SingleLoop, 2, "\u5355\u66f2\u5faa\u73af")]
    [InlineData(PlayMode.Shuffle, 3, "\u968f\u673a\u64ad\u653e")]
    public void Playback_mode_rows_map_the_stored_mode_to_the_documented_label(
        PlayMode savedMode,
        int expectedIndex,
        string expectedLabel)
    {
        WinFormsTestRunner.Run(() =>
        {
            var settings = LoadSettings(new UserSettings { DefaultPlayMode = savedMode });
            using var form = new SettingsForm(new RecordingAnnouncer(), settings, new ListVoicesTts(), new IdleDownloads(), _ => false);
            form.Show();
            Application.DoEvents();

            var playMode = Assert.IsType<ComboBox>(form.Controls.Find("\u9ed8\u8ba4\u64ad\u653e\u6a21\u5f0f", true).Single());
            Assert.Equal(new[]
            {
                "\u987a\u5e8f\u64ad\u653e",
                "\u5217\u8868\u5faa\u73af",
                "\u5355\u66f2\u5faa\u73af",
                "\u968f\u673a\u64ad\u653e"
            }, playMode.Items.Cast<string>().ToArray());
            Assert.Equal(expectedIndex, playMode.SelectedIndex);
            Assert.Equal(expectedLabel, playMode.Text);
        });
    }

    [Theory]
    [InlineData(0, PlayMode.Sequential)]
    [InlineData(1, PlayMode.Loop)]
    [InlineData(2, PlayMode.SingleLoop)]
    [InlineData(3, PlayMode.Shuffle)]
    public void Playback_mode_selections_store_the_label_documented_to_the_user(
        int selectedIndex,
        PlayMode expectedMode)
    {
        WinFormsTestRunner.Run(() =>
        {
            var settings = LoadSettings(new UserSettings());
            using var form = new SettingsForm(new RecordingAnnouncer(), settings, new ListVoicesTts(), new IdleDownloads(), _ => false);
            form.Show();
            Application.DoEvents();

            var playMode = Assert.IsType<ComboBox>(form.Controls.Find("\u9ed8\u8ba4\u64ad\u653e\u6a21\u5f0f", true).Single());
            playMode.SelectedIndex = selectedIndex;
            Application.DoEvents();

            Assert.Equal(expectedMode, settings.Current.Settings.DefaultPlayMode);
        });
    }

    [Fact]
    public void Changing_the_voice_stores_the_identifier_the_backend_can_resolve()
    {
        WinFormsTestRunner.Run(() =>
        {
            var settings = LoadSettings(new UserSettings());
            using var form = new SettingsForm(new RecordingAnnouncer(), settings, new ListVoicesTts(), new IdleDownloads(), _ => false);
            form.Show();
            Application.DoEvents();

            var voice = VoiceBox(form);
            Assert.Equal(0, voice.SelectedIndex);

            voice.SelectedIndex = 1;
            Application.DoEvents();

            var stored = settings.Current.Settings.AccessibilitySettings.AnnouncementVoiceId;
            Assert.Equal("sapi:Huihui", stored);
            Assert.DoesNotContain("（", stored, StringComparison.Ordinal);
            Assert.Equal("Huihui（zh-CN）", voice.Text);
        });
    }

    [Fact]
    public void Scope_checkboxes_are_accessible_and_save_each_speech_range()
    {
        WinFormsTestRunner.Run(() =>
        {
            var settings = LoadSettings(new UserSettings());
            using var form = new SettingsForm(new RecordingAnnouncer(), settings, new ListVoicesTts(), new IdleDownloads(), _ => false);
            form.Show();
            Application.DoEvents();

            var cases = new (string Name, string Property)[]
            {
                ("AnnouncePlaybackState", nameof(AccessibilitySettings.AnnouncePlaybackState)),
                ("AnnounceTrackChange", nameof(AccessibilitySettings.AnnounceTrackChange)),
                ("AnnounceVolume", nameof(AccessibilitySettings.AnnounceVolume)),
                ("AnnounceSeek", nameof(AccessibilitySettings.AnnounceSeek)),
                ("AnnounceWindowVisibility", nameof(AccessibilitySettings.AnnounceWindowVisibility)),
                ("AnnouncePlaybackMode", nameof(AccessibilitySettings.AnnouncePlaybackMode)),
                ("AnnounceAudioDevice", nameof(AccessibilitySettings.AnnounceAudioDevice))
            };

            foreach (var (name, property) in cases)
            {
                var checkBox = Assert.IsType<CheckBox>(form.Controls.Find(name, true).Single());
                Assert.False(string.IsNullOrWhiteSpace(checkBox.AccessibleName));
                Assert.True(checkBox.Checked);
                checkBox.Checked = false;
                Application.DoEvents();
                Assert.False((bool)typeof(AccessibilitySettings).GetProperty(property)!.GetValue(settings.Current.Settings.AccessibilitySettings)!);
            }
        });
    }

    [Fact]
    public void Stored_voice_and_rate_reach_the_speech_backend_at_startup_and_on_every_change()
    {
        var stored = new UserSettings();
        stored.AccessibilitySettings.AnnouncementRate = 1.5;
        stored.AccessibilitySettings.AnnouncementVoiceId = "sapi:Huihui";
        var settings = LoadSettings(stored);
        var tts = new ListVoicesTts();

        using var shell = new WinFormsShell(new WinFormsShellServices
        {
            Announcer = new RecordingAnnouncer(),
            BrowseSource = new FakeBrowseSource(),
            Playback = new RecordingPlayback(),
            Settings = settings,
            Tts = tts,
            TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-speech-prefs-" + Guid.NewGuid().ToString("N"))
        });

        Assert.Equal(new[] { "sapi:Huihui" }, tts.Voices.ToArray());
        Assert.Equal(new[] { 1.5 }, tts.Rates.ToArray());

        Assert.True(settings.Edit(candidate => candidate.AccessibilitySettings.AnnouncementVoiceId = "prism:3").Accepted);
        Assert.Equal("prism:3", tts.Voices.Last());
    }

    [Fact]
    public void Tray_menu_entries_expose_pressable_access_keys_and_their_commands()
    {
        var requested = new List<DesktopCommand>();
        var menu = WinFormsDesktopIntegration.BuildTrayMenu(requested.Add);
        var entries = menu.MenuItems.Cast<MenuItem>().Where(entry => entry.Text != "-").ToArray();

        Assert.Equal(5, entries.Length);
        Assert.All(entries, entry =>
        {
            Assert.NotEqual('\0', entry.Mnemonic);
            Assert.True(char.IsLetterOrDigit(entry.Mnemonic), "Every tray command needs a printable access key: " + entry.Text);
            Assert.Equal(Shortcut.None, entry.Shortcut);
        });
        Assert.Equal(entries.Length, entries.Select(entry => entry.Mnemonic).Distinct().Count());

        foreach (var entry in entries) entry.PerformClick();
        Assert.Equal(new[]
        {
            DesktopCommand.Restore,
            DesktopCommand.TogglePlayback,
            DesktopCommand.Previous,
            DesktopCommand.Next,
            DesktopCommand.Exit
        }, requested.ToArray());
    }

    /// <summary>
    /// Enabling and disabling a task command by matching its caption left the command stuck in its last
    /// state as soon as the text was reworded. The state now follows the button the window actually owns,
    /// so a reworded caption cannot make 取消 or 重试 unreachable.
    /// </summary>
    [Fact]
    public void Download_task_commands_follow_the_selection_not_the_button_caption()
    {
        WinFormsTestRunner.Run(() =>
        {
            var workflow = new IdleDownloads();
            workflow.Add(new DownloadTask
            {
                Id = "running",
                Music = new MusicInfo { Artist = "A", Title = "Running" },
                Status = DownloadTaskStatus.Downloading,
                Progress = 40
            });
            using var form = new DownloadManagerForm(new RecordingAnnouncer(), workflow);
            form.Show();
            Application.DoEvents();

            var cancel = (Button)form.Controls.Find("取消", true).Single();
            Assert.False(cancel.Enabled, "取消 must stay disabled while no task is selected.");

            var tasks = (ListView)form.Controls.Find("DownloadTasks", true).Single();
            tasks.Items[0].Selected = true;
            Assert.True(cancel.Enabled, "取消 must enable for a selected downloading task.");

            cancel.Text = "取消下载任务";
            tasks.Items[0].Selected = false;
            RefreshButtonState(form);

            Assert.False(cancel.Enabled, "取消 must disable again once no selected task is running, whatever its caption says.");
            Assert.Equal("取消", cancel.Name);
        });
    }

    private static SettingsWorkflow LoadSettings(UserSettings stored)
    {
        var workflow = new SettingsWorkflow(
            new StoredSettings(stored),
            new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, new AlwaysRegisters()),
            NullLogger<SettingsWorkflow>.Instance);
        workflow.LoadAsync(IntPtr.Zero).GetAwaiter().GetResult();
        return workflow;
    }

    private static ComboBox VoiceBox(Form form) => Assert.IsType<ComboBox>(form.Controls.Find("朗读声音", true).Single());

    private static ComboBox RateBox(Form form) => Assert.IsType<ComboBox>(form.Controls.Find("朗读语速", true).Single());

    private static void RefreshButtonState(Form form)
    {
        var method = typeof(DownloadManagerForm).GetMethod("UpdateButtonState", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(form, Array.Empty<object?>());
    }

    private sealed class StoredSettings : ISettingsStore
    {
        private readonly UserSettings _stored;

        public StoredSettings(UserSettings stored) => _stored = stored;

        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<UserSettings?>(_stored);

        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AlwaysRegisters : IGlobalHotkeyAdapter
    {
        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers) => true;
        public bool Unregister(IntPtr windowHandle, int id) => true;
        public int LastRegisterError { get; }
    }

    private sealed class ListVoicesTts : ITtsService
    {
        public List<double> Rates { get; } = new();
        public List<string> Voices { get; } = new();
        public bool IsAvailable => true;
        public string ActiveBackendName => "Test voice list";
        public Task SpeakAsync(string text) => Task.CompletedTask;
        public Task SpeakQueuedAsync(string text) => Task.CompletedTask;
        public void Stop() { }
        public void SetRate(double rate) => Rates.Add(rate);
        public void SetVoice(string voiceId) => Voices.Add(voiceId);

        public List<VoiceInfo> GetAvailableVoices() => new()
        {
            new VoiceInfo { Id = "sapi:Huihui", Name = "Huihui", Culture = "zh-CN" },
            new VoiceInfo { Id = "sapi:XiaoYi", Name = "XiaoYi", Culture = "zh-CN" }
        };
    }

    private sealed class IdleDownloads : IDownloadWorkflow
    {
        private readonly ObservableCollection<DownloadTask> _tasks = new();

        public ReadOnlyObservableCollection<DownloadTask> Tasks { get; }

        public IdleDownloads() => Tasks = new ReadOnlyObservableCollection<DownloadTask>(_tasks);

        public void Add(DownloadTask task) => _tasks.Add(task);
        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new DownloadTask());
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new DownloadListResult(0, 0, 0, 0, 0, new List<DownloadTask>(), "ok"));
        public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
        public Task<bool> RedownloadAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void Cancel(DownloadTask task) { }
    public void Pause(DownloadTask task) { }
    public void Resume(DownloadTask task) { }
    public bool Delete(DownloadTask task, bool deleteSourceFile) => true;
    public Task<bool> DeleteAsync(DownloadTask task, bool deleteSourceFile) => Task.FromResult(Delete(task, deleteSourceFile));
        public void ClearCompleted() { }
        public string ResolveDownloadDirectory(string? requested = null) => requested ?? Path.GetTempPath();
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default)
            => Task.FromResult<ExternalPlaybackTarget?>(null);
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExternalPlaybackResult(false, "not supported"));
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }
}