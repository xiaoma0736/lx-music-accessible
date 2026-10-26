using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>Native source manager with a ListBox and traditional ContextMenu.</summary>
public class SourceManagerForm : Form
{
    private readonly IAccessibleAnnouncer _announcer;
    private readonly IMusicSourceService _sources;
    private readonly Func<KeyEventArgs, bool> _handleHotkey;
    private readonly Func<string, bool> _confirmDelete;
    private readonly ListBox _list = WinFormsUi.ListBox("音源", 1);
    private readonly Label _empty = new() { Text = "暂无自定义音源。", AutoSize = true, Name = "SourceListStatus", TabStop = false };
    private string _sourceSignature = string.Empty;
    private volatile bool _sourcesChangingByThisWindow;

    public SourceManagerForm(
        IAccessibleAnnouncer announcer,
        IMusicSourceService sources,
        Func<KeyEventArgs, bool> handleHotkey,
        Func<string, bool>? confirmDelete = null)
    {
        _announcer = announcer;
        _sources = sources;
        _handleHotkey = handleHotkey;
        _confirmDelete = confirmDelete ?? (message => WinFormsUi.Confirm(this, message));
        Text = "音源管理";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 580);
        KeyPreview = true;
        var importLocal = CreateButton("导入本地音源", 2, (_, _) => ImportLocal());
        var importNetwork = CreateButton("导入网络音源", 3, (_, _) => ImportNetwork());
        var refresh = CreateButton("刷新", 4, (_, _) => RefreshSources());
        importLocal.SetBounds(12, 12, 120, 28);
        importNetwork.SetBounds(140, 12, 120, 28);
        refresh.SetBounds(268, 12, 80, 28);
        _list.SetBounds(12, 48, 720, 440);
        _empty.SetBounds(20, 56, 300, 22);
        Controls.AddRange(new Control[] { importLocal, importNetwork, refresh, _list, _empty });
        // The empty-state text is added last, which puts it behind the list in the z-order; it has to be
        // lifted to the front so the message is actually perceivable when there are no sources.
        _empty.BringToFront();
        var menu = new ContextMenu();
        menu.MenuItems.Add(new MenuItem("设为默认", (_, _) => SetDefault()));
        menu.MenuItems.Add(new MenuItem("启用", async (_, _) => await EnableAsync()));
        menu.MenuItems.Add(new MenuItem("禁用", (_, _) => Disable()));
        menu.MenuItems.Add(new MenuItem("测试", async (_, _) => await TestAsync()));
        menu.MenuItems.Add(new MenuItem("检查更新", async (_, _) => await UpdateAsync()));
        menu.MenuItems.Add(new MenuItem("删除", (_, _) => Delete()));
        _list.ContextMenu = menu;
        _list.KeyDown += async (_, e) => await HandleSourceListKeyAsync(e, menu);
        _sources.SourcesChanged += OnSourcesChanged;
        _sources.ScriptUpdateAlert += OnScriptUpdateAlert;
        Disposed += (_, _) =>
        {
            _sources.SourcesChanged -= OnSourcesChanged;
            _sources.ScriptUpdateAlert -= OnScriptUpdateAlert;
        };
        RefreshSources();
        Shown += (_, _) => _list.Focus();
    }

    internal async Task<bool> HandleSourceListKeyAsync(KeyEventArgs e, ContextMenu? menu = null, bool requireConfirmation = true)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            menu?.Show(_list, new Point(10, 10));
            return true;
        }

        if (e.KeyCode == Keys.Delete && Selected() is not null)
        {
            e.Handled = true;
            await DeleteAsync(requireConfirmation);
            return true;
        }

        return false;
    }

    private void OnScriptUpdateAlert(object? sender, LxScriptUpdateAlertEventArgs e)
    {
        // lx.send(updateAlert) is part of the upstream source contract. Announcing it keeps the message
        // perceivable without stealing keyboard focus from playback or source-management tasks.
        var updateUrl = string.IsNullOrWhiteSpace(e.UpdateUrl) ? "" : $"，更新地址：{e.UpdateUrl}";
        Announce($"音源更新：{e.Log}{updateUrl}", "source-update-alert");
    }

    private void OnSourcesChanged(object? sender, EventArgs e)
    {
        // The real service raises this from background work. An import can still be running after the
        // source manager is closed, and subscriptions can fire before its handle exists; posting to a
        // missing control handle must not turn a successful import into an InvalidOperationException.
        var alreadyAnnounced = _sourcesChangingByThisWindow;
        BeginOnUiThread(() => RefreshSourcesAfterExternalChange(alreadyAnnounced));
    }

    private void BeginOnUiThread(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (ObjectDisposedException)
        {
            // The manager was closed while background source work was completing.
        }
        catch (InvalidOperationException)
        {
            // A handle-creation or close race removed the target before the refresh could post.
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_handleHotkey(e)) { e.Handled = true; return; }
        if (e.KeyCode == Keys.Escape) Close();
    }
    private void RefreshSourcesAfterExternalChange(bool alreadyAnnounced)
    {
        var before = _sourceSignature;
        RefreshSources();
        // A change this window made already came with its own result ("已禁用音源：X"). Speaking
        // 音源列表已更新 on top of it would read as a second confirmation of one command, so only a
        // change that arrived from outside this window (or while focus was elsewhere) is announced.
        if (!alreadyAnnounced && before != _sourceSignature) Announce("音源列表已更新", "source-list");
    }

    internal void RefreshSources()
    {
        var sources = _sources.GetAllSources().Where(source => !source.IsSystemHidden).ToList();
        var selectedIndex = _list.SelectedIndex;
        var selected = WinFormsUi.Selected<MusicSource>(_list)?.Id;
        _list.Items.Clear();
        foreach (var source in sources)
            _list.Items.Add(new ObjectBox<MusicSource>(source.AccessibleName, source));
        _empty.Visible = sources.Count == 0;
        _sourceSignature = string.Join("|", sources.Select(source => source.Id + ":" + source.AccessibleName));
        if (sources.Count > 0)
        {
            var identityMatch = Enumerable.Range(0, _list.Items.Count).Where(i => ((ObjectBox<MusicSource>)_list.Items[i]).Value.Id == selected).ToArray();
            var index = identityMatch.Length > 0 ? identityMatch[0] : Math.Max(0, Math.Min(selectedIndex, _list.Items.Count - 1));
            _list.SelectedIndex = index;
        }
    }

    private async void ImportLocal()
    {
        using var dialog = new OpenFileDialog { Title = "导入本地音源", Filter = "音源脚本|*.js;*.json|所有文件|*.*", Multiselect = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await ImportSourceAsync(dialog.FileName);
    }

    private async void ImportNetwork()
    {
        var url = WinFormsUi.AskText(this, "导入网络音源", "音源地址：");
        if (string.IsNullOrWhiteSpace(url)) return;
        await ImportSourceAsync(url);
    }

    private async Task ImportSourceAsync(string label)
    {
        try
        {
            var isHttpLocation = Uri.TryCreate(label, UriKind.Absolute, out var location)
                && (string.Equals(location.Scheme, "http", StringComparison.Ordinal)
                    || string.Equals(location.Scheme, "https", StringComparison.Ordinal));
            var result = await InSourceCommand(() => isHttpLocation
                ? _sources.ImportFromUrlDetailedAsync(label)
                : _sources.ImportFromFileDetailedAsync(label));
            if (result is null)
            {
                Announce("导入音源失败，请检查脚本格式。", "source-import", AnnouncementKind.Error);
                return;
            }

            RefreshSources();
            if (result.Status == MusicSourceImportStatus.VersionConflict)
            {
                Announce(
                    $"导入音源失败：已存在相同或更高版本的 {result.Source.Name}，保留当前音源。",
                    "source-import",
                    AnnouncementKind.Error);
                return;
            }

            var verb = result.Status switch
            {
                MusicSourceImportStatus.Merged when result.Source.IsDefault => "已合并并设为默认音源",
                MusicSourceImportStatus.Merged => "已合并音源",
                MusicSourceImportStatus.Replaced when result.Source.IsDefault => "已用高版本替换并设为默认音源",
                MusicSourceImportStatus.Replaced => "已用高版本替换音源",
                _ => "已导入并设为默认音源"
            };
            Announce($"{verb}：{result.Source.Name}", "source-import", AnnouncementKind.Success);
        }
        catch (Exception ex) { WinFormsUi.Error(this, ex, "导入音源"); }
    }

    private async void SetDefault()
    {
        var source = Selected();
        if (source is null) return;
        if (!await InSourceCommand(() => _sources.SetDefaultSourceAsync(source.Id)))
        {
            Announce($"默认音源保存失败：{source.Name}", "source-default", AnnouncementKind.Error);
            return;
        }
        RefreshSources();
        Announce($"默认音源：{source.Name}", "source-default", AnnouncementKind.Success);
    }

    private async Task EnableAsync()
    {
        var source = Selected();
        if (source is null) return;
        var result = await InSourceCommand(() => _sources.EnableSourceAsync(source));
        RefreshSources();
        Announce(result.Message, "source-enable", result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private async void Disable()
    {
        var source = Selected();
        if (source is null) return;
        if (!await InSourceCommand(() => _sources.DisableSourceAsync(source)))
        {
            Announce($"禁用音源保存失败：{source.Name}", "source-disable", AnnouncementKind.Error);
            return;
        }
        RefreshSources();
        Announce($"已禁用音源：{source.Name}", "source-disable", AnnouncementKind.Success);
    }

    private async Task TestAsync()
    {
        var source = Selected();
        if (source is null) return;
        var ok = await _sources.TestSourceAsync(source);
        Announce(ok ? $"音源测试成功：{source.Name}" : $"音源测试失败：{source.Name}", "source-test", ok ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private async Task UpdateAsync()
    {
        var source = Selected();
        if (source is null) return;
        var result = await InSourceCommand(() => _sources.RefreshSourceDetailedAsync(source));
        RefreshSources();
        if (!result.Succeeded)
            Announce($"音源更新失败：{source.Name}", "source-update", AnnouncementKind.Error);
        else if (result.Changed && result.TrustState == ScriptTrustState.Untrusted)
            Announce($"音源已更新但未信任：{source.Name}；需要显式启用", "source-update", AnnouncementKind.Error);
        else
            Announce($"音源更新成功：{source.Name}", "source-update", AnnouncementKind.Success);
    }

    private async void Delete() => await DeleteAsync();

    private async Task DeleteAsync(bool requireConfirmation = true)
    {
        var source = Selected();
        if (source is null) return;
        if (requireConfirmation && !_confirmDelete($"确定要删除音源“{source.Name}”吗？")) return;
        if (!await InSourceCommand(() => _sources.RemoveSourceAsync(source)))
        {
            Announce($"删除音源保存失败：{source.Name}", "source-delete", AnnouncementKind.Error);
            return;
        }
        RefreshSources();
        Announce($"已删除音源：{source.Name}", "source-delete", AnnouncementKind.Success);
    }

    /// <summary>
    /// Runs a mutation this window owns. The marker lets the deferred list refresh tell a change of its
    /// own command (already announced by that command) from a change that came from anywhere else.
    /// </summary>
    private async Task<T> InSourceCommand<T>(Func<Task<T>> operation)
    {
        _sourcesChangingByThisWindow = true;
        try
        {
            return await operation();
        }
        finally
        {
            _sourcesChangingByThisWindow = false;
        }
    }

    private MusicSource? Selected() => WinFormsUi.Selected<MusicSource>(_list);

    private void Announce(string message, string activity, AnnouncementKind kind = AnnouncementKind.Status) =>
        _announcer.Announce(new AccessibleAnnouncement(message, kind, activity));


    private static Button CreateButton(string text, int tabIndex, EventHandler onClick)
    {
        var button = new Button { Text = text };
        WinFormsUi.Name(button, text, tabIndex);
        button.Click += onClick;
        return button;
    }
}

internal enum DownloadWorkflowCommand
{
    Pause,
    Resume
}

/// <summary>Download manager backed by the existing DownloadWorkflow observable collection.</summary>
public class DownloadManagerForm : Form
{
    private readonly IAccessibleAnnouncer _announcer;
    private readonly IDownloadWorkflow _downloads;
    private readonly Func<KeyEventArgs, bool>? _handleHotkey;
    private readonly ListView _tasks = new() { Name = "DownloadTasks", View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, TabStop = true, TabIndex = 1, AccessibleName = "下载任务" };
    private readonly Label _empty = new() { Text = "暂无下载任务。", AutoSize = true, Name = "DownloadListStatus", TabStop = false };
    private readonly Button _cancel;
    private readonly Button _retry;
    private readonly Button _pause;
    private readonly Button _resume;
    private readonly Button _delete;
    private readonly Button _clearCompleted;
    private readonly ComboBox _filter = new ComboBox { Name = "DownloadTaskFilter", AccessibleName = "下载任务分类", DropDownStyle = ComboBoxStyle.DropDownList, TabIndex = 2 };
    private readonly NotifyCollectionChangedEventHandler _tasksChanged;
    private readonly PropertyChangedEventHandler _taskPropertyChanged;
    private readonly HashSet<DownloadTask> _observedTasks = new();
    private readonly object _observerGate = new();
    private readonly Func<string, bool> _confirmDelete;
    private DownloadTask? _menuTarget;
    private bool _downloadCommandBusy;

    public DownloadManagerForm(
        IAccessibleAnnouncer announcer,
        IDownloadWorkflow downloads,
        Func<KeyEventArgs, bool>? handleHotkey = null,
        Func<string, bool>? confirmDelete = null)
    {
        _announcer = announcer;
        _downloads = downloads;
        _handleHotkey = handleHotkey;
        _confirmDelete = confirmDelete ?? (message => WinFormsUi.Confirm(this, message));
        Text = "下载管理";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 580);
        KeyPreview = true;
        _tasks.Columns.Add("歌曲", 330);
        _tasks.Columns.Add("状态", 120);
        _tasks.Columns.Add("进度", 100);
        _tasks.Columns.Add("文件", 300);
        _tasks.ItemSelectionChanged += (_, _) => UpdateButtonState();
        _cancel = CreateButton("取消", 3, (_, _) => Cancel());
        _pause = CreateButton("暂停", 4, (_, _) => ChangeState(DownloadWorkflowCommand.Pause));
        _resume = CreateButton("继续", 5, (_, _) => ChangeState(DownloadWorkflowCommand.Resume));
        _retry = CreateButton("重试", 6, (_, _) => _ = Retry());
        _delete = CreateButton("删除", 7, (_, _) => _ = Delete());
        var open = CreateButton("打开文件夹", 8, (_, _) => _downloads.OpenDownloadDirectory());
        _clearCompleted = CreateButton("清除已完成", 9, (_, _) => ClearCompleted());
        _filter.Items.AddRange(new object[] { "全部", "下载中", "已完成", "失败/取消" });
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => Reload();
        _cancel.SetBounds(140, 12, 80, 28);
        _pause.SetBounds(228, 12, 80, 28);
        _resume.SetBounds(316, 12, 80, 28);
        _retry.SetBounds(404, 12, 80, 28);
        _delete.SetBounds(492, 12, 80, 28);
        open.SetBounds(580, 12, 105, 28);
        _clearCompleted.SetBounds(693, 12, 105, 28);
        _tasks.SetBounds(12, 48, 860, 440);
        _empty.SetBounds(20, 56, 300, 22);
        Controls.AddRange(new Control[] { _filter, _cancel, _pause, _resume, _retry, _delete, open, _clearCompleted, _tasks, _empty });
        // Same as in the source manager: the empty-state label must not be hidden behind the task list.
        _empty.BringToFront();
        _tasks.ContextMenu = BuildTaskMenu();
        _tasks.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _tasks.HitTest(e.Location);
            if (hit.Item is null) return;
            hit.Item.Selected = true;
            hit.Item.Focused = true;
            _menuTarget = hit.Item.Tag as DownloadTask;
        };
        Reload();
        _taskPropertyChanged = (_, e) => OnTaskPropertyChanged(e);
        _tasksChanged = (_, e) => OnTasksChanged(e);
        ((INotifyCollectionChanged)_downloads.Tasks).CollectionChanged += _tasksChanged;
        AttachTaskObservers();
        FormClosed += (_, _) => DetachTaskObservers();
        Shown += (_, _) => _tasks.Focus();

    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_handleHotkey?.Invoke(e) == true) { e.Handled = true; return; }
        if (e.KeyCode == Keys.Escape) Close();
    }

    private void AttachTaskObservers()
    {
        lock (_observerGate)
        {
            foreach (var task in _downloads.Tasks)
            {
                if (_observedTasks.Add(task))
                    task.PropertyChanged += _taskPropertyChanged;
            }
        }
    }

    private void DetachTaskObservers()
    {
        lock (_observerGate)
        {
            foreach (var task in _observedTasks)
                task.PropertyChanged -= _taskPropertyChanged;
            _observedTasks.Clear();
        }
    }

    private void SyncTaskObservers()
    {
        lock (_observerGate)
        {
            foreach (var task in _observedTasks)
                task.PropertyChanged -= _taskPropertyChanged;
            _observedTasks.Clear();

            foreach (var task in _downloads.Tasks)
            {
                if (_observedTasks.Add(task))
                    task.PropertyChanged += _taskPropertyChanged;
            }
        }
    }

    private void OnTasksChanged(NotifyCollectionChangedEventArgs e)
    {
        SyncTaskObservers();
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        BeginInvoke(Reload);
    }

    private void OnTaskPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (e.PropertyName is not nameof(DownloadTask.Status)
            and not nameof(DownloadTask.ProgressText)
            and not nameof(DownloadTask.AccessibleName))
        {
            return;
        }

        BeginInvoke(Reload);
    }
    private void Reload()
    {
        var selectedIds = Selected().Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        var focusedTask = (_tasks.FocusedItem?.Tag ?? _tasks.SelectedItems.OfType<ListViewItem>().FirstOrDefault()?.Tag) as DownloadTask;
        var listHadFocus = _tasks.Focused;
        _tasks.BeginUpdate();
        _tasks.Items.Clear();
        var category = _filter.SelectedItem?.ToString() ?? "全部";
        foreach (var task in _downloads.Tasks.Where(task => MatchesCategory(task, category)))
        {
            var item = new ListViewItem(task.DisplayName) { Tag = task };
            item.SubItems.Add(task.StatusText);
            item.SubItems.Add(task.ProgressText);
            item.SubItems.Add(task.SavePath ?? string.Empty);
            _tasks.Items.Add(item);
        }
        _tasks.EndUpdate();
        _empty.Visible = _tasks.Items.Count == 0;
        if (selectedIds.Count > 0)
        {
            foreach (ListViewItem item in _tasks.Items)
            {
                item.Selected = item.Tag is DownloadTask task && selectedIds.Contains(task.Id);
                if (focusedTask is not null && ReferenceEquals(item.Tag, focusedTask)) item.Focused = true;
            }
        }

        if (listHadFocus) _tasks.Focus();
        UpdateButtonState();
    }

    private List<DownloadTask> Selected() => _tasks.SelectedItems.Cast<ListViewItem>().Select(item => (DownloadTask)item.Tag).ToList();

    private void UpdateButtonState()
    {
        var selected = Selected();
        bool downloading = selected.Any(task => task.Status is DownloadTaskStatus.Pending or DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused);
        bool retryable = selected.Any(task => task.Status is DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled);
        var focused = ActiveControl;
        // Each task command keeps its own reference. Matching buttons by their caption stopped enabling or
        // disabling a command as soon as its text was reworded, and a command that can never enable is
        // unreachable for a user who cannot see that it is greyed out.
        _cancel.Enabled = downloading;
        _pause.Enabled = selected.Any(task => task.Status == DownloadTaskStatus.Downloading);
        _resume.Enabled = selected.Any(task => task.Status == DownloadTaskStatus.Paused);
        _delete.Enabled = selected.Count > 0;
        _retry.Enabled = retryable;
        _clearCompleted.Enabled = _downloads.Tasks.Any(task => task.Status == DownloadTaskStatus.Completed);
        if (_downloadCommandBusy)
        {
            _cancel.Enabled = false;
            _pause.Enabled = false;
            _resume.Enabled = false;
            _retry.Enabled = false;
            _delete.Enabled = false;
            _clearCompleted.Enabled = false;
        }

        // A progress update can disable the button the user is standing on, and a disabled control cannot
        // hold focus: WinForms hands it to the form, which leaves a screen reader with nothing to read.
        // The reading position returns to the task list the button acts on.
        if (focused is Button { Enabled: false }) _tasks.Focus();
    }

    private void Cancel()
    {
        var tasks = Selected();
        if (tasks.Count == 0 || !BeginDownloadCommand())
        {
            if (tasks.Count > 0) Announce("上一个下载命令仍在执行", "download-manager", AnnouncementKind.Error);
            return;
        }
        try
        {
            foreach (var task in tasks) _downloads.Cancel(task);
            Reload();
            Announce($"已请求取消 {tasks.Count} 个所选任务", "download-manager");
        }
        finally
        {
            _downloadCommandBusy = false;
        }
    }

    private void ChangeState(DownloadWorkflowCommand command)
    {
        var tasks = Selected();
        if (tasks.Count == 0 || !BeginDownloadCommand())
        {
            if (tasks.Count > 0) Announce("上一个下载命令仍在执行", "download-manager", AnnouncementKind.Error);
            return;
        }
        try
        {
            foreach (var task in tasks)
            {
                if (command == DownloadWorkflowCommand.Pause) _downloads.Pause(task);
                else if (command == DownloadWorkflowCommand.Resume) _downloads.Resume(task);
            }
            Reload();
            var verb = command == DownloadWorkflowCommand.Pause ? "暂停" : "继续";
            Announce($"已请求{verb} {tasks.Count} 个所选任务", "download-manager");
        }
        finally
        {
            _downloadCommandBusy = false;
        }
    }

    private async Task Delete()
    {
        var tasks = Selected();
        if (tasks.Count == 0) return;
        if (!BeginDownloadCommand())
        {
            Announce("上一个下载命令仍在执行", "download-manager", AnnouncementKind.Error);
            return;
        }
        var accepted = 0;
        var rejected = 0;
        try
        {
            if (!_confirmDelete(tasks.Count == 1
                ? "确定删除所选下载任务，并删除已下载源文件吗？"
                : $"确定删除 {tasks.Count} 个下载任务，并删除已下载源文件吗？")) return;

            foreach (var task in tasks)
            {
                if (await _downloads.DeleteAsync(task, deleteSourceFile: true)) accepted++;
                else rejected++;
            }

            Reload();
            if (accepted > 0 && rejected == 0)
                Announce("已删除所选任务", "download-manager-delete", AnnouncementKind.Success);
            else
                Announce($"下载删除结束：已删除 {accepted}，未删除 {rejected}", "download-manager-delete", rejected > 0 ? AnnouncementKind.Error : AnnouncementKind.Success);
        }
        finally
        {
            _downloadCommandBusy = false;
        }
    }

    private bool BeginDownloadCommand()
    {
        if (_downloadCommandBusy) return false;
        _downloadCommandBusy = true;
        return true;
    }

    private DownloadTask? GetMenuTarget()
    {
        var item = _tasks.FocusedItem ?? _tasks.SelectedItems.OfType<ListViewItem>().FirstOrDefault();
        return item?.Tag as DownloadTask;
    }

    internal void OnTaskMenuPopup()
    {
        _menuTarget = GetMenuTarget();
        UpdateButtonState();
    }

    private ContextMenu BuildTaskMenu()
    {
        var menu = new ContextMenu();
        var redownloadItem = new MenuItem("手动重新下载", async (_, _) => await RunFocusedRestartAsync(redownload: true));
        var retryItem = new MenuItem("重试下载", async (_, _) => await RunFocusedRestartAsync(redownload: false));
        var pauseItem = new MenuItem("暂停下载", (_, _) =>
        {
            if (_menuTarget is null) return;
            _downloads.Pause(_menuTarget);
            Announce("已请求暂停下载：" + _menuTarget.DisplayName, "download-manager-focused");
            Reload();
        });
        var resumeItem = new MenuItem("继续下载", (_, _) =>
        {
            if (_menuTarget is null) return;
            _downloads.Resume(_menuTarget);
            Announce("已请求继续下载：" + _menuTarget.DisplayName, "download-manager-focused");
            Reload();
        });
        var stopItem = new MenuItem("停止下载", (_, _) =>
        {
            if (_menuTarget is null || !BeginDownloadCommand())
            {
                if (_menuTarget is not null) Announce("上一个下载命令仍在执行", "download-manager", AnnouncementKind.Error);
                return;
            }
            try
            {
                _downloads.Cancel(_menuTarget);
                Announce("已请求停止下载：" + _menuTarget.DisplayName, "download-manager-focused");
                Reload();
            }
            finally
            {
                _downloadCommandBusy = false;
            }
        });
        var removeItem = new MenuItem("移除下载", async (_, _) => await RemoveFocusedAsync());
        menu.MenuItems.Add(redownloadItem);
        menu.MenuItems.Add(retryItem);
        menu.MenuItems.Add(pauseItem);
        menu.MenuItems.Add(resumeItem);
        menu.MenuItems.Add(stopItem);
        menu.MenuItems.Add(removeItem);
        menu.Popup += (_, _) => OnTaskMenuPopup(redownloadItem, retryItem, pauseItem, resumeItem, stopItem, removeItem);
        return menu;
    }

    internal void OnTaskMenuPopup(
        MenuItem? redownload = null,
        MenuItem? retry = null,
        MenuItem? pause = null,
        MenuItem? resume = null,
        MenuItem? stop = null,
        MenuItem? remove = null)
    {
        _menuTarget = GetMenuTarget();
        var status = _menuTarget?.Status;
        var active = status is DownloadTaskStatus.Pending or DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused;
        if (redownload is not null) redownload.Enabled = status == DownloadTaskStatus.Completed;
        if (retry is not null) retry.Enabled = status is DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled;
        if (pause is not null) pause.Enabled = status == DownloadTaskStatus.Downloading;
        if (resume is not null) resume.Enabled = status == DownloadTaskStatus.Paused;
        if (stop is not null) stop.Enabled = active;
        if (remove is not null) remove.Enabled = _menuTarget is not null;
        UpdateButtonState();
    }

    private async Task RunFocusedRestartAsync(bool redownload)
    {
        var task = _menuTarget;
        if (task is null) return;
        if (!BeginDownloadCommand())
        {
            Announce("上一个下载命令仍在执行", "download-manager", AnnouncementKind.Error);
            return;
        }
        try
        {
            Announce((redownload ? "已请求手动重新下载：" : "已请求重试下载：") + task.DisplayName, "download-manager-focused");
            var started = await (redownload
                ? _downloads.RedownloadAsync(task, null, CancellationToken.None)
                : _downloads.RetryAsync(task, null, CancellationToken.None));
            Reload();
            if (!started)
                Announce($"任务不能重新开始：{task.DisplayName}", "download-manager-focused", AnnouncementKind.Error);
            else if (task.Status == DownloadTaskStatus.Completed)
                Announce($"已完成重新下载：{task.DisplayName}", "download-manager-focused", AnnouncementKind.Success);
            else
                Announce($"{task.StatusText}：{task.DisplayName}", "download-manager-focused", AnnouncementKind.Error);
        }
        finally
        {
            _downloadCommandBusy = false;
        }
    }

    private async Task RemoveFocusedAsync()
    {
        var task = _menuTarget;
        if (task is null) return;
        if (!BeginDownloadCommand())
        {
            Announce("上一个下载命令仍在执行", "download-manager", AnnouncementKind.Error);
            return;
        }
        try
        {
            if (!_confirmDelete($"确定移除“{task.DisplayName}”及其已下载源文件吗？")) return;
            if (!await _downloads.DeleteAsync(task, deleteSourceFile: true))
            {
                Announce($"任务仍无法移除：{task.DisplayName}", "download-manager-delete", AnnouncementKind.Error);
                return;
            }
            Announce($"已移除：{task.DisplayName}", "download-manager-delete", AnnouncementKind.Success);
            Reload();
        }
        finally
        {
            _downloadCommandBusy = false;
        }
    }

private static bool MatchesCategory(DownloadTask task, string category) => category switch
    {
        "下载中" => task.Status is DownloadTaskStatus.Pending or DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused,
        "已完成" => task.Status == DownloadTaskStatus.Completed,
        "失败/取消" => task.Status is DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled,
        _ => true
    };

    private async Task Retry()
    {
        var candidates = Selected().Where(task => task.Status is DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled).ToList();
        if (candidates.Count == 0) return;
        if (!BeginDownloadCommand())
        {
            Announce("上一个下载命令仍在执行", "download-manager", AnnouncementKind.Error);
            return;
        }
        var accepted = 0;
        var rejected = 0;
        try
        {
            foreach (var task in candidates)
            {
                if (await _downloads.RetryAsync(task)) accepted++;
                else rejected++;
            }
            Reload();
            if (accepted > 0)
                Announce($"已请求重试所选下载：接受 {accepted}，拒绝 {rejected}", "download-manager");
            else
                Announce($"所选下载不能重试：拒绝 {rejected}", "download-manager", AnnouncementKind.Error);
        }
        finally
        {
            _downloadCommandBusy = false;
        }
    }

    private void ClearCompleted()
    {
        if (!BeginDownloadCommand())
        {
            Announce("上一个下载命令仍在执行", "download-manager", AnnouncementKind.Error);
            return;
        }
        try
        {
            _downloads.ClearCompleted();
            Reload();
            Announce("已清除已完成下载", "download-manager", AnnouncementKind.Success);
        }
        finally
        {
            _downloadCommandBusy = false;
        }
    }

    private void Announce(string message, string activity, AnnouncementKind kind = AnnouncementKind.Status) =>
        _announcer.Announce(new AccessibleAnnouncement(message, kind, activity));

    protected static Button CreateButton(string text, int tabIndex, EventHandler onClick)
    {
        var button = new Button { Text = text };
        WinFormsUi.Name(button, text, tabIndex);
        button.Click += onClick;
        return button;
    }
}

