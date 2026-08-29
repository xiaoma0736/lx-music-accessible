using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

[Collection("WPF accessibility semantics")]
public sealed class SettingsWindowTests
{
    [Fact]
    public async Task Ordinary_settings_starts_on_the_native_general_category_without_editing_state()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var factory = app.Services.GetRequiredService<ApplicationWindowFactory>();
        var workflow = app.Services.GetRequiredService<SettingsWorkflow>();

        await RunOnStaThreadAsync(() =>
        {
            var owner = new Window { ShowInTaskbar = false };
            owner.Show();
            var window = factory.CreateSettings(owner, _ => false, _ => { });
            try
            {
                var initialVersion = workflow.Current.Version;
                Assert.Equal(
                    ["常规", "播放", "歌词", "下载", "存储与缓存", "无障碍", "快捷键"],
                    window.CategoryList.Items.Cast<ListBoxItem>().Select(item => item.Content));
                Assert.Equal(0, window.CategoryList.SelectedIndex);
                Assert.Equal("设置分类", new ListBoxAutomationPeer(window.CategoryList).GetName());
                Assert.Equal(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                Assert.Same(owner, window.Owner);

                foreach (var index in Enumerable.Range(0, 7))
                    window.CategoryList.SelectedIndex = index;
                Assert.Equal(initialVersion, workflow.Current.Version);

                window.CategoryList.SelectedIndex = 0;
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(window.CategoryList.IsKeyboardFocusWithin);
                var category = Assert.IsType<ListBoxItem>(
                    window.CategoryList.ItemContainerGenerator.ContainerFromIndex(0));
                var categoryPeer = UIElementAutomationPeer.CreatePeerForElement(category);
                Assert.NotNull(categoryPeer);
                Assert.Same(category, Keyboard.FocusedElement);

                RaiseKey(category, Key.Down, Keyboard.KeyDownEvent);
                Assert.Equal(1, window.CategoryList.SelectedIndex);
                var playback = Assert.IsType<ListBoxItem>(
                    window.CategoryList.ItemContainerGenerator.ContainerFromIndex(1));
                RaiseKey(playback, Key.Up, Keyboard.KeyDownEvent);
                Assert.Equal(0, window.CategoryList.SelectedIndex);
                Assert.Equal(initialVersion, workflow.Current.Version);
                Assert.Equal("常规", categoryPeer.GetName());

            }
            finally
            {
                window.Close();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                owner.Close();
            }
        });
    }

