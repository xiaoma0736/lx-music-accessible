using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.App;

/// <summary>
/// 导入个人歌单视图（并入主窗口的标签页，无障碍：全程键盘导航 + 语音播报）。
/// 流程：选择平台 → 网页登录 / 粘贴Cookie登录 → 获取我的歌单 → 多选导入为本地快照。
/// 登录弹窗（网页登录/酷狗扫码）保持为独立窗口；歌曲预览列表挂接主窗口的播放/右键菜单/收藏能力。
/// </summary>
public partial class PlaylistImportView : UserControl
{
    private readonly IPlaylistImportService _service;
    private readonly IAccessibilityService _announce;
    private readonly MusicHttpClient _httpClient;
    private readonly ILogger<KugouQrLoginService> _kugouLogger;
    private readonly ILogger<WebLoginWindow> _webLoginLogger;
    private string _platform = "netease";
    private bool _loggedIn;
    private List<RemotePlaylist> _playlists = new();
    private bool _selectAllState;
    private bool _songListAttached;

    public PlaylistImportView(
        IPlaylistImportService service,
        IAccessibilityService announce,
        MusicHttpClient httpClient,
        ILogger<KugouQrLoginService> kugouLogger,
        ILogger<WebLoginWindow> webLoginLogger)
    {
        InitializeComponent();
        _service = service;
        _announce = announce;
        _httpClient = httpClient;
        _kugouLogger = kugouLogger;
        _webLoginLogger = webLoginLogger;
        Loaded += OnLoaded;
    }

    /// <summary>宿主主窗口（本视图嵌入主窗口标签页）</summary>
    private Window? HostWindow => Window.GetWindow(this);

    /// <summary>本页第一个可聚焦控件（主窗口页边界 Tab 循环用）。</summary>
    public FrameworkElement FirstFocusable => PlatformNeteaseBtn;

    /// <summary>本页最后一个可聚焦控件（主窗口页边界 Tab 循环用）。
    /// 折叠控件不可聚焦（会卡住页边界 Shift+Tab），因此始终返回当前可见的最后一个可聚焦控件；
    /// 登录入口按平台可能被折叠（酷狗隐藏网页登录、酷我隐藏粘贴Cookie），最后兜底到始终可见的平台按钮。</summary>
    public FrameworkElement LastFocusable
    {
        get
        {
            if (ImportSelectedBtn.Visibility == Visibility.Visible) return ImportSelectedBtn;
            if (SelectAllBtn.Visibility == Visibility.Visible) return SelectAllBtn;
            if (LoadPlaylistsBtn.Visibility == Visibility.Visible) return LoadPlaylistsBtn;
            if (LogoutBtn.Visibility == Visibility.Visible) return LogoutBtn;
            if (KugouQrLoginBtn.Visibility == Visibility.Visible) return KugouQrLoginBtn;
            if (CookieLoginBtn.Visibility == Visibility.Visible) return CookieLoginBtn;
            if (WebLoginBtn.Visibility == Visibility.Visible) return WebLoginBtn;
            return PlatformKwBtn; // 平台按钮始终可见
        }
    }

    /// <summary>歌曲预览列表（供主窗口全局歌曲操作识别）</summary>
    public ListBox SongList => PlaylistSongsList;