/// <summary>Playlist import workflow, including platform selection, preview and atomic import commands.</summary>
public class PlaylistImportForm : Form
{
    private readonly IPlaylistImportService _service;
    private readonly IAccessibleAnnouncer _announcer;
    private readonly IPlaylistLoginWindowFactory _login;
    private readonly Action<ListBox>? _attachSongList;
    private readonly Func<KeyEventArgs, bool>? _handleHotkey;
    private readonly ListBox _playlists = WinFormsUi.ListBox("个人歌单", 10);
    private readonly Button _defaultPlatform;
    private readonly Button _loadPlaylists;
    private readonly ListBox _songs = WinFormsUi.ListBox("歌曲预览", 11);
    private readonly TextBox _cookie = new() { Name = "Cookie", AccessibleName = "登录 Cookie", TabIndex = 8 };
    private readonly Label _cookieCaption = WinFormsUi.Caption("Cookie：", 7);
    private readonly Button _submitCookie = new() { Name = "使用 Cookie 登录", AccessibleName = "使用 Cookie 登录", Text = "使用 Cookie 登录", TabIndex = 9 };
    private readonly Label _account = new() { Text = "未登录", AutoSize = true, TabStop = false, Name = "ImportAccountStatus" };
    private readonly Label _status = new() { Text = "请选择音乐平台。", AutoSize = true, TabStop = false, Name = "ImportStatus" };
    private string? _platform;
    private IReadOnlyList<RemotePlaylist>? _remotePlaylists;
    private bool _busy;
    private bool _previewingSongs;

