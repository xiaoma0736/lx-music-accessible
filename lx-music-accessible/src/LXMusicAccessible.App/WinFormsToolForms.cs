using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
    private readonly ListBox _list = WinFormsUi.ListBox("音源", 1);
    private readonly Label _empty = new() { Text = "暂无自定义音源。", AutoSize = true, TabStop = false };
    private string _sourceSignature = string.Empty;

    public SourceManagerForm(IAccessibleAnnouncer announcer, IMusicSourceService sources, Func<KeyEventArgs, bool> handleHotkey)
    {
        _announcer = announcer;
        _sources = sources;
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
        var menu = new ContextMenu();
        menu.MenuItems.Add(new MenuItem("设为默认", (_, _) => SetDefault()));
        menu.MenuItems.Add(new MenuItem("启用", async (_, _) => await EnableAsync()));
        menu.MenuItems.Add(new MenuItem("禁用", (_, _) => Disable()));
        menu.MenuItems.Add(new MenuItem("测试", async (_, _) => await TestAsync()));
        menu.MenuItems.Add(new MenuItem("检查更新", async (_, _) => await UpdateAsync()));
        menu.MenuItems.Add(new MenuItem("删除", (_, _) => Delete()));
        _list.ContextMenu = menu;
        _list.KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; menu.Show(_list, new Point(10, 10)); } };
        _sources.SourcesChanged += (_, _) => BeginInvoke(RefreshSourcesAfterExternalChange);
        RefreshSources();
        Shown += (_, _) => _list.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }
    private void RefreshSourcesAfterExternalChange()
    {
        var before = _sourceSignature;
        RefreshSources();
        if (before != _sourceSignature) Announce("音源列表已更新", "source-list");
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
        await ImportAsync(File.ReadAllText(dialog.FileName), dialog.FileName);
    }

    private async void ImportNetwork()
    {
        var url = WinFormsUi.AskText(this, "导入网络音源", "音源地址：");
        if (string.IsNullOrWhiteSpace(url)) return;
        using var client = new System.Net.Http.HttpClient();
        try
        {
            var content = await client.GetStringAsync(url);
            await ImportAsync(content, url);
        }
        catch (Exception ex) { WinFormsUi.Error(this, ex, "下载音源"); }
    }

    private async Task ImportAsync(string content, string label)
    {
        try
        {
            var source = await _sources.ImportFromUrlAsync(label) ?? await _sources.ImportFromFileAsync(label);
            if (source is null)
            {
                Announce("导入音源失败，请检查脚本格式。", "source-import", AnnouncementKind.Error);
                return;
            }
            source.ScriptContent = content;
            RefreshSources();
            Announce($"已导入音源：{source.Name}", "source-import", AnnouncementKind.Success);
        }
        catch (Exception ex) { WinFormsUi.Error(this, ex, "导入音源"); }
    }

    private void SetDefault()
    {
        var source = Selected();
        if (source is null) return;
        _sources.SetDefaultSource(source.Id);
        RefreshSources();
        Announce($"默认音源：{source.Name}", "source-default", AnnouncementKind.Success);
    }

    private async Task EnableAsync()
    {
        var source = Selected();
        if (source is null) return;
        var result = await _sources.EnableSourceAsync(source);
        RefreshSources();
        Announce(result.Message, "source-enable", result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private void Disable()
    {
        var source = Selected();
        if (source is null) return;
        _sources.DisableSource(source);
        RefreshSources();
        Announce($"已禁用音源：{source.Name}", "source-enable");
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
        var ok = await _sources.RefreshSourceAsync(source);
        RefreshSources();
        Announce(ok ? $"音源更新成功：{source.Name}" : $"音源更新失败：{source.Name}", "source-update", ok ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private void Delete()
    {
        var source = Selected();
        if (source is null || !WinFormsUi.Confirm(this, $"确定要删除音源“{source.Name}”吗？")) return;
        _sources.RemoveSource(source);
        RefreshSources();
        Announce($"已删除音源：{source.Name}", "source-delete", AnnouncementKind.Success);
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

/// <summary>Download manager backed by the existing DownloadWorkflow observable collection.</summary>
public class DownloadManagerForm : Form
{
    private readonly IAccessibleAnnouncer _announcer;
    private readonly IDownloadWorkflow _downloads;
    private readonly ListView _tasks = new() { Name = "DownloadTasks", View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, TabStop = true, TabIndex = 1, AccessibleName = "下载任务" };
    private readonly Label _empty = new() { Text = "暂无下载任务。", AutoSize = true, TabStop = false };

    public DownloadManagerForm(IAccessibleAnnouncer announcer, IDownloadWorkflow downloads)
    {
        _announcer = announcer;
        _downloads = downloads;
        _tasks.AccessibleName = "下载任务";
        Text = "下载管理";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 580);
        KeyPreview = true;
        _tasks.Columns.Add("歌曲", 330);
        _tasks.Columns.Add("状态", 120);
        _tasks.Columns.Add("进度", 100);
        _tasks.Columns.Add("文件", 300);
        _tasks.ItemSelectionChanged += (_, _) => UpdateButtonState();
        var cancel = CreateButton("取消", 2, (_, _) => Cancel());
        var retry = CreateButton("重试", 3, (_, _) => _ = Retry());
        var open = CreateButton("打开文件夹", 4, (_, _) => _downloads.OpenDownloadDirectory());
        var clear = CreateButton("清除已完成", 5, (_, _) => ClearCompleted());
        cancel.SetBounds(12, 12, 90, 28);
        retry.SetBounds(110, 12, 90, 28);
        open.SetBounds(208, 12, 105, 28);
        clear.SetBounds(321, 12, 105, 28);
        _tasks.SetBounds(12, 48, 860, 440);
        _empty.SetBounds(20, 56, 300, 22);
        Controls.AddRange(new Control[] { cancel, retry, open, clear, _tasks, _empty });
        _tasks.AccessibleName = "下载任务";
        Reload();
        ((INotifyCollectionChanged)_downloads.Tasks).CollectionChanged += (_, _) => BeginInvoke(Reload);
        Shown += (_, _) => _tasks.Focus();
        if (_tasks.Items.Count == 0) _empty.Visible = true;
    }

    protected TextBox? RecordingBox { get; set; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }
    private void Reload()
    {
        var selectedIds = Selected().Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        var listHadFocus = _tasks.Focused;
        _tasks.BeginUpdate();
        _tasks.Items.Clear();
        foreach (var task in _downloads.Tasks)
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
                item.Selected = item.Tag is DownloadTask task && selectedIds.Contains(task.Id);
        }

        if (listHadFocus) _tasks.Focus();
        UpdateButtonState();
    }

    private List<DownloadTask> Selected() => _tasks.SelectedItems.Cast<ListViewItem>().Select(item => (DownloadTask)item.Tag).ToList();

    private void UpdateButtonState()
    {
        var selected = Selected();
        bool downloading = selected.Any(task => task.Status is DownloadTaskStatus.Pending or DownloadTaskStatus.Downloading);
        bool retryable = selected.Any(task => task.Status is DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled);
        foreach (var button in Controls.OfType<Button>())
        {
            if (button.Text == "取消") button.Enabled = downloading;
            if (button.Text == "重试") button.Enabled = retryable;
            if (button.Text == "清除已完成") button.Enabled = _downloads.Tasks.Any(task => task.Status == DownloadTaskStatus.Completed);
        }
    }

    private void Cancel()
    {
        foreach (var task in Selected()) _downloads.Cancel(task);
        Announce("已请求取消所选下载", "download-manager");
        Reload();
    }

    private async Task Retry()
    {
        foreach (var task in Selected().Where(task => task.Status is DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled).ToList())
            await _downloads.RetryAsync(task);
        Announce("已重试所选下载", "download-manager");
        Reload();
    }

    private void ClearCompleted()
    {
        _downloads.ClearCompleted();
        Announce("已清除已完成下载", "download-manager", AnnouncementKind.Success);
        Reload();
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
    private readonly ListBox _playlists = WinFormsUi.ListBox("个人歌单", 5);
    private readonly ListBox _songs = WinFormsUi.ListBox("歌曲预览", 6);
    private readonly TextBox _cookie = new() { Name = "Cookie", AccessibleName = "Cookie" };
    private readonly Label _account = new() { Text = "未登录", AutoSize = true, TabStop = false, AccessibleName = "账号状态" };
    private readonly Label _status = new() { Text = "请选择音乐平台。", AutoSize = true, TabStop = false, AccessibleName = "导入状态" };
    private string? _platform;
    private IReadOnlyList<RemotePlaylist>? _remotePlaylists;

    public PlaylistImportForm(
        IPlaylistImportService service,
        IAccessibleAnnouncer announcer,
        IPlaylistLoginWindowFactory login,
        Action<ListBox> attachSongList)
    {
        _service = service;
        _announcer = announcer;
        _login = login;
        _attachSongList = attachSongList;
        Text = "导入个人歌单";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1050, 680);
        KeyPreview = true;
        var netease = CreateButton("网易云音乐", 1, (_, _) => SelectPlatform("netease"));
        var qq = CreateButton("QQ音乐", 2, (_, _) => SelectPlatform("qq"));
        var kg = CreateButton("酷狗音乐", 3, (_, _) => SelectPlatform("kg"));
        var kw = CreateButton("酷我音乐", 4, (_, _) => SelectPlatform("kw"));
        var logout = CreateButton("退出登录", 6, (_, _) => _ = LogoutAsync());
        var load = CreateButton("加载歌单", 7, (_, _) => _ = LoadPlaylistsAsync());
        var back = CreateButton("返回歌单列表", 8, (_, _) => ShowPlaylists());
        var preview = CreateButton("预览歌曲", 9, (_, _) => _ = PreviewAsync());
        var importThis = CreateButton("导入当前歌单", 10, (_, _) => _ = ImportAsync(false));
        var selectAll = CreateButton("全选歌曲", 11, (_, _) => SelectAllSongs());
        var importSelected = CreateButton("导入选中歌曲", 12, (_, _) => _ = ImportAsync(true));
        var cookieCaption = WinFormsUi.Caption("Cookie：", 13);
        var submitCookie = CreateButton("使用 Cookie 登录", 14, (_, _) => _ = CookieLoginAsync());
        netease.SetBounds(12, 12, 110, 28); qq.SetBounds(130, 12, 100, 28); kg.SetBounds(238, 12, 100, 28); kw.SetBounds(346, 12, 100, 28);
        _account.SetBounds(460, 18, 300, 22); logout.SetBounds(770, 12, 90, 28); load.SetBounds(868, 12, 95, 28);
        cookieCaption.SetBounds(12, 52, 0, 0); _cookie.SetBounds(75, 48, 670, 24); submitCookie.SetBounds(755, 47, 140, 28);
        _playlists.SetBounds(12, 85, 350, 500); _songs.SetBounds(370, 85, 650, 500);
        _status.SetBounds(12, 595, 800, 22); back.SetBounds(12, 620, 120, 28); preview.SetBounds(140, 620, 100, 28);
        importThis.SetBounds(248, 620, 130, 28); selectAll.SetBounds(386, 620, 100, 28); importSelected.SetBounds(494, 620, 130, 28);
        Controls.AddRange(new Control[] { netease, qq, kg, kw, _account, logout, load, cookieCaption, _cookie, submitCookie, _playlists, _songs, _status, back, preview, importThis, selectAll, importSelected });
        _playlists.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = PreviewAsync(); } };
        _songs.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = ImportAsync(false); } };
        Shown += async (_, _) => { await _service.EnsureAccountsLoadedAsync(); RefreshAccount(); _playlists.Focus(); };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }
    private void SelectPlatform(string platform)
    {
        _platform = platform;
        _status.Text = "已选择平台，请登录或加载歌单。";
        Announce(_status.Text, "playlist-import");
        RefreshAccount();
        _playlists.Focus();
    }

    private void RefreshAccount()
    {
        var account = _platform is null ? null : _service.GetAccount(_platform);
        _account.Text = account is null ? "未登录" : $"已登录：{account.DisplayName ?? account.Platform}";
    }

    private async Task CookieLoginAsync()
    {
        if (_platform is null || string.IsNullOrWhiteSpace(_cookie.Text)) return;
        var result = await _service.LoginWithCookieAsync(_platform, _cookie.Text.Trim());
        RefreshAccount();
        _status.Text = result is null ? "Cookie 登录失败。" : $"Cookie 登录成功：{result.DisplayName}。";
        Announce(_status.Text, "playlist-login", result is null ? AnnouncementKind.Error : AnnouncementKind.Success);
    }

    private async Task LogoutAsync()
    {
        if (_platform is null) return;
        await _service.LogoutAsync(_platform);
        RefreshAccount();
        _status.Text = "已退出登录。";
        Announce(_status.Text, "playlist-login");
    }

    private async Task LoadPlaylistsAsync()
    {
        if (_platform is null) return;
        try
        {
            _remotePlaylists = await _service.GetMyPlaylistsAsync(_platform);
            _playlists.Items.Clear();
            foreach (var playlist in _remotePlaylists)
                _playlists.Items.Add(new ObjectBox<RemotePlaylist>(playlist.Name, playlist));
            _status.Text = _remotePlaylists.Count == 0 ? "没有找到个人歌单。" : $"已加载 {_remotePlaylists.Count} 个歌单，回车可预览。";
            Announce(_status.Text, "playlist-import", AnnouncementKind.Success);
            ShowPlaylists();
        }
        catch (Exception ex) { WinFormsUi.Error(this, ex, "加载歌单"); }
    }

    private async Task PreviewAsync()
    {
        if (_platform is null || _remotePlaylists is null || _playlists.SelectedItem is not ObjectBox<RemotePlaylist> box) return;
        try
        {
            var songs = await _service.GetPlaylistSongsPreviewAsync(_platform, box.Value);
            _songs.BeginUpdate(); _songs.Items.Clear();
            foreach (var song in songs) _songs.Items.Add(new ObjectBox<MusicInfo>(song.AccessibleName, song));
            _songs.EndUpdate();
            _attachSongList?.Invoke(_songs);
            _status.Text = $"已预览 {songs.Count} 首歌曲。";
            Announce(_status.Text, "playlist-preview");
            _songs.Focus();
        }
        catch (Exception ex) { WinFormsUi.Error(this, ex, "预览歌单"); }
    }

    private async Task ImportAsync(bool selectedOnly)
    {
        if (_platform is null || _playlists.SelectedItem is not ObjectBox<RemotePlaylist> box) return;
        try
        {
            var result = await _service.ImportPlaylistAsync(_platform, box.Value, message => { _status.Text = message; Announce(message, "playlist-import", AnnouncementKind.Busy); });
            _status.Text = $"导入完成：{result.PlaylistName}，新增 {result.ImportedCount}，跳过 {result.SkippedCount}。";
            Announce(_status.Text, "playlist-import", AnnouncementKind.Success);
        }
        catch (Exception ex) { WinFormsUi.Error(this, ex, "导入歌单"); }
    }

    private void SelectAllSongs() { for (var index = 0; index < _songs.Items.Count; index++) _songs.SetSelected(index, true); }

    private void ShowPlaylists()
    {
        _playlists.Focus();
        Announce("已返回歌单列表", "playlist-import");
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

/// <summary>Live lyrics reader bound to LyricsWorkflow.</summary>
public class LyricsForm : Form
{
    private readonly IAccessibleAnnouncer _announcer;
    private readonly LyricsWorkflow _lyrics;
    private readonly TextBox _text = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window, AccessibleRole = AccessibleRole.None };
    private readonly Label _title = new() { Text = "暂无歌词", AutoSize = true, TabStop = false, Dock = DockStyle.Top, AccessibleName = "歌词标题" };
    private readonly Button _copy = new() { Text = "复制歌词", Dock = DockStyle.Bottom };

    public LyricsForm(IAccessibleAnnouncer announcer, LyricsWorkflow lyrics, Func<KeyEventArgs, bool> handleHotkey)
    {
        _announcer = announcer;
        _lyrics = lyrics;
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
            Clipboard.SetText(_text.Text.Length == 0 ? string.Empty : _text.Text);
            Announce("歌词已复制", "lyrics", AnnouncementKind.Success);
        };
        if (lyrics is not null) lyrics.StateChanged += (_, state) => BeginInvoke(() => Render(state));
        Render(lyrics?.Current ?? LyricsState.Empty);
        Shown += (_, _) => _text.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }
    private void Render(LyricsState state)
    {
        _title.Text = state.Music is null ? "暂无歌词" : $"歌词：{state.Music.Artist}，{state.Music.Title}";
        _text.Lines = state.Lines.Select((line, index) => (index + 1) + ". " + line.DisplayText).ToArray();
        if (!string.IsNullOrWhiteSpace(state.Message)) _text.AppendText(Environment.NewLine + state.Message);
    }

    private void Announce(string message, string activity, AnnouncementKind kind = AnnouncementKind.Status) =>
        _announcer.Announce(new AccessibleAnnouncement(message, kind, activity));
}

