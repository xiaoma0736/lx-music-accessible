using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Serilog;

namespace LXMusicAccessible.App;

/// <summary>
/// 导入个人歌单工具窗体（无障碍：全程键盘导航 + 状态播报）。
/// 流程：选择平台与登录方式 → 登录 → 获取我的歌单 → 多选导入为本地快照。
/// 登录弹窗（网页登录/酷狗扫码）保持为独立窗口；歌曲预览列表挂接主窗口的播放/右键菜单/收藏能力。
/// </summary>
internal enum PlaylistImportCloseDecision
{
    ContinueImport,
    CancelImportAndClose
}

public partial class PlaylistImportWindow : Window
{
    private const string ImportWorkSlot = "playlist-import";
    private readonly IPlaylistImportService _service;
    private readonly IAccessibleAnnouncer _announce;
    private readonly IPlaylistLoginWindowFactory _loginWindows;
    private readonly BackgroundWorkHost _backgroundWork;
    private readonly Func<KeyEventArgs, bool> _handleHotkey;
    private readonly Action<ListBox> _detachSongList;
    private readonly Func<Window, PlaylistImportCloseDecision> _confirmClose;
    private string _platform = "netease";
    private bool _loggedIn;
    private List<RemotePlaylist> _playlists = new();
    private bool _selectAllState;
    private bool _importing;
    private bool _canceling;
    private bool _closeAfterCancel;
    private bool _allowClose;
    private bool _closed;
    private Button? _loginInitiator;

    internal PlaylistImportWindow(
        IPlaylistImportService service,
        IAccessibleAnnouncer announce,
        IPlaylistLoginWindowFactory loginWindows,
        BackgroundWorkHost backgroundWork,
        Func<KeyEventArgs, bool> handleHotkey,
        Action<ListBox> attachSongList,
        Action<ListBox> detachSongList,
        Func<Window, PlaylistImportCloseDecision>? confirmClose = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(announce);
        ArgumentNullException.ThrowIfNull(loginWindows);
        ArgumentNullException.ThrowIfNull(backgroundWork);
        ArgumentNullException.ThrowIfNull(handleHotkey);
        ArgumentNullException.ThrowIfNull(attachSongList);
        ArgumentNullException.ThrowIfNull(detachSongList);
        InitializeComponent();
        _service = service;
        _announce = announce;
        _loginWindows = loginWindows;
        _backgroundWork = backgroundWork;
        _handleHotkey = handleHotkey;
        _detachSongList = detachSongList;
        _confirmClose = confirmClose ?? ConfirmClose;
        attachSongList(PlaylistSongsList);
        UpdatePlatformSelectionState();
        SetImportStatus("就绪");
        Loaded += OnLoaded;
        Closed += Window_Closed;
    }

    /// <summary>登录与确认对话框的所属工具窗体。</summary>
    private Window HostWindow => this;

    private void Announce(
        string message,
        AnnouncementKind kind = AnnouncementKind.Status,
        string? activityId = null)
    {
        if (_closed) return;
        _announce.Announce(new AccessibleAnnouncement(message, kind, activityId));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(PlatformNeteaseBtn.Focus, DispatcherPriority.Input);
        try { await _service.EnsureAccountsLoadedAsync(); }
        catch (Exception ex)
        {
            Log.Warning("Playlist account restoration failed: ErrorType={ErrorType}", ex.GetType().Name);
        }
        if (_closed) return;
        await RefreshAccountUiAsync();
        if (!_loggedIn)
        {
            Announce("导入个人歌单：当前未登录，请按下平台按钮选择登录方式。");
        }
        else
        {
            Announce("导入个人歌单：已恢复登录状态。");
        }
    }

    // ============ 登录态 UI ============

    private Task RefreshAccountUiAsync()
    {
        var acc = _service.GetAccount(_platform);
        _loggedIn = acc != null;
        if (_loggedIn)
        {
            AccountStatusText.Text = $"已登录：{acc!.DisplayName}";
            LogoutBtn.Visibility = Visibility.Visible;
            LoadPlaylistsBtn.Visibility = Visibility.Visible;
            PlaylistListHintText.Visibility = Visibility.Visible;
            MyPlaylistsList.Visibility = Visibility.Visible;
        }
        else
        {
            AccountStatusText.Text = "未登录";
            LogoutBtn.Visibility = Visibility.Collapsed;
            LoadPlaylistsBtn.Visibility = Visibility.Collapsed;
            PlaylistListHintText.Visibility = Visibility.Collapsed;
            MyPlaylistsList.Visibility = Visibility.Collapsed;
            SelectAllBtn.Visibility = Visibility.Collapsed;
            ImportSelectedBtn.Visibility = Visibility.Collapsed;
        }
        return Task.CompletedTask;
    }

