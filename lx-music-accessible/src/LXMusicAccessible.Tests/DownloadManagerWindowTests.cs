using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
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
using Xunit;

namespace LXMusicAccessible.Tests;

[Collection("WPF accessibility semantics")]
public sealed class DownloadManagerWindowTests
{
    [Fact]
    public void Main_window_has_one_download_manager_entry_and_no_embedded_download_page()
    {
        var root = FindRepositoryRoot();
        var mainXaml = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "MainWindow.xaml"));
        var mainCode = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "MainWindow.xaml.cs"));
        var downloadXaml = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "DownloadManagerWindow.xaml"));

        Assert.Single(Regex.Matches(mainXaml, "Header=\"下载管理\\(_D\\)\"").Cast<Match>());
        Assert.DoesNotContain("<TabItem Header=\"下载管理\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MenuNavigateToDownload", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadTaskList", mainCode, StringComparison.Ordinal);
        Assert.Contains("MenuOpenDownloadManager", mainCode, StringComparison.Ordinal);
        Assert.Contains("_settingsWorkflow.Current.Settings.DownloadPath", mainCode, StringComparison.Ordinal);
        Assert.Contains("_settingsWorkflow.Current.Settings.DefaultDownloadQuality", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadPathBox", mainCode, StringComparison.Ordinal);
        Assert.Contains("Content=\"清空\"", downloadXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"关闭\"", downloadXaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_task_list_is_the_initial_focus_and_exposes_a_concise_empty_state()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var factory = app.Services.GetRequiredService<ApplicationWindowFactory>();

        await RunOnStaThreadAsync(() =>
        {
            var owner = new Window { ShowInTaskbar = false };
            owner.Show();
            var window = factory.CreateDownloadManager(owner, _ => false);
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.Same(window.DownloadTaskList, Keyboard.FocusedElement);
                Assert.Equal("下载任务", new ListBoxAutomationPeer(window.DownloadTaskList).GetName());
                Assert.Equal("暂无下载任务", AutomationProperties.GetItemStatus(window.DownloadTaskList));
                Assert.Equal(Visibility.Visible, window.EmptyDownloadText.Visibility);
                Assert.Equal(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                Assert.Same(owner, window.Owner);
            }
            finally
            {
                window.Close();
                owner.Close();
            }
        });
    }

    [Fact]
    public async Task Closing_keeps_tasks_while_explicit_commands_use_current_settings_and_clear_only_terminal_tasks()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var settings = app.Services.GetRequiredService<SettingsWorkflow>();
        await settings.LoadAsync(IntPtr.Zero);
        settings.Edit(value => value.DownloadPath = @"C:\Music");
        var downloads = new RecordingDownloads(
            Task("active", DownloadTaskStatus.Downloading),
            Task("done", DownloadTaskStatus.Completed),
            Task("failed", DownloadTaskStatus.Failed),
            Task("cancelled", DownloadTaskStatus.Cancelled));
        var announcer = new RecordingAnnouncer();

        await RunOnStaThreadAsync(() =>
        {
            var first = new DownloadManagerWindow(announcer, settings, downloads, _ => false);
            first.Show();
            first.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Same(first.DownloadTaskList, Keyboard.FocusedElement);
            RaisePreviewKey(first, Key.Escape);
            Assert.False(first.IsVisible);
            Assert.Equal(0, downloads.CancelCount);

            var second = new DownloadManagerWindow(announcer, settings, downloads, _ => false);
            try
            {
                second.Show();
                second.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Same(downloads.Tasks, second.DownloadTaskList.ItemsSource);
                Assert.Equal(4, second.DownloadTaskList.Items.Count);

                second.DownloadCancelBtn.Focus();
                downloads.Tasks[0].Progress = 25;
                second.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.Same(second.DownloadCancelBtn, Keyboard.FocusedElement);

                second.DownloadTaskList.SelectedIndex = 0;
                second.DownloadCancelBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal(1, downloads.CancelCount);

                second.DownloadTaskList.SelectedIndex = 2;
                second.DownloadRetryBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal(@"C:\Music", downloads.RetryTarget);

                second.DownloadOpenFolderBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal(@"C:\Music", downloads.OpenTarget);

                second.DownloadTaskList.SelectedIndex = 2;
                second.DownloadTaskList.Focus();
                second.DownloadClearBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal("active", Assert.Single(downloads.Tasks).Music.Id);
                Assert.Equal(0, second.DownloadTaskList.SelectedIndex);
                Assert.True(second.DownloadTaskList.IsKeyboardFocusWithin);
                Assert.Contains(announcer.Messages, message => message.Contains("已清空", StringComparison.Ordinal));
            }
            finally
            {
                second.Close();
            }
            Assert.Equal(1, downloads.CancelCount);
        });
    }

    [Fact]
    public async Task Playback_hotkeys_are_delegated_but_editable_text_suppresses_them()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var factory = app.Services.GetRequiredService<ApplicationWindowFactory>();
        var routed = 0;

        await RunOnStaThreadAsync(() =>
        {
            var owner = new Window { ShowInTaskbar = false };
            owner.Show();
            var window = factory.CreateDownloadManager(owner, _ => { routed++; return true; });
            try
            {
                var input = new TextBox { Width = 100 };
                Assert.IsType<Grid>(window.Content).Children.Add(input);
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                input.Focus();
                RaisePreviewKey(window, Key.F5);
                Assert.Equal(0, routed);

                window.DownloadTaskList.Focus();
                RaisePreviewKey(window, Key.F5);
                Assert.Equal(1, routed);
            }
            finally
            {
                window.Close();
                owner.Close();
            }
        });
    }

    private static DownloadTask Task(string id, DownloadTaskStatus status) => new()
    {
        Music = new MusicInfo { Id = id, Title = id, Artist = "artist", Source = "test" },
        Status = status
    };

    private static void RaisePreviewKey(UIElement element, Key key)
    {
        var source = PresentationSource.FromVisual(element);
        Assert.NotNull(source);
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
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

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "verify.ps1"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class RecordingAnnouncer : IAccessibleAnnouncer
    {
        public bool SpeechEnabled { get; set; }
        public List<string> Messages { get; } = [];
        public void Announce(AccessibleAnnouncement announcement) => Messages.Add(announcement.Text);
    }

    private sealed class RecordingDownloads : IDownloadWorkflow
    {
        private readonly ObservableCollection<DownloadTask> _tasks;
        private readonly ReadOnlyObservableCollection<DownloadTask> _view;

        public RecordingDownloads(params DownloadTask[] tasks)
        {
            _tasks = [.. tasks];
            _view = new(_tasks);
        }

        public ReadOnlyObservableCollection<DownloadTask> Tasks => _view;
        public int CancelCount { get; private set; }
        public string? RetryTarget { get; private set; }
        public string? OpenTarget { get; private set; }

        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default)
        {
            RetryTarget = targetDirectory;
            return System.Threading.Tasks.Task.FromResult(true);
        }
        public void Cancel(DownloadTask task) => CancelCount++;
        public void ClearCompleted()
        {
            foreach (var task in _tasks.Where(task => task.Status is DownloadTaskStatus.Completed or DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled).ToArray())
                _tasks.Remove(task);
        }
        public string ResolveDownloadDirectory(string? requested = null) => requested ?? @"C:\Downloads";
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null)
        {
            OpenTarget = requested;
            return new(true, "已打开下载文件夹");
        }
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }
}
