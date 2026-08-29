using System.IO;
using System.Text.Json;
using System.Windows.Input;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class SettingsWorkflowTests
{
    [Fact]
    public async Task Defaults_are_complete_and_edit_state_is_immutable_and_validated()
    {
        var store = new RecordingSettingsStore();
        var workflow = CreateWorkflow(store, new RecordingHotkeyAdapter());

        var loaded = await workflow.LoadAsync(IntPtr.Zero);
        var firstCopy = loaded.Settings;
        firstCopy.DefaultVolume = 0.1;

        Assert.Equal(0.8, loaded.Settings.DefaultVolume);
        Assert.Equal(29, loaded.Settings.CustomHotkeys.Count);
        Assert.Equal(15, loaded.Settings.CustomHotkeys.Count(item => item.IsGlobal));
        Assert.Equal(14, loaded.Settings.CustomHotkeys.Count(item => !item.IsGlobal));

        var invalid = workflow.Edit(settings => settings.DownloadPath = "relative\\path");
        Assert.False(invalid.Accepted);
        Assert.Contains(invalid.Errors, error => error.Contains("绝对路径", StringComparison.Ordinal));
        Assert.Equal(string.Empty, workflow.Current.Settings.DownloadPath);

        var invalidPlayer = workflow.Edit(settings => settings.ThirdPartyPlayerPath = "player.exe");
        Assert.False(invalidPlayer.Accepted);
        Assert.Contains(invalidPlayer.Errors, error => error.Contains("播放器路径", StringComparison.Ordinal));
        Assert.Equal(string.Empty, workflow.Current.Settings.ThirdPartyPlayerPath);

        var changed = workflow.Edit(settings =>
        {
            settings.DefaultVolume = 0.42;
            settings.DefaultPlayQuality = "320k";
        });
        Assert.True(changed.Accepted);
        Assert.True(changed.State.Version > loaded.Version);
        Assert.Equal(0.42, changed.State.Settings.DefaultVolume);
        Assert.Equal("320k", changed.State.Settings.DefaultPlayQuality);
    }

    [Fact]
    public async Task Flac24bit_and_default_download_directory_survive_unrelated_edits_and_save()
    {
        var store = new RecordingSettingsStore
        {
            Loaded = new UserSettings
            {
                DefaultPlayQuality = "flac24bit",
                DefaultDownloadQuality = "flac24bit",
                DownloadPath = string.Empty
            }
        };
        var workflow = CreateWorkflow(store, new RecordingHotkeyAdapter());

        await workflow.LoadAsync(IntPtr.Zero);
        var edited = workflow.Edit(settings => settings.LyricAutoScroll = false);
        await workflow.SaveAsync(edited.State);

        Assert.Equal("flac24bit", edited.State.Settings.DefaultPlayQuality);
        Assert.Equal("flac24bit", edited.State.Settings.DefaultDownloadQuality);
        Assert.Equal(string.Empty, edited.State.Settings.DownloadPath);
        Assert.Equal("flac24bit", store.Saved.Single().DefaultPlayQuality);
        Assert.Equal("flac24bit", store.Saved.Single().DefaultDownloadQuality);
        Assert.Equal(string.Empty, store.Saved.Single().DownloadPath);
    }

    [Theory]
    [InlineData(false, false, PlayMode.Sequential)]
    [InlineData(false, true, PlayMode.Loop)]
    [InlineData(true, false, PlayMode.Shuffle)]
    [InlineData(true, true, PlayMode.Shuffle)]
    public async Task Legacy_play_flags_are_migrated_to_one_authoritative_mode(
        bool shuffle,
        bool loop,
        PlayMode expected)
    {
        var legacy = JsonSerializer.Deserialize<UserSettings>(
            $$"""{"ShuffleMode":{{shuffle.ToString().ToLowerInvariant()}},"LoopMode":{{loop.ToString().ToLowerInvariant()}}}""");
        var workflow = CreateWorkflow(
            new RecordingSettingsStore { Loaded = legacy },
            new RecordingHotkeyAdapter());

        var loaded = await workflow.LoadAsync(IntPtr.Zero);

        Assert.Equal(expected, loaded.Settings.DefaultPlayMode);
    }

    [Theory]
    [InlineData(PlayMode.Sequential)]
    [InlineData(PlayMode.Loop)]
    [InlineData(PlayMode.SingleLoop)]
    [InlineData(PlayMode.Shuffle)]
    public async Task Every_play_mode_round_trips_without_loss(PlayMode mode)
    {
        var store = new RecordingSettingsStore();
        var workflow = CreateWorkflow(store, new RecordingHotkeyAdapter());
        await workflow.LoadAsync(IntPtr.Zero);

        workflow.Edit(settings => settings.DefaultPlayMode = mode == PlayMode.Sequential
            ? PlayMode.Loop
            : PlayMode.Sequential);
        var edited = workflow.Edit(settings => settings.DefaultPlayMode = mode);
        await workflow.SaveAsync(edited.State);
        var restarted = CreateWorkflow(
            new RecordingSettingsStore { Loaded = store.Saved.Single() },
            new RecordingHotkeyAdapter());

        Assert.Equal(mode, (await restarted.LoadAsync(IntPtr.Zero)).Settings.DefaultPlayMode);
    }

    [Fact]
    public async Task Accepted_change_is_published_once_while_noop_and_rejection_are_silent()
    {
        var workflow = CreateWorkflow(new RecordingSettingsStore(), new RecordingHotkeyAdapter());
        await workflow.LoadAsync(IntPtr.Zero);
        var changes = new List<SettingsChangedEventArgs>();
        workflow.SettingsChanged += (_, change) => changes.Add(change);

        var changed = workflow.Edit(settings => settings.DefaultVolume = 0.42);
        var noop = workflow.Edit(settings => settings.DefaultVolume = 0.42);
        var rejected = workflow.Edit(settings => settings.DownloadPath = "relative\\path");

        Assert.True(changed.Accepted);
        Assert.True(noop.Accepted);
        Assert.False(rejected.Accepted);
        Assert.Single(changes);
        Assert.Equal(changed.State.Version, changes[0].Current.Version);
        Assert.Equal(0.8, changes[0].Previous!.Settings.DefaultVolume);
        Assert.Equal(0.42, changes[0].Current.Settings.DefaultVolume);
    }

    [Fact]
    public async Task Load_publishes_one_initial_snapshot_for_runtime_consumers()
    {
        var workflow = CreateWorkflow(
            new RecordingSettingsStore
            {
                Loaded = new UserSettings { DefaultPlayMode = PlayMode.SingleLoop }
            },
            new RecordingHotkeyAdapter());
        var changes = new List<SettingsChangedEventArgs>();
        workflow.SettingsChanged += (_, change) => changes.Add(change);

        var loaded = await workflow.LoadAsync(IntPtr.Zero);

        var change = Assert.Single(changes);
        Assert.Null(change.Previous);
        Assert.Same(loaded, change.Current);
        Assert.Equal(PlayMode.SingleLoop, change.Current.Settings.DefaultPlayMode);
    }

    [Fact]
    public async Task Local_and_global_bindings_share_one_conflict_rule_and_do_not_mutate_state()
    {
        var native = new RecordingHotkeyAdapter();
        var workflow = CreateWorkflow(new RecordingSettingsStore(), native);
        await workflow.LoadAsync(IntPtr.Zero);
        var before = workflow.Current;

        var result = await workflow.ReplaceHotkeyAsync(
            HotkeyActions.PlayPause,
            isGlobal: false,
            Key.Space,
            ModifierKeys.Control);

        Assert.Equal(HotkeyEditStatus.Conflict, result.Status);
        Assert.Equal(before.Version, result.State.Version);
        Assert.Equal(2, result.Conflicts.Count);
        Assert.Equal(Key.F5, workflow.GetHotkeys().Single(item =>
            item.ActionId == HotkeyActions.PlayPause && !item.IsGlobal).Key);
        Assert.Empty(native.Operations);
    }

    [Fact]
    public async Task Alt_5_through_8_are_released_while_Alt_1_through_4_remain_reserved()
    {
        foreach (var key in new[] { Key.D5, Key.D6, Key.D7, Key.D8 })
        {
            var workflow = CreateWorkflow(new RecordingSettingsStore(), new RecordingHotkeyAdapter());
            await workflow.LoadAsync(IntPtr.Zero);
            var result = await workflow.ReplaceHotkeyAsync(
                HotkeyActions.PlayPause,
                isGlobal: false,
                key,
                ModifierKeys.Alt);
            Assert.Equal(HotkeyEditStatus.Applied, result.Status);
        }

        var reservedWorkflow = CreateWorkflow(new RecordingSettingsStore(), new RecordingHotkeyAdapter());
        await reservedWorkflow.LoadAsync(IntPtr.Zero);
        var reserved = await reservedWorkflow.ReplaceHotkeyAsync(
            HotkeyActions.PlayPause,
            isGlobal: false,
            Key.D4,
            ModifierKeys.Alt);
        Assert.Equal(HotkeyEditStatus.Invalid, reserved.Status);
    }

    [Fact]
    public async Task Failed_native_replacement_restores_every_old_registration_and_old_state()
    {
        var native = new RecordingHotkeyAdapter();
        var workflow = CreateWorkflow(new RecordingSettingsStore(), native);
        await workflow.LoadAsync(new IntPtr(7));
        var before = workflow.GetHotkeys();
        var oldRegistered = native.Registered.ToDictionary(pair => pair.Key, pair => pair.Value);
        native.FailNext(Key.P, ModifierKeys.Control | ModifierKeys.Alt);

        var result = await workflow.ReplaceHotkeyAsync(
            HotkeyActions.Next,
            isGlobal: true,
            Key.P,
            ModifierKeys.Control | ModifierKeys.Alt);

        Assert.Equal(HotkeyEditStatus.RegistrationFailed, result.Status);
        Assert.Equal(oldRegistered, native.Registered);
        Assert.Equal(
            before.Select(Binding),
            workflow.GetHotkeys().Select(Binding));
        Assert.Equal(Key.Right, workflow.GetHotkeys().Single(item =>
            item.ActionId == HotkeyActions.Next && item.IsGlobal).Key);
    }

    [Fact]
    public async Task Rollback_failure_is_observable_and_never_leaves_the_rejected_new_binding_registered()
    {
        var native = new RecordingHotkeyAdapter();
        var workflow = CreateWorkflow(new RecordingSettingsStore(), native);
        await workflow.LoadAsync(new IntPtr(8));
        native.Fail(
            (Key.P, ModifierKeys.Control | ModifierKeys.Alt),
            (Key.Right, ModifierKeys.Control));

        var result = await workflow.ReplaceHotkeyAsync(
            HotkeyActions.Next,
            isGlobal: true,
            Key.P,
            ModifierKeys.Control | ModifierKeys.Alt);

        Assert.Equal(HotkeyEditStatus.RollbackFailed, result.Status);
        Assert.DoesNotContain(
            native.Registered.Values,
            binding => binding == $"{Key.P}|{ModifierKeys.Control | ModifierKeys.Alt}");
        Assert.Equal(Key.Right, workflow.GetHotkeys().Single(item =>
            item.ActionId == HotkeyActions.Next && item.IsGlobal).Key);
    }

    [Fact]
    public async Task Local_edit_does_not_touch_native_registrations_or_inherit_unrelated_global_failure()
    {
        var native = new RecordingHotkeyAdapter();
        native.Fail((Key.Right, ModifierKeys.Control));
        var workflow = CreateWorkflow(new RecordingSettingsStore(), native);
        await workflow.LoadAsync(new IntPtr(9));
        var operationsBefore = native.Operations.Count;
        var registrationsBefore = native.Registered.ToDictionary(pair => pair.Key, pair => pair.Value);

        var result = await workflow.ClearHotkeyAsync(
            HotkeyActions.PlayPause,
            isGlobal: false);

        Assert.Equal(HotkeyEditStatus.Applied, result.Status);
        Assert.Equal(operationsBefore, native.Operations.Count);
        Assert.Equal(registrationsBefore, native.Registered);
        Assert.False(workflow.GetHotkeys().Single(item =>
            item.ActionId == HotkeyActions.PlayPause && !item.IsGlobal).IsEnabled);
    }

    [Fact]
    public async Task Concurrent_and_repeated_saves_finish_with_latest_version_and_stale_never_overwrites()
    {
        var store = new RecordingSettingsStore { BlockFirstSave = true };
        var workflow = CreateWorkflow(store, new RecordingHotkeyAdapter());
        await workflow.LoadAsync(IntPtr.Zero);
        var older = workflow.Edit(settings => settings.DefaultVolume = 0.2).State;

        var oldSave = workflow.SaveAsync(older);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var latest = workflow.Edit(settings => settings.DefaultVolume = 0.9).State;
        var latestSave = workflow.SaveAsync(latest);
        store.ReleaseFirstSave();

        Assert.True((await oldSave).Succeeded);
        Assert.Equal(SettingsSaveStatus.Unchanged, (await latestSave).Status);
        Assert.Equal(new[] { 0.2, 0.9 }, store.Saved.Select(item => item.DefaultVolume));

        var repeated = await workflow.SaveAsync(latest);
        var stale = await workflow.SaveAsync(older);
        Assert.Equal(SettingsSaveStatus.Unchanged, repeated.Status);
        Assert.Equal(SettingsSaveStatus.Stale, stale.Status);
        Assert.Equal(2, store.Saved.Count);
        Assert.Equal(0.9, store.Saved[^1].DefaultVolume);
    }

    [Fact]
    public async Task Real_sqlite_restart_restores_settings_and_hotkeys_without_creating_main_window()
    {
        var directory = Directory.CreateTempSubdirectory("LXMusicAccessible.Settings-").FullName;
        try
        {
            var initializer = new DatabaseInitializer(
                NullLogger<DatabaseInitializer>.Instance,
                directory);
            Assert.True((await initializer.InitializeAsync()).Succeeded);
            var firstData = new DataService(NullLogger<DataService>.Instance, directory);
            var first = new SettingsWorkflow(firstData, NullLoggerFactory.Instance);
            await first.LoadAsync(IntPtr.Zero);
            var edited = first.Edit(settings =>
            {
                settings.DefaultVolume = 0.37;
                settings.DefaultPlayMode = PlayMode.SingleLoop;
                settings.DefaultPlayQuality = "flac24bit";
                settings.DefaultDownloadQuality = "flac24bit";
                settings.DownloadPath = string.Empty;
            }).State;
            var hotkey = await first.ReplaceHotkeyAsync(
                HotkeyActions.Next,
                isGlobal: false,
                Key.F12,
                ModifierKeys.Shift);
            Assert.True(hotkey.Applied);
            Assert.True((await first.SaveAsync(edited)).Succeeded);

            SqliteConnection.ClearAllPools();
            var restartedData = new DataService(NullLogger<DataService>.Instance, directory);
            var restarted = new SettingsWorkflow(restartedData, NullLoggerFactory.Instance);
            var restored = await restarted.LoadAsync(IntPtr.Zero);

            Assert.Equal(0.37, restored.Settings.DefaultVolume);
            Assert.Equal(PlayMode.SingleLoop, restored.Settings.DefaultPlayMode);
            Assert.Equal("flac24bit", restored.Settings.DefaultPlayQuality);
            Assert.Equal("flac24bit", restored.Settings.DefaultDownloadQuality);
            Assert.Equal(string.Empty, restored.Settings.DownloadPath);
            var restoredHotkey = restored.Settings.CustomHotkeys.Single(item =>
                item.ActionId == HotkeyActions.Next && !item.IsGlobal);
            Assert.Equal(Key.F12, restoredHotkey.Key);
            Assert.Equal(ModifierKeys.Shift, restoredHotkey.Modifiers);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Main_window_is_only_a_settings_adapter_and_conflict_state_has_one_focused_name_channel()
    {
        var root = FindRepositoryRoot();
        var mainCode = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "MainWindow.xaml.cs"));
        var mainXaml = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "MainWindow.xaml"));
        var settingsXaml = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "SettingsWindow.xaml"));
        var conflictXaml = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "HotkeyConflictDialog.xaml"));

        Assert.DoesNotContain("GetSettingsAsync", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveSettingsAsync", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterHotKey", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("UnregisterHotKey", mainCode, StringComparison.Ordinal);
        Assert.Contains("SettingsWorkflow settingsWorkflow", mainCode, StringComparison.Ordinal);
        Assert.Contains("_settingsWorkflow.SettingsChanged += SettingsWorkflow_SettingsChanged", mainCode);
        Assert.Contains("_settingsWorkflow.Current.Settings.HideToTrayOnAltF4", mainCode);
        Assert.Contains("_settingsWorkflow.Current.Settings.DownloadPath", mainCode);
        Assert.Contains("_settingsWorkflow.Current.Settings.ThirdPartyPlayerPath", mainCode);
        Assert.Contains("GetDefaultDownloadQuality()", mainCode);
        Assert.DoesNotContain("settings.AutoPlay = AutoPlayCheckBox", mainCode);
        Assert.DoesNotContain("settings.EnableScreenReader = EnableScreenReaderCheckBox", mainCode);
        Assert.DoesNotContain("settings.DownloadPath = DownloadPathBox", mainCode);
        Assert.DoesNotContain("settings.ThirdPartyPlayerPath = ThirdPartyPlayerPathBox", mainCode);
        Assert.DoesNotContain("settings.ShuffleMode = ShufflePlayCheckBox", mainCode);
        Assert.DoesNotContain("settings.LoopMode = RepeatPlayCheckBox", mainCode);
        Assert.DoesNotContain("<TabItem Header=\"设置\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoPlayCheckBox", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableScreenReaderCheckBox", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"关闭\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("if (_settingsInputSuppressed) return;", mainCode, StringComparison.Ordinal);
        Assert.Contains("Property=\"AutomationProperties.Name\" Value=\"{Binding AccessibleText}\"", settingsXaml);
        Assert.DoesNotContain("Property=\"AutomationProperties.HelpText\"", mainXaml);
        Assert.DoesNotContain("Property=\"AutomationProperties.ItemStatus\" Value=\"{Binding ConflictStatus}\"", mainXaml);
        Assert.Contains("Title=\"快捷键冲突提示\"", conflictXaml);
        Assert.DoesNotContain("AutomationProperties.Name=", conflictXaml);
        Assert.DoesNotContain("AutomationProperties.HelpText=", conflictXaml);
        Assert.Contains("AutomationProperties.ItemStatus=\"存在全局快捷键注册冲突\"", conflictXaml);
        Assert.DoesNotContain("SendInput", mainCode, StringComparison.Ordinal);
    }

    private static SettingsWorkflow CreateWorkflow(
        ISettingsStore store,
        IGlobalHotkeyAdapter native) =>
        new(
            store,
            new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, native),
            NullLogger<SettingsWorkflow>.Instance);

    private static string Binding(Hotkey hotkey) =>
        $"{hotkey.ActionId}|{hotkey.IsGlobal}|{hotkey.Key}|{hotkey.Modifiers}|{hotkey.IsEnabled}";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "verify.ps1")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _saveCount;

        public UserSettings? Loaded { get; set; }
        public bool BlockFirstSave { get; set; }
        public List<UserSettings> Saved { get; } = new();
        public TaskCompletionSource FirstSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Loaded);
        }

        public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken)
        {
            var ordinal = Interlocked.Increment(ref _saveCount);
            Saved.Add(settings);
            if (ordinal == 1)
            {
                FirstSaveStarted.TrySetResult();
                if (BlockFirstSave) await _release.Task.WaitAsync(cancellationToken);
            }
        }

        public void ReleaseFirstSave() => _release.TrySetResult();
    }

    private sealed class RecordingHotkeyAdapter : IGlobalHotkeyAdapter
    {
        private (Key Key, ModifierKeys Modifiers)? _failNext;
        private readonly HashSet<(Key Key, ModifierKeys Modifiers)> _fail = new();

        public Dictionary<int, string> Registered { get; } = new();
        public List<string> Operations { get; } = new();

        public bool Register(IntPtr windowHandle, int id, Key key, ModifierKeys modifiers)
        {
            Operations.Add($"register:{id}:{key}:{modifiers}");
            if (_failNext == (key, modifiers) || _fail.Contains((key, modifiers)))
            {
                _failNext = null;
                return false;
            }
            Registered[id] = $"{key}|{modifiers}";
            return true;
        }

        public bool Unregister(IntPtr windowHandle, int id)
        {
            Operations.Add($"unregister:{id}");
            Registered.Remove(id);
            return true;
        }

        public void FailNext(Key key, ModifierKeys modifiers) =>
            _failNext = (key, modifiers);

        public void Fail(params (Key Key, ModifierKeys Modifiers)[] bindings) =>
            _fail.UnionWith(bindings);
    }
}