/// <summary>Manual exact source switching window.</summary>
public class ManualSourceSwitchForm : Form
{
    private readonly IAccessibleAnnouncer _announcer;
    private readonly IManualSourceDiscoveryWorkflow _workflow;
    private readonly MusicInfo? _target;
    private readonly ListBox _options = WinFormsUi.ListBox("可用音源", 1);
    private readonly Label _targetLabel = new() { AutoSize = true, TabStop = false, AccessibleName = "目标歌曲" };
    private readonly Label _status = new() { Text = "按 Enter 搜索可用音源。", AutoSize = true, TabStop = false, AccessibleName = "切换状态" };

    public ManualSourceSwitchForm(
        IAccessibleAnnouncer announcer,
        IManualSourceDiscoveryWorkflow workflow,
        MusicInfo? target,
        Func<KeyEventArgs, bool> handleHotkey)
    {
        _announcer = announcer;
        _workflow = workflow;
        _target = target;
        Text = "切换音源";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 540);
        KeyPreview = true;
        var search = CreateButton("搜索可用音源", 2, (_, _) => _ = SearchAsync());
        var play = CreateButton("用选中音源播放", 3, (_, _) => _ = PlayAsync());
        _targetLabel.Text = target is null ? "未选择歌曲" : $"目标歌曲：{target.Artist}，{target.Title}";
        _targetLabel.SetBounds(12, 15, 0, 0);
        search.SetBounds(12, 40, 130, 28); play.SetBounds(150, 40, 140, 28);
        _status.SetBounds(12, 76, 600, 22);
        _options.SetBounds(12, 105, 680, 350);
        Controls.AddRange(new Control[] { _targetLabel, search, play, _status, _options });
        _options.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = PlayAsync(); } };
        Shown += (_, _) => { search.Focus(); if (_target is not null) _ = SearchAsync(); };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }
    private async Task SearchAsync()
    {
        if (_target is null) return;
        try
        {
            var result = await _workflow.DiscoverAsync(_target);
            _options.Items.Clear();
            foreach (var option in result.Options)
                _options.Items.Add(new ObjectBox<ManualSourceOption>($"{option.DisplayLabel}，{option.Platform}", option));
            _status.Text = result.Message;
            Announce(result.Message, "manual-source-query:" + Guid.NewGuid().ToString("N"), result.Status == ManualSourceDiscoveryStatus.Success ? AnnouncementKind.Success : AnnouncementKind.Error);
            if (_options.Items.Count > 0) _options.SelectedIndex = 0;
        }
        catch (Exception ex) { WinFormsUi.Error(this, ex, "搜索音源"); }
    }

    private async Task PlayAsync()
    {
        if (_target is null || _options.SelectedItem is not ObjectBox<ManualSourceOption> box) return;
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
    private readonly TextBox _guide = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window, AccessibleName = "使用说明" };
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
        WinFormsUi.Name(close, "关闭", 2);
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
            3 => "设置中的普通项目修改后立即生效，关闭时保存。\n快捷键页选择项目后按 Enter 录制，Delete 清除。",
            4 => "F5 播放/暂停，F6 停止，F3 上一首，F4 下一首。\nF7/F8 快退快进，F9/F10 音量，F11/F12 设备，F1 模式，F2 下载。",
            _ => "所有焦点控件都有语义名称和明确 Tab 顺序。\n状态、进度、错误和完成通过统一播报区域通知。\n列表选择和焦点变化不会触发联网、播放或删除。"
        };
        _guide.SelectionStart = 0;
        _guide.ScrollToCaret();
        Announce("已切换到" + _chapter.Text, "guide");
    }

    private void Announce(string message, string activity) => _announcer.Announce(new AccessibleAnnouncement(message, AnnouncementKind.Status, activity));
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