    public PlaylistImportForm(
        IPlaylistImportService service,
        IAccessibleAnnouncer announcer,
        IPlaylistLoginWindowFactory login,
        Action<ListBox> attachSongList,
        Func<KeyEventArgs, bool>? handleHotkey = null)
    {
        _service = service;
        _announcer = announcer;
        _login = login;
        _attachSongList = attachSongList;
        _handleHotkey = handleHotkey;
        Text = "导入个人歌单";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1050, 680);
        KeyPreview = true;
        _defaultPlatform = CreateButton("网易云音乐", 1, (sender, args) => _ = SelectPlatformCommandAsync("netease", sender));
        var qq = CreateButton("QQ音乐", 2, (sender, args) => _ = SelectPlatformCommandAsync("qq", sender));
        var kg = CreateButton("酷狗音乐", 3, (sender, args) => _ = SelectPlatformCommandAsync("kg", sender));
        var kw = CreateButton("酷我音乐", 4, (sender, args) => _ = SelectPlatformCommandAsync("kw", sender));
        var logout = CreateButton("退出登录", 5, (_, _) => _ = LogoutCommandAsync());
        // Kept as a field because it is the visible recovery command after a saved-session check or login load
        // fails; successful sign-in now starts the load immediately instead of parking focus here.
        _loadPlaylists = CreateButton("加载歌单", 6, (_, _) => _ = LoadPlaylistsCommandAsync());
        _cookieCaption.Name = "CookieCaption";
        var back = CreateButton("返回歌单列表", 12, (_, _) => ShowPlaylists());
        var preview = CreateButton("预览歌曲", 13, (_, _) => _ = PreviewCommandAsync());
        var importThis = CreateButton("导入当前歌单", 14, (_, _) => _ = ImportAsync());
        var importAll = CreateButton("导入全部歌单", 15, (_, _) => _ = ImportAllAsync());
        _submitCookie.Click += (_, _) => _ = CookieLoginCommandAsync();
        _defaultPlatform.SetBounds(12, 12, 110, 28); qq.SetBounds(130, 12, 100, 28); kg.SetBounds(238, 12, 100, 28); kw.SetBounds(346, 12, 100, 28);
        _account.SetBounds(460, 18, 300, 22); logout.SetBounds(770, 12, 90, 28); _loadPlaylists.SetBounds(868, 12, 95, 28);
        _cookieCaption.SetBounds(12, 52, 0, 0); _cookie.SetBounds(75, 48, 670, 24); _submitCookie.SetBounds(755, 47, 140, 28);
        _playlists.SetBounds(12, 85, 350, 500); _songs.SetBounds(370, 85, 650, 500);
        _status.SetBounds(12, 595, 800, 22); back.SetBounds(12, 620, 120, 28); preview.SetBounds(140, 620, 100, 28);
        importThis.SetBounds(248, 620, 130, 28);
        importAll.SetBounds(386, 620, 125, 28);
        Controls.AddRange(new Control[] { _defaultPlatform, qq, kg, kw, _account, logout, _loadPlaylists, _cookieCaption, _cookie, _submitCookie, _playlists, _songs, _status, back, preview, importThis, importAll });
        _playlists.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = PreviewCommandAsync(); } };
        // The preview list is a song list, and every song list in the application uses Enter for playback
        // (the shell attaches that binding when a preview is shown). Importing has its own command, so Enter
        // is not bound a second time here: one key must not play a song and start an import at once.
        // The preview list is single-selection by design and the import service can only import a whole
        // playlist, so there is deliberately no "select all songs"/"import selected songs" pair: those
        // commands would read as if they could import part of a playlist, which nothing behind them does.
        // ADR 0004: the import window opens on the platform command that starts the task. The playlist
        // list is empty until a platform is chosen and loaded, so focusing it would leave the reader with
        // a name and no content while the status line asks the user to choose a platform.
        Shown += async (_, _) => { await _service.EnsureAccountsLoadedAsync(); RefreshAccount(); _defaultPlatform.Focus(); };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_handleHotkey?.Invoke(e) == true) { e.Handled = true; return; }
        if (e.KeyCode != Keys.Escape) return;

        if (_previewingSongs)
        {
            e.Handled = true;
            ShowPlaylists();
            return;
        }

        Close();
    }
    /// <summary>
    /// Selecting a platform first verifies the saved session by loading personal playlists. The network
    /// answer, not the mere existence of a stored cookie, decides whether returning users are taken straight
    /// to their playlists or asked to sign in again.
    /// </summary>
    private async Task SelectPlatformCommandAsync(string platform, object? origin)
    {
        if (!TryBeginBusy()) return;
        try { await SelectPlatformAsync(platform, origin); }
        finally { EndBusy(); }
    }

    private async Task SelectPlatformAsync(string platform, object? origin)
    {
        _platform = platform;
        _remotePlaylists = null;
        _playlists.Items.Clear();
        _songs.Items.Clear();
        RefreshAccount();
        await _service.EnsureAccountsLoadedAsync();
        RefreshAccount();

        if (_service.GetAccount(platform) is not null)
        {
            _status.Text = "正在检测已保存登录状态，请稍候。";
            Announce(_status.Text, "playlist-import", AnnouncementKind.Busy);

            if (await LoadPlaylistsCoreAsync(reportErrors: false))
                return;

            _status.Text = "已保存登录状态无效，请重新登录。";
            Announce(_status.Text, "playlist-login", AnnouncementKind.Error);
            UpdateCookieControlsVisibility(showCookieLogin: true);
        }
        else
        {
            _status.Text = "已选择平台，请登录或加载歌单。";
            Announce(_status.Text, "playlist-import");
        }

        OpenLoginMethod(platform, origin, "\u5df2\u4fdd\u5b58\u767b\u5f55\u72b6\u6001\u65e0\u6548\u3002");
    }

    private void OpenLoginMethod(string platform, object? origin, string? reason = null)
    {
        switch (_login.SelectLoginMethod(platform, this))
        {
            case PlaylistLoginMethod.Web:
                FinishLogin(_login.ShowWebLogin(platform, this).Succeeded, "网页登录", origin);
                break;
            case PlaylistLoginMethod.KugouQr:
                FinishLogin(_login.ShowKugouQrLogin(this), "扫码登录", origin);
                break;
            case PlaylistLoginMethod.Cookie:
                UpdateCookieControlsVisibility(showCookieLogin: true);
                _status.Text = (string.IsNullOrEmpty(reason) ? "" : reason) + "请在 Cookie 输入框粘贴完整 Cookie，再按“使用 Cookie 登录”。";
                Announce(_status.Text, "playlist-login");
                _cookie.Focus();
                break;
            default:
                (origin as Control ?? _defaultPlatform).Focus();
                break;
        }
    }

    /// <summary>
    /// A successful window login now continues immediately into the verified playlist load, so the user does
    /// not have to find a second command before seeing whether the new session really works.
    /// </summary>
    private void FinishLogin(bool closed, string method, object? origin)
    {
        RefreshAccount();
        var loggedIn = closed && _platform is not null && _service.GetAccount(_platform) is not null;
        if (loggedIn)
        {
            _status.Text = _account.Text + "。正在加载个人歌单。";
            Announce(_status.Text, "playlist-login", AnnouncementKind.Success);
            _ = LoadPlaylistsCoreAsync();
        }
        else
        {
            _status.Text = method + "未完成。" + _account.Text + "。";
            Announce(_status.Text, "playlist-login", AnnouncementKind.Status);
            (origin as Control ?? _defaultPlatform).Focus();
        }
    }

    private void RefreshAccount()
    {
        var account = _platform is null ? null : _service.GetAccount(_platform);
        _account.Text = account is null ? "未登录" : $"已登录：{account.DisplayName ?? account.Platform}";
        UpdateCookieControlsVisibility();
    }

    /// <summary>Cookie entry is recovery UI: it is noise after a valid session and returns when login is required.</summary>
    private void UpdateCookieControlsVisibility(bool showCookieLogin = false)
    {
        var account = _platform is null ? null : _service.GetAccount(_platform);
        var visible = account is null || showCookieLogin;
        _cookieCaption.Visible = visible;
        _cookie.Visible = visible;
        _submitCookie.Visible = visible;
    }

    private async Task CookieLoginCommandAsync()
    {
        if (!TryBeginBusy()) return;
        try { await CookieLoginAsync(); }
        finally { EndBusy(); }
    }

    private async Task CookieLoginAsync()
    {
        if (_platform is null || string.IsNullOrWhiteSpace(_cookie.Text)) return;
        var result = await _service.LoginWithCookieAsync(_platform, _cookie.Text.Trim());
        RefreshAccount();
        if (result is null)
        {
            _status.Text = "Cookie 登录失败。";
            Announce(_status.Text, "playlist-login", AnnouncementKind.Error);
            return;
        }

        _status.Text = $"Cookie 登录成功：{result.DisplayName}。正在加载个人歌单。";
        Announce(_status.Text, "playlist-login", AnnouncementKind.Success);
        await LoadPlaylistsCoreAsync();
    }

    private async Task LogoutCommandAsync()
    {
        if (!TryBeginBusy()) return;
        try { await LogoutAsync(); }
        finally { EndBusy(); }
    }

    private async Task LogoutAsync()
    {
        if (_platform is null) return;
        await _service.LogoutAsync(_platform);
        RefreshAccount();
        _status.Text = "已退出登录。";
        Announce(_status.Text, "playlist-login");
    }

    private async Task LoadPlaylistsCommandAsync()
    {
        if (!TryBeginBusy()) return;
        try { await LoadPlaylistsCoreAsync(); }
        finally { EndBusy(); }
    }

    private async Task<bool> LoadPlaylistsCoreAsync(bool reportErrors = true)
    {
        var platform = _platform;
        if (platform is null) return false;
        try
        {
            var playlists = await _service.GetMyPlaylistsAsync(platform);
            if (!string.Equals(platform, _platform, StringComparison.Ordinal)) return false;
            _remotePlaylists = playlists;
            _playlists.Items.Clear();
            foreach (var playlist in _remotePlaylists)
                _playlists.Items.Add(new ObjectBox<RemotePlaylist>(playlist.Name, playlist));
            _status.Text = _remotePlaylists.Count == 0
                ? "没有找到个人歌单。"
                : "已加载个人歌单，回车可预览。";
            Announce(_status.Text, "playlist-import", AnnouncementKind.Success);
            ShowPlaylists();
            return true;
        }
        catch (Exception ex)
        {
            if (!string.Equals(platform, _platform, StringComparison.Ordinal)) return false;
            _remotePlaylists = null;
            _playlists.Items.Clear();
            if (reportErrors)
            {
                WinFormsUi.Error(this, ex, "加载歌单");
            }
            else
            {
                _status.Text = "登录状态检测失败：" + ex.Message;
                Announce(_status.Text, "playlist-import", AnnouncementKind.Error);
            }

            return false;
        }
    }

    private async Task PreviewCommandAsync()
    {
        if (!TryBeginBusy()) return;
        try { await PreviewAsync(); }
        finally { EndBusy(); }
    }

    private async Task PreviewAsync()
    {
        if (_platform is null || _remotePlaylists is null || _playlists.SelectedItem is not ObjectBox<RemotePlaylist> box) return;
        try
        {
            _previewingSongs = false;
            var songs = await _service.GetPlaylistSongsPreviewAsync(_platform, box.Value);
            _songs.BeginUpdate(); _songs.Items.Clear();
            foreach (var song in songs) _songs.Items.Add(new ObjectBox<MusicInfo>(song.AccessibleName, song));
            _songs.EndUpdate();
            _attachSongList?.Invoke(_songs);
            _status.Text = songs.Count == 0 ? "没有找到歌曲。" : "已预览歌曲。";
            // The preview list takes focus immediately afterwards, and the reader names the list and its
            // selected item by itself, so repeating 已预览歌曲 only interrupts it. An empty list reads as
            // nothing, therefore only that outcome still speaks. The text reaches the live region either way.
            Announce(
                _status.Text,
                "playlist-preview",
                AnnouncementKind.Status,
                songs.Count == 0 ? AnnouncementSpeech.FollowPreference : AnnouncementSpeech.None);
            _songs.Focus();
            _previewingSongs = true;
        }
        catch (Exception ex) { WinFormsUi.Error(this, ex, "预览歌单"); }
    }

    private async Task ImportAsync()
    {
        if (_busy || _platform is null || _playlists.SelectedItem is not ObjectBox<RemotePlaylist> box) return;
        TryBeginBusy();
        try
        {
            var result = await _service.ImportPlaylistAsync(_platform, box.Value, message => { _status.Text = message; Announce(message, "playlist-import", AnnouncementKind.Busy); });
            _status.Text = $"导入完成：{result.PlaylistName}，新增 {result.ImportedCount}，跳过 {result.SkippedCount}。";
            Announce(_status.Text, "playlist-import", AnnouncementKind.Success);
        }
        catch (Exception ex) { WinFormsUi.Error(this, ex, "导入歌单"); }
        finally { EndBusy(); }
    }

    private async Task ImportAllAsync()
    {
        if (_busy || _platform is null || _remotePlaylists is null || _remotePlaylists.Count == 0) return;
        TryBeginBusy();
        try
        {
            _status.Text = $"正在导入 {_remotePlaylists.Count} 个歌单，请稍候。";
            Announce(_status.Text, "playlist-import", AnnouncementKind.Busy);
            var results = await _service.ImportPlaylistsAsync(_platform, _remotePlaylists, message => { _status.Text = message; Announce(message, "playlist-import", AnnouncementKind.Busy); });
            var imported = results.Where(result => result.Succeeded).Sum(result => result.ImportedCount);
            var skipped = results.Where(result => result.Succeeded).Sum(result => result.SkippedCount);
            var failed = results.Count(result => !result.Succeeded);
            var pending = Math.Max(0, _remotePlaylists.Count - results.Count);
            _status.Text = $"批量导入结束：成功 {results.Count - failed} 个歌单，新增 {imported}，跳过 {skipped}"
                + (failed > 0 ? $"，失败 {failed}" : string.Empty)
                + (pending > 0 ? $"，未开始 {pending}" : string.Empty) + "。";
            Announce(_status.Text, "playlist-import",
                failed > 0 || pending > 0 ? AnnouncementKind.Error : AnnouncementKind.Success);
        }
        catch (Exception ex) { WinFormsUi.Error(this, ex, "导入全部歌单"); }
        finally { EndBusy(); }
    }

    private void ShowPlaylists()
    {
        _previewingSongs = false;
        _playlists.Focus();
        // Moving the focus to the playlist list is the whole result of this command, and the reader names
        // the newly focused list by itself. The text still reaches the live region to be re-read.
        Announce("已返回歌单列表", "playlist-import", speech: AnnouncementSpeech.None);
    }

    private void Announce(
        string message,
        string activity,
        AnnouncementKind kind = AnnouncementKind.Status,
        AnnouncementSpeech speech = AnnouncementSpeech.FollowPreference) =>
        _announcer.Announce(new AccessibleAnnouncement(message, kind, activity, speech));


    private bool TryBeginBusy()
    {
        if (_busy) return false;
        _busy = true;
        return true;
    }

    private void EndBusy() => _busy = false;

    private static Button CreateButton(string text, int tabIndex, EventHandler onClick)
    {
        var button = new Button { Text = text };
        WinFormsUi.Name(button, text, tabIndex);
        button.Click += onClick;
        return button;
    }
}

