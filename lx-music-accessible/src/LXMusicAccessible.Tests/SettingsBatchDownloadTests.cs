using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

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