    [Fact]
    public async Task Settings_category_keyboard_navigation_survives_tab_away_and_back()
    {
        var dependencies = await CreateDependenciesAsync();
        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(dependencies);
            try
            {
                var initialVersion = dependencies.Settings.Current.Version;
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                window.CategoryList.SelectedIndex = 1;
                var playback = Assert.IsType<ListBoxItem>(
                    window.CategoryList.ItemContainerGenerator.ContainerFromIndex(1));
                Assert.True(playback.Focus());
                Assert.True(window.MoveAcrossCategoryBoundary(reverse: false));
                Assert.True(window.MoveAcrossCategoryBoundary(reverse: true));
                Assert.Same(playback, Keyboard.FocusedElement);

                RaiseKey(playback, Key.Down, Keyboard.KeyDownEvent);

                Assert.Equal(2, window.CategoryList.SelectedIndex);
                var lyrics = Assert.IsType<ListBoxItem>(
                    window.CategoryList.ItemContainerGenerator.ContainerFromIndex(2));
                RaiseKey(lyrics, Key.Up, Keyboard.KeyDownEvent);
                Assert.Equal(1, window.CategoryList.SelectedIndex);
                Assert.Equal(initialVersion, dependencies.Settings.Current.Version);
                Assert.Equal(0, dependencies.Store.SaveCount);
            }
            finally
            {
                window.Close();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
        });
    }

    [Fact]
    public async Task User_edits_apply_once_while_invalid_typed_paths_block_close_and_keep_focus()
    {
        var dependencies = await CreateDependenciesAsync();
        await RunOnStaThreadAsync(() =>
        {
            var changes = new List<SettingsChangedEventArgs>();
            dependencies.Settings.SettingsChanged += (_, change) => changes.Add(change);
            var window = CreateWindow(dependencies);
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            window.CategoryList.SelectedIndex = 1;
            window.DefaultPlayModeCombo.SelectedIndex = 2;
            Assert.Equal(PlayMode.SingleLoop, dependencies.Settings.Current.Settings.DefaultPlayMode);
            Assert.Single(changes);

            window.CategoryList.SelectedIndex = 3;
            window.DownloadPathBox.Text = "relative\\music";
            window.DownloadPathBox.Focus();
            window.CategoryList.Focus();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(string.Empty, dependencies.Settings.Current.Settings.DownloadPath);
            Assert.Equal("relative\\music", window.DownloadPathBox.Text);
            Assert.True(window.DownloadPathBox.IsKeyboardFocused);

            window.Close();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(window.IsVisible);
            Assert.Equal(0, window.CloseSaveAttempts);
            Assert.Contains(dependencies.Announcer.Messages, message => message.Contains("绝对路径", StringComparison.Ordinal));

            window.DownloadPathBox.Text = @"C:\Music";
            window.DownloadPathBox.Focus();
            RaisePreviewKey(window.DownloadPathBox, Key.Enter);
            Assert.Equal(@"C:\Music", dependencies.Settings.Current.Settings.DownloadPath);
            Assert.Equal(2, changes.Count);

            window.Close();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.False(window.IsVisible);
            Assert.Equal(1, window.CloseSaveAttempts);
            Assert.Equal(1, dependencies.Store.SaveCount);
            Assert.Equal(@"C:\Music", dependencies.Store.Saved!.DownloadPath);
        });
    }

    [Fact]
    public async Task Save_failure_still_closes_and_announces_exit_retry_without_losing_current_state()
    {
        var dependencies = await CreateDependenciesAsync(failSave: true);
        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(dependencies);
            window.Show();
            window.CategoryList.SelectedIndex = 2;
            window.LyricScrollCheckBox.IsChecked = false;

            window.Close();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.False(window.IsVisible);
            Assert.Equal(1, dependencies.Store.SaveCount);
            Assert.False(dependencies.Settings.Current.Settings.LyricAutoScroll);
            Assert.Contains(dependencies.Announcer.Messages, message => message.Contains("退出应用时重试", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Conflict_reconfigure_deep_links_and_first_escape_only_cancels_recording()
    {
        var dependencies = await CreateDependenciesAsync();
        var recordingStates = new List<bool>();
        var target = dependencies.Settings.GetHotkeys().First(hotkey =>
            hotkey.ActionId == HotkeyActions.Next && hotkey.IsGlobal);
        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(dependencies, recordingStates.Add, target);
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal(6, window.CategoryList.SelectedIndex);
            Assert.True(window.HotkeyListBox.IsKeyboardFocusWithin);
            Assert.True(window.IsRecordingHotkey);
            Assert.Equal(new[] { true }, recordingStates);

            RaisePreviewKey(window, Key.Escape);
            Assert.True(window.IsVisible);
            Assert.False(window.IsRecordingHotkey);
            Assert.Equal(new[] { true, false }, recordingStates);

            RaisePreviewKey(window, Key.Escape);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public async Task Text_editing_suppresses_shared_playback_shortcuts_and_tab_returns_to_categories()
    {
        var dependencies = await CreateDependenciesAsync();
        var routed = 0;
        var suppression = new List<bool>();
        await RunOnStaThreadAsync(() =>
        {
            var window = new SettingsWindow(
                dependencies.Announcer,
                dependencies.Settings,
                dependencies.Tts,
                dependencies.Downloads,
                _ => { routed++; return true; },
                suppression.Add);
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.True(window.MoveAcrossCategoryBoundary(reverse: false));
            Assert.True(window.GeneralPanel.IsKeyboardFocusWithin);
            Assert.True(window.MoveAcrossCategoryBoundary(reverse: true));
            Assert.True(window.CategoryList.IsKeyboardFocusWithin);

            window.CategoryList.SelectedIndex = 1;
            window.ThirdPartyPlayerPathBox.Focus();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(suppression[^1]);
            RaisePreviewKey(window, Key.F5);
            Assert.Equal(0, routed);
            window.CategoryList.Focus();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.False(suppression[^1]);
            RaisePreviewKey(window, Key.F5);
            Assert.Equal(1, routed);

            window.Close();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        });
    }

    private static async Task<TestDependencies> CreateDependenciesAsync(bool failSave = false)
    {
        var store = new RecordingSettingsStore { FailSave = failSave };
        var settings = new SettingsWorkflow(
            store,
            new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, new RecordingHotkeyAdapter()),
            NullLogger<SettingsWorkflow>.Instance);
        await settings.LoadAsync(IntPtr.Zero);
        return new(store, settings, new RecordingAnnouncer(), new RecordingTts(), new RecordingDownloads());
    }

    private static SettingsWindow CreateWindow(
        TestDependencies dependencies,
        Action<bool>? setRecording = null,
        Hotkey? target = null) =>
        new(
            dependencies.Announcer,
            dependencies.Settings,
            dependencies.Tts,
            dependencies.Downloads,
            _ => false,
            setRecording ?? (_ => { }),
            target);

    private static void RaisePreviewKey(UIElement element, Key key) =>
        RaiseKey(element, key, Keyboard.PreviewKeyDownEvent);

    private static void RaiseKey(UIElement element, Key key, RoutedEvent routedEvent)
    {
        var source = PresentationSource.FromVisual(element);
        Assert.NotNull(source);
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = routedEvent
        });
    }

    private static Task RunOnStaThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            if (failure is null) completion.SetResult();
            else completion.SetException(failure);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed record TestDependencies(
        RecordingSettingsStore Store,
        SettingsWorkflow Settings,
        RecordingAnnouncer Announcer,
        RecordingTts Tts,
        RecordingDownloads Downloads);

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public bool FailSave { get; init; }
        public int SaveCount { get; private set; }
        public UserSettings? Saved { get; private set; }

        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<UserSettings?>(new());

        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken)
        {
            SaveCount++;
            if (FailSave) throw new IOException("fixture");
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHotkeyAdapter : IGlobalHotkeyAdapter
    {
        public bool Register(IntPtr windowHandle, int id, Key key, ModifierKeys modifiers) => true;
        public bool Unregister(IntPtr windowHandle, int id) => true;
    }

    private sealed class RecordingAnnouncer : IAccessibleAnnouncer
    {
        public bool SpeechEnabled { get; set; }
        public List<string> Messages { get; } = [];
        public void Announce(AccessibleAnnouncement announcement) => Messages.Add(announcement.Text);
    }

    private sealed class RecordingTts : ITtsService
    {
        public bool IsAvailable => true;
        public string ActiveBackendName => "测试后端";
        public Task SpeakAsync(string text) => Task.CompletedTask;
        public Task SpeakQueuedAsync(string text) => Task.CompletedTask;
        public void Stop() { }
        public void SetRate(double rate) { }
        public void SetVoice(string voiceId) { }
        public List<VoiceInfo> GetAvailableVoices() => [];
    }

    private sealed class RecordingDownloads : IDownloadWorkflow
    {
        private readonly ObservableCollection<LXMusicAccessible.Models.DownloadTask> _tasks = [];
        public ReadOnlyObservableCollection<LXMusicAccessible.Models.DownloadTask> Tasks => new(_tasks);
        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<LXMusicAccessible.Models.DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RetryAsync(LXMusicAccessible.Models.DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Cancel(LXMusicAccessible.Models.DownloadTask task) { }
        public void ClearCompleted() { }
        public string ResolveDownloadDirectory(string? requested = null) => string.IsNullOrWhiteSpace(requested) ? @"C:\Downloads" : requested;
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }
}