/// <summary>Live lyrics reader bound to LyricsWorkflow.</summary>
public class LyricsForm : Form
{
    private readonly IAccessibleAnnouncer _announcer;
    private readonly LyricsWorkflow _lyrics;
    private readonly Func<KeyEventArgs, bool> _handleHotkey;
    private readonly LyricsTextBox _text = new() { Multiline = true, ReadOnly = true, ScrollBars = RichTextBoxScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window, TabIndex = 1 };
    private readonly Label _title = new() { Text = "暂无歌词", AutoSize = true, TabStop = false, Dock = DockStyle.Top, Name = "LyricsSongInfo" };
    private readonly Button _copy = new() { Text = "复制歌词", Dock = DockStyle.Bottom };
    private EventHandler<LyricsState>? _lyricsStateChanged;
    private string _rendered = string.Empty;

    public LyricsForm(IAccessibleAnnouncer announcer, LyricsWorkflow lyrics, Func<KeyEventArgs, bool> handleHotkey)
    {
        _announcer = announcer;
        _lyrics = lyrics;
        _handleHotkey = handleHotkey;
        Text = "歌词";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 600);
        KeyPreview = true;
        Controls.Add(_text);
        Controls.Add(_copy);
        Controls.Add(_title);
        WinFormsUi.Name(_text, "歌词", 1);
        WinFormsUi.Name(_copy, "复制歌词", 2);
        _copy.Click += (_, _) =>
        {
            Clipboard.SetDataObject(_text.Text.Length == 0 ? string.Empty : _text.Text, true, 5, 100);
            Announce("歌词已复制", "lyrics", AnnouncementKind.Success);
        };
        if (lyrics is not null)
        {
            _lyricsStateChanged = (_, state) =>
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                try { BeginInvoke(() => { if (!IsDisposed && !Disposing) Render(state); }); }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
                {
                    // The viewer was closed while a background lyric update was in flight.
                }
            };
            lyrics.StateChanged += _lyricsStateChanged;
            FormClosed += (_, _) => lyrics.StateChanged -= _lyricsStateChanged;
        }
        Render(lyrics?.Current ?? LyricsState.Empty);
        Shown += (_, _) => _text.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Control && e.Shift && e.KeyCode == Keys.C)
        {
            e.Handled = true;
            _copy.PerformClick();
            return;
        }
        if (_handleHotkey(e))
        {
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Escape) Close();
    }

    private void Render(LyricsState state)
    {
        _title.Text = state.Music is null
            ? string.IsNullOrWhiteSpace(state.Message) ? "暂无歌词" : state.Message
            : $"歌词：{state.Music.Artist}，{state.Music.Title}";

        var lyricText = state.Lines.Count == 0
            ? state.Message
            : string.Join(Environment.NewLine, state.Lines.Select(line => line.DisplayText));
        var caret = _text.SelectionStart;
        var selected = _text.SelectionLength;
        var replaced = lyricText != _rendered;
        _rendered = lyricText;
        _text.Text = lyricText;

        // 设置→歌词→自动滚动歌词 decides whether the viewer moves the reading position to the line that is
        // being sung. Switched off, the caret stays exactly where the user put it, so a reader can look ahead
        // or behind while the song plays and the current line still arrives through 自动朗读当前歌词.
        // A brand new lyric replaces the whole text, so in that case the position starts from the top again.
        if (state.AutoScroll) HighlightCurrentLine(state);
        else if (!replaced) RestoreReadingPosition(caret, selected);
    }

    private void RestoreReadingPosition(int start, int length)
    {
        if (start > _text.TextLength) start = _text.TextLength;
        if (length > _text.TextLength - start) length = _text.TextLength - start;
        _text.Select(start, length);
    }

    private void HighlightCurrentLine(LyricsState state)
    {
        if (state.CurrentIndex < 0 || state.CurrentIndex >= state.Lines.Count)
        {
            _text.SelectionStart = 0;
            _text.SelectionLength = 0;
            return;
        }

        // RichTextBox normalizes its Text to LF and a lyric can wrap into several visual lines.
        // Map the logical lyric to visual lines so the whole current entry is selected for UIA.
        var visualLines = _text.Lines;
        var firstVisualLine = state.Lines.Take(state.CurrentIndex)
            .Sum(line => line.DisplayText.Count(character => character == '\n') + 1);
        var visualLineCount = state.Lines[state.CurrentIndex].DisplayText
            .Count(character => character == '\n') + 1;
        if (firstVisualLine + visualLineCount > visualLines.Length) return;

        var start = _text.GetFirstCharIndexFromLine(firstVisualLine);
        var lastLine = firstVisualLine + visualLineCount - 1;
        var length = _text.GetFirstCharIndexFromLine(lastLine) + visualLines[lastLine].Length - start;
        if (start < 0 || length <= 0) return;

        _text.Select(start, length);
        _text.SelectionBackColor = SystemColors.Highlight;
        _text.SelectionColor = SystemColors.HighlightText;
        _text.ScrollToCaret();
        _text.Select(start, length);
    }

    private sealed class LyricsTextBox : RichTextBox
    {
        protected override AccessibleObject CreateAccessibilityInstance() => new LyricsAccessibleObject(this);

        private sealed class LyricsAccessibleObject(Control owner) : ControlAccessibleObject(owner)
        {
            public override AccessibleRole Role => AccessibleRole.Document;
        }
    }

    private void Announce(string message, string activity, AnnouncementKind kind = AnnouncementKind.Status) =>
        _announcer.Announce(new AccessibleAnnouncement(message, kind, activity));
}

