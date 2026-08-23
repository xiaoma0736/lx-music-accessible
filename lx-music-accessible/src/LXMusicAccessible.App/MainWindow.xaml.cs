// Compiled: 2026-08-01 - 全链路打通 + 无障碍键盘导航 + 简体中文界面
using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LXMusicAccessible.Services;
using LXMusicAccessible.Models;
using System.Collections.ObjectModel;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Serilog;

namespace LXMusicAccessible.App;

public partial class MainWindow : Window
{
    private readonly IAccessibleAnnouncer _accessibilityService;
    private readonly IPlaybackCoordinator _playback;
    private readonly IAudioPlaybackControl _playerService;
    private readonly SettingsWorkflow _settingsWorkflow;
    private readonly ITtsService _ttsService;
    private readonly IMusicSourceService _musicSourceService;
    private readonly LyricsWorkflow _lyrics;
    private readonly IDownloadWorkflow _downloads;
    private readonly SavedPlaylistWorkflow _savedPlaylists;
    private readonly PlaylistImportView _playlistImportPanel;
    private readonly ApplicationWindowFactory _windowFactory;
    private readonly DesktopLifecycleWorkflow _desktopLifecycle;
    private readonly MusicBrowseWorkflow _browseWorkflow;
    private bool _shutdownBlocked;
    private bool _settingsLoaded = false;
    private bool _suppressPlaybackSettingChanges;
    private ObservableCollection<HotkeySettingItem> _hotkeySettingItems = new();
    private HotkeySettingItem? _recordingHotkeyItem;
    private bool _hotkeyConflictDialogOpen;
    private int _tabFocusToken;
    private bool _suppressTabContentFocus;
    private const int WM_HOTKEY = 0x0312;

    // ===== 小键盘误触发方向键热键修复（NumLock 关闭时小键盘 4/6/2/8 上报为方向键） =====
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_EXTENDED = 0x01;
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelKeyboardProc? _llKeyboardProc;
    private IntPtr _llKeyboardHook = IntPtr.Zero;
    private readonly object _numpadLock = new();
    private readonly HashSet<Key> _numpadNavKeysDown = new();
    private static readonly HashSet<int> NumpadNavScanCodes = new() { 0x47, 0x48, 0x49, 0x4B, 0x4C, 0x4D, 0x4F, 0x50, 0x51, 0x52, 0x53 };
    private static readonly Dictionary<int, Key> NumpadScanToKey = new()
    {
        [0x47] = Key.Home,
        [0x48] = Key.Up,
        [0x49] = Key.PageUp,
        [0x4B] = Key.Left,
        [0x4C] = Key.Clear, // NumLock 关闭时小键盘 5 上报为 Clear
        [0x4D] = Key.Right,
        [0x4F] = Key.End,
        [0x50] = Key.Down,
        [0x51] = Key.PageDown,
        [0x52] = Key.Insert,
        [0x53] = Key.Delete
    };

    // NumLock 关闭时，小键盘 0-9/. 会分别以上述导航键上报；命中这些键的热键若由物理小键盘触发则忽略
    private static readonly HashSet<Key> NumpadOriginKeys = new()
    {
        Key.Left, Key.Right, Key.Up, Key.Down,
        Key.Home, Key.End, Key.PageUp, Key.PageDown,
        Key.Insert, Key.Delete, Key.Clear
    };

    private bool _searchShowingAlbumDetail;
    private int _searchSelectedIndexBeforeAlbum = 0; // 打开专辑前搜索结果的光标位置（返回时恢复）

    private LyricsWindow? _lyricsWindow;

    private ContextMenu _sourceContextMenu = null!;
    private readonly HashSet<ListBox> _externalSongLists = new();

    private readonly System.Windows.Threading.DispatcherTimer _positionTimer =
        new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly System.Windows.Threading.DispatcherTimer _clockTimer =
        new() { Interval = TimeSpan.FromSeconds(1) };


    public MainWindow(
        IAccessibleAnnouncer accessibilityService,
        IPlaybackCoordinator playback,
        IAudioPlaybackControl playerService,
        SettingsWorkflow settingsWorkflow,
        ITtsService ttsService,
        IMusicSourceService musicSourceService,
        LyricsWorkflow lyrics,
        IDownloadWorkflow downloads,
        SavedPlaylistWorkflow savedPlaylists,
        PlaylistImportView playlistImportPanel,
        ApplicationWindowFactory windowFactory,
        DesktopLifecycleWorkflow desktopLifecycle,
        MusicBrowseWorkflow browseWorkflow)
    {
        _accessibilityService = accessibilityService;
        _playback = playback;
        _playerService = playerService;
        _settingsWorkflow = settingsWorkflow;
        _ttsService = ttsService;
        _musicSourceService = musicSourceService;
        _lyrics = lyrics;
        _downloads = downloads;
        _savedPlaylists = savedPlaylists;
        _playlistImportPanel = playlistImportPanel;
        _windowFactory = windowFactory;
        _desktopLifecycle = desktopLifecycle;
        _browseWorkflow = browseWorkflow;

        InitializeComponent();
        _desktopLifecycle.Attach(this);
        if (_accessibilityService is WpfAccessibleAnnouncer wpfAnnouncer)
            wpfAnnouncer.Attach(LiveRegion);
        _accessibilityService.SpeechEnabled = true;
        PlaylistImportHost.Content = _playlistImportPanel;
        AttachSongListCapabilities(_playlistImportPanel.SongList);
        StatusText.Text = "正在初始化...";
        InitializePlayMode();
        _positionTimer.Tick += PositionTimer_Tick;
        _clockTimer.Tick += ClockTimer_Tick;
        AssignSongContextMenus();
        _sourceContextMenu = (ContextMenu)FindResource("SourceContextMenu");
        _sourceContextMenu.PreviewKeyUp += ContextMenu_PreviewKeyUp;
        _playback.QueueCompleted += PlayerService_QueueCompleted;
        _settingsWorkflow.HotkeyPressed += HotkeyManager_HotkeyPressed;
        _settingsWorkflow.HotkeysRegistrationFailed += HotkeyManager_HotkeysRegistrationFailed;
        _musicSourceService.SourcesChanged += MusicSourceService_SourcesChanged;
        _musicSourceService.ScriptUpdateAlert += MusicSourceService_ScriptUpdateAlert;
        DownloadTaskList.ItemsSource = _downloads.Tasks;

        SetupEventHandlers();
        InstallNumpadHook();

        StatusText.Text = "就绪";
        TimeText.Text = DateTime.Now.ToString("HH:mm:ss");
        SendWelcomeMessage();

        _positionTimer.Start();
        _clockTimer.Start();

        _ = LoadSettingsAsync();
        _browseWorkflow.SelectCategorySource(GetComboTag(CategorySourceCombo, "netease"));
        _browseWorkflow.SelectRankingSource(GetComboTag(RankingSourceCombo, "netease"));
        _ = _browseWorkflow.LoadSearchHistoryAsync();
        RefreshSourceList();
    }