    // ============ 平台选择 ============

    private async void PlatformBtn_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button ?? PlatformNeteaseBtn;
        var tag = button.Tag?.ToString() ?? "netease";
        CookieArea.Visibility = Visibility.Collapsed;
        _platform = tag;
        _loginInitiator = button;
        UpdatePlatformSelectionState();
        await RefreshAccountUiAsync();

        var method = _loginWindows.SelectLoginMethod(_platform, HostWindow);
        if (_closed) return;
        switch (method)
        {
            case PlaylistLoginMethod.Web:
                await StartWebLoginAsync(button);
                break;
            case PlaylistLoginMethod.Cookie:
                ShowCookieLogin();
                break;
            case PlaylistLoginMethod.KugouQr:
                await StartKugouQrLoginAsync(button);
                break;
            default:
                button.Focus();
                Announce("已取消选择登录方式。");
                break;
        }
    }

    private void UpdatePlatformSelectionState()
    {
        foreach (var button in new[] { PlatformNeteaseBtn, PlatformQqBtn, PlatformKgBtn, PlatformKwBtn })
        {
            var selected = string.Equals(
                button.Tag?.ToString(),
                _platform,
                StringComparison.OrdinalIgnoreCase);
            AutomationProperties.SetItemStatus(button, selected ? "当前平台" : string.Empty);
        }
    }

    // ============ 酷狗 APP 扫码登录（仅酷狗；获取客户端 token 才能读取个人歌单） ============

    private async Task StartKugouQrLoginAsync(Button initiator)
    {
        CookieArea.Visibility = Visibility.Collapsed;
        if (await LoginWithKugouQrDialogAsync())
        {
            var acc = _service.GetAccount("kg");
            var msg = acc == null ? "酷狗扫码登录成功。" : $"酷狗扫码登录成功：{acc.DisplayName}。";
            Announce(msg, AnnouncementKind.Success);
            FocusLoadedPlaylistsOr(initiator);
            return;
        }
        Announce("酷狗扫码登录已取消或失败。");
        initiator.Focus();
    }

    internal async Task<bool> LoginWithKugouQrDialogAsync()
    {
        if (!_loginWindows.ShowKugouQrLogin(HostWindow)) return false;
        await RefreshAccountUiAsync();
        return true;
    }
    // ============ 网页登录（内嵌浏览器，四个平台通用） ============

    private async Task StartWebLoginAsync(Button initiator)
    {
        CookieArea.Visibility = Visibility.Collapsed;
        var result = await LoginWithWebDialogAsync();
        if (result.Succeeded)
        {
            var acc = _service.GetAccount(_platform);
            var accountName = result.AccountName ?? acc?.DisplayName ?? "";
            var msg = string.IsNullOrEmpty(accountName)
                ? "Cookie获取成功，登录成功。"
                : $"Cookie获取成功，登录成功：{accountName}。";
            Announce(msg, AnnouncementKind.Success);
            FocusLoadedPlaylistsOr(initiator);
            return;
        }
        Announce("网页登录已取消或失败。");
        initiator.Focus();
    }

    internal async Task<PlaylistLoginDialogResult> LoginWithWebDialogAsync()
    {
        var result = _loginWindows.ShowWebLogin(_platform, HostWindow);
        if (result.Succeeded) await RefreshAccountUiAsync();
        return result;
    }

    // ============ 粘贴 Cookie 登录 ============

    private void ShowCookieLogin()
    {
        CookieArea.Visibility = Visibility.Visible;
        CookieBox.Focus();
        var site = _platform switch { "netease" => "music.163.com", "qq" => "y.qq.com", "kg" => "www.kugou.com", "kw" => "www.kuwo.cn", _ => "对应平台网页版" };
        Announce($"请在电脑浏览器登录{site}后，按 F12 打开开发者工具，在控制台输入 document.cookie 回车，复制显示的内容粘贴到输入框，然后按提交Cookie按钮。");
    }

    private async void CookieSubmitBtn_Click(object sender, RoutedEventArgs e)
    {
        var cookie = CookieBox.Text?.Trim();
        if (string.IsNullOrEmpty(cookie))
        {
            Announce("Cookie 内容为空，请先复制 Cookie 再提交。");
            return;
        }
        CookieSubmitBtn.IsEnabled = false;
        var activityId = $"cookie-login:{Guid.NewGuid():N}";
        try
        {
            Announce("正在校验 Cookie，请稍候。", AnnouncementKind.Busy, activityId);
            var acc = await _service.LoginWithCookieAsync(_platform, cookie);
            if (_closed) return;
            if (acc == null)
            {
                Announce("Cookie 无效或已过期，登录失败。请重新登录后复制最新 Cookie。", AnnouncementKind.Error, activityId);
                _loginInitiator?.Focus();
                return;
            }
            CookieArea.Visibility = Visibility.Collapsed;
            await RefreshAccountUiAsync();
            Announce($"登录成功：{acc.DisplayName}。按 Tab 到“获取我的歌单”按钮。", AnnouncementKind.Success, activityId);
            FocusLoadedPlaylistsOr(_loginInitiator);
        }
        catch (Exception)
        {
            var correlationId = LoginDiagnostics.CreateCorrelationId();
            Announce(LoginDiagnostics.CreateUserMessage(
                LoginDiagnosticOutcome.Exception, LoginDiagnosticError.LogoutFailed, correlationId),
                AnnouncementKind.Error,
                activityId);
            _loginInitiator?.Focus();
        }
        finally
        {
            if (!_closed) CookieSubmitBtn.IsEnabled = true;
        }
    }

    // ============ 退出登录 ============

    private async void LogoutBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _service.LogoutAsync(_platform);
        }
        catch (Exception)
        {
            var correlationId = LoginDiagnostics.CreateCorrelationId();
            Announce(LoginDiagnostics.CreateUserMessage(
                LoginDiagnosticOutcome.Exception, LoginDiagnosticError.Unexpected, correlationId));
            return;
        }
        if (_closed) return;
        _playlists.Clear();
        MyPlaylistsList.ItemsSource = null;
        MyPlaylistsList.Items.Clear();
        await RefreshAccountUiAsync();
        Announce("已退出登录。");
    }

    // ============ 获取我的歌单 ============

    private async void LoadPlaylistsBtn_Click(object sender, RoutedEventArgs e)
    {
        LoadPlaylistsBtn.IsEnabled = false;
        var previousFocus = Keyboard.FocusedElement;
        var selectedId = (MyPlaylistsList.SelectedItem as RemotePlaylist)?.RemoteId;
        try
        {
            Announce("正在获取我的歌单，请稍候。");
            _playlists = await _service.GetMyPlaylistsAsync(_platform);
            if (_closed) return;
            var restoreFocus = ReferenceEquals(Keyboard.FocusedElement, previousFocus);
            MyPlaylistsList.ItemsSource = null;
            MyPlaylistsList.Items.Clear();
            foreach (var pl in _playlists)
                MyPlaylistsList.Items.Add(pl);
            if (_playlists.Count > 0)
                MyPlaylistsList.SelectedIndex = Math.Max(0, _playlists.FindIndex(item =>
                    string.Equals(item.RemoteId, selectedId, StringComparison.Ordinal)));
            SelectAllBtn.Visibility = Visibility.Visible;
            ImportSelectedBtn.Visibility = Visibility.Visible;
            if (_playlists.Count == 0)
            {
                if (restoreFocus) FocusListItem(MyPlaylistsList, 0);
                Announce("没有找到歌单。");
            }
            else
            {
                if (restoreFocus) FocusListItem(MyPlaylistsList, 0);
                Announce($"共找到 {_playlists.Count} 个歌单。按上下方向键浏览，按回车打开该歌单的歌曲列表，按 Ctrl+空格 选中或取消。");
            }
        }
        catch (Exception ex)
        {
            if (_closed) return;
            if (ex.Message.Contains("登录已过期"))
                await RefreshAccountUiAsync();
            Announce("获取歌单失败：" + ex.Message);
        }
        finally
        {
            if (!_closed) LoadPlaylistsBtn.IsEnabled = true;
        }
    }

    // ============ 选择与导入 ============

    private void SelectAllBtn_Click(object sender, RoutedEventArgs e)
    {
        _selectAllState = !_selectAllState;
        MyPlaylistsList.SelectedItems.Clear();
        if (_selectAllState)
        {
            foreach (var pl in _playlists)
                MyPlaylistsList.SelectedItems.Add(pl);
        }
        Announce(_selectAllState
            ? $"已全选 {MyPlaylistsList.SelectedItems.Count} 个歌单。"
            : "已取消全选。");
    }

    private async void ImportSelectedBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = MyPlaylistsList.SelectedItems.Cast<RemotePlaylist>().ToList();
        if (selected.Count == 0)
        {
            Announce("请先选择要导入的歌单。");
            return;
        }
        await StartImportAsync(selected);
    }

    // ============ 歌单歌曲预览（回车打开歌曲列表） ============

    private async void MyPlaylistsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (MyPlaylistsList.SelectedItem is not RemotePlaylist remote) return;
        e.Handled = true;
        await ShowSongPreviewAsync(remote);
    }

    private async Task ShowSongPreviewAsync(RemotePlaylist remote)
    {
        if (remote == null) return;
        var previousFocus = Keyboard.FocusedElement;
        // 歌曲预览列表挂接主窗口的播放/右键菜单/收藏能力（仅一次）
        Announce($"正在获取歌单《{remote.Name}》的歌曲，请稍候。");
        try
        {
            var songs = await _service.GetPlaylistSongsPreviewAsync(_platform, remote);
            if (_closed) return;
            var restoreFocus = ReferenceEquals(Keyboard.FocusedElement, previousFocus);
            PlaylistSongsList.ItemsSource = null;
            PlaylistSongsList.Items.Clear();
            foreach (var m in songs)
                PlaylistSongsList.Items.Add(m);
            if (songs.Count > 0) PlaylistSongsList.SelectedIndex = 0;
            SongPreviewHeaderText.Text = $"歌单《{remote.Name}》歌曲预览，共 {songs.Count} 首";
            ShowPreviewArea();
            if (songs.Count == 0)
            {
                if (restoreFocus) FocusListItem(PlaylistSongsList, 0);
                Announce($"歌单《{remote.Name}》暂无歌曲。");
                return;
            }
            if (restoreFocus) FocusListItem(PlaylistSongsList, 0);
            Announce($"已加载《{remote.Name}》共 {songs.Count} 首歌曲。按上下方向键浏览，按回车或空格播放，按应用键打开操作菜单可加入本地收藏，按返回歌单列表可返回，按导入此歌单可导入。");
        }
        catch (Exception ex)
        {
            Announce("获取歌曲列表失败：" + ex.Message);
        }
    }

    /// <summary>切换为歌曲预览视图：隐藏歌单列表区，显示预览区</summary>
    private void ShowPreviewArea()
    {
        LoadPlaylistsBtn.Visibility = Visibility.Collapsed;
        PlaylistListHintText.Visibility = Visibility.Collapsed;
        MyPlaylistsList.Visibility = Visibility.Collapsed;
        SelectAllBtn.Visibility = Visibility.Collapsed;
        ImportSelectedBtn.Visibility = Visibility.Collapsed;
        SongPreviewArea.Visibility = Visibility.Visible;
    }

    /// <summary>返回歌单列表视图，并回焦到之前选中的歌单</summary>
    private void BackToPlaylistsBtn_Click(object sender, RoutedEventArgs e)
    {
        SongPreviewArea.Visibility = Visibility.Collapsed;
        LoadPlaylistsBtn.Visibility = Visibility.Visible;
        PlaylistListHintText.Visibility = Visibility.Visible;
        MyPlaylistsList.Visibility = Visibility.Visible;
        SelectAllBtn.Visibility = Visibility.Visible;
        ImportSelectedBtn.Visibility = Visibility.Visible;
        var idx = MyPlaylistsList.SelectedIndex >= 0 ? MyPlaylistsList.SelectedIndex : 0;
        FocusListItem(MyPlaylistsList, idx);
        Announce("已返回歌单列表。");
    }

    private async void ImportThisPlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        var remote = MyPlaylistsList.SelectedItem as RemotePlaylist;
        if (remote == null)
        {
            Announce("未选择要导入的歌单。");
            return;
        }
        await StartImportAsync([remote]);
    }

    private async Task StartImportAsync(IReadOnlyList<RemotePlaylist> playlists)
    {
        if (_importing || playlists.Count == 0) return;
        _importing = true;
        SetImportControlsEnabled(false);
        var activityId = $"playlist-import:{Guid.NewGuid():N}";
        SetImportStatus($"正在导入，共 {playlists.Count} 个歌单");
        try
        {
            await _backgroundWork.RunLatestAsync(ImportWorkSlot, async context =>
            {
                try
                {
                    var results = await _service.ImportPlaylistsAsync(
                        _platform,
                        playlists,
                        message =>
                        {
                            SetImportStatus(message);
                            Announce(message, AnnouncementKind.Progress, activityId);
                        },
                        context.CancellationToken);
                    if (!context.CancellationToken.IsCancellationRequested)
                    {
                        var message = results.Count == 1
                            ? results[0].ToString()
                            : "全部歌单导入完成，已保存到“在线收藏”。";
                        SetImportStatus(message);
                        Announce(message, AnnouncementKind.Success, activityId);
                    }
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    SetImportStatus("导入已取消");
                    Announce("导入已取消", AnnouncementKind.Status, activityId);
                    throw;
                }
                catch (Exception ex)
                {
                    SetImportStatus("导入失败");
                    Announce("导入失败：" + ex.Message, AnnouncementKind.Error, activityId);
                }
            });
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _importing = false;
                    _canceling = false;
                    SetImportControlsEnabled(true);
                    if (_closeAfterCancel && !_closed)
                    {
                        _allowClose = true;
                        Close();
                    }
                });
            }
        }
    }

    private void FocusLoadedPlaylistsOr(Button? fallback)
    {
        if (LoadPlaylistsBtn.Visibility == Visibility.Visible && LoadPlaylistsBtn.IsEnabled)
            LoadPlaylistsBtn.Focus();
        else
            fallback?.Focus();
    }

    /// <summary>
    /// 工具窗体 Esc 路由：歌曲预览状态下先返回歌单列表，返回是否已处理。
    /// </summary>
    private bool TryHandleEscape()
    {
        if (SongPreviewArea.Visibility == Visibility.Visible)
        {
            BackToPlaylistsBtn_Click(this, new RoutedEventArgs());
            return true;
        }
        return false;
    }

    private void SetImportControlsEnabled(bool enabled)
    {
        PlatformNeteaseBtn.IsEnabled = enabled;
        PlatformQqBtn.IsEnabled = enabled;
        PlatformKgBtn.IsEnabled = enabled;
        PlatformKwBtn.IsEnabled = enabled;
        LogoutBtn.IsEnabled = enabled;
        LoadPlaylistsBtn.IsEnabled = enabled;
        SelectAllBtn.IsEnabled = enabled;
        ImportSelectedBtn.IsEnabled = enabled;
        ImportThisPlaylistBtn.IsEnabled = enabled;
    }

    private void SetImportStatus(string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.Invoke(() => SetImportStatus(status));
            return;
        }
        if (_closed) return;
        ImportStatusText.Text = status;
        AutomationProperties.SetItemStatus(ImportStatusText, status);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (PlaylistSongsList.ContextMenu?.IsOpen == true) return;
            if (!TryHandleEscape()) Close();
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
            or PasswordBox)
        {
            return;
        }
        if (_handleHotkey(e)) e.Handled = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_importing) return;
        e.Cancel = true;
        if (_canceling) return;

        if (_confirmClose(this) == PlaylistImportCloseDecision.ContinueImport)
        {
            Announce("导入继续进行，窗口保持打开");
            return;
        }

        _canceling = true;
        _closeAfterCancel = true;
        SetImportControlsEnabled(false);
        SetImportStatus("正在取消");
        Announce("正在取消", AnnouncementKind.Busy, "playlist-import-cancel");
        _backgroundWork.Cancel(ImportWorkSlot);
    }

    private static PlaylistImportCloseDecision ConfirmClose(Window owner)
    {
        var result = MessageBox.Show(
            owner,
            "歌单仍在导入。\n\n选择“是”保留窗口并继续导入；选择“否”取消导入并在当前歌单安全回滚后关闭窗口。",
            "关闭导入个人歌单",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        return result == MessageBoxResult.Yes
            ? PlaylistImportCloseDecision.ContinueImport
            : PlaylistImportCloseDecision.CancelImportAndClose;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        Loaded -= OnLoaded;
        Closed -= Window_Closed;
        _detachSongList(PlaylistSongsList);
        PlaylistSongsList.ItemsSource = null;
        PlaylistSongsList.Items.Clear();
        MyPlaylistsList.ItemsSource = null;
        MyPlaylistsList.Items.Clear();
        CookieBox.Clear();
    }

    /// <summary>聚焦列表中的指定条目（读屏进入焦点模式），延迟回退避免容器抢焦</summary>
    private void FocusListItem(ListBox list, int index)
    {
        if (list == null) return;
        if (list.Items.Count == 0)
        {
            list.FocusItemOrList();
            return;
        }
        if (index < 0) index = 0;
        if (index >= list.Items.Count) index = list.Items.Count - 1;
        list.SelectedIndex = index;
        list.FocusItemOrList();
    }
}
