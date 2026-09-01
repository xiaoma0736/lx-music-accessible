using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

[Collection("WPF accessibility semantics")]
public sealed class Issue49SettingsAccessibilityTests
{
    [Fact]
    public async Task Settings_category_items_expose_explicit_accessible_names()
    {
        var expected = new[]
        {
            "常规", "播放", "歌词", "下载", "存储与缓存", "无障碍", "快捷键"
        };
        var dependencies = await CreateDependenciesAsync();
        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(dependencies);
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.Equal(expected, window.CategoryList.Items.Cast<ListBoxItem>()
                    .Select(AutomationProperties.GetName));
                Assert.Equal(expected, window.CategoryList.Items.Cast<ListBoxItem>()
                    .Select(item => item.Content));
            }
            finally
            {
                window.Close();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
        });
    }

    [Fact]
    public async Task Returning_focus_to_settings_category_announces_selected_item_without_tts()
    {
        var dependencies = await CreateDependenciesAsync();
        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(dependencies);
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                window.CategoryList.SelectedIndex = 1;
                var playback = Assert.IsType<ListBoxItem>(
                    window.CategoryList.ItemContainerGenerator.ContainerFromIndex(1));
                Assert.True(playback.Focus());
                Assert.True(window.MoveAcrossCategoryBoundary(reverse: false));
                Assert.True(window.MoveAcrossCategoryBoundary(reverse: true));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var announcement = Assert.Single(
                    dependencies.Announcer.Announcements,
                    item => item.Text == "播放");
                Assert.Equal("settings", announcement.ActivityId);
                Assert.Equal(AnnouncementSpeech.None, announcement.Speech);
            }
            finally
            {
                window.Close();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
        });
    }

    [Fact]
    public async Task Disabling_voice_announcement_uses_settings_activity_and_is_always_spoken()
    {
        var dependencies = await CreateDependenciesAsync();
        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(dependencies);
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                window.CategoryList.SelectedIndex = 5;
                window.EnableVoiceAnnouncementCheckBox.IsChecked = false;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var announcement = dependencies.Announcer.Announcements.Last(item =>
                    item.Text.Contains("语音朗读提示", StringComparison.Ordinal));
                Assert.Equal("已关闭语音朗读提示", announcement.Text);
                Assert.Equal("settings", announcement.ActivityId);
                Assert.Equal(AnnouncementSpeech.Always, announcement.Speech);
            }
            finally
            {
                window.Close();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
        });
    }

    [Fact]
    public async Task Hotkey_recording_and_cancel_announcements_use_hotkey_activity()
    {
        var dependencies = await CreateDependenciesAsync();
        await RunOnStaThreadAsync(() =>
        {
            var window = CreateWindow(dependencies);
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                window.CategoryList.SelectedIndex = 6;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.HotkeyListBox.SelectedIndex = 0;
                Assert.True(window.HotkeyListBox.FocusItemOrList());
                RaiseKey(window.HotkeyListBox, Key.Enter, Keyboard.KeyDownEvent);
                Assert.True(window.IsRecordingHotkey);
                RaisePreviewKey(window, Key.Escape);
                Assert.False(window.IsRecordingHotkey);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var messages = dependencies.Announcer.Announcements.ToArray();
                Assert.EndsWith("快捷键，Esc 取消，Delete 清除", messages[^2].Text, StringComparison.Ordinal);
                Assert.Equal("hotkey-recording", messages[^2].ActivityId);
                Assert.Equal("已取消，快捷键未更改", messages[^1].Text);
                Assert.Equal("hotkey-recording", messages[^1].ActivityId);
            }
            finally
            {
                window.Close();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
        });
    }

    private static async Task<TestDependencies> CreateDependenciesAsync()
    {
        var settings = new SettingsWorkflow(
            new RecordingSettingsStore(),
            new HotkeyManagerService(
                NullLogger<HotkeyManagerService>.Instance,
                new RecordingHotkeyAdapter()),
            NullLogger<SettingsWorkflow>.Instance);
        await settings.LoadAsync(IntPtr.Zero);
        return new(settings, new RecordingAnnouncer(), new RecordingTtsAdapter(), new RecordingDownloads());
    }

    private static SettingsWindow CreateWindow(TestDependencies dependencies) => new(
        dependencies.Announcer,
        dependencies.Settings,
        dependencies.Tts,
        dependencies.Downloads,
        _ => false,
        _ => { });

    private static void RaisePreviewKey(UIElement element, Key key) =>
        RaiseKey(element, key, Keyboard.PreviewKeyDownEvent);

    private static void RaiseKey(UIElement element, Key key, RoutedEvent routedEvent)
    {
        var source = PresentationSource.FromVisual(element);
        Assert.NotNull(source);
        element.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
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
        SettingsWorkflow Settings,
        RecordingAnnouncer Announcer,
        RecordingTtsAdapter Tts,
        RecordingDownloads Downloads);

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<UserSettings?>(new());

        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingDownloads : IDownloadWorkflow
    {
        private readonly System.Collections.ObjectModel.ObservableCollection<DownloadTask> _tasks = [];
        public System.Collections.ObjectModel.ReadOnlyObservableCollection<DownloadTask> Tasks => new(_tasks);
        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Cancel(DownloadTask task) { }
        public void ClearCompleted() { }
        public string ResolveDownloadDirectory(string? requested = null) => @"C:\Downloads";
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }
    private sealed class RecordingHotkeyAdapter : IGlobalHotkeyAdapter
    {
        public int LastRegisterError { get; private set; }
        public bool Register(IntPtr windowHandle, int id, Key key, ModifierKeys modifiers) => true;
        public bool Unregister(IntPtr windowHandle, int id) => true;
    }
}
