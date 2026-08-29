using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

public partial class DownloadManagerWindow : Window
{
    private readonly IAccessibleAnnouncer _announcer;
    private readonly SettingsWorkflow _settings;
    private readonly IDownloadWorkflow _downloads;
    private readonly INotifyCollectionChanged _taskChanges;
    private readonly Func<KeyEventArgs, bool> _handleHotkey;

    public DownloadManagerWindow(
        IAccessibleAnnouncer announcer,
        SettingsWorkflow settings,
        IDownloadWorkflow downloads,
        Func<KeyEventArgs, bool> handleHotkey)
    {
        ArgumentNullException.ThrowIfNull(announcer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(handleHotkey);
        InitializeComponent();
        _announcer = announcer;
        _settings = settings;
        _downloads = downloads;
        _taskChanges = downloads.Tasks;
        _handleHotkey = handleHotkey;
        DownloadTaskList.ItemsSource = downloads.Tasks;
        _taskChanges.CollectionChanged += Tasks_CollectionChanged;
        UpdateListState();
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(DownloadTaskList.FocusItemOrList, DispatcherPriority.Input);

    private void Tasks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateListState();

    private void UpdateListState()
    {
        var count = _downloads.Tasks.Count;
        EmptyDownloadText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(
            DownloadTaskList,
            count == 0 ? "暂无下载任务" : $"共 {count} 个下载任务");
    }

    private void DownloadCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadTaskList.SelectedItem is DownloadTask task)
            _downloads.Cancel(task);
        else
            Announce("请先选择要取消的下载任务");
    }

    private async void DownloadRetryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadTaskList.SelectedItem is not DownloadTask task
            || task.Status is not (DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled))
        {
            Announce("请选择失败或已取消的下载任务进行重试");
            return;
        }

        await _downloads.RetryAsync(
            task,
            _downloads.ResolveDownloadDirectory(_settings.Current.Settings.DownloadPath));
    }

    private void DownloadOpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = _downloads.OpenDownloadDirectory(_settings.Current.Settings.DownloadPath);
        Announce(result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private void DownloadClearBtn_Click(object sender, RoutedEventArgs e)
    {
        var selectedIndex = DownloadTaskList.SelectedIndex;
        _downloads.ClearCompleted();

        if (_downloads.Tasks.Count > 0 && selectedIndex >= 0)
            DownloadTaskList.SelectedIndex = Math.Min(Math.Max(0, selectedIndex), _downloads.Tasks.Count - 1);
        DownloadTaskList.FocusItemOrList();
        Announce("已清空终态下载任务");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.F4 || e.SystemKey == Key.F4)
            && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase { IsReadOnly: false }
            or System.Windows.Controls.PasswordBox)
        {
            return;
        }
        if (_handleHotkey(e)) e.Handled = true;
    }

    private void Announce(string text, AnnouncementKind kind = AnnouncementKind.Status) =>
        _announcer.Announce(new(text, kind, Speech: AnnouncementSpeech.FollowPreference));

    private void Window_Closed(object? sender, EventArgs e)
    {
        Loaded -= Window_Loaded;
        Closed -= Window_Closed;
        _taskChanges.CollectionChanged -= Tasks_CollectionChanged;
    }
}