    private void InitializePlayMode()
    {
        UpdatePlayModeDisplay();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    private void SendWelcomeMessage()
    {
        Announce("欢迎使用落雪音乐无障碍版");
    }

    /// <summary>音源列表变化（订阅更新/默认/启停/增删）：在 UI 线程刷新音源管理列表</summary>
    private void MusicSourceService_SourcesChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { RefreshSourceList(); }
            catch (Exception ex) { Log.Warning("Source list refresh failed: ErrorType={ErrorType}", ex.GetType().Name); }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>脚本音源 updateAlert 上抛：语音提示音源更新信息</summary>
    private void MusicSourceService_ScriptUpdateAlert(object? sender, LxScriptUpdateAlertEventArgs e)
    {
        if (e == null || string.IsNullOrWhiteSpace(e.Log)) return;
        var msg = "音源更新提示：" + e.Log;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { Announce(msg); }
            catch (Exception ex) { Log.Warning("Script source update announcement failed: ErrorType={ErrorType}", ex.GetType().Name); }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
    private void Announce(
        string message,
        AnnouncementKind kind = AnnouncementKind.Status,
        string? activityId = null,
        AnnouncementSpeech speech = AnnouncementSpeech.FollowPreference)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _ = Dispatcher.BeginInvoke(
            () => StatusText.Text = message,
            System.Windows.Threading.DispatcherPriority.Input);
        _accessibilityService.Announce(new AccessibleAnnouncement(message, kind, activityId, speech));
    }


    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_playerService == null) return;
        // 与音量合成器双向同步：用户在合成器调整音量后，应用内部音量值随之更新
        _playerService.SyncVolumeFromDevice();
        var pos = _playerService.Position;
        var dur = _playerService.Duration;
        PositionText.Text = $"{FormatTime(pos)} / {FormatTime(dur)}";

    }

    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        TimeText.Text = DateTime.Now.ToString("HH:mm:ss");
    }

    private static string FormatTime(TimeSpan t)
    {
        return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
    }
    // ==================== 标签页导航 ====================

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged 是冒泡路由事件：若不判断来源，TabControl 内部
        // 组合框/列表的选择变化也会触发本方法，导致焦点被强行抢走
        // （在组合框/列表里按方向键“跳出”的根源）。
        if (!ReferenceEquals(e.OriginalSource, MainTabControl) && !ReferenceEquals(e.Source, MainTabControl))
            return;
        if (MainTabControl.SelectedItem is not TabItem tab) return;
        _tabFocusToken++;
        var idx = MainTabControl.SelectedIndex;

        // 标签条方向键切页时保持焦点在标签条上，不抢入页面内容（页面内容用 Tab 键进入）
        if (_suppressTabContentFocus)
        {
            _suppressTabContentFocus = false;
            return;
        }

        switch (idx)
        {
            case 0: FocusControl(SearchBox); break;
            case 1: FocusListContent(FavoriteSongsList); break;
            case 2: FocusControl(_playlistImportPanel.FirstFocusable); break;
            case 3: FocusListContent(CategoryList); break;
            case 4: FocusListContent(RankingList); break;
            case 5: FocusControl(AutoPlayCheckBox); break;
            case 6: FocusListContent(DownloadTaskList); break;
            case 7: RefreshSourceList(); FocusListContent(SourceList); break;
        }
    }

    private void NavigateToTab(int index)
    {
        if (index >= 0 && index < MainTabControl.Items.Count)
            MainTabControl.SelectedIndex = index;
    }

    // 菜单导航
    private void MenuNavigateToSearch(object sender, RoutedEventArgs e) => NavigateToTab(0);
    private void MenuNavigateToPlaylist(object sender, RoutedEventArgs e) => NavigateToTab(1);
    private void MenuNavigateToImport(object sender, RoutedEventArgs e) => NavigateToTab(2);
    private void MenuNavigateToCategory(object sender, RoutedEventArgs e) => NavigateToTab(3);
    private void MenuNavigateToRanking(object sender, RoutedEventArgs e) => NavigateToTab(4);
    private void MenuNavigateToLyrics(object sender, RoutedEventArgs e) => OpenLyricsWindow();
    private void MenuNavigateToSettings(object sender, RoutedEventArgs e) => NavigateToTab(5);
    private void MenuNavigateToDownload(object sender, RoutedEventArgs e) => NavigateToTab(6);
    private void MenuNavigateToSources(object sender, RoutedEventArgs e) => NavigateToTab(7);

    // ==================== 键盘导航 ====================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 快捷键录制模式：吞掉所有按键，交给录制处理
        if (_recordingHotkeyItem != null)
        {
            HandleHotkeyRecording(e);
            e.Handled = true;
            return;
        }

        // Alt+F4：按设置决定关闭应用或隐藏到系统托盘
        if ((e.Key == Key.F4 || e.SystemKey == Key.F4) && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            e.Handled = true;
            _ = _desktopLifecycle.ExecuteAsync(
                DesktopCommand.AltF4,
                string.Equals(GetComboTag(CloseBehaviorCombo, "close"), "hide", StringComparison.OrdinalIgnoreCase));
            return;
        }

        var mods = Keyboard.Modifiers;

        // 搜索历史操作（搜索框聚焦时，在隧道阶段处理，确保先于 TextBox 默认按键行为）
        if (ReferenceEquals(Keyboard.FocusedElement, SearchBox))
        {
            var delKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (e.Key == Key.Up && mods == ModifierKeys.None && BrowseSearchHistory(step: 1))
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down && mods == ModifierKeys.None && BrowseSearchHistory(step: -1))
            {
                e.Handled = true;
                return;
            }
            if (delKey == Key.Delete && mods == ModifierKeys.None)
            {
                if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    ClearSearchContent();
                    e.Handled = true;
                    return;
                }
            }
            if (delKey == Key.Delete && (mods & ModifierKeys.Control) != 0 && (mods & ModifierKeys.Alt) == 0)
            {
                if (DeleteCurrentSearchHistory())
                {
                    e.Handled = true;
                    return;
                }
            }
            if (delKey == Key.Delete && (mods & ModifierKeys.Alt) != 0 && (mods & ModifierKeys.Control) == 0)
            {
                ClearAllSearchHistory();
                e.Handled = true;
                return;
            }
        }

        // 修复：主标签条 Shift+Tab/Tab（与 Tab 键相同的导航逻辑，不跨选项卡、不卡死在标签条上）
        // 页内 Tab/Shift+Tab 由各标签页自身的 TabNavigation="Cycle" 处理；当焦点落在主标签条
        // 标签头上时（方向键切页、鼠标、读屏导航可能到达），Tab 进入当前页首控件、Shift+Tab
        // 回到当前页末控件，避免在标签条上卡死，且绝不跳去其它选项卡。
        if (e.Key == Key.Tab && (mods & ~ModifierKeys.Shift) == 0)
        {
            bool isShiftTab = (mods & ModifierKeys.Shift) != 0;
            if (Keyboard.FocusedElement is TabItem stripTab
                && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(stripTab), MainTabControl))
            {
                int cur = MainTabControl.SelectedIndex;
                if (cur < 0) return;
                FrameworkElement? target = isShiftTab ? GetLastControlOfCurrentTab() : GetFirstControlOfPage(cur);
                if (target != null)
                {
                    FocusListContent(target);
                    e.Handled = true;
                    return;
                }
            }
        }

        // 焦点在标签页标题上：左右/上下键确定性切换标签页，绕过原生 TabControl 的焦点管理（避免切页后焦点乱跳）
        if (mods == ModifierKeys.None && (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            && Keyboard.FocusedElement is TabItem focusedTab
            && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(focusedTab), MainTabControl))
        {
            var dir = e.Key is Key.Left or Key.Up ? -1 : 1;
            var target = MainTabControl.SelectedIndex + dir;
            if (target >= 0 && target < MainTabControl.Items.Count)
            {
                // 标签条内切页：焦点保持在新标签标题上，不抢入页面内容（内容由 Tab 键进入）
                _suppressTabContentFocus = true;
                NavigateToTab(target);
                if (MainTabControl.ItemContainerGenerator.ContainerFromIndex(target) is TabItem newTab)
                    newTab.Focus();
            }
            e.Handled = true;
            return;
        }


        // 配置的全局快捷键（仅处理全局注册失败的组合，避免与 WM_HOTKEY 重复触发）
        var effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
        var fallbackHotkey = _settingsWorkflow.MatchFallback(effectiveKey, mods);
        if (fallbackHotkey != null)
        {
            // 修复：NumLock 关闭时小键盘 4/6/2/8 会作为方向键上报，避免其误触发方向键热键
            if (!IsNumpadOriginHotkey(fallbackHotkey.Key))
                ExecuteHotkeyAction(fallbackHotkey.ActionId);
            e.Handled = true;
            return;
        }

        // Alt+1~8 快速切换页面
        if (mods == ModifierKeys.Alt)
        {
            int? tab = e.SystemKey switch
            {
                Key.D1 or Key.NumPad1 => 0,
                Key.D2 or Key.NumPad2 => 1,
                Key.D3 or Key.NumPad3 => 2,
                Key.D4 or Key.NumPad4 => 3,
                Key.D5 or Key.NumPad5 => 4,
                Key.D6 or Key.NumPad6 => 5,
                Key.D7 or Key.NumPad7 => 6,
                Key.D8 or Key.NumPad8 => 7,

                _ => null
            };
            if (tab.HasValue) { NavigateToTab(tab.Value); e.Handled = true; return; }

            switch (e.SystemKey)
            {
                case Key.M: FocusSourceCombo(); e.Handled = true; return;
                case Key.C:
                    if (MainTabControl.SelectedIndex == 3) FocusListContent(CategoryList);
                    e.Handled = true; return;
                case Key.L:
                    if (MainTabControl.SelectedIndex == 1) FocusListContent(FavoriteSongsList);
                    else if (MainTabControl.SelectedIndex == 3) FocusListContent(CategoryPlaylistsListBox);
                    e.Handled = true; return;
                case Key.S: FocusSongList(); e.Handled = true; return;
                case Key.R:
                    if (MainTabControl.SelectedIndex == 4) FocusListContent(RankingList);
                    e.Handled = true; return;
            }
        }

        if (mods == ModifierKeys.Control && e.Key == Key.F)
        {
            NavigateToTab(0);
            SearchBox.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            if (TryOpenSongContextMenu() || TryOpenPlaylistContextMenu() || TryOpenSourceContextMenu())
            {
                e.Handled = true;
                return;
            }
        }

        // 局部快捷键（IsGlobal=false，仅主窗口聚焦时生效；保留组合键优先）
        var localHotkey = _settingsWorkflow.MatchLocalHotkey(effectiveKey, mods);
        if (localHotkey != null)
        {
            // 修复：NumLock 关闭时小键盘 4/6/2/8 会作为方向键上报，避免其误触发方向键热键
            if (!IsNumpadOriginHotkey(localHotkey.Key))
                ExecuteHotkeyAction(localHotkey.ActionId);
            e.Handled = true;
            return;
        }

        // Ctrl+R：刷新当前页面（原 F5 已改为局部快捷键“播放/暂停”）
        if (mods == ModifierKeys.Control && e.Key == Key.R)
        {
            RefreshCurrentTab();
            e.Handled = true;
            return;
        }

        // 退格键：专辑详情返回搜索结果（保留之前光标位置；搜索框内退格仍为删字）
        if (e.Key == Key.Back && mods == ModifierKeys.None && _searchShowingAlbumDetail
            && !ReferenceEquals(Keyboard.FocusedElement, SearchBox))
        {
            _ = ReturnFromAlbumDetailAsync();
            e.Handled = true;
            return;
        }
        // ESC：导入个人歌单页在歌曲预览态先返回歌单列表
        if (e.Key == Key.Escape && MainTabControl.SelectedIndex == 2 && _playlistImportPanel.TryHandleEscape())
        {
            e.Handled = true;
            return;
        }

        // ESC：菜单栏激活时逐级关闭菜单，不隐藏窗口；焦点回到主窗口内容后才隐藏到托盘
        if (e.Key == Key.Escape && IsMainMenuActive())
        {
            if (MainMenu.IsKeyboardFocusWithin)
            {
                // 焦点在菜单内：放行给 WPF MenuItem 原生 ESC 处理（先关子菜单、再取消菜单栏）
                return;
            }
            // 子菜单开着但焦点已回主窗口（边缘情况）：显式关闭最外层子菜单
            CloseOpenSubmenus();
            e.Handled = true;
            return;
        }

        // ESC：隐藏窗口到系统托盘
        if (e.Key == Key.Escape)
        {
            _ = _desktopLifecycle.ExecuteAsync(DesktopCommand.Escape);
            e.Handled = true;
        }
    }

    /// <summary>清空搜索框全部内容并复位搜索结果（搜索框内按 Delete 触发）</summary>
    private void ClearSearchContent()
    {
        SearchBox.Text = string.Empty;
        _browseWorkflow.ClearSearch();
        SearchResultsList.ItemsSource = null;
        _searchShowingAlbumDetail = false;
        SearchPageInfo.Text = "请输入关键词开始搜索";
        Announce("搜索已清空");
    }

    /// <summary>主菜单栏是否处于激活状态（键盘焦点在菜单内，或任一子菜单弹出中）</summary>
    private bool IsMainMenuActive()
    {
        if (MainMenu.IsKeyboardFocusWithin) return true;
        foreach (var item in MainMenu.Items)
        {
            if (item is MenuItem mi && mi.IsSubmenuOpen) return true;
        }
        return false;
    }

    /// <summary>显式关闭主菜单所有已弹出的子菜单（ESC 时焦点已回主窗口的边缘情况）</summary>
    private void CloseOpenSubmenus()
    {
        foreach (var item in MainMenu.Items)
        {
            if (item is MenuItem mi && mi.IsSubmenuOpen)
                mi.IsSubmenuOpen = false;
        }
    }

    /// <summary>应用键弹起时在菜单内拦截：WPF 的 ContextMenu 会在打开状态下收到 Key.Apps 的 KeyUp 时自动关闭菜单，
    /// 而本程序是在 KeyDown 阶段打开菜单，导致同一次按键的 KeyUp 立刻把菜单关掉（表现为“按应用键弹不出菜单”）。
    /// 在菜单的 PreviewKeyUp（隧道阶段，早于 ContextMenu 自身的 KeyUp 类处理）标记已处理，避免闪关。</summary>
    private void ContextMenu_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Apps) e.Handled = true;
    }

    /// <summary>应用键弹起兜底（主窗口焦点场景）：防止打开菜单后立即被关闭</summary>
    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Apps) e.Handled = true;
    }

    /// <summary>
    /// 方向键兜底保护（冒泡阶段）：让列表/组合框/滑块/文本框/选项卡/菜单等控件先自行处理方向键；
    /// 只有当方向键未被任何控件处理（如焦点在普通按钮上）时，才在此吞掉，
    /// 防止 WPF 的方向键焦点导航把焦点在无关控件之间跳来跳去。
    /// 注意：不能放在 Preview 阶段，否则控件永远收不到方向键，原生导航反而被破坏。
    /// </summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None && (e.Key is Key.Up or Key.Down or Key.Left or Key.Right))
        {
            // 焦点在普通控件（按钮等）上时，方向键未被子控件处理：
            // 在此吞掉，避免 WPF 的方向键焦点导航把焦点在无关控件之间跳来跳去。
            // 焦点在列表/组合框/滑块/文本框/选项卡/菜单内时，按键由这些控件自行处理，
            // 冒泡到这里时通常已 Handled；若未处理则放行给键盘导航系统。
            var fe = Keyboard.FocusedElement as DependencyObject;
            bool inList = fe != null && FindAncestor<ListBox>(fe) != null;
            bool inCombo = fe != null && FindAncestor<ComboBox>(fe) != null;
            bool inContainer = fe != null &&
                (inList || inCombo ||
                 FindAncestor<Slider>(fe) != null ||
                 FindAncestor<TextBox>(fe) != null ||
                 FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(fe) != null ||
                 FindAncestor<TabItem>(fe) != null ||
                 FindAncestor<MenuItem>(fe) != null);
            if (!inContainer)
            {
                // 焦点在普通控件（按钮等）上：吞掉方向键，避免焦点跳来跳去
                e.Handled = true;
                return;
            }
            if ((e.Key is Key.Left or Key.Right) && (inList || inCombo))
            {
                // 竖向列表/闭合组合框不处理左右键：拦截，防止被当作方向导航而跳出
                e.Handled = true;
            }
        }
    }
    /// <summary>聚焦当前页的音源下拉框</summary>
    private void FocusSourceCombo()
    {
        switch (MainTabControl.SelectedIndex)
        {
            case 0: SearchSourceCombo.Focus(); break;
            case 3: CategorySourceCombo.Focus(); break;
            case 4: RankingSourceCombo.Focus(); break;
        }
    }

    /// <summary>聚焦当前页的歌曲列表</summary>
    private void FocusSongList()
    {
        switch (MainTabControl.SelectedIndex)
        {
            case 0: FocusListContent(SearchResultsList); break;
            case 1: FocusListContent(FavoriteSongsList); break;
            case 2: FocusListContent(_playlistImportPanel.SongList); break;
            case 3: FocusListContent(CategorySongsList); break;
            case 4: FocusListContent(RankingSongsList); break;
        }
    }

    /// <summary>F5 刷新当前页面</summary>
    private void RefreshCurrentTab()
    {
        switch (MainTabControl.SelectedIndex)
        {
            case 0:
                if (!string.IsNullOrWhiteSpace(SearchBox.Text)) _ = SearchMusicAsync(resetPage: false);
                else Announce("请输入搜索关键词后再刷新");
                break;
            case 1:
                _ = LoadOnlinePlaylistsAsync();
                Announce("在线收藏已刷新");
                break;
            case 2:
                Announce("导入个人歌单页面无需刷新");
                break;
            case 3:
                _ = LoadCategoriesAsync(GetComboTag(CategorySourceCombo, "netease"));
                break;
            case 4:
                _ = LoadRankingsAsync(GetComboTag(RankingSourceCombo, "netease"));
                break;
            case 6:
                Announce(DownloadTaskList.Items.Count > 0 ? $"当前有 {DownloadTaskList.Items.Count} 个下载任务" : "暂无下载任务");
                break;
            case 7:
                RefreshSourceList();
                Announce("音源列表已刷新");
                break;
            default:
                Announce("当前页面无需刷新");
                break;
        }
    }

    // ==================== 播放器事件 ====================

    private void SetupEventHandlers()
    {
        _playback.StateChanged += (s, e) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (e.CurrentMusic != null)
                {
                    CurrentTitle.Text = e.CurrentMusic.Title;
                    CurrentArtist.Text = e.CurrentMusic.Artist;
                    CurrentAlbum.Text = e.CurrentMusic.Album;
                }
                UpdatePlayPauseMenuHeader(e.NewState == Models.PlayerState.Playing ? "暂停" : "播放");
                var text = e.NewState switch
                {
                    Models.PlayerState.Playing => "正在播放",
                    Models.PlayerState.Paused => "已暂停",
                    Models.PlayerState.Stopped => "已停止",
                    Models.PlayerState.Loading => "正在加载",
                    Models.PlayerState.Error => "播放错误",
                    _ => "就绪"
                };
                StatusText.Text = text;
                if (e.NewState is Models.PlayerState.Playing or Models.PlayerState.Paused or Models.PlayerState.Stopped)
                    Announce(text);
            });
        };
    }

    // ==================== 设置 ====================

    private async Task LoadSettingsAsync()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var settings = (await _settingsWorkflow.LoadAsync(handle)).Settings;
            if (settings != null)
            {
                ApplyVolume(settings.DefaultVolume, announce: false);
                AutoPlayCheckBox.IsChecked = settings.AutoPlay;
                ShufflePlayCheckBox.IsChecked = settings.ShuffleMode;
                RepeatPlayCheckBox.IsChecked = settings.LoopMode;
                EnableScreenReaderCheckBox.IsChecked = settings.EnableScreenReader;
                EnableVoiceAnnouncementCheckBox.IsChecked = settings.EnableVoiceAnnouncement;
                var defaultQuality = string.IsNullOrEmpty(settings.DefaultPlayQuality) ? "128k" : settings.DefaultPlayQuality;
                SelectComboByTag(DefaultQualityCombo, defaultQuality);
                SelectComboByTag(DownloadQualityCombo,
                    string.IsNullOrEmpty(settings.DefaultDownloadQuality) ? "128k" : settings.DefaultDownloadQuality);
                var savedDownloadPath = settings.DownloadPath?.Trim();
                DownloadPathBox.Text = _downloads.ResolveDownloadDirectory(savedDownloadPath);
                DownloadLyricCheckBox.IsChecked = settings.DownloadLyric;
                DownloadNameQualityCheckBox.IsChecked = settings.DownloadNameIncludeQuality;
                SelectComboByTag(DownloadNameOrderCombo, settings.DownloadNameOrder.ToString());
                DataDirBox.Text = _settingsWorkflow.DataDirectory;
                if (ThirdPartyPlayerPathBox != null)
                    ThirdPartyPlayerPathBox.Text = settings.ThirdPartyPlayerPath ?? string.Empty;
                _lyrics.ApplySettings(
                    settings.LyricAutoAnnounce,
                    settings.LyricAutoScroll,
                    settings.LyricOffsets);
                LyricAnnounceCheckBox.IsChecked = _lyrics.AutoAnnounce;
                LyricScrollCheckBox.IsChecked = _lyrics.AutoScroll;
                SelectComboByTag(SearchSourceCombo, settings.DefaultSearchSource);
                _playback.Mode = settings.ShuffleMode
                    ? PlayMode.Shuffle
                    : settings.LoopMode ? PlayMode.Loop : PlayMode.Sequential;
                _playback.DefaultQuality = defaultQuality;
                UpdatePlayModeDisplay();

                if (CloseBehaviorCombo != null)
                    SelectComboByTag(CloseBehaviorCombo, settings.HideToTrayOnAltF4 ? "hide" : "close");
                _accessibilityService.SpeechEnabled = settings.EnableVoiceAnnouncement;
            }

            // 加载本地收藏与在线收藏（歌单结构）
            _ = LoadOnlinePlaylistsAsync();

            // 语音设置：朗读语音 + 语速（自动路由后端）
            LoadVoiceSettings(settings);

            // 快捷键配置：载入 + 注册全局热键
            BuildHotkeySettingItems();
            RefreshHotkeyConflictMarkers();

            // 应用下载歌词/命名设置到下载服务（立即生效）
            ApplyDownloadSettings();

            // 恢复上次选择的输出设备（无效索引回退系统默认）
            if (_playerService != null && settings != null)
            {
                var idx = settings.OutputDeviceIndex;
                var valid = idx == -1 || _playerService.GetOutputDevices().Any(d => d.Index == idx);
                if (valid && idx != _playerService.CurrentOutputDeviceIndex)
                    _playerService.SwitchOutputDevice(idx);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载设置失败: {ex.Message}");
        }
        finally
        {
            _settingsLoaded = true;
        }
    }

    private void ShufflePlayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || _suppressPlaybackSettingChanges) return;
        _playback.Mode = ShufflePlayCheckBox.IsChecked == true
            ? PlayMode.Shuffle
            : RepeatPlayCheckBox.IsChecked == true ? PlayMode.Loop : PlayMode.Sequential;
        UpdatePlayModeDisplay();
    }

    private void RepeatPlayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded || _suppressPlaybackSettingChanges) return;
        _playback.Mode = ShufflePlayCheckBox.IsChecked == true
            ? PlayMode.Shuffle
            : RepeatPlayCheckBox.IsChecked == true ? PlayMode.Loop : PlayMode.Sequential;
        UpdatePlayModeDisplay();
    }

    private void VoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded || _ttsService == null) return;
        if (VoiceCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string voiceId)
            _ttsService.SetVoice(voiceId);
    }

    private void DefaultQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded) return;
        var q = GetComboTag(DefaultQualityCombo, "128k");
        if (string.IsNullOrEmpty(q)) return;
        _playback.DefaultQuality = q;
    }

    private void SpeechRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded || _ttsService == null) return;
        if (SpeechRateCombo.SelectedItem is ComboBoxItem cbi &&
            double.TryParse(cbi.Tag?.ToString(), out var rate))
            _ttsService.SetRate(rate);
    }

    private void VoiceAnnouncementCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return;
        var enabled = EnableVoiceAnnouncementCheckBox.IsChecked == true;
        _accessibilityService.SpeechEnabled = enabled;
        Announce(enabled ? "已开启语音朗读提示" : "已关闭语音朗读提示");
    }

    private void LyricAnnounceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return;
        _lyrics.AutoAnnounce = LyricAnnounceCheckBox.IsChecked == true;
        Announce(_lyrics.AutoAnnounce ? "已开启歌词自动朗读" : "已关闭歌词自动朗读");
    }

    private void LyricScrollCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return;
        _lyrics.AutoScroll = LyricScrollCheckBox.IsChecked == true;
        Announce(_lyrics.AutoScroll ? "已开启歌词自动滚动" : "已关闭歌词自动滚动");
    }

    /// <summary>清理下载模块拥有且当前未使用的外部播放临时文件。</summary>
    private void ClearCacheBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = _downloads.ClearTemporaryFiles();
        Announce(result.DeletedFiles > 0
            ? $"已清理缓存 {result.DeletedFiles} 个文件，释放 {FormatBytes(result.FreedBytes)}。"
            : "没有可清理的缓存文件。");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes} B";
    }
    private void BrowseDownloadPathBtn_Click(object sender, RoutedEventArgs e)
    {
        var folderDialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择歌曲下载目录" };
        var current = GetEffectiveDownloadDir();
        folderDialog.SelectedPath = current;
        if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            DownloadPathBox.Text = folderDialog.SelectedPath;
            Announce($"下载目录已设置为 {folderDialog.SelectedPath}");
        }
    }

    /// <summary>浏览并选择第三方播放器 exe 路径</summary>
    private void BrowseThirdPartyPlayerBtn_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "播放器程序|*.exe|所有文件|*.*",
            Title = "选择第三方播放器程序",
            Multiselect = false
        };
        if (openFileDialog.ShowDialog() != true) return;
        ThirdPartyPlayerPathBox.Text = openFileDialog.FileName;
        Announce($"第三方播放器已设置为 {openFileDialog.FileName}");
    }

    /// <summary>Browse and apply a custom data directory (database + logs).</summary>
    private void BrowseDataDirBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择数据目录（将保存数据库与日志）",
                UseDescriptionForTitle = true
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var selectedParent = dlg.SelectedPath?.Trim();
            if (string.IsNullOrEmpty(selectedParent)) return;
            var result = _settingsWorkflow.ChangeDataDirectory(selectedParent);
            DataDirBox.Text = result.Path;
            Announce(result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Set data dir failed: " + ex.Message);
            Announce("设置数据目录失败");
        }
    }

    /// <summary>当前生效的下载目录（设置优先，非法/不可用的路径自动回退到服务默认值）</summary>
    private string GetEffectiveDownloadDir()
        => _downloads.ResolveDownloadDirectory(DownloadPathBox?.Text);

    /// <summary>把下载设置应用到下载服务（立即生效，影响之后的下载任务）</summary>
    private void ApplyDownloadSettings()
    {
        var orderTag = GetComboTag(DownloadNameOrderCombo, "ArtistTitle");
        _downloads.ApplyPreferences(new DownloadPreferences(
            DownloadLyricCheckBox?.IsChecked ?? true,
            string.Equals(orderTag, "TitleArtist", StringComparison.OrdinalIgnoreCase)
                ? DownloadNameOrder.TitleArtist
                : DownloadNameOrder.ArtistTitle,
            DownloadNameQualityCheckBox?.IsChecked ?? true));
    }

    private void DownloadSetting_Changed(object sender, RoutedEventArgs e) => ApplyDownloadSettings();

    private void DownloadNameOrderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyDownloadSettings();

    /// <summary>载入朗读语音/语速设置并应用到 TTS 服务</summary>
    private void LoadVoiceSettings(UserSettings? settings)
    {
        if (_ttsService == null || VoiceCombo == null) return;
        try
        {
            VoiceCombo.Items.Clear();
            VoiceCombo.Items.Add(new ComboBoxItem { Content = "自动检测（推荐）", Tag = "" });
            foreach (var v in _ttsService.GetAvailableVoices())
                VoiceCombo.Items.Add(new ComboBoxItem { Content = $"{v.Name}（{v.Culture}）", Tag = v.Id });

            var voiceId = settings?.AccessibilitySettings?.AnnouncementVoiceId ?? "";
            SelectComboByTag(VoiceCombo, voiceId);
            var rate = settings?.AccessibilitySettings?.AnnouncementRate ?? 1.0;
            SelectComboByTag(SpeechRateCombo, rate.ToString("0.##"));

            _ttsService.SetRate(rate);
            if (!string.IsNullOrEmpty(voiceId))
                _ttsService.SetVoice(voiceId);

            // 朗读后端指示（争渡/保益/读屏/SAPI）
            if (SpeechBackendText != null)
                SpeechBackendText.Text = $"朗读后端：{_ttsService.ActiveBackendName}（自动检测）";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载语音设置失败: {ex.Message}");
        }
    }

    /// <summary>按 Tag 选中组合框项（找不到则保持现状）</summary>
    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        if (combo == null || string.IsNullOrEmpty(tag)) return;
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }


    internal ApplicationShutdownSnapshot CaptureAndBlockShutdown()
    {
        if (_shutdownBlocked)
            throw new InvalidOperationException("Shutdown state has already been captured.");

        var edit = _settingsWorkflow.Edit(settings =>
        {
            settings.DefaultVolume = _playerService.Volume;
            settings.WindowWidth = (int)Math.Round(ActualWidth);
            settings.WindowHeight = (int)Math.Round(ActualHeight);
            settings.AutoPlay = AutoPlayCheckBox.IsChecked ?? true;
            settings.ShuffleMode = ShufflePlayCheckBox.IsChecked ?? false;
            settings.LoopMode = RepeatPlayCheckBox.IsChecked ?? false;
            settings.EnableScreenReader = EnableScreenReaderCheckBox.IsChecked ?? true;
            settings.EnableVoiceAnnouncement = EnableVoiceAnnouncementCheckBox.IsChecked ?? true;
            settings.DefaultPlayQuality = GetComboTag(DefaultQualityCombo, "128k");
            settings.DefaultDownloadQuality = GetComboTag(DownloadQualityCombo, "128k");
            settings.DownloadPath = DownloadPathBox.Text?.Trim() ?? string.Empty;
            settings.DownloadLyric = DownloadLyricCheckBox?.IsChecked ?? true;
            settings.DownloadNameOrder = string.Equals(
                GetComboTag(DownloadNameOrderCombo, "ArtistTitle"),
                "TitleArtist",
                StringComparison.OrdinalIgnoreCase)
                ? DownloadNameOrder.TitleArtist
                : DownloadNameOrder.ArtistTitle;
            settings.DownloadNameIncludeQuality = DownloadNameQualityCheckBox?.IsChecked ?? true;
            settings.ThirdPartyPlayerPath = ThirdPartyPlayerPathBox?.Text?.Trim() ?? string.Empty;
            settings.LyricAutoAnnounce = LyricAnnounceCheckBox.IsChecked ?? true;
            settings.LyricAutoScroll = LyricScrollCheckBox.IsChecked ?? true;
            settings.DefaultSearchSource = GetComboTag(SearchSourceCombo, "all");
            settings.AccessibilitySettings ??= new AccessibilitySettings();
            settings.AccessibilitySettings.AnnouncementVoiceId = GetComboTag(VoiceCombo, "");
            settings.AccessibilitySettings.AnnouncementRate =
                double.TryParse(GetComboTag(SpeechRateCombo, "1"), out var speechRate)
                    ? speechRate
                    : 1.0;
            settings.CustomHotkeys = _settingsWorkflow.GetHotkeys().ToList();
            settings.OutputDeviceIndex = _playerService.CurrentOutputDeviceIndex;
            settings.LyricOffsets = new Dictionary<string, int>(_lyrics.GetOffsetsSnapshot());
            settings.HideToTrayOnAltF4 = string.Equals(
                GetComboTag(CloseBehaviorCombo, "close"),
                "hide",
                StringComparison.OrdinalIgnoreCase);
            settings.Playback = new PlaybackSessionState
            {
                CurrentMusic = _playback.CurrentMusic is null
                    ? null
                    : CapturePlaybackMusic(_playback.CurrentMusic),
                PositionSeconds = Math.Max(0, _playerService.Position.TotalSeconds),
                WasPlaying = _playback.IsPlaying,
                WasPaused = _playback.IsPaused
            };
        });
        if (!edit.Accepted)
            throw new InvalidOperationException(string.Join(" ", edit.Errors));
        _shutdownBlocked = true;
        IsEnabled = false;
        _positionTimer.Stop();
        _clockTimer.Stop();
        _settingsWorkflow.UnregisterAllGlobal();
        UninstallNumpadHook();
        _musicSourceService.SourcesChanged -= MusicSourceService_SourcesChanged;
        _musicSourceService.ScriptUpdateAlert -= MusicSourceService_ScriptUpdateAlert;
        _playback.QueueCompleted -= PlayerService_QueueCompleted;
        _desktopLifecycle.PrepareForShutdown();

        return new ApplicationShutdownSnapshot(edit.State);
    }

    private static MusicInfo CapturePlaybackMusic(MusicInfo music) => new()
    {
        Id = music.Id,
        Hash = music.Hash,
        Songmid = music.Songmid,
        Title = music.Title,
        Artist = music.Artist,
        Album = music.Album,
        Duration = music.Duration,
        Source = music.Source,
        Quality = music.Quality,
        AddedDate = music.AddedDate,
        PlayCount = music.PlayCount,
        IsAlbum = music.IsAlbum
    };
    // ==================== 搜索 ====================

    private async Task SearchMusicAsync(bool resetPage = true)
    {
        var query = SearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            Announce("请输入搜索关键词");
            return;
        }

        _searchShowingAlbumDetail = false;
        var searchType = GetComboTag(SearchTypeComboBox, "song");
        if (searchType == "artist")
        {
            Announce("提示：当前暂不支持歌手搜索，已按歌曲搜索");
            searchType = "song";
        }

        if (resetPage)
        {
            _browseWorkflow.SelectSearch(
                query,
                searchType,
                GetComboTag(SearchSourceCombo, "all"));
            await _browseWorkflow.RecordSearchAsync(query);
            await LoadSearchMoreAsync(reset: true);
        }
        else
        {
            RenderSearchResults(0);
        }
    }

    /// <summary>加载下一批搜索结果并累积显示（“加载更多”/自动续播共用；结果全局去重、每批最多 pageSize 条）</summary>
    private async Task<BrowseResult<SearchBrowseSnapshot>> LoadSearchMoreAsync(
        bool reset = false,
        bool applyFocus = true)
    {
        var result = await _browseWorkflow.ActivateSearchAsync(reset);
        if (result.Succeeded && result.Value is not null
            && _browseWorkflow.IsCurrent(result.Revision))
        {
            RenderSearchResults(
                applyFocus ? result.Value.PreviousCount : null,
                result.Value);
        }
        return result;
    }

    /// <summary>把已累积结果 + “加载更多”伪条目渲染到搜索结果列表</summary>
    private void RenderSearchResults(
        int? selectIndex,
        SearchBrowseSnapshot? snapshot = null)
    {
        snapshot ??= _browseWorkflow.CurrentSearch;
        var items = new List<MusicInfo>(snapshot.Items);
        if (snapshot.HasMore)
            items.Add(new MusicInfo { Id = SearchMoreId, Title = "加载更多", Artist = $"当前已显示 {snapshot.Items.Count} 首，回车继续加载" });

        SearchResultsList.ItemsSource = items;
        SearchPageInfo.Text = $"已加载 {snapshot.Items.Count} 首" + (snapshot.HasMore ? "，还有更多" : "，已到底");

        if (selectIndex.HasValue && selectIndex.Value >= 0 && selectIndex.Value < items.Count)
        {
            SearchResultsList.SelectedIndex = selectIndex.Value;
            // 先滚动到选中项，确保其容器已实例化并可见，焦点才会落在该项而非列表首项
            if (SearchResultsList.SelectedItem is MusicInfo selectedMusic)
                SearchResultsList.ScrollIntoView(selectedMusic);
            FocusListContent(SearchResultsList);
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = SearchMusicAsync();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchMusicAsync();
    }

    /// <summary>浏览搜索历史：step=+1 上翻（更早记录），step=-1 下翻（更新记录）。返回是否处理了按键。</summary>
    private bool BrowseSearchHistory(int step)
    {
        var navigation = _browseWorkflow.NavigateSearchHistory(SearchBox.Text ?? string.Empty, step);
        if (!navigation.Handled) return false;
        SearchBox.Text = navigation.Text;
        SearchBox.CaretIndex = navigation.Text.Length;
        return true;
    }

    private bool DeleteCurrentSearchHistory()
    {
        _ = DeleteCurrentSearchHistoryAsync();
        return true;
    }

    private async Task DeleteCurrentSearchHistoryAsync()
    {
        var deleted = await _browseWorkflow.DeleteSearchHistoryAsync(SearchBox.Text ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(deleted)) Announce($"已删除搜索记录：{deleted}");
    }

    private bool ClearAllSearchHistory()
    {
        _ = ClearAllSearchHistoryAsync();
        return true;
    }

    private async Task ClearAllSearchHistoryAsync()
    {
        if (await _browseWorkflow.ClearSearchHistoryAsync()) Announce("已清空全部搜索记录");
    }

    private void SearchClearButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        _browseWorkflow.ClearSearch();
        SearchResultsList.ItemsSource = null;
        _searchShowingAlbumDetail = false;
        SearchPageInfo.Text = "请输入关键词开始搜索";
        Announce("搜索已清空");
    }

    private void SearchPrevPageBtn_Click(object sender, RoutedEventArgs e)
    {
        Announce("结果已累积显示，请用向上方向键或 Home 回到前面。");
    }

    private async void SearchNextPageBtn_Click(object sender, RoutedEventArgs e)
    {
        await LoadSearchMoreAsync();
    }

    /// <summary>列表内“上一页/下一页”条目与歌曲的统一激活处理</summary>
    private async Task HandleSongActivationAsync(ListBox list, MusicInfo music)
    {
        if (music.Id == PagePrevId)
        {
            if (list == SearchResultsList) { Announce("结果已累积显示，请用向上方向键或 Home 回到前面。"); }
            else if (list == CategorySongsList && _browseWorkflow.CurrentPlaylistPage is { } playlistPage)
                await LoadCategoryPlaylistDetailAsync(playlistPage.Playlist, playlistPage.Page - 1, applyFocus: false);
            else if (list == RankingSongsList && _browseWorkflow.CurrentRankingPage is { } rankingPage)
                await LoadRankingDetailAsync(rankingPage.Ranking, rankingPage.Page - 1, applyFocus: false);
            return;
        }
        if (music.Id == PageNextId)
        {
            if (list == SearchResultsList) await LoadSearchMoreAsync();
            else if (list == CategorySongsList && _browseWorkflow.CurrentPlaylistPage is { } playlistPage)
                await LoadCategoryPlaylistDetailAsync(playlistPage.Playlist, playlistPage.Page + 1, applyFocus: false);
            else if (list == RankingSongsList && _browseWorkflow.CurrentRankingPage is { } rankingPage)
                await LoadRankingDetailAsync(rankingPage.Ranking, rankingPage.Page + 1, applyFocus: false);
            return;
        }
        // 搜索“加载更多”伪条目：继续累积加载
        if (music.Id == SearchMoreId)
        {
            if (list == SearchResultsList) await LoadSearchMoreAsync();
            return;
        }
        // 专辑歌曲列表的“返回专辑列表”伪条目
        if (music.Id == AlbumBackId)
        {
            await ReturnFromAlbumDetailAsync();
            return;
        }
        // 专辑搜索行：回车展开专辑歌曲列表
        if (music.IsAlbum)
        {
            await OpenAlbumDetailAsync(music);
            return;
        }
        await PlayMusicAsync(music, null, list);
    }

    /// <summary>展开专辑搜索结果：分页拉取专辑全部歌曲并装入搜索结果列表，第一项为“返回专辑列表”</summary>
    private async Task OpenAlbumDetailAsync(MusicInfo album)
    {
        if (album == null) return;
        // 记录打开专辑前搜索结果中的光标位置，返回时恢复
        _searchSelectedIndexBeforeAlbum = Math.Max(0, SearchResultsList.SelectedIndex);
        _searchShowingAlbumDetail = true;
        var result = await _browseWorkflow.ActivateAlbumAsync(album);
        if (!result.Succeeded || result.Value is null
            || !_browseWorkflow.IsCurrent(result.Revision))
        {
            _searchShowingAlbumDetail = false;
            return;
        }

        var navList = new List<MusicInfo>
        {
            new() { Id = AlbumBackId, Title = "← 返回专辑列表", Artist = "回车返回专辑搜索结果" }
        };
        navList.AddRange(result.Value);
        SearchResultsList.ItemsSource = navList;
        if (result.Value.Count > 0)
        {
            SearchResultsList.SelectedIndex = 1;
            ApplyBrowseFocus(result.FocusTarget);
        }
        else
        {
            _searchShowingAlbumDetail = false;
        }
    }

    /// <summary>从专辑详情返回搜索结果，并把光标恢复到打开专辑前的位置</summary>
    private Task ReturnFromAlbumDetailAsync()
    {
        if (!_searchShowingAlbumDetail) return Task.CompletedTask;
        _searchShowingAlbumDetail = false;
        RenderSearchResults(Math.Max(0, _searchSelectedIndexBeforeAlbum));
        return Task.CompletedTask;
    }


    private async void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultsList.SelectedItem is MusicInfo music)
            await HandleSongActivationAsync(SearchResultsList, music);
    }

    private async void SearchResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift && SearchResultsList.SelectedItem is MusicInfo lyricMusic)
        {
            ShowLyricsForSong(lyricMusic);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Enter || e.Key == Key.Space) && SearchResultsList.SelectedItem is MusicInfo music)
        {
            await HandleSongActivationAsync(SearchResultsList, music);
            e.Handled = true;
        }
    }

    // ==================== 分类歌单 ====================

    private async Task LoadCategoriesAsync(string sourceId, bool applyFocus = true)
    {
        _browseWorkflow.SelectCategorySource(sourceId, GetComboDisplayText(CategorySourceCombo));
        var result = await _browseWorkflow.ActivateCategorySourceAsync();
        if (!result.Succeeded || result.Value is null
            || !_browseWorkflow.IsCurrent(result.Revision)) return;

        CategoryPlaylistsListBox.ItemsSource = null;
        CategorySongsList.ItemsSource = null;
        CategoryList.ItemsSource = result.Value;
        if (result.Value.Count > 0) CategoryList.SelectedIndex = 0;
        if (applyFocus) ApplyBrowseFocus(result.FocusTarget);
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selection = _browseWorkflow.SelectCategory(CategoryList.SelectedItem as PlaylistCategory);
        StatusText.Text = selection.Description;
    }

    private async Task LoadCategoryPlaylistsAsync(
        PlaylistCategory category,
        bool applyFocus = true)
    {
        _browseWorkflow.SelectCategory(category);
        var result = await _browseWorkflow.ActivateCategoryAsync();
        if (!result.Succeeded || result.Value is null
            || !_browseWorkflow.IsCurrent(result.Revision)) return;

        CategorySongsList.ItemsSource = null;
        CategoryPageInfo.Text = "第 1 页";
        CategoryPlaylistsListBox.ItemsSource = result.Value.Items;
        if (result.Value.Items.Count > 0) CategoryPlaylistsListBox.SelectedIndex = 0;
        if (applyFocus) ApplyBrowseFocus(result.FocusTarget);
    }

    private void CategoryPlaylistsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selection = _browseWorkflow.SelectPlaylist(CategoryPlaylistsListBox.SelectedItem as Playlist);
        StatusText.Text = selection.Description;
    }

    private async Task LoadCategoryPlaylistDetailAsync(
        Playlist playlist,
        int page,
        bool applyFocus = true)
    {
        _browseWorkflow.SelectPlaylist(playlist);
        var result = await _browseWorkflow.ActivatePlaylistAsync(page);
        if (!result.Succeeded || result.Value is null
            || !_browseWorkflow.IsCurrent(result.Revision)) return;

        var totalPages = EstimateTotalPages(result.Value.TotalItems, 20, page, result.Value.Items.Count);
        CategorySongsList.ItemsSource = AppendPageNav(result.Value.Items, page, totalPages);
        CategoryPageInfo.Text = $"第 {page}/{totalPages} 页";
        if (applyFocus) ApplyBrowseFocus(result.FocusTarget);
    }

    private void CategorySourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var selection = _browseWorkflow.SelectCategorySource(
            GetComboTag(CategorySourceCombo, "netease"),
            GetComboDisplayText(CategorySourceCombo));
        StatusText.Text = selection.Description;
        CategoryList.ItemsSource = null;
        CategoryPlaylistsListBox.ItemsSource = null;
        CategorySongsList.ItemsSource = null;
    }

    private void CategoryRefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadCategoriesAsync(GetComboTag(CategorySourceCombo, "netease"));
    }

    private async void CategorySourceCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await LoadCategoriesAsync(GetComboTag(CategorySourceCombo, "netease"));
        e.Handled = true;
    }

    private async void CategoryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CategoryList.SelectedItem is PlaylistCategory category)
        {
            await LoadCategoryPlaylistsAsync(category);
            e.Handled = true;
        }
    }

    private async void CategoryPlaylistsListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CategoryPlaylistsListBox.SelectedItem is Playlist playlist)
        {
            await LoadCategoryPlaylistDetailAsync(playlist, 1);
            e.Handled = true;
        }
    }

    private async void CategorySongsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CategorySongsList.SelectedItem is MusicInfo music)
            await HandleSongActivationAsync(CategorySongsList, music);
    }

    private async void CategorySongsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift && CategorySongsList.SelectedItem is MusicInfo lyricMusic)
        {
            ShowLyricsForSong(lyricMusic);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Enter || e.Key == Key.Space) && CategorySongsList.SelectedItem is MusicInfo music)
        {
            await HandleSongActivationAsync(CategorySongsList, music);
            e.Handled = true;
        }
    }

    private async void CategoryPrevPageBtn_Click(object sender, RoutedEventArgs e)
    {
        var current = _browseWorkflow.CurrentPlaylistPage;
        if (current is null || current.Page <= 1) return;
        await LoadCategoryPlaylistDetailAsync(current.Playlist, current.Page - 1, applyFocus: false);
    }

    private async void CategoryNextPageBtn_Click(object sender, RoutedEventArgs e)
    {
        var current = _browseWorkflow.CurrentPlaylistPage;
        if (current is null) return;
        var totalPages = EstimateTotalPages(current.TotalItems, 20, current.Page, current.Items.Count);
        if (current.Page >= totalPages) return;
        await LoadCategoryPlaylistDetailAsync(current.Playlist, current.Page + 1, applyFocus: false);
    }
    // ==================== 排行榜 ====================

    private async Task LoadRankingsAsync(string sourceId, bool applyFocus = true)
    {
        _browseWorkflow.SelectRankingSource(sourceId, GetComboDisplayText(RankingSourceCombo));
        var result = await _browseWorkflow.ActivateRankingSourceAsync();
        if (!result.Succeeded || result.Value is null
            || !_browseWorkflow.IsCurrent(result.Revision)) return;

        RankingSongsList.ItemsSource = null;
        RankingList.ItemsSource = result.Value;
        if (result.Value.Count > 0) RankingList.SelectedIndex = 0;
        if (applyFocus) ApplyBrowseFocus(result.FocusTarget);
    }

    private void RankingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selection = _browseWorkflow.SelectRanking(RankingList.SelectedItem as Ranking);
        StatusText.Text = selection.Description;
    }

    private async Task LoadRankingDetailAsync(
        Ranking ranking,
        int page,
        bool applyFocus = true)
    {
        _browseWorkflow.SelectRanking(ranking);
        var result = await _browseWorkflow.ActivateRankingAsync(page);
        if (!result.Succeeded || result.Value is null
            || !_browseWorkflow.IsCurrent(result.Revision)) return;

        var totalPages = EstimateTotalPages(result.Value.TotalItems, 20, page, result.Value.Items.Count);
        RankingSongsList.ItemsSource = AppendPageNav(result.Value.Items, page, totalPages);
        RankingPageInfo.Text = $"第 {page}/{totalPages} 页";
        if (applyFocus) ApplyBrowseFocus(result.FocusTarget);
    }

    private void RankingSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var selection = _browseWorkflow.SelectRankingSource(
            GetComboTag(RankingSourceCombo, "netease"),
            GetComboDisplayText(RankingSourceCombo));
        StatusText.Text = selection.Description;
        RankingList.ItemsSource = null;
        RankingSongsList.ItemsSource = null;
    }

    private void RefreshRankingButton_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadRankingsAsync(GetComboTag(RankingSourceCombo, "netease"));
    }

    private async void RankingSourceCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await LoadRankingsAsync(GetComboTag(RankingSourceCombo, "netease"));
        e.Handled = true;
    }

    private async void RankingList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && RankingList.SelectedItem is Ranking ranking)
        {
            await LoadRankingDetailAsync(ranking, 1);
            e.Handled = true;
        }
    }

    private async void RankingSongsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RankingSongsList.SelectedItem is MusicInfo music)
            await HandleSongActivationAsync(RankingSongsList, music);
    }

    private async void RankingSongsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift && RankingSongsList.SelectedItem is MusicInfo lyricMusic)
        {
            ShowLyricsForSong(lyricMusic);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Enter || e.Key == Key.Space) && RankingSongsList.SelectedItem is MusicInfo music)
        {
            await HandleSongActivationAsync(RankingSongsList, music);
            e.Handled = true;
        }
    }

    private async void RankingPrevPageBtn_Click(object sender, RoutedEventArgs e)
    {
        var current = _browseWorkflow.CurrentRankingPage;
        if (current is null || current.Page <= 1) return;
        await LoadRankingDetailAsync(current.Ranking, current.Page - 1, applyFocus: false);
    }

    private async void RankingNextPageBtn_Click(object sender, RoutedEventArgs e)
    {
        var current = _browseWorkflow.CurrentRankingPage;
        if (current is null) return;
        var totalPages = EstimateTotalPages(current.TotalItems, 20, current.Page, current.Items.Count);
        if (current.Page >= totalPages) return;
        await LoadRankingDetailAsync(current.Ranking, current.Page + 1, applyFocus: false);
    }

    // ==================== 播放歌曲 ====================

    private async Task PlayMusicAsync(MusicInfo music, string? quality = null, ListBox? sourceList = null)
    {
        if (music == null) return;
        var q = string.IsNullOrEmpty(quality) ? _playback.DefaultQuality : quality;
        Announce($"正在播放：{music.Title}，{music.Artist}");
        var queue = sourceList is null ? new List<MusicInfo> { music } : GetListSongs(sourceList);
        var result = await _playback.PlayAsync(new PlaybackRequest(
            queue,
            music,
            q,
            GetPlaybackOrigin(sourceList),
            ForceResolve: quality is not null));
        if (result.Status == PlaybackResultStatus.Playing && result.Music is not null)
            UpdatePlayingUI(result.Music);
    }

    private PlaybackQueueOrigin GetPlaybackOrigin(ListBox? list) => list switch
    {
        _ when ReferenceEquals(list, SearchResultsList) => PlaybackQueueOrigin.Search,
        _ when ReferenceEquals(list, CategorySongsList) => PlaybackQueueOrigin.Category,
        _ when ReferenceEquals(list, RankingSongsList) => PlaybackQueueOrigin.Ranking,
        _ => PlaybackQueueOrigin.Static
    };

    /// <summary>取列表中的歌曲项（排除“上一页/下一页”伪条目）</summary>
    private static List<MusicInfo> GetListSongs(ListBox? list)
    {
        if (list == null) return new List<MusicInfo>();
        return list.Items.Cast<object>()
            .Where(o => o is MusicInfo m && m.Id is not PagePrevId and not PageNextId and not AlbumBackId and not SearchMoreId)
            .Cast<MusicInfo>()
            .ToList();
    }

    // ==================== 自动翻页续播 ====================

    /// <summary>当前播放队列已播完（自动连播/手动下一首到列表末尾）：对分页列表自动加载下一页并续播</summary>
    private async void PlayerService_QueueCompleted(object? sender, PlaybackQueueCompletedEventArgs e)
    {
        try
        {
            var continuation = await _browseWorkflow.ContinuePlaybackAsync(e.Origin);
            if (continuation is null) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (continuation.Search is not null)
                    RenderSearchResults(null, continuation.Search);
                if (continuation.Playlist is not null)
                {
                    var page = continuation.Playlist;
                    var totalPages = EstimateTotalPages(page.TotalItems, 20, page.Page, page.Items.Count);
                    CategorySongsList.ItemsSource = AppendPageNav(page.Items, page.Page, totalPages);
                    CategoryPageInfo.Text = $"第 {page.Page}/{totalPages} 页";
                }
                if (continuation.Ranking is not null)
                {
                    var page = continuation.Ranking;
                    var totalPages = EstimateTotalPages(page.TotalItems, 20, page.Page, page.Items.Count);
                    RankingSongsList.ItemsSource = AppendPageNav(page.Items, page.Page, totalPages);
                    RankingPageInfo.Text = $"第 {page.Page}/{totalPages} 页";
                }
            });
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"自动翻页失败: {exception.GetType().Name}");
        }
    }

    private void UpdatePlayingUI(MusicInfo music)
    {
        if (music == null) return;
        CurrentTitle.Text = music.Title;
        CurrentArtist.Text = music.Artist;
        CurrentAlbum.Text = music.Album;
        UpdatePlayPauseMenuHeader("暂停");
        // 队列由播放协调模块拥有；界面只呈现当前歌曲。
    }


    /// <summary>音源 id → 显示名称（歌词播报用）</summary>
    private static string GetSourceDisplayName(string sourceId) => sourceId.ToLowerInvariant() switch
    {
        "netease" => "网易云音乐",
        "qq" => "QQ音乐",
        "kg" => "酷狗音乐",
        "kw" => "酷我音乐",
        "migu" => "咪咕音乐",
        "script" => "脚本源",
        "local" => "本地",
        _ => sourceId
    };

    /// <summary>音质码 → 中文显示名（仅展示层翻译；接口请求/下载仍用原始音质码）</summary>
    private static string TranslateQuality(string q)
    {
        if (string.IsNullOrEmpty(q)) return q;
        return q.ToLowerInvariant() switch
        {
            "128k" => "128k 标准",
            "192k" => "192k",
            "320k" => "320k 高品质",
            "flac" => "FLAC 无损",
            "flac24bit" => "FLAC 24bit 高解析",
            "hires" => "Hi-Res 高解析",
            "atmos" => "杜比全景声",
            "atmos_plus" => "杜比全景声增强",
            "master" => "母带级",
            "jyeffect" => "至臻音效",
            "sky" => "至臻全景声",
            "dolby" => "杜比音效",
            "jymaster" => "至臻母带",
            "standard" => "标准",
            "higher" => "较高",
            "exhigh" => "极高",
            "lossless" => "无损",
            _ => q
        };
    }

    // ==================== 独立歌词窗口 ====================

    private LyricsWindow EnsureLyricsWindow()
    {
        if (_lyricsWindow == null)
            _lyricsWindow = _windowFactory.CreateLyrics(TryHandleLyricsWindowHotkey);
        return _lyricsWindow;
    }

    /// <summary>打开（或显示）独立歌词窗口：推送当前歌词、滚动到当前行并聚焦列表（焦点修复点）</summary>
    private void OpenLyricsWindow()
    {
        var w = EnsureLyricsWindow();
        if (!w.IsVisible)
            w.Show();
        w.Activate();
        w.FocusLyricsList();
        _lyrics.AnnounceCurrentLine();
    }

    /// <summary>为指定歌曲打开歌词窗口（右键“查看歌词”/Shift+回车共用）</summary>
    private async void ShowLyricsForSong(MusicInfo music)
    {
        if (!await _lyrics.LoadAsync(music)) return;
        Announce($"正在查看歌词：{music.Title}");
        OpenLyricsWindow();
    }

    /// <summary>歌词窗口内局部热键处理：匹配局部快捷键并执行（F1~F12、Shift+F7/F8 等），返回是否已处理</summary>
    internal bool TryHandleLyricsWindowHotkey(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
        var localHotkey = _settingsWorkflow.MatchLocalHotkey(effectiveKey, mods);
        if (localHotkey == null) return false;
        if (!IsNumpadOriginHotkey(localHotkey.Key))
            ExecuteHotkeyAction(localHotkey.ActionId);
        return true;
    }
    // ==================== 在线收藏 ====================

    private async Task LoadOnlinePlaylistsAsync()
    {
        var result = await _savedPlaylists.LoadAsync();
        RenderSavedPlaylists(result.Snapshot);
    }

    private async Task<BrowseFocusTarget> ActivateOnlinePlaylistAsync(Playlist playlist)
    {
        _savedPlaylists.Select(playlist);
        var result = await _savedPlaylists.ActivateAsync();
        RenderSavedPlaylists(result.Snapshot);
        if (!string.IsNullOrWhiteSpace(result.Message))
            Announce(result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
        return result.FocusTarget;
    }

    private void RenderSavedPlaylists(SavedPlaylistSnapshot snapshot)
    {
        OnlinePlaylistList.SelectionChanged -= OnlinePlaylistList_SelectionChanged;
        try
        {
            OnlinePlaylistList.ItemsSource = snapshot.Playlists;
            OnlinePlaylistList.SelectedItem = snapshot.Playlists.FirstOrDefault(item =>
                item.Id == snapshot.Selected?.Id);
        }
        finally
        {
            OnlinePlaylistList.SelectionChanged += OnlinePlaylistList_SelectionChanged;
        }
        FavoriteSongsList.ItemsSource = snapshot.Songs;
        FavoriteCountText.Text = $"共 {snapshot.Songs.Count} 首";
    }

    private void OnlinePlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selection = _savedPlaylists.Select(OnlinePlaylistList.SelectedItem as Playlist);
        StatusText.Text = selection.Description;
        FavoriteSongsList.ItemsSource = null;
        FavoriteCountText.Text = "按 Enter 加载歌曲";
    }

    private async void OnlinePlaylistList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OnlinePlaylistList.SelectedItem is Playlist playlist)
        {
            ApplyBrowseFocus(await ActivateOnlinePlaylistAsync(playlist));
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && OnlinePlaylistList.SelectedItem is Playlist deletedPlaylist)
        {
            await DeleteOnlinePlaylistAsync(deletedPlaylist);
            e.Handled = true;
        }
    }

    private async Task DeleteOnlinePlaylistAsync(Playlist playlist)
    {
        if (playlist.Type == PlaylistType.System)
        {
            Announce("默认收藏歌单不能删除");
            return;
        }
        var confirmation = MessageBox.Show(
            $"确定删除在线歌单“{playlist.Name}”吗？",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;
        _savedPlaylists.Select(playlist);
        ApplySavedPlaylistResult(await _savedPlaylists.DeleteSelectedAsync());
    }

    private async void NewOnlinePlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("新建在线歌单", "歌单名称：", "");
        if (dialog.ShowDialog() != true) return;
        ApplySavedPlaylistResult(await _savedPlaylists.CreateAsync(dialog.Value ?? string.Empty));
    }

    private async void RenameOnlinePlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = _savedPlaylists.Current.Selected;
        if (selected is null)
        {
            Announce("请先选择在线歌单");
            return;
        }
        var dialog = new TextInputDialog("重命名在线歌单", "歌单名称：", selected.Name);
        if (dialog.ShowDialog() != true) return;
        ApplySavedPlaylistResult(await _savedPlaylists.RenameSelectedAsync(dialog.Value ?? string.Empty));
    }

    private void DeleteOnlinePlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = _savedPlaylists.Current.Selected;
        if (selected is null) Announce("请先选择要删除的在线歌单");
        else _ = DeleteOnlinePlaylistAsync(selected);
    }

    private async Task AddFavoriteSongsAsync(IEnumerable<MusicInfo> songs) =>
        ApplySavedPlaylistResult(await _savedPlaylists.AddSongsAsync(songs));

    private async Task RemoveFavoriteSongAsync(MusicInfo music) =>
        ApplySavedPlaylistResult(await _savedPlaylists.RemoveSongAsync(music));

    private async void FavoriteSongsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoriteSongsList.SelectedItem is MusicInfo music)
            await PlayMusicAsync(music, null, FavoriteSongsList);
    }

    private async void FavoriteSongsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift && FavoriteSongsList.SelectedItem is MusicInfo lyricMusic)
        {
            ShowLyricsForSong(lyricMusic);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Enter || e.Key == Key.Space) && FavoriteSongsList.SelectedItem is MusicInfo music)
        {
            await PlayMusicAsync(music, null, FavoriteSongsList);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && FavoriteSongsList.SelectedItem is MusicInfo deleted)
        {
            await RemoveFavoriteSongAsync(deleted);
            e.Handled = true;
        }
    }

    private async void FavRefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        await LoadOnlinePlaylistsAsync();
        Announce("在线收藏已刷新");
    }

    private async void FavRemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteSongsList.SelectedItem is MusicInfo music)
            await RemoveFavoriteSongAsync(music);
        else
            Announce("请先选择要从歌单移除的歌曲");
    }

    private async void FavClearBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = _savedPlaylists.Current.Selected;
        if (selected is null) return;
        var count = FavoriteSongsList.Items.Count;
        if (count == 0)
        {
            Announce("当前歌单为空");
            return;
        }
        var confirmation = MessageBox.Show(
            $"确定清空在线歌单“{selected.Name}”（共 {count} 首）吗？",
            "确认清空",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;
        ApplySavedPlaylistResult(await _savedPlaylists.ClearAsync());
    }

    private async Task CollectRemotePlaylistAsync(
        string name,
        string source,
        string remoteId,
        PlaylistType type) =>
        ApplySavedPlaylistResult(await _savedPlaylists.CollectRemoteAsync(
            new(name, source, remoteId, type)));

    private void ApplySavedPlaylistResult(SavedPlaylistResult result)
    {
        RenderSavedPlaylists(result.Snapshot);
        if (!string.IsNullOrWhiteSpace(result.Message))
            Announce(result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    // ==================== 歌曲列表右键菜单 ====================

    /// <summary>为 5 个歌曲列表各自构建独立的右键菜单</summary>
    private void AssignSongContextMenus()
    {
        SearchResultsList.ContextMenu = BuildSongContextMenu(SearchResultsList);
        CategorySongsList.ContextMenu = BuildSongContextMenu(CategorySongsList);
        RankingSongsList.ContextMenu = BuildSongContextMenu(RankingSongsList);
        FavoriteSongsList.ContextMenu = BuildSongContextMenu(FavoriteSongsList);
    }

    internal ContextMenu BuildSongContextMenu(ListBox owner)
    {
        var menu = new ContextMenu { Tag = owner };
        menu.Opened += SongContextMenu_Opened;
        menu.PreviewKeyUp += ContextMenu_PreviewKeyUp;

        // 查看歌曲详情（所有歌曲列表通用）
        var details = new MenuItem { Header = "查看歌曲详情" };
        System.Windows.Automation.AutomationProperties.SetName(details, "查看歌曲详情");
        details.Click += SongMenu_ViewDetails_Click;
        menu.Items.Add(details);
        var play = new MenuItem { Header = "播放" };
        System.Windows.Automation.AutomationProperties.SetName(play, "播放");
        play.Click += SongMenu_Play_Click;
        menu.Items.Add(play);

        var quality = new MenuItem { Header = "播放音质" };
        System.Windows.Automation.AutomationProperties.SetName(quality, "播放音质");
        quality.Items.Add(new MenuItem { Header = "128k", Tag = "128k" });
        menu.Items.Add(quality);

        var lyrics = new MenuItem { Header = "查看歌词" };
        System.Windows.Automation.AutomationProperties.SetName(lyrics, "查看歌词");
        lyrics.Click += SongMenu_ViewLyrics_Click;
        menu.Items.Add(lyrics);

        var download = new MenuItem { Header = "下载" };
        System.Windows.Automation.AutomationProperties.SetName(download, "下载");
        download.Items.Add(new MenuItem { Header = "128k", Tag = "128k" });
        menu.Items.Add(download);

        // 从当前歌曲开始顺序播放整个列表（搜索/歌单/排行榜/播放列表/收藏均可用）
        var playAll = new MenuItem { Header = "从当前歌曲播放整个列表" };
        System.Windows.Automation.AutomationProperties.SetName(playAll, "从当前歌曲播放整个列表");
        playAll.Click += SongMenu_PlayAllFromCurrent_Click;
        menu.Items.Add(playAll);

        // 批量下载：下载整个列表（全部在线歌曲）；下载整张专辑（专辑详情/专辑搜索结果时显示）
        var downloadAll = new MenuItem { Header = "下载整个列表", Tag = BatchDownloadListTag };
        System.Windows.Automation.AutomationProperties.SetName(downloadAll, "下载整个列表");
        downloadAll.Click += SongMenu_DownloadList_Click;
        menu.Items.Add(downloadAll);

        var downloadAlbum = new MenuItem { Header = "下载整张专辑", Tag = BatchDownloadAlbumTag, Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetName(downloadAlbum, "下载整张专辑");
        downloadAlbum.Click += SongMenu_DownloadAlbum_Click;
        menu.Items.Add(downloadAlbum);

        // 网络歌曲来源列表（搜索/歌单/排行榜）可添加到本地收藏
        bool isRemoteList = ReferenceEquals(owner, SearchResultsList)
            || ReferenceEquals(owner, CategorySongsList)
            || ReferenceEquals(owner, RankingSongsList)
            || _externalSongLists.Contains(owner);
        if (isRemoteList)
        {
            var addFav = new MenuItem { Header = "添加到收藏" };
            System.Windows.Automation.AutomationProperties.SetName(addFav, "添加到收藏");
            addFav.Click += SongMenu_AddFavorite_Click;
            menu.Items.Add(addFav);

            var addList = new MenuItem { Header = "添加整个列表到收藏" };
            System.Windows.Automation.AutomationProperties.SetName(addList, "添加整个列表到收藏");
            addList.Click += SongMenu_AddListToFavorites_Click;
            menu.Items.Add(addList);

            // 分类歌单/排行榜：可把整个远程歌单收藏到在线收藏（仅存元数据，打开时按需加载）
            if (ReferenceEquals(owner, CategorySongsList) || ReferenceEquals(owner, RankingSongsList))
            {
                var collectList = new MenuItem { Header = "收藏整个歌单到在线收藏" };
                System.Windows.Automation.AutomationProperties.SetName(collectList, "收藏整个歌单到在线收藏");
                collectList.Click += SongMenu_CollectRemoteList_Click;
                menu.Items.Add(collectList);
            }
        }

        // 收藏列表专用：从收藏移除
        if (ReferenceEquals(owner, FavoriteSongsList))
        {
            var removeFav = new MenuItem { Header = "从收藏移除" };
            System.Windows.Automation.AutomationProperties.SetName(removeFav, "从收藏移除");
            removeFav.Click += SongMenu_RemoveFavorite_Click;
            menu.Items.Add(removeFav);
        }

        // 用第三方播放器播放（所有歌曲列表通用；路径在“设置→第三方播放器”中配置）
        var playExternal = new MenuItem { Header = "用第三方播放器播放" };
        System.Windows.Automation.AutomationProperties.SetName(playExternal, "用第三方播放器播放");
        playExternal.Click += SongMenu_PlayWithExternal_Click;
        menu.Items.Add(playExternal);

        return menu;
    }

    /// <summary>右键按下时选中被点中的歌曲（保证菜单操作作用于正确对象）</summary>
    internal void SongList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is MusicInfo m)
            list.SelectedItem = m;
    }

    /// <summary>打开菜单前过滤：分页导航条目不弹菜单；无歌曲时拦截</summary>
    internal void SongList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (list.SelectedItem is MusicInfo m)
        {
            if (m.Id is PagePrevId or PageNextId)
            {
                e.Handled = true;
                return;
            }
        }
        else
        {
            e.Handled = true;
        }
    }

    /// <summary>菜单打开时按当前歌曲音源动态填充音质子菜单</summary>
    private async void SongContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = sender as ContextMenu;
        if (menu == null || menu.PlacementTarget is not ListBox list || list.SelectedItem is not MusicInfo music)
            return;

        bool isLocal = string.Equals(music.Source, "Local", StringComparison.OrdinalIgnoreCase);
        var playQualityItem = menu.Items.Count > 2 ? menu.Items[2] as MenuItem : null;
        var downloadItem = menu.Items.Count > 4 ? menu.Items[4] as MenuItem : null;
        if (playQualityItem == null || downloadItem == null) return;

        // “下载整张专辑”仅在专辑搜索结果或专辑详情列表时显示
        var downloadAlbumItem = menu.Items.OfType<MenuItem>().FirstOrDefault(x => string.Equals(x.Tag as string, BatchDownloadAlbumTag));
        if (downloadAlbumItem != null)
        {
            bool inAlbumDetail = ReferenceEquals(list, SearchResultsList) && _searchShowingAlbumDetail;
            downloadAlbumItem.Visibility = (inAlbumDetail || music.IsAlbum) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (isLocal)
        {
            playQualityItem.Visibility = Visibility.Collapsed;
            downloadItem.Items.Clear();
            var copy = new MenuItem { Header = "复制原文件", Tag = "local" };
            System.Windows.Automation.AutomationProperties.SetName(copy, "复制原文件");
            copy.Click += SongMenu_Download_Click;
            downloadItem.Items.Add(copy);
            return;
        }

        playQualityItem.Visibility = Visibility.Visible;
        try
        {
            var qualities = await _musicSourceService!.GetQualitiesForSourceAsync(music.Source);
            RebuildQualityMenu(playQualityItem, qualities, SongMenu_PlayQuality_Click, _playback.DefaultQuality);
            RebuildQualityMenu(downloadItem, qualities, SongMenu_Download_Click, GetComboTag(DownloadQualityCombo, "128k"));
        }
        catch
        {
            RebuildQualityMenu(playQualityItem, new List<string> { "128k" }, SongMenu_PlayQuality_Click, _playback.DefaultQuality);
            RebuildQualityMenu(downloadItem, new List<string> { "128k" }, SongMenu_Download_Click, GetComboTag(DownloadQualityCombo, "128k"));
        }
    }

    private static void RebuildQualityMenu(MenuItem parent, IEnumerable<string> qualities, RoutedEventHandler handler, string currentQuality)
    {
        parent.Items.Clear();
        foreach (var q in qualities)
        {
            var display = TranslateQuality(q);
            var item = new MenuItem
            {
                Header = display,
                Tag = q,
                IsCheckable = true,
                IsChecked = string.Equals(q, currentQuality, StringComparison.OrdinalIgnoreCase)
            };
            System.Windows.Automation.AutomationProperties.SetName(item, $"音质 {display}");
            item.Click += handler;
            parent.Items.Add(item);
        }
    }

    private MusicInfo? GetContextMusic(object? sender)
    {
        var menu = FindContextMenu(sender as DependencyObject);
        if (menu?.Tag is ListBox list && list.SelectedItem is MusicInfo m)
            return m;
        return null;
    }

    /// <summary>从事件源反查触发菜单所属的歌曲列表（菜单 Tag 中保存）</summary>
    private static ListBox? GetContextList(object? sender)
    {
        var menu = FindContextMenu(sender as DependencyObject);
        return menu?.Tag as ListBox;
    }

    private static ContextMenu? FindContextMenu(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is ContextMenu cm) return cm;
            node = LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>查看歌曲详情：以列表逐条展示来源、品质、歌手、专辑等；正在播放的歌曲额外显示当前播放音质</summary>
    private async void SongMenu_ViewDetails_Click(object sender, RoutedEventArgs e)
    {
        var music = GetContextMusic(sender);
        if (music == null) return;
        try
        {
            var lines = new List<string>();
            lines.Add($"歌名：{music.Title}");
            lines.Add($"歌手：{music.Artist}");
            lines.Add($"专辑：{music.Album}");
            lines.Add($"来源：{GetSourceDisplayName(music.Source)}");
            bool isLocal = string.Equals(music.Source, "Local", StringComparison.OrdinalIgnoreCase);
            if (isLocal)
            {
                lines.Add("品质：本地原文件");
                if (!string.IsNullOrEmpty(music.PlayUrl))
                    lines.Add($"本地文件：{music.PlayUrl}");
            }
            else
            {
                // 正在播放/加载的歌曲：显示当前播放真实音质（按 Source+Id 判定为当前播放器歌曲）。
                // 优先读取播放器流的实际音质（编码格式+平均码率），读取不到时回退到请求音质。
                bool isCurrent = _playback.CurrentMusic != null
                    && string.Equals(music.Source, _playback.CurrentMusic.Source, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(music.Id, _playback.CurrentMusic.Id, StringComparison.OrdinalIgnoreCase);
                if (isCurrent)
                {
                    var actual = _playerService?.ActualQuality;
                    var shown = string.IsNullOrEmpty(actual) ? _playback.DefaultQuality : actual;
                    if (!string.IsNullOrEmpty(shown))
                        lines.Add($"当前播放音质：{TranslateQuality(shown)}");
                }
                try
                {
                    var qs = await _musicSourceService!.GetQualitiesForSourceAsync(music.Source);
                    lines.Add($"来源支持品质：{(qs.Count > 0 ? string.Join("、", qs.Select(TranslateQuality)) : "未知")}");
                }
                catch (Exception ex)
                {
                    Log.Warning("Source quality lookup failed: ErrorType={ErrorType}", ex.GetType().Name);
                    lines.Add("来源支持品质：未知");
                }
            }
            if (!string.IsNullOrEmpty(music.Duration))
                lines.Add($"时长：{music.Duration}");
            if (!string.IsNullOrEmpty(music.Id))
                lines.Add($"歌曲ID：{music.Id}");
            if (!string.IsNullOrEmpty(music.Songmid))
                lines.Add($"SongMID：{music.Songmid}");

            var dialog = new SongDetailDialog(string.IsNullOrEmpty(music.Title) ? "未知歌曲" : music.Title, lines)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Announce("查看歌曲详情失败：" + ex.Message);
            System.Diagnostics.Debug.WriteLine($"查看歌曲详情失败: {ex.Message}");
        }
    }

    private async void SongMenu_Play_Click(object sender, RoutedEventArgs e)
    {
        var music = GetContextMusic(sender);
        if (music != null) await PlayMusicAsync(music, null, GetContextList(sender));
    }

    private async void SongMenu_PlayQuality_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string q) return;
        var music = GetContextMusic(sender);
        if (music == null) return;
        await PlayMusicAsync(music, q, GetContextList(sender));
        _playback.DefaultQuality = q;
        SelectComboByTag(DefaultQualityCombo, q);
        Announce($"已将默认播放音质设为 {TranslateQuality(q)}");
    }

    /// <summary>从当前歌曲开始顺序播放整个列表</summary>
    private async void SongMenu_PlayAllFromCurrent_Click(object sender, RoutedEventArgs e)
    {
        var music = GetContextMusic(sender);
        var list = GetContextList(sender);
        if (music == null) return;
        var count = GetListSongs(list).Count;
        await PlayMusicAsync(music, null, list);
        Announce(count > 1 ? $"已从当前歌曲开始顺序播放整个列表，共 {count} 首" : "已开始播放当前歌曲");
    }

    /// <summary>将当前歌曲添加到本地收藏</summary>
    private async void SongMenu_AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        var music = GetContextMusic(sender);
        if (music == null) return;
        await AddFavoriteSongsAsync(new[] { music });
    }

    /// <summary>将整个歌曲列表添加到本地收藏</summary>
    private async void SongMenu_AddListToFavorites_Click(object sender, RoutedEventArgs e)
    {
        var list = GetContextList(sender);
        if (list == null) return;
        var songs = GetListSongs(list);
        if (songs.Count == 0) return;
        await AddFavoriteSongsAsync(songs);
    }

    /// <summary>把分类歌单/排行榜整单收藏到在线收藏（仅存元数据，打开时按需联网加载）</summary>
    private async void SongMenu_CollectRemoteList_Click(object sender, RoutedEventArgs e)
    {
        var list = GetContextList(sender);
        if (list == null) return;
        if (ReferenceEquals(list, CategorySongsList))
        {
            var selected = _browseWorkflow.CurrentPlaylistPage?.Playlist;
            if (selected == null) { Announce("暂无可收藏的歌单"); return; }
            var source = GetComboTag(CategorySourceCombo, "netease");
            await CollectRemotePlaylistAsync(selected.Name, source, selected.Id, PlaylistType.Cloud);
        }
        else if (ReferenceEquals(list, RankingSongsList))
        {
            var selected = _browseWorkflow.CurrentRankingPage?.Ranking;
            if (selected == null) { Announce("暂无可收藏的榜单"); return; }
            var remoteId = $"{selected.Source}_{selected.Id}";
            await CollectRemotePlaylistAsync(selected.Name, selected.Source, remoteId, PlaylistType.Recommend);
        }
    }

    /// <summary>歌单/排行榜列表右键菜单打开过滤：有选中项才弹出</summary>
    private void PlaylistList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem == null)
            e.Handled = true;
    }

    /// <summary>分类歌单列表右键：收藏整个歌单到在线收藏</summary>
    private async void CategoryPlaylist_Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryPlaylistsListBox.SelectedItem is not Playlist p) return;
        var source = GetComboTag(CategorySourceCombo, "netease");
        await CollectRemotePlaylistAsync(p.Name, source, p.Id, PlaylistType.Cloud);
    }

    /// <summary>排行榜列表右键：收藏整个榜单到在线收藏</summary>
    private async void Ranking_Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (RankingList.SelectedItem is not Ranking r) return;
        await CollectRemotePlaylistAsync(r.Name, r.Source, $"{r.Source}_{r.Id}", PlaylistType.Recommend);
    }

    /// <summary>将当前收藏歌曲从收藏中移除</summary>
    private async void SongMenu_RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        var music = GetContextMusic(sender);
        if (music == null) return;
        await RemoveFavoriteSongAsync(music);
    }

    private void SongMenu_ViewLyrics_Click(object sender, RoutedEventArgs e)
    {
        var music = GetContextMusic(sender);
        if (music == null) return;
        ShowLyricsForSong(music);
    }

    private async void SongMenu_Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string q) return;
        var music = GetContextMusic(sender);
        if (music == null) return;
        var quality = string.Equals(q, "local", StringComparison.OrdinalIgnoreCase) ? "原文件" : q;
        await _downloads.DownloadAsync(new DownloadRequest(
            music,
            quality,
            GetEffectiveDownloadDir()));
    }

    /// <summary>批量下载当前列表的全部在线歌曲（右键“下载整个列表”）</summary>
    private async void SongMenu_DownloadList_Click(object sender, RoutedEventArgs e)
    {
        var list = GetContextList(sender);
        if (list == null) return;
        var songs = GetDownloadableSongs(list);
        await DownloadSongsAsync(songs, "当前列表");
    }

    /// <summary>批量下载整张专辑（右键“下载整张专辑”；专辑详情视图/专辑搜索结果均可）</summary>
    private async void SongMenu_DownloadAlbum_Click(object sender, RoutedEventArgs e)
    {
        var list = GetContextList(sender);
        if (list == null) return;
        var music = list.SelectedItem as MusicInfo;
        var inAlbumDetail = ReferenceEquals(list, SearchResultsList) && _searchShowingAlbumDetail;
        List<MusicInfo> songs;
        if (music?.IsAlbum == true && !inAlbumDetail)
            songs = await FetchAlbumSongsAsync(music);
        else
            songs = GetDownloadableSongs(list);
        // 专辑下载：在下载目录内按专辑名新建文件夹（专辑详情视图时 music 为单曲，用其 Album 字段）
        var albumName = music?.IsAlbum == true ? music.Title : (music?.Album ?? music?.Title);
        if (string.IsNullOrWhiteSpace(albumName)) albumName = "整张专辑";
        await DownloadSongsAsync(songs, music?.Title ?? "整张专辑", albumName);
    }

    /// <summary>按页拉取专辑的全部歌曲（与展开专辑共用分页逻辑）</summary>
    private async Task<List<MusicInfo>> FetchAlbumSongsAsync(MusicInfo album)
    {
        if (album == null) return [];
        var result = await _browseWorkflow.ActivateAlbumAsync(album);
        return result.Succeeded && result.Value is not null
            && _browseWorkflow.IsCurrent(result.Revision)
            ? result.Value.ToList()
            : [];
    }

    /// <summary>获取列表中可批量下载的在线歌曲（排除分页/返回/加载更多等伪条目与本地歌曲）</summary>
    private static List<MusicInfo> GetDownloadableSongs(ListBox list)
    {
        var result = new List<MusicInfo>();
        if (list?.ItemsSource is not System.Collections.IEnumerable items) return result;
        foreach (var it in items)
        {
            if (it is not MusicInfo m) continue;
            if (m.Id is PagePrevId or PageNextId or AlbumBackId or SearchMoreId) continue;
            if (string.Equals(m.Source, "Local", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(m);
        }
        return result;
    }

    /// <summary>批量下载一批在线歌曲（使用默认下载音质；本地歌曲自动跳过）</summary>
    private async Task DownloadSongsAsync(IReadOnlyList<MusicInfo> songs, string label, string? subFolder = null)
    {
        var online = songs.Where(s => !string.Equals(s.Source, "Local", StringComparison.OrdinalIgnoreCase)).ToList();
        await _downloads.DownloadListAsync(new DownloadListRequest(
            online,
            label,
            GetComboTag(DownloadQualityCombo, "128k"),
            GetEffectiveDownloadDir(),
            subFolder));
    }

    /// <summary>用第三方播放器播放当前歌曲：本地歌曲直接传本地路径；在线歌曲先下载到临时文件再交外部播放器（下载失败回退直接传 URL），确保各类播放器都能正常播放</summary>
    private async void SongMenu_PlayWithExternal_Click(object sender, RoutedEventArgs e)
    {
        var music = GetContextMusic(sender);
        if (music == null) return;
        var result = await _downloads.PlayExternallyAsync(new(
            ThirdPartyPlayerPathBox?.Text ?? string.Empty,
            music,
            _playback.DefaultQuality));
        Announce(result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private bool TryOpenSongContextMenu()
    {
        var fe = Keyboard.FocusedElement as DependencyObject;
        var list = FindAncestor<ListBox>(fe);
        if (list == null) return false;
        if (list.SelectedItem is not MusicInfo m) return false;
        if (m.Id is PagePrevId or PageNextId) return false;
        var menu = list.ContextMenu;
        if (menu == null) return false;
        menu.PlacementTarget = list;
        menu.IsOpen = true;
        return true;
    }

    /// <summary>键盘（应用键/Shift+F10）在歌单/排行榜列表打开右键菜单</summary>
    private bool TryOpenPlaylistContextMenu()
    {
        var fe = Keyboard.FocusedElement as DependencyObject;
        var list = FindAncestor<ListBox>(fe);
        if (list == null) return false;
        if (list != CategoryPlaylistsListBox && list != RankingList) return false;
        if (list.SelectedItem == null) return false;
        var menu = list.ContextMenu;
        if (menu == null) return false;
        menu.PlacementTarget = list;
        menu.IsOpen = true;
        return true;
    }

    /// <summary>给外部窗口的歌曲列表挂接播放/右键菜单/收藏等能力（用于“导入个人歌单”窗口的歌曲预览列表）</summary>
    private void AttachSongListCapabilities(ListBox list)
    {
        if (list == null) return;
        _externalSongLists.Add(list);
        list.ContextMenu = BuildSongContextMenu(list);
        list.PreviewMouseRightButtonDown += SongList_PreviewMouseRightButtonDown;
        list.ContextMenuOpening += SongList_ContextMenuOpening;
        list.KeyDown -= ExternalSongList_KeyDown;
        list.KeyDown += ExternalSongList_KeyDown;
    }

    /// <summary>解除外部歌曲列表挂接（窗口关闭时调用，避免播放队列引用已关闭窗口的列表）</summary>
    /// <summary>外部歌曲列表回车/空格直接播放</summary>
    private async void ExternalSongList_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift && list.SelectedItem is MusicInfo lyricMusic)
        {
            ShowLyricsForSong(lyricMusic);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Enter || e.Key == Key.Space) && list.SelectedItem is MusicInfo music)
        {
            await PlayMusicAsync(music, null, list);
            e.Handled = true;
        }
    }

    /// <summary>供外部窗口（如导入个人歌单窗口）键盘打开指定歌曲列表的右键菜单</summary>
    // ==================== 下载管理 ====================

    private void DownloadCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadTaskList.SelectedItem is DownloadTask t)
        {
            _downloads.Cancel(t);
        }
        else Announce("请先选择要取消的下载任务");
    }

    private async void DownloadRetryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadTaskList.SelectedItem is DownloadTask t &&
            t.Status is DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled)
        {
            await _downloads.RetryAsync(t, GetEffectiveDownloadDir());
        }
        else Announce("请选择失败的下载任务进行重试");
    }

    private void DownloadOpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = _downloads.OpenDownloadDirectory(DownloadPathBox?.Text);
        Announce(result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private void DownloadClearDoneBtn_Click(object sender, RoutedEventArgs e)
    {
        _downloads.ClearCompleted();
        Announce("已清空已完成的任务");
    }

    // ==================== 音源管理 ====================

    private void RefreshSourceList()
    {
        if (_musicSourceService == null || SourceList == null) return;
        SourceList.ItemsSource = null;
        SourceList.ItemsSource = _musicSourceService.GetAllSources();
    }

    private async void ImportLocalSourceBtn_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "音源脚本|*.js|所有文件|*.*",
            Title = "导入本地音源脚本",
            Multiselect = false
        };
        if (openFileDialog.ShowDialog() != true) return;
        try
        {
            Announce("正在导入本地音源...");
            var src = await _musicSourceService!.ImportFromFileAsync(openFileDialog.FileName);
            if (src == null) { Announce("导入失败：无法读取或解析脚本"); return; }
            RefreshSourceList();
            await ConfirmAndActivateStagedSourceAsync(src);
        }
        catch (Exception ex) { Announce($"导入失败：{ex.Message}"); }
    }

    private async void ImportNetworkSourceBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("从网络导入音源", "音源URL：", "", "音源名称（可留空）：", "");
        if (dialog.ShowDialog() != true) return;
        var url = dialog.Value?.Trim();
        if (string.IsNullOrEmpty(url)) { Announce("导入已取消"); return; }
        try
        {
            Announce("正在从网络导入音源...");
            var src = await _musicSourceService!.ImportFromUrlAsync(url);
            if (src == null) { Announce("导入失败：无法下载或解析脚本"); return; }
            if (!string.IsNullOrWhiteSpace(dialog.Value2)) src.Name = dialog.Value2.Trim();
            RefreshSourceList();
            await ConfirmAndActivateStagedSourceAsync(src);
        }
        catch (Exception ex) { Announce($"导入失败：{ex.Message}"); }
    }

    private async Task ConfirmAndActivateStagedSourceAsync(MusicSource source)
    {
        var confirmation = MessageBox.Show(
            $"音源已暂存，尚未执行。\n\n名称：{source.Name}\n版本：{source.Version}\n来源：{source.ScriptUrl}\nSHA-256：{source.ContentSha256}\n权限：可通过音源运行时访问网络\n变化：新增独立音源，不替换当前可信版本\n\n是否验证并启用此音源？",
            "启用暂存音源",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            await _musicSourceService!.UpdateSourceAsync(source);
            Announce($"已暂存但未启用：{source.Name}");
            return;
        }

        Announce($"正在验证并启用音源：{source.Name}");
        var result = await _musicSourceService!.EnableSourceAsync(source);
        RefreshSourceList();
        Announce(result.Succeeded ? $"已启用音源：{source.Name}" : result.Message);
    }

    private void RefreshSourcesBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshSourceList();
        Announce("音源列表已刷新");
    }

    private void SourceList_KeyDown(object sender, KeyEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource src) return;
        if (e.Key == Key.Enter)
        {
            Announce($"{src.Name}，版本{src.Version}，类型{src.TypeText}，{(src.IsDefault ? "默认音源，" : "")}{(src.IsEnabled ? "已启用" : "已禁用")}");
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            SourceMenu_Delete_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    /// <summary>键盘（应用键/Shift+F10）在音源列表打开右键菜单</summary>
    private bool TryOpenSourceContextMenu()
    {
        var fe = Keyboard.FocusedElement as DependencyObject;
        var list = FindAncestor<ListBox>(fe);
        if (list == null || !ReferenceEquals(list, SourceList)) return false;
        if (SourceList.SelectedItem is not MusicSource) return false;
        if (_sourceContextMenu == null) return false;
        _sourceContextMenu.PlacementTarget = SourceList;
        _sourceContextMenu.IsOpen = true;
        return true;
    }

    private void SourceMenu_SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource src) return;
        if (src.TrustState == ScriptTrustState.Untrusted)
        {
            Announce("暂存音源必须先验证并启用，才能设为默认");
            return;
        }
        _musicSourceService?.SetDefaultSource(src.Id);
        RefreshSourceList();
        Announce($"已将 {src.Name} 设为默认音源");
    }

    private async void SourceMenu_Enable_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource src) return;
        if (src.TrustState == ScriptTrustState.Untrusted)
        {
            await ConfirmAndActivateStagedSourceAsync(src);
            return;
        }
        var result = await _musicSourceService!.EnableSourceAsync(src);
        RefreshSourceList();
        Announce(result.Succeeded ? $"已启用音源：{src.Name}" : result.Message);
    }

    private void SourceMenu_Disable_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource src) return;
        _musicSourceService?.DisableSource(src);
        RefreshSourceList();
        Announce($"已禁用音源：{src.Name}");
    }

    private async void SourceMenu_Test_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource src) return;
        Announce($"正在检测 {src.Name} 有效性...");
        var ok = await _musicSourceService!.TestSourceAsync(src);
        RefreshSourceList();
        Announce(ok ? $"{src.Name} 有效" : $"{src.Name} 无效");
    }

    private async void SourceMenu_Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource src) return;
        var nameDlg = new TextInputDialog("编辑音源名称", "音源名称：", src.Name);
        if (nameDlg.ShowDialog() != true) return;
        var name = nameDlg.Value?.Trim();
        if (string.IsNullOrEmpty(name)) { Announce("名称不能为空"); return; }
        src.Name = name;
        RefreshSourceList();
        await _musicSourceService!.UpdateSourceAsync(src);
        Announce($"音源名称已改为 {src.Name}");
    }

    private void SourceMenu_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource src) return;
        if (src.Type == SourceType.BuiltIn) { Announce("内置音源不可删除"); return; }
        var result = MessageBox.Show($"确定删除音源 {src.Name} 吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        _musicSourceService?.RemoveSource(src);
        RefreshSourceList();
        Announce($"已删除音源：{src.Name}");
    }

    // ==================== 播放控制 ====================

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playback.IsPlaying)
        {
            _playback.Pause();
            UpdatePlayPauseMenuHeader("播放");
            Announce("已暂停");
        }
        else if (_playback.IsPaused && _playback.CurrentMusic != null)
        {
            // 恢复播放：不重新加载，直接从暂停位置继续
            _ = _playback.ResumeAsync();
            UpdatePlayPauseMenuHeader("暂停");
            Announce("继续播放");
        }
        else
        {
            if (_playback.CurrentMusic != null)
            {
                _ = _playback.ReplayAsync();
                UpdatePlayPauseMenuHeader("暂停");
                Announce("继续播放");
            }
            else
            {
                Announce("当前没有正在播放的歌曲");
            }
        }
    }

    /// <summary>同步“播放控制”菜单中播放/暂停项的文字状态（原右侧按钮已移除）</summary>
    private void UpdatePlayPauseMenuHeader(string label)
    {
        if (MenuPlayPauseItem != null)
            MenuPlayPauseItem.Header = label == "播放" ? "播放/暂停（F5）" : "暂停（F5）";
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _playback.NextAsync();
        Announce("下一首");
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _playback.PreviousAsync();
        Announce("上一首");
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _playback.Stop();
        UpdatePlayPauseMenuHeader("播放");
        Announce("已停止");
    }

    private void PlayModeButton_Click(object sender, RoutedEventArgs e)
    {
        _playback.Mode = _playback.Mode switch
        {
            PlayMode.Sequential => PlayMode.Loop,
            PlayMode.Loop => PlayMode.SingleLoop,
            PlayMode.SingleLoop => PlayMode.Shuffle,
            _ => PlayMode.Sequential
        };
        _suppressPlaybackSettingChanges = true;
        try
        {
            ShufflePlayCheckBox.IsChecked = _playback.Mode == PlayMode.Shuffle;
            RepeatPlayCheckBox.IsChecked = _playback.Mode is PlayMode.Loop or PlayMode.SingleLoop;
        }
        finally
        {
            _suppressPlaybackSettingChanges = false;
        }
        UpdatePlayModeDisplay();
        Announce($"播放模式：{GetPlayModeName(_playback.Mode)}");
    }

    /// <summary>同步“播放控制”菜单与信息面板中的播放模式显示（原右侧按钮已移除）</summary>
    private void UpdatePlayModeDisplay()
    {
        var name = GetPlayModeName(_playback.Mode);
        if (MenuPlayModeItem != null)
            MenuPlayModeItem.Header = $"播放模式：{name}（F1）";
        if (CurrentPlayModeText != null)
            CurrentPlayModeText.Text = $"播放模式：{name}";
    }

    private static string GetPlayModeName(PlayMode mode) => mode switch
    {
        PlayMode.Loop => "列表循环",
        PlayMode.SingleLoop => "单曲循环",
        PlayMode.Shuffle => "随机播放",
        _ => "顺序播放"
    };

    // ==================== 播放控制菜单处理 ====================

    private void RewindMenu_Click(object sender, RoutedEventArgs e) => SeekBy(-10);

    private void ForwardMenu_Click(object sender, RoutedEventArgs e) => SeekBy(10);

    private void VolumeDownMenu_Click(object sender, RoutedEventArgs e) => ChangeVolume(-0.1);

    private void VolumeUpMenu_Click(object sender, RoutedEventArgs e) => ChangeVolume(0.1);

    private void PrevDeviceMenu_Click(object sender, RoutedEventArgs e) => SwitchOutputDevice(-1);

    private void NextDeviceMenu_Click(object sender, RoutedEventArgs e) => SwitchOutputDevice(1);

    private void LyricDelayMenu_Click(object sender, RoutedEventArgs e) => _lyrics.AdjustOffset(500);

    private void LyricAdvanceMenu_Click(object sender, RoutedEventArgs e) => _lyrics.AdjustOffset(-500);

    private void DownloadMenu_Click(object sender, RoutedEventArgs e) => DownloadCurrentSong();

    private void HideShowWindowMenu_Click(object sender, RoutedEventArgs e) => ToggleWindowVisibility();

    // ==================== 菜单处理 ====================

    private void OpenLocalFile_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "音频文件|*.mp3;*.flac;*.wav;*.m4a|所有文件|*.*",
            Title = "选择音频文件"
        };
        if (openFileDialog.ShowDialog() == true)
            Announce($"已选择：{System.IO.Path.GetFileName(openFileDialog.FileName)}");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folderDialog = new System.Windows.Forms.FolderBrowserDialog();
        if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            Announce($"已选择文件夹：{System.IO.Path.GetFileName(folderDialog.SelectedPath)}");
    }

    private void Exit_Click(object sender, RoutedEventArgs e) =>
        _ = _desktopLifecycle.ExecuteAsync(DesktopCommand.Exit);

    private void UserGuide_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var guide = _windowFactory.CreateUserGuide(this);
            guide.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开使用说明：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Sponsor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = _windowFactory.CreateSponsor(this);
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开赞助二维码：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "落雪音乐 无障碍版\n\n当前版本：V2.0（全部功能免费）\n\n常用快捷键：\n" +
            "Alt+1~8：切换页面\n" +
            "Ctrl+F：搜索\n" +
            "Ctrl+R：刷新当前页面\n" +
            "播放/暂停、停止、上下首、快进快退、播放模式、下载、音量等：设置→快捷键设置 中自定义\n" +
            "Alt+M：音源选择\n" +
            "Alt+C：分类列表\n" +
            "Alt+L：歌单列表\n" +
            "Alt+S：歌曲列表\n" +
            "Alt+R：排行榜列表\n" +
            "列表内回车或空格：播放\n" +
            "Shift+回车：在歌曲列表查看歌词\n" +
            "应用键或Shift+F10：打开列表操作菜单\n" +
            "搜索框内 Delete：清空搜索\n" +
            "删除键：从播放列表移除/删除音源",
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ==================== 辅助 ====================

    // ==================== 列表内翻页条目 ====================

    private const string PagePrevId = "__PAGE_PREV__";
    private const string PageNextId = "__PAGE_NEXT__";
    private const string AlbumBackId = "__ALBUM_BACK__";
    private const string SearchMoreId = "__SEARCH_MORE__";
    private const string BatchDownloadListTag = "__BATCH_DOWNLOAD_LIST__";
    private const string BatchDownloadAlbumTag = "__BATCH_DOWNLOAD_ALBUM__";

    /// <summary>估算总页数：接口未返回总数时按当前页已加载数量推断</summary>
    private static int EstimateTotalPages(int totalItems, int pageSize, int page, int loadedCount)
    {
        if (totalItems > 0) return Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
        return loadedCount >= pageSize ? page + 1 : page;
    }
    /// <summary>在歌曲列表末尾追加“上一页/下一页”条目（分类歌单/排行榜分页用）</summary>
    private static List<MusicInfo> AppendPageNav(IEnumerable<MusicInfo> items, int currentPage, int totalPages)
    {
        var list = items.ToList();
        if (totalPages <= 1) return list;
        if (currentPage > 1)
            list.Add(new MusicInfo { Id = PagePrevId, Title = "上一页", Artist = $"第 {currentPage - 1} 页，回车翻页" });
        if (currentPage < totalPages)
            list.Add(new MusicInfo { Id = PageNextId, Title = "下一页", Artist = $"第 {currentPage + 1} 页，回车翻页" });
        return list;
    }


    private static string GetComboTag(ComboBox combo, string defaultValue)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? defaultValue;
    }

    private static string GetComboDisplayText(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString()
        ?? GetComboTag(combo, string.Empty);


    // ==================== 快捷键 ====================

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            _settingsWorkflow.RaiseGlobalHotkey(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>低层键盘钩子：跟踪物理数字小键盘导航键，避免其误触发方向键热键</summary>
    private IntPtr NumpadKeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool extended = (info.flags & LLKHF_EXTENDED) != 0;
                int scan = (int)info.scanCode;
                if (!extended && NumpadNavScanCodes.Contains(scan) && NumpadScanToKey.TryGetValue(scan, out var key))
                {
                    int msg = wParam.ToInt32();
                    bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    lock (_numpadLock)
                    {
                        if (isDown) _numpadNavKeysDown.Add(key);
                        else _numpadNavKeysDown.Remove(key);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Numpad keyboard hook event could not be decoded: ErrorType={ErrorType}", ex.GetType().Name);
            }
        }
        return CallNextHookEx(_llKeyboardHook, nCode, wParam, lParam);
    }

    private void InstallNumpadHook()
    {
        try
        {
            if (_llKeyboardHook != IntPtr.Zero) return;
            _llKeyboardProc = NumpadKeyboardHookProc;
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            using var module = proc.MainModule;
            _llKeyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _llKeyboardProc,
                GetModuleHandle(module?.ModuleName), 0);
        }
        catch (Exception ex)
        {
            Log.Warning("Numpad keyboard hook installation failed: ErrorType={ErrorType}", ex.GetType().Name);
            _llKeyboardHook = IntPtr.Zero;
        }
    }

    private void UninstallNumpadHook()
    {
        if (_llKeyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_llKeyboardHook);
            _llKeyboardHook = IntPtr.Zero;
        }
        lock (_numpadLock) _numpadNavKeysDown.Clear();
    }

    /// <summary>热键是否由物理小键盘键触发（NumLock 关闭时整个小键盘 0-9/. 会分别上报为方向键/Home/End/PgUp/PgDn/Insert/Delete 等），
    /// 命中则忽略，避免整个小键盘区域误触发 Alt+Shift 等方向键热键组。</summary>
    private bool IsNumpadOriginHotkey(Key key) =>
        NumpadOriginKeys.Contains(key) && IsNumpadKeyDown(key);

    private bool IsNumpadKeyDown(Key key)
    {
        lock (_numpadLock)
            return _numpadNavKeysDown.Contains(key);
    }

    private void HotkeyManager_HotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        // 修复：NumLock 关闭时小键盘 4/6/2/8 会作为方向键上报（NVDA 桌面布局下常见），
        // 全局热键是方向键组合时，若由物理小键盘触发则忽略，避免误触发。
        if (IsNumpadOriginHotkey(e.Hotkey.Key)) return;
        Dispatcher.BeginInvoke(new Action(() => ExecuteHotkeyAction(e.Hotkey.ActionId)),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void HotkeyManager_HotkeysRegistrationFailed(object? sender, HotkeysRegistrationFailedEventArgs e)
    {
        if (e.FailedHotkeys == null || e.FailedHotkeys.Count == 0) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_hotkeyConflictDialogOpen) return;
            _hotkeyConflictDialogOpen = true;
            try
            {
                var dlg = new HotkeyConflictDialog(e.FailedHotkeys) { Owner = this };
                var result = dlg.ShowDialog();
                if (result == true && dlg.SelectedHotkey != null)
                    StartHotkeyRecordingFor(dlg.SelectedHotkey);
            }
            finally
            {
                _hotkeyConflictDialogOpen = false;
                RefreshHotkeyConflictMarkers();
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>针对指定冲突快捷键直接进入重新录制（切到设置页并聚焦该热键项）</summary>
    private void StartHotkeyRecordingFor(Hotkey hotkey)
    {
        var item = _hotkeySettingItems.FirstOrDefault(i => i.ActionId == hotkey.ActionId && i.IsGlobal == hotkey.IsGlobal);
        if (item == null) return;
        NavigateToTab(5);
        HotkeyListBox.SelectedItem = item;
        HotkeyListBox.ScrollIntoView(item);
        FocusListContent(HotkeyListBox);
        StartHotkeyRecording(item);
    }

    /// <summary>刷新设置列表中全局快捷键的“（冲突）”标记（注册失败的全局项）</summary>
    private void RefreshHotkeyConflictMarkers()
    {
        foreach (var item in _hotkeySettingItems)
        {
            var h = _settingsWorkflow.GetHotkeys().FirstOrDefault(x => x.ActionId == item.ActionId && x.IsGlobal == item.IsGlobal);
            if (h == null) continue;
            var text = FormatHotkey(h);
            var conflicted = h.IsGlobal && h.IsEnabled && h.Key != Key.None
                && !_settingsWorkflow.IsGlobalHotkeyRegistered(h);
            item.ShortcutText = conflicted ? $"{text}（冲突）" : text;
            item.HasConflict = conflicted;
        }
    }

    private void BuildHotkeySettingItems()
    {
        _hotkeySettingItems.Clear();
        foreach (var h in _settingsWorkflow.GetHotkeys())
        {
            _hotkeySettingItems.Add(new HotkeySettingItem
            {
                ActionId = h.ActionId,
                ActionName = h.IsGlobal ? h.Name : $"{h.Name}（局部）",
                IsGlobal = h.IsGlobal,
                IsEnabled = h.IsEnabled,
                ShortcutText = FormatHotkey(h)
            });
        }
        HotkeyListBox.ItemsSource = _hotkeySettingItems;
    }

    private static string FormatHotkey(Hotkey h)
    {
        if (!h.IsEnabled || h.Key == Key.None) return "未设置";
        var parts = new List<string>();
        if ((h.Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((h.Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((h.Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((h.Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(KeyName(h.Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        Key.Space => "空格",
        Key.Left => "左方向键",
        Key.Right => "右方向键",
        Key.Up => "上方向键",
        Key.Down => "下方向键",
        Key.MediaPlayPause => "播放媒体键",
        Key.MediaStop => "停止媒体键",
        Key.MediaNextTrack => "下一曲媒体键",
        Key.MediaPreviousTrack => "上一曲媒体键",
                _ => key.ToString()
    };

    private void HotkeyListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (HotkeyListBox.SelectedItem is not HotkeySettingItem item) return;
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            StartHotkeyRecording(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            ClearHotkey(item);
            e.Handled = true;
        }
    }

    private void StartHotkeyRecording(HotkeySettingItem item)
    {
        _recordingHotkeyItem = item;
        Announce($"请按下{item.ActionName}的快捷键，Esc取消，Delete清除");
    }

    private async void ClearHotkey(HotkeySettingItem item)
    {
        var result = await _settingsWorkflow.ClearHotkeyAsync(item.ActionId, item.IsGlobal);
        PresentHotkeyResult(result, null, $"{item.ActionName}快捷键已清除");
    }

    private async void ResetHotkeysBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = await _settingsWorkflow.ResetHotkeysAsync();
        PresentHotkeyResult(result, null, "快捷键已恢复默认");
    }

    private async void HandleHotkeyRecording(KeyEventArgs e)
    {
        var item = _recordingHotkeyItem!;
        if (e.Key == Key.Escape)
        {
            _recordingHotkeyItem = null;
            Announce("已取消，快捷键未更改");
            RefocusHotkeyList();
            return;
        }
        if (e.Key == Key.Delete)
        {
            _recordingHotkeyItem = null;
            ClearHotkey(item);
            RefocusHotkeyList();
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool isModifierOnly = key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
        if (isModifierOnly) return;
        var result = await _settingsWorkflow.ReplaceHotkeyAsync(
            item.ActionId,
            item.IsGlobal,
            key,
            mods);
        _recordingHotkeyItem = null;
        var saved = _settingsWorkflow.GetHotkeys().First(h =>
            h.ActionId == item.ActionId && h.IsGlobal == item.IsGlobal);
        var message = $"{item.ActionName}功能，{FormatHotkey(saved)}热键设置完成";
        PresentHotkeyResult(result, item, message);
        RefocusHotkeyList();
    }

    private void PresentHotkeyResult(
        HotkeyEditResult result,
        HotkeySettingItem? item,
        string successMessage)
    {
        BuildHotkeySettingItems();
        RefreshHotkeyConflictMarkers();
        var message = result.Status switch
        {
            HotkeyEditStatus.Applied or HotkeyEditStatus.Unchanged => successMessage,
            HotkeyEditStatus.Invalid => "该组合不是有效快捷键，请使用带修饰键的组合、功能键或媒体键。",
            HotkeyEditStatus.Conflict => $"该快捷键已被{result.Conflicts.FirstOrDefault()?.Name ?? "其他功能"}使用。",
            HotkeyEditStatus.RegistrationFailed => "该全局快捷键已被其他程序占用，原快捷键已恢复。",
            HotkeyEditStatus.RollbackFailed => "快捷键注册失败，旧注册也无法恢复；请重新设置冲突项。",
            HotkeyEditStatus.PersistenceFailed => "快捷键已生效，但保存失败；退出时将再次保存。",
            _ => "快捷键未更改。"
        };
        Announce(message);
        if (result.Applied && item is not null)
            new HotkeyConfirmDialog(message) { Owner = this }.ShowDialog();
    }

    private void RefocusHotkeyList() => FocusListContent(HotkeyListBox);

    private void ExecuteHotkeyAction(string actionId)
    {
        switch (actionId)
        {
            case HotkeyActions.PlayPause:
                // 保持原 Ctrl+空格 行为：焦点在可编辑文本框时不触发，避免影响输入
                if (Keyboard.FocusedElement is TextBox { IsReadOnly: false }) return;
                PlayPauseButton_Click(this, new RoutedEventArgs());
                break;
            case HotkeyActions.Stop:
                StopButton_Click(this, new RoutedEventArgs());
                break;
            case HotkeyActions.Previous:
                PreviousButton_Click(this, new RoutedEventArgs());
                break;
            case HotkeyActions.Next:
                NextButton_Click(this, new RoutedEventArgs());
                break;
            case HotkeyActions.Rewind:
                SeekBy(-10);
                break;
            case HotkeyActions.Forward:
                SeekBy(10);
                break;
            case HotkeyActions.TogglePlayMode:
                PlayModeButton_Click(this, new RoutedEventArgs());
                break;
            case HotkeyActions.Download:
                DownloadCurrentSong();
                break;
            case HotkeyActions.VolumeUp:
                ChangeVolume(0.1);
                break;
            case HotkeyActions.VolumeDown:
                ChangeVolume(-0.1);
                break;
            case HotkeyActions.SwitchNextOutputDevice:
                SwitchOutputDevice(1);
                break;
            case HotkeyActions.SwitchPreviousOutputDevice:
                SwitchOutputDevice(-1);
                break;
            case HotkeyActions.LyricDelay:
                _lyrics.AdjustOffset(500);
                break;
            case HotkeyActions.LyricAdvance:
                _lyrics.AdjustOffset(-500);
                break;
            case HotkeyActions.ToggleWindow:
                ToggleWindowVisibility();
                break;
        }
    }

    /// <summary>切换主窗口可见性：可见→隐藏到托盘；隐藏→恢复窗口</summary>
    private void ToggleWindowVisibility() =>
        _ = _desktopLifecycle.ExecuteAsync(DesktopCommand.ToggleVisibility);

    /// <summary>播放设备切换专用播报：排队朗读，期间暂停歌词自动朗读，避免设备播报被歌词打断</summary>
    private async void AnnounceDevice(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        _lyrics.SpeechSuppressed = true;
        try
        {
            Announce(message, AnnouncementKind.Status, "audio-device", AnnouncementSpeech.Queued);
            // 按文本长度估算朗读时长，期间暂停歌词朗读；无语音时也稍作停留，避免读屏实时区域被下一句歌词覆盖
            await Task.Delay(EstimateSpeakDurationMs(message));
        }
        catch (Exception ex)
        {
            Log.Warning("Audio device announcement failed: ErrorType={ErrorType}", ex.GetType().Name);
        }
        finally
        {
            _lyrics.SpeechSuppressed = false;
        }
    }

    /// <summary>估算播报朗读时长（毫秒）：中文约每字 200ms，含排队等待缓冲</summary>
    private static int EstimateSpeakDurationMs(string message) =>
        Math.Clamp(message.Length * 200, 1500, 5000) + 1200;

    /// <summary>按方向切换输出设备（1 = 下一个，-1 = 上一个）并播报结果、持久化</summary>
    private void SwitchOutputDevice(int direction)
    {
        if (_playerService == null) return;
        var devices = _playerService.GetOutputDevices();
        if (devices.Count <= 1)
        {
            Announce("未找到可切换的输出设备");
            return;
        }
        string? name = direction > 0
            ? _playerService.SwitchToNextOutputDevice()
            : _playerService.SwitchToPreviousOutputDevice();
        if (name == null)
        {
            Announce("切换播放设备失败");
            return;
        }
        AnnounceDevice($"已切换到播放设备：{name}");
        _ = PersistOutputDeviceAsync();
    }

    /// <summary>持久化当前输出设备选择，供下次启动恢复</summary>
    private async Task PersistOutputDeviceAsync()
    {
        try
        {
            var edit = _settingsWorkflow.Edit(settings =>
                settings.OutputDeviceIndex = _playerService.CurrentOutputDeviceIndex);
            if (!edit.Accepted) return;
            var saved = await _settingsWorkflow.SaveAsync(edit.State);
            if (!saved.Succeeded)
                Announce("保存输出设备设置失败，退出时将再次保存");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存输出设备设置失败: {ex.Message}");
        }
    }

    private void SeekBy(double seconds)
    {
        if (_playback.CurrentMusic == null || !_playback.IsPlaying)
        {
            Announce("当前没有正在播放的歌曲");
            return;
        }
        var target = _playerService.Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (_playerService.Duration > TimeSpan.Zero && target > _playerService.Duration)
            target = _playerService.Duration;
        // 用“清空输出缓冲”的定位方式：确保音频立即跳到新位置，避免快进后歌词先于听到的歌曲提前朗读
        _playerService.SeekTo(target);
        // After a seek, immediately sync the display; the spoken line is deferred to
        // the timer once the audio settles, so the voice never runs ahead of playback.
        _lyrics.Seek(target);
        Announce($"{(seconds > 0 ? "快进" : "快退")}十秒，当前 {FormatTime(target)}");
    }

    private void ChangeVolume(double delta)
    {
        if (_playerService == null) return;
        var v = Math.Clamp(_playerService.Volume + delta, 0.0, 1.0);
        ApplyVolume(v, announce: true);
    }

    /// <summary>集中设置音量：announce=true 时播报（局部热键/播放控制菜单），false 用于启动时恢复默认音量不播报</summary>
    private void ApplyVolume(double volume, bool announce = false)
    {
        if (_playerService == null) return;
        _playerService.SetVolume(Math.Clamp(volume, 0.0, 1.0));
        if (announce)
        {
            Announce(
                $"音量 {_playerService.Volume * 100:0}%",
                AnnouncementKind.Progress,
                "volume");
        }
    }

    private async void DownloadCurrentSong()
    {
        var music = GetSelectedSongInFocusedList() ?? _playback.CurrentMusic;
        if (music == null)
        {
            Announce("没有可下载的歌曲");
            return;
        }
        await _downloads.DownloadAsync(new DownloadRequest(
            music,
            GetComboTag(DownloadQualityCombo, "128k"),
            GetEffectiveDownloadDir()));
    }

    private MusicInfo? GetSelectedSongInFocusedList()
    {
        var fe = Keyboard.FocusedElement as DependencyObject;
        if (fe == null) return null;
        var list = FindAncestor<ListBox>(fe);
        if (list == null) return null;
        var songLists = new ListBox?[] { SearchResultsList, CategorySongsList, RankingSongsList, FavoriteSongsList, _playlistImportPanel.SongList };
        if (!songLists.Contains(list)) return null;
        return list.SelectedItem as MusicInfo;
    }

    /// <summary>当前标签页的最后一个可聚焦控件（用于页边界 Tab/Shift+Tab 跨页循环）</summary>
    private FrameworkElement? GetLastControlOfCurrentTab() => GetLastControlOfPage(MainTabControl.SelectedIndex);

    /// <summary>指定主标签页的最后一个可聚焦控件（页边界跨页循环用）</summary>
    private FrameworkElement? GetLastControlOfPage(int index)
    {
        return index switch
        {
            0 => SearchResultsList,
            1 => FavoriteSongsList,
            2 => _playlistImportPanel.LastFocusable,
            3 => CategorySongsList,
            4 => RankingSongsList,
            5 => ResetHotkeysBtn,
            6 => DownloadTaskList,
            7 => SourceList,
            _ => null
        };
    }

    /// <summary>指定主标签页的第一个可聚焦控件（页边界跨页循环用）</summary>
    private FrameworkElement? GetFirstControlOfPage(int index)
    {
        return index switch
        {
            0 => SearchSourceCombo,
            1 => NewOnlinePlaylistBtn,
            2 => _playlistImportPanel.FirstFocusable,
            3 => CategorySourceCombo,
            4 => RankingSourceCombo,
            5 => DefaultQualityCombo,
            6 => DownloadCancelBtn,
            7 => ImportLocalSourceBtn,
            _ => null
        };
    }


    /// <summary>判断 node 是否位于 ancestor 的可视/逻辑树内</summary>
    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            var visualParent = System.Windows.Media.VisualTreeHelper.GetParent(node);
            if (visualParent != null) { node = visualParent; continue; }
            node = LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>聚焦列表：有选中项时聚焦选中条目（读屏进入焦点模式，方向键即可浏览），否则聚焦容器</summary>
    private void FocusListContent(FrameworkElement element)
    {
        if (element is ListBox list && list.SelectedItem != null
            && list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is FrameworkElement item)
        {
            FocusControl(item);
            return;
        }
        FocusControl(element);
    }

    private void ApplyBrowseFocus(BrowseFocusTarget target)
    {
        var element = target switch
        {
            BrowseFocusTarget.SearchResults => SearchResultsList,
            BrowseFocusTarget.CategoryList => CategoryList,
            BrowseFocusTarget.CategoryPlaylists => CategoryPlaylistsListBox,
            BrowseFocusTarget.CategorySongs => CategorySongsList,
            BrowseFocusTarget.SavedPlaylistSongs => FavoriteSongsList,
            BrowseFocusTarget.RankingList => RankingList,
            BrowseFocusTarget.RankingSongs => RankingSongsList,
            _ => null
        };
        if (element is not null) FocusListContent(element);
    }

    /// <summary>聚焦控件；延迟重试带页面令牌校验，避免切页后延迟抢焦导致焦点乱跳。
    /// 已加载但折叠/不可见的控件无法聚焦：直接跳过，避免页边界 Tab/Shift+Tab 聚焦到折叠控件时
    /// WPF 焦点系统/BringIntoView 卡死或焦点卡在标签条上。</summary>
    private void FocusControl(FrameworkElement element)
    {
        if (element == null) return;
        if (element.IsLoaded && !element.IsVisible) return;
        if (element.Focus()) return;
        var token = _tabFocusToken;
        element.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (token != _tabFocusToken) return;
            if (element.IsLoaded && !element.IsVisible) return;
            element.Focus();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>沿可视树/逻辑树查找祖先控件（兼容组合框下拉弹层内的项）</summary>
    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            var visualParent = System.Windows.Media.VisualTreeHelper.GetParent(current);
            if (visualParent != null) { current = visualParent; continue; }
            current = LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_desktopLifecycle.AllowWindowClose) return;

        e.Cancel = true;
        _ = _desktopLifecycle.ExecuteAsync(DesktopCommand.Exit);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_musicSourceService != null)
            _musicSourceService.SourcesChanged -= MusicSourceService_SourcesChanged;
        _settingsWorkflow.UnregisterAllGlobal();
        UninstallNumpadHook();
        if (_lyricsWindow != null)
        {
            _lyricsWindow.PrepareForAppShutdown();
            _lyricsWindow.Close();
        }
        base.OnClosed(e);
        _positionTimer.Stop();
        _clockTimer.Stop();
    }

}

/// <summary>

/// 无障碍专用滚动容器：方向键只负责滚动，绝不把焦点在控件之间跳来跳去。
/// WPF 默认 ScrollViewer 在方向键按下时会调用 PredictFocus 把焦点移到“下一个控件”，
/// 这正是盲人键盘导航中“在组合框/列表/按钮上按方向键焦点跳出”的根源；
/// 本控件移除了该行为，方向键按键继续冒泡给键盘导航系统，
/// 由外层 Window_KeyDown 决定是否拦截（普通控件）或放行（列表/组合框等容器）。
/// </summary>
public class SafeScrollViewer : System.Windows.Controls.ScrollViewer
{
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // 方向键：不做默认的“预测焦点并跳转”，也不标记处理，
        // 让按键继续冒泡，交由容器控件（列表框/组合框/滑块/文本框）或 Window 兜底处理。
        if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down
            or System.Windows.Input.Key.Left or System.Windows.Input.Key.Right)
        {
            return;
        }
        base.OnKeyDown(e);
    }
}
/// <summary>对屏幕阅读器/自动化树隐藏但仍可见、可鼠标操作的按钮（用于冗余的翻页按钮）</summary>
public class InvisibleToScreenReaderButton : System.Windows.Controls.Button
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
    {
        return new HiddenButtonAutomationPeer(this);
    }

    private sealed class HiddenButtonAutomationPeer : System.Windows.Automation.Peers.ButtonAutomationPeer
    {
        public HiddenButtonAutomationPeer(Button owner) : base(owner) { }
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
}

/// <summary>对屏幕阅读器/自动化树隐藏但仍可见的文本（用于仅作视觉提示的页码文本）</summary>
public class InvisibleToScreenReaderTextBlock : System.Windows.Controls.TextBlock
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
    {
        return new HiddenTextBlockAutomationPeer(this);
    }

    private sealed class HiddenTextBlockAutomationPeer : System.Windows.Automation.Peers.TextBlockAutomationPeer
    {
        public HiddenTextBlockAutomationPeer(TextBlock owner) : base(owner) { }
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
}
/// <summary>
/// 读屏单次朗读的列表：通过自定义自动化对等类把每个列表项设为“叶子节点”，
/// 使读屏只按列表项名称朗读一次，不再把内部多个文本块重复朗读。
/// </summary>
public class SingleReadListBox : System.Windows.Controls.ListBox
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
    {
        return new SingleReadListBoxAutomationPeer(this);
    }
}

internal sealed class SingleReadListBoxAutomationPeer : System.Windows.Automation.Peers.ListBoxAutomationPeer
{
    public SingleReadListBoxAutomationPeer(System.Windows.Controls.ListBox owner) : base(owner) { }

    protected override System.Windows.Automation.Peers.ItemAutomationPeer CreateItemAutomationPeer(object item)
    {
        return new SingleReadListBoxItemAutomationPeer(item, this);
    }
}

/// <summary>
/// 列表项自动化对等类：不暴露内部子元素（GetChildrenCore 返回空），
/// 名称取列表项名称（HotkeySettingItem.ToString()），读屏只朗读一次。
/// </summary>
internal sealed class SingleReadListBoxItemAutomationPeer : System.Windows.Automation.Peers.ListBoxItemAutomationPeer
{
    public SingleReadListBoxItemAutomationPeer(object item, System.Windows.Automation.Peers.ListBoxAutomationPeer listPeer)
        : base(item, listPeer) { }

    protected override string GetNameCore()
    {
        var name = base.GetNameCore();
        if (string.IsNullOrWhiteSpace(name))
            return Item?.ToString() ?? string.Empty;
        return name;
    }

    protected override List<System.Windows.Automation.Peers.AutomationPeer> GetChildrenCore()
    {
        // 叶子节点：不暴露内部文本块，读屏只朗读名称一次
        return new List<System.Windows.Automation.Peers.AutomationPeer>();
    }
}