/// <summary>Manual exact source switching window.</summary>
public class ManualSourceSwitchForm : Form
{
    private readonly IAccessibleAnnouncer _announcer;
    private readonly IManualSourceDiscoveryWorkflow _workflow;
    private MusicInfo? _target;
    private readonly Func<KeyEventArgs, bool> _handleHotkey;
    private readonly ListBox _options = WinFormsUi.ListBox("可用音源", 1);
    private readonly Label _targetLabel = new() { AutoSize = true, TabStop = false, Name = "ManualTargetSong" };
    private readonly Label _status = new() { Text = "按 Enter 搜索可用音源。", AutoSize = true, TabStop = false, Name = "ManualSwitchStatus" };
    private CancellationTokenSource? _searchCancellation;
    private int _searchGeneration;

    public ManualSourceSwitchForm(
        IAccessibleAnnouncer announcer,
        IManualSourceDiscoveryWorkflow workflow,
        MusicInfo? target,
        Func<KeyEventArgs, bool> handleHotkey)
    {
        _announcer = announcer;
        _workflow = workflow;
        _target = target;
        _handleHotkey = handleHotkey;
        Text = "切换音源";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 540);
        KeyPreview = true;
        var search = CreateButton("搜索可用音源", 2, (_, _) => _ = SearchAsync());
        var play = CreateButton("用选中音源播放", 3, (_, _) => _ = PlayAsync());
        DescribeTarget();
        _targetLabel.SetBounds(12, 15, 0, 0);
        search.SetBounds(12, 40, 130, 28); play.SetBounds(150, 40, 140, 28);
        _status.SetBounds(12, 76, 600, 22);
        _options.SetBounds(12, 105, 680, 350);
        Controls.AddRange(new Control[] { _targetLabel, search, play, _status, _options });
        _options.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = PlayAsync(); } };
        Shown += (_, _) => { search.Focus(); if (_target is not null) _ = SearchAsync(); };
        FormClosed += (_, _) => CancelSearch();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_handleHotkey(e)) { e.Handled = true; return; }
        if (e.KeyCode == Keys.Escape) Close();
    }
    private async Task SearchAsync()
    {
        if (_target is null || IsDisposed) return;
        var target = _target;
        var generation = ++_searchGeneration;
        var cancellation = new CancellationTokenSource();
        var previous = _searchCancellation;
        _searchCancellation = cancellation;
        previous?.Cancel();
        const string searching = "正在查询可用的音源，请稍候。";
        _status.Text = searching;
        Announce(searching, "manual-source-busy:" + generation, AnnouncementKind.Busy);
        try
        {
            var result = await _workflow.DiscoverAsync(target, cancellation.Token);
            if (generation != _searchGeneration || IsDisposed) return;
            RenderResult(result);
        }
        catch (OperationCanceledException) when (generation != _searchGeneration || IsDisposed)
        {
            // A newer search (or closing the window) superseded this one; its own result wins.
        }
        catch (Exception ex)
        {
            if (generation != _searchGeneration || IsDisposed) return;
            WinFormsUi.Error(this, ex, "搜索音源");
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
                _searchCancellation = null;
            cancellation.Dispose();
        }
    }

    private void RenderResult(ManualSourceDiscoveryResult result)
    {
        // Unavailable candidates used to sit in the list as disabled rows. They only repeated the
        // same "解析失败" line and pushed the actually playable sources out of the reading position,
        // so the list now carries playable results only and the status reports how many were hidden.
        var playable = result.Options.Where(option => option.IsPlayable).ToArray();
        _options.Items.Clear();
        foreach (var option in playable)
            _options.Items.Add(new ObjectBox<ManualSourceOption>($"{option.DisplayLabel}，{option.Platform}", option));
        _status.Text = result.Message;
        var kind = result.Status == ManualSourceDiscoveryStatus.Success && playable.Length > 0
            ? AnnouncementKind.Success
            : AnnouncementKind.Error;
        Announce(result.Message, "manual-source-query:" + Guid.NewGuid().ToString("N"), kind);
        if (_options.Items.Count > 0)
        {
            _options.SelectedIndex = 0;
            // The results are the point of the command, so the reading position moves to the first one
            // instead of staying on the button that was just pressed.
            _options.Focus();
        }
    }

    private void CancelSearch()
    {
        _searchGeneration++;
        var cancellation = _searchCancellation;
        _searchCancellation = null;
        cancellation?.Cancel();
    }

    private void DescribeTarget() => _targetLabel.Text = _target is null
        ? "未选择歌曲"
        : $"目标歌曲：{_target.Artist}，{_target.Title}";

    /// <summary>
    /// Repoints the one allowed instance of this window at another song. Two windows titled 切换音源 cannot be
    /// told apart in Alt+Tab or in a screen reader's window list, so the second command brings the existing
    /// window forward and searches for the new target instead of opening an identical copy.
    /// </summary>
    internal void ShowTarget(MusicInfo? music)
    {
        _target = music;
        DescribeTarget();
        _options.Items.Clear();
        _status.Text = "按 Enter 搜索可用音源。";
        if (_target is not null) _ = SearchAsync();
        else CancelSearch();
    }

    private async Task PlayAsync()
    {
        if (_target is null || _options.SelectedItem is not ObjectBox<ManualSourceOption> box) return;
        if (!box.Value.IsPlayable)
        {
            var unavailable = box.Value.StateMessage ?? "所选音源音质不可用";
            _status.Text = unavailable;
            Announce(unavailable, "manual-source-unavailable:" + Guid.NewGuid().ToString("N"), AnnouncementKind.Error);
            return;
        }

        var result = await _workflow.PlayAsync(new ManualSourcePlaybackRequest(new[] { _target }, _target, box.Value, PlaybackQueueOrigin.Search));
        _status.Text = result.Status is PlaybackResultStatus.Playing or PlaybackResultStatus.QueueCompleted ? "已用选中音源播放。" : "音源播放失败。";
        Announce(_status.Text, "manual-source-playback:" + Guid.NewGuid().ToString("N"), result.Status is PlaybackResultStatus.Playing or PlaybackResultStatus.QueueCompleted ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private void Announce(string message, string activity, AnnouncementKind kind = AnnouncementKind.Status) =>
        _announcer.Announce(new AccessibleAnnouncement(message, kind, activity));


    private static Button CreateButton(string text, int tabIndex, EventHandler onClick)
    {
        var button = new Button { Text = text };
        WinFormsUi.Name(button, text, tabIndex);
        button.Click += onClick;
        return button;
    }
}

/// <summary>User guide with a chapter ComboBox and native read-only text.</summary>
public class UserGuideForm : Form
{
    private readonly IAccessibleAnnouncer _announcer;
    // The reading surface needs its own place in the tab order: chapter (1), guide text (2), close (3),
    // so a keyboard user reaches the new chapter content with a single Tab after changing the chapter.
    private readonly TextBox _guide = new() { Name = "使用说明", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window, AccessibleName = "使用说明", TabIndex = 2 };
    private readonly ComboBox _chapter = WinFormsUi.Combo("指南章节", 1);

    public UserGuideForm(IAccessibleAnnouncer announcer)
    {
        _announcer = announcer;
        Text = "使用说明";
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(820, 640);
        var top = new Panel { Dock = DockStyle.Top, Height = 36, TabStop = false };
        var caption = WinFormsUi.Caption("章节：", 1);
        caption.SetBounds(10, 8, 0, 0);
        _chapter.SetBounds(65, 5, 220, 24);
        _chapter.Items.AddRange(new object[] { "主窗口", "搜索与播放", "歌单", "设置", "快捷键", "无障碍支持" });
        _chapter.SelectedIndex = 0;
        top.Controls.Add(caption);
        top.Controls.Add(_chapter);
        var close = new Button { Text = "关闭", Dock = DockStyle.Bottom };
        WinFormsUi.Name(close, "关闭", 3);
        Controls.Add(_guide);
        Controls.Add(close);
        Controls.Add(top);
        close.Click += (_, _) => Close();
        _chapter.SelectedIndexChanged += (_, _) => Render(_chapter.SelectedIndex);
        Render(0);
        Shown += (_, _) => _guide.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }
    private void Render(int chapter)
    {
        _guide.Text = chapter switch
        {
            0 => "主窗口包含文件、播放、导航、工具和帮助菜单。\nCtrl+1 到 Ctrl+4 切换四个音乐页面，Ctrl+5 到 Ctrl+8 打开工具窗口。\nEsc 可隐藏窗口或清空搜索。",
            1 => "在搜索关键词中输入内容后按 Enter 或选择搜索。\n列表中选择歌曲后按 Enter 或 Space 播放。\nF5 播放暂停，F6 停止，F3/F4 切歌。",
            2 => "播放列表用于在线收藏；分类歌单和排行榜需先选择分类或榜单。\n列表选择不会自动联网，回车后才加载或播放。\n右键菜单可收藏歌曲或整张歌单。",
            3 => "设置中的普通项目修改后立即生效，关闭时保存。\n路径输入框按 Enter 保存，朗读声音与语速改动立即生效，下次启动仍按保存值恢复。\n快捷键页选择项目后按 Enter 录制，Delete 清除。",
            4 => "F5 播放/暂停，F6 停止，F3 上一首，F4 下一首。\nF7/F8 快退快进，F9/F10 音量，F11/F12 设备，F1 模式。",
            _ => "所有焦点控件都有语义名称和明确 Tab 顺序。\n状态、进度、错误和完成既朗读，也写进主窗口底部的播报行；关闭“语音播报”后播报行仍可查看。\n列表选择和焦点变化不会触发联网、播放或删除。"
        };
        _guide.SelectionStart = 0;
        _guide.ScrollToCaret();
        // The reader already speaks the new chapter when the combo box selection changes, and the new text
        // is one Tab away, so repeating the chapter name in TTS only interrupts the reader. The live region
        // still carries the sentence for anyone who wants to re-read it.
        Announce("已切换到" + _chapter.Text, "guide", AnnouncementSpeech.None);
    }

    private void Announce(string message, string activity, AnnouncementSpeech speech = AnnouncementSpeech.FollowPreference) =>
        _announcer.Announce(new AccessibleAnnouncement(message, AnnouncementKind.Status, activity, speech));
}

/// <summary>Native song detail list dialog.</summary>
public class SongDetailForm : Form
{
    public SongDetailForm(string songTitle, IReadOnlyList<string> detailLines)
    {
        Text = "歌曲详情：" + songTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 320);
        var title = new Label { Text = "歌曲详情：" + songTitle, AutoSize = true, TabStop = false };
        title.SetBounds(12, 10, 0, 0);
        var list = WinFormsUi.ListBox("歌曲详情", 1);
        list.SetBounds(12, 35, 495, 220);
        foreach (var line in detailLines) list.Items.Add(line);
        var close = new Button { Text = "关闭", DialogResult = DialogResult.Cancel };
        WinFormsUi.Name(close, "关闭", 2);
        close.SetBounds(410, 270, 95, 30);
        Controls.AddRange(new Control[] { title, list, close });
        CancelButton = close;
        list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
    }
}

/// <summary>Sponsor QR window with an image and explicit close control.</summary>
public class SponsorForm : Form
{
    public SponsorForm(IAccessibleAnnouncer announcer)
    {
        Text = "赞助";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(430, 480);
        var picture = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Fill,
            AccessibleName = "赞助二维码",
            TabStop = false
        };
        var image = Path.Combine(AppContext.BaseDirectory, "Resources", "sponsor_qr.png");
        if (File.Exists(image)) picture.Image = System.Drawing.Image.FromFile(image);
        var close = new Button { Text = "关闭", Dock = DockStyle.Bottom };
        WinFormsUi.Name(close, "关闭", 1);
        Controls.Add(picture);
        Controls.Add(close);
        close.Click += (_, _) => Close();
        CancelButton = close;
        Shown += (_, _) =>
        {
            announcer.Announce(new("赞助二维码已显示。本软件所有功能均免费，如蒙赞助支持，请用手机扫描窗口中的二维码。", AnnouncementKind.Status, "sponsor"));
            close.Focus();
        };
    }
}
