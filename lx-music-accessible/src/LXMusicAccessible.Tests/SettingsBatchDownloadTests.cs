using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using RecordingAnnouncer = LXMusicAccessible.Tests.Infrastructure.RecordingAnnouncer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class SettingsBatchDownloadTests
{
    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(61, 0, 60, 0)]
    [InlineData(2, -1, 2, 0)]
    [InlineData(2, 31, 2, 32)]
    public async Task Load_clamps_out_of_range_batch_download_values(
        int storedDelay,
        int storedSpeed,
        int expectedDelay,
        int expectedSpeed)
    {
        var store = new RecordingSettingsStore
        {
            Loaded = new UserSettings
            {
                BatchDownloadDelaySeconds = storedDelay,
                DownloadSpeedLimitKbps = storedSpeed
            }
        };
        var workflow = CreateWorkflow(store, new RecordingHotkeyAdapter());

        var loaded = await workflow.LoadAsync(IntPtr.Zero);

        Assert.Equal(expectedDelay, loaded.Settings.BatchDownloadDelaySeconds);
        Assert.Equal(expectedSpeed, loaded.Settings.DownloadSpeedLimitKbps);
    }

    [Fact]
    public async Task Batch_download_preferences_round_trip()
    {
        var store = new RecordingSettingsStore();
        var workflow = CreateWorkflow(store, new RecordingHotkeyAdapter());
        await workflow.LoadAsync(IntPtr.Zero);

        var edited = workflow.Edit(settings =>
        {
            settings.BatchDownloadDelaySeconds = 5;
            settings.DownloadSpeedLimitKbps = 128;
        });
        Assert.True(edited.Accepted);
        Assert.Equal(5, edited.State.Settings.BatchDownloadDelaySeconds);
        Assert.Equal(128, edited.State.Settings.DownloadSpeedLimitKbps);

        Assert.True((await workflow.SaveAsync(edited.State)).Succeeded);
        Assert.Single(store.Saved);
        Assert.Equal(5, store.Saved.Single().BatchDownloadDelaySeconds);
        Assert.Equal(128, store.Saved.Single().DownloadSpeedLimitKbps);

        var restarted = CreateWorkflow(
            new RecordingSettingsStore { Loaded = store.Saved.Single() },
            new RecordingHotkeyAdapter());
        var settings = (await restarted.LoadAsync(IntPtr.Zero)).Settings;

        Assert.Equal(5, settings.BatchDownloadDelaySeconds);
        Assert.Equal(128, settings.DownloadSpeedLimitKbps);
    }

    [Fact]
    public async Task Settings_controls_apply_and_persist_batch_download_throttles_immediately()
    {
        var store = new RecordingSettingsStore();
        var workflow = CreateWorkflow(store, new RecordingHotkeyAdapter());
        await workflow.LoadAsync(IntPtr.Zero);
        var announcer = new RecordingAnnouncer();
        using var form = new SettingsForm(
            announcer,
            workflow,
            new RecordingTtsAdapter(),
            new FakeDownloadWorkflow(),
            _ => false);
        form.Show();
        Application.DoEvents();

        var delay = Assert.IsType<NumericUpDown>(Required(form, "批量下载间隔"));
        var speed = Assert.IsType<NumericUpDown>(Required(form, "下载限速"));
        Assert.Equal(2, delay.Value);
        Assert.Equal(0, speed.Value);
        Assert.Equal("批量下载间隔（秒）", delay.AccessibleName);
        Assert.Equal("下载限速（KB/s，0 表示不限速）", speed.AccessibleName);
        Assert.True(delay.TabIndex < speed.TabIndex);

        var downloadPage = delay.Parent!;
        var delayCaption = Assert.Single(downloadPage.Controls.OfType<Label>(), label => label.Text == "批量下载间隔：");
        var speedCaption = Assert.Single(downloadPage.Controls.OfType<Label>(), label => label.Text == "下载限速：");
        Assert.Equal("批量下载间隔：", delayCaption.Text);
        Assert.Equal("下载限速：", speedCaption.Text);
        Assert.True(delayCaption.Bounds.Y < delay.Bounds.Y);
        Assert.True(speedCaption.Bounds.Y < speed.Bounds.Y);

        delay.Value = 5;
        Assert.Equal(5, workflow.Current.Settings.BatchDownloadDelaySeconds);
        speed.Value = 1;
        Assert.Equal(32, workflow.Current.Settings.DownloadSpeedLimitKbps);
        speed.Value = 128;
        Assert.Equal(128, workflow.Current.Settings.DownloadSpeedLimitKbps);

        Assert.Contains(announcer.Announcements, announcement => announcement.Text == "批量下载间隔已设置为 5 秒");
        Assert.Contains(announcer.Announcements, announcement => announcement.Text == "下载限速已设置为 32 KB/s");
        Assert.Contains(announcer.Announcements, announcement => announcement.Text == "下载限速已设置为 128 KB/s");

        form.Close();
        await WaitForSaveAsync(store);
        var saved = Assert.Single(store.Saved);
        Assert.Equal(5, saved.BatchDownloadDelaySeconds);
        Assert.Equal(128, saved.DownloadSpeedLimitKbps);
    }

    [Fact]
    public async Task Settings_controls_reload_external_batch_download_changes_without_recursive_announcements()
    {
        var store = new RecordingSettingsStore();
        var workflow = CreateWorkflow(store, new RecordingHotkeyAdapter());
        await workflow.LoadAsync(IntPtr.Zero);
        var announcer = new RecordingAnnouncer();
        using var form = new SettingsForm(
            announcer,
            workflow,
            new RecordingTtsAdapter(),
            new FakeDownloadWorkflow(),
            _ => false);
        form.Show();
        Application.DoEvents();
        announcer.Announcements.Clear();

        workflow.Edit(settings =>
        {
            settings.BatchDownloadDelaySeconds = 9;
            settings.DownloadSpeedLimitKbps = 64;
        });
        Application.DoEvents();

        var delay = Assert.IsType<NumericUpDown>(Required(form, "批量下载间隔"));
        var speed = Assert.IsType<NumericUpDown>(Required(form, "下载限速"));
        Assert.Equal(9, delay.Value);
        Assert.Equal(64, speed.Value);
        Assert.Empty(announcer.Announcements);
    }

    [Fact]
    public void Settings_control_loads_clamp_out_of_range_batch_download_values()
    {
        WinFormsTestRunner.Run(() =>
        {
            var workflow = CreateWorkflow(new RecordingSettingsStore(), new RecordingHotkeyAdapter());
            using var form = new SettingsForm(
                new RecordingAnnouncer(),
                workflow,
                new RecordingTtsAdapter(),
                new FakeDownloadWorkflow(),
                _ => false);
            var delay = Assert.IsType<NumericUpDown>(Required(form, "批量下载间隔"));
            var speed = Assert.IsType<NumericUpDown>(Required(form, "下载限速"));
            var loading = typeof(SettingsForm).GetField("_loading", BindingFlags.NonPublic | BindingFlags.Instance);
            var load = typeof(SettingsForm).GetMethod("LoadControls", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(loading);
            Assert.NotNull(load);

            loading!.SetValue(form, true);
            load!.Invoke(form, new object?[]
            {
                new UserSettings
                {
                    BatchDownloadDelaySeconds = -5,
                    DownloadSpeedLimitKbps = 123456
                }
            });
            loading.SetValue(form, false);
            Application.DoEvents();

            Assert.Equal(0, delay.Value);
            Assert.Equal(100000, speed.Value);
        });
    }

    private static Control Required(Control root, string name)
    {
        var matches = root.Controls.Find(name, true);
        Assert.True(matches.Length == 1, $"{name} count={matches.Length}");
        return matches[0];
    }

    private static async Task WaitForSaveAsync(RecordingSettingsStore store)
    {
        while (store.Saved.Count == 0) await Task.Delay(10);
    }

    private static SettingsWorkflow CreateWorkflow(
        ISettingsStore store,
        IGlobalHotkeyAdapter native) =>
        new(
            store,
            new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, native),
            NullLogger<SettingsWorkflow>.Instance);

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public UserSettings? Loaded { get; set; }
        public List<UserSettings> Saved { get; } = new();

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
        public Dictionary<int, string> Registered { get; } = new();
        public List<string> Operations { get; } = new();

        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers)
        {
            Operations.Add($"register:{id}:{key}:{modifiers}");
            Registered[id] = $"{key}|{modifiers}";
            return true;
        }

        public bool Unregister(IntPtr windowHandle, int id)
        {
            Operations.Add($"unregister:{id}");
            return Registered.Remove(id);
        }
    }
}