    private void Announce(string message)
    {
        try { _announce.Announce(message); }
        catch { /* 播报失败不阻塞 */ }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await _service.EnsureAccountsLoadedAsync(); }
        catch { /* 忽略恢复失败 */ }
        await RefreshAccountUiAsync();
        // 不再在此处抢焦点：主窗口切到本标签时会统一把焦点放到 FirstFocusable（平台选择按钮），
        // 否则 TabControl 每次切到本页触发 Loaded，会把焦点抢到“获取我的歌单”按钮。
        if (!_loggedIn)
        {
            Announce("导入个人歌单：当前未登录，请先选择平台，再按 Tab 键到“网页登录”或“粘贴Cookie登录”。");
        }
        else
        {
            Announce("导入个人歌单：已恢复登录状态。");
        }
    }

    // ============ 登录态 UI ============

    private Task RefreshAccountUiAsync()
    {
        // 登录入口按平台可用性显示：
        //  - 酷狗：网页登录无法获取个人歌单，隐藏“网页登录”，保留“粘贴Cookie登录”与“APP扫码登录”
        //  - 酷我：反爬导致粘贴Cookie无法获取歌单，隐藏“粘贴Cookie登录”，保留“网页登录”（页面内拉取歌单）
        //  - 网易云 / QQ：网页登录与粘贴Cookie均可用
        var isKg = string.Equals(_platform, "kg", StringComparison.OrdinalIgnoreCase);
        var isKw = string.Equals(_platform, "kw", StringComparison.OrdinalIgnoreCase);
        WebLoginBtn.Visibility = isKg ? Visibility.Collapsed : Visibility.Visible;
        CookieLoginBtn.Visibility = isKw ? Visibility.Collapsed : Visibility.Visible;
        KugouQrLoginBtn.Visibility = isKg ? Visibility.Visible : Visibility.Collapsed;
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

    private void PlatformBtn_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.Tag?.ToString() ?? "netease";
        if (string.Equals(tag, _platform, StringComparison.OrdinalIgnoreCase)) return;

        CookieArea.Visibility = Visibility.Collapsed;
        _platform = tag;
        var name = (sender as Button)?.Content?.ToString() ?? "该平台";

        RefreshAccountUiAsync();

        var loginHint = _platform switch
        {
            "kg" => "可使用粘贴Cookie登录或酷狗APP扫码登录",
            "kw" => "可使用网页登录",
            _ => "可使用网页登录或粘贴Cookie登录"
        };
        Announce(_loggedIn
            ? $"已切换到{name}平台，已恢复登录状态。"
            : $"已切换到{name}平台。当前未登录，{loginHint}。");
    }

    // ============ 酷狗 APP 扫码登录（仅酷狗；获取客户端 token 才能读取个人歌单） ============

    private async void KugouQrLoginBtn_Click(object sender, RoutedEventArgs e)
    {
        CookieArea.Visibility = Visibility.Collapsed;
        var dialog = new KugouQrLoginWindow(_service, _announce, _httpClient, _kugouLogger)
        {
            Owner = HostWindow
        };
        if (dialog.ShowDialog() == true)
        {
            await RefreshAccountUiAsync();
            var acc = _service.GetAccount("kg");
            var msg = acc == null ? "酷狗扫码登录成功。" : $"酷狗扫码登录成功：{acc.DisplayName}。";
            Announce(msg);
            MessageBox.Show(HostWindow, msg, "登录成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    // ============ 网页登录（内嵌浏览器，四个平台通用） ============

    private async void WebLoginBtn_Click(object sender, RoutedEventArgs e)
    {
        CookieArea.Visibility = Visibility.Collapsed;
        var dialog = new WebLoginWindow(_platform, _service, _announce, _webLoginLogger)
        {
            Owner = HostWindow
        };
        if (dialog.ShowDialog() == true)
        {
            await RefreshAccountUiAsync();
            var acc = _service.GetAccount(_platform);
            var accountName = dialog.LastAccountName ?? acc?.DisplayName ?? "";
            var msg = string.IsNullOrEmpty(accountName)
                ? "Cookie获取成功，登录成功。"
                : $"Cookie获取成功，登录成功：{accountName}。";
            Announce(msg);
            MessageBox.Show(HostWindow, msg, "登录成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ============ 粘贴 Cookie 登录 ============

    private void CookieLoginBtn_Click(object sender, RoutedEventArgs e)
    {
        CookieArea.Visibility = CookieArea.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (CookieArea.Visibility == Visibility.Visible)
        {
            CookieBox.Focus();
            var site = _platform switch { "netease" => "music.163.com", "qq" => "y.qq.com", "kg" => "www.kugou.com", "kw" => "www.kuwo.cn", _ => "对应平台网页版" };
            Announce($"请在电脑浏览器登录{site}后，按 F12 打开开发者工具，在控制台输入 document.cookie 回车，复制显示的内容粘贴到输入框，然后按提交Cookie按钮。");
        }
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
        try
        {
            Announce("正在校验 Cookie，请稍候。");
            var acc = await _service.LoginWithCookieAsync(_platform, cookie);
            if (acc == null)
            {
                Announce("Cookie 无效或已过期，登录失败。请重新登录后复制最新 Cookie。");
                return;
            }
            CookieArea.Visibility = Visibility.Collapsed;
            await RefreshAccountUiAsync();
            Announce($"登录成功：{acc.DisplayName}。按 Tab 到“获取我的歌单”按钮。");
        }
        catch (Exception)
        {
            var correlationId = LoginDiagnostics.CreateCorrelationId();
            Announce(LoginDiagnostics.CreateUserMessage(
                LoginDiagnosticOutcome.Exception, LoginDiagnosticError.LogoutFailed, correlationId));
        }
        finally
        {
            CookieSubmitBtn.IsEnabled = true;
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
        try
        {
            Announce("正在获取我的歌单，请稍候。");
            _playlists = await _service.GetMyPlaylistsAsync(_platform);
            MyPlaylistsList.ItemsSource = null;
            MyPlaylistsList.Items.Clear();
            foreach (var pl in _playlists)
                MyPlaylistsList.Items.Add(pl);
            SelectAllBtn.Visibility = Visibility.Visible;
            ImportSelectedBtn.Visibility = Visibility.Visible;
            if (_playlists.Count == 0)
            {
                Announce("没有找到歌单。");
            }
            else
            {
                FocusListItem(MyPlaylistsList, 0);
                Announce($"共找到 {_playlists.Count} 个歌单。按上下方向键浏览，按回车打开该歌单的歌曲列表，按 Ctrl+空格 选中或取消。");
            }
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("登录已过期"))
                await RefreshAccountUiAsync();
            Announce("获取歌单失败：" + ex.Message);
        }
        finally
        {
            LoadPlaylistsBtn.IsEnabled = true;
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

        ImportSelectedBtn.IsEnabled = false;
        LoadPlaylistsBtn.IsEnabled = false;
        try
        {
            foreach (var remote in selected)
            {
                var result = await _service.ImportPlaylistAsync(_platform, remote, m => Announce(m));
                Announce(result.ToString());
            }
            Announce("全部歌单导入完成，已保存到“在线收藏”，可切换到“播放列表”标签页查看。");
        }
        catch (Exception ex)
        {
            Announce("导入失败：" + ex.Message);
        }
        finally
        {
            ImportSelectedBtn.IsEnabled = true;
            LoadPlaylistsBtn.IsEnabled = true;
        }
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
        // 歌曲预览列表挂接主窗口的播放/右键菜单/收藏能力（仅一次）
        if (!_songListAttached)
        {
            _songListAttached = true;
            (HostWindow as MainWindow)?.AttachSongListCapabilities(PlaylistSongsList);
        }
        Announce($"正在获取歌单《{remote.Name}》的歌曲，请稍候。");
        try
        {
            var songs = await _service.GetPlaylistSongsPreviewAsync(_platform, remote);
            PlaylistSongsList.ItemsSource = null;
            PlaylistSongsList.Items.Clear();
            foreach (var m in songs)
                PlaylistSongsList.Items.Add(m);
            SongPreviewHeaderText.Text = $"歌单《{remote.Name}》歌曲预览，共 {songs.Count} 首";
            ShowPreviewArea();
            if (songs.Count == 0)
            {
                Announce($"歌单《{remote.Name}》暂无歌曲。");
                return;
            }
            FocusListItem(PlaylistSongsList, 0);
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
        ImportThisPlaylistBtn.IsEnabled = false;
        try
        {
            var result = await _service.ImportPlaylistAsync(_platform, remote, m => Announce(m));
            Announce(result.ToString());
        }
        catch (Exception ex)
        {
            Announce("导入失败：" + ex.Message);
        }
        finally
        {
            ImportThisPlaylistBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// 主窗口 Esc 委托：歌曲预览状态下 Esc 先返回歌单列表，返回是否已处理。
    /// </summary>
    public bool TryHandleEscape()
    {
        if (SongPreviewArea.Visibility == Visibility.Visible)
        {
            BackToPlaylistsBtn_Click(this, new RoutedEventArgs());
            return true;
        }
        return false;
    }

    /// <summary>聚焦列表中的指定条目（读屏进入焦点模式），延迟回退避免容器抢焦</summary>
    private void FocusListItem(ListBox list, int index)
    {
        if (list == null || list.Items.Count == 0) return;
        if (index < 0) index = 0;
        if (index >= list.Items.Count) index = list.Items.Count - 1;
        list.SelectedIndex = index;
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement item && item.Focus())
            return;
        list.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement c)
                c.Focus();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
