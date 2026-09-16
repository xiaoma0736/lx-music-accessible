using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

public partial class SettingsWindow : Window
{
    private readonly IAccessibleAnnouncer _announcer;
    private readonly SettingsWorkflow _settings;
    private readonly ITtsService _tts;
    private readonly IDownloadWorkflow _downloads;
    private readonly Func<KeyEventArgs, bool> _handleHotkey;
    private readonly Action<bool> _setInputSuppressed;
    private readonly Hotkey? _reconfigure;
    private readonly ObservableCollection<HotkeySettingItem> _hotkeys = [];
    private HotkeySettingItem? _recording;
    private bool _loading = true;
    private bool _savingClose;
    private bool _allowClose;
    private bool _closed;
    private bool _inputSuppressed;
    private DependencyObject? _lastFocusedElement;
    private bool _hasTrackedFocus;

    public SettingsWindow(
        IAccessibleAnnouncer announcer,
        SettingsWorkflow settings,
        ITtsService tts,
        IDownloadWorkflow downloads,
        Func<KeyEventArgs, bool> handleHotkey,
        Action<bool> setInputSuppressed,
        Hotkey? reconfigure = null)
    {
        ArgumentNullException.ThrowIfNull(announcer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tts);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(handleHotkey);
        ArgumentNullException.ThrowIfNull(setInputSuppressed);
        _announcer = announcer;
        _settings = settings;
        _tts = tts;
        _downloads = downloads;
        _handleHotkey = handleHotkey;
        _setInputSuppressed = setInputSuppressed;
        _reconfigure = reconfigure;

        InitializeComponent();
        DataDirBox.Text = _settings.DataDirectory;
        InitializeVoiceControls();
        HotkeyListBox.ItemsSource = _hotkeys;
        LoadControls(_settings.Current.Settings);
        BuildHotkeys();
        CategoryList.SelectedIndex = reconfigure is null ? 0 : 6;
        ShowCategory(CategoryList.SelectedIndex);
        _settings.SettingsChanged += Settings_SettingsChanged;
        _loading = false;
        Loaded += Window_Loaded;
    }

    internal bool IsRecordingHotkey => _recording is not null;
    internal int CloseSaveAttempts { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_reconfigure is null)
            {
                FocusSelectedCategory();
                return;
            }

            var item = _hotkeys.FirstOrDefault(candidate =>
                candidate.ActionId == _reconfigure.ActionId
                && candidate.IsGlobal == _reconfigure.IsGlobal);
            if (item is null)
            {
                FocusSelectedCategory();
                return;
            }
            HotkeyListBox.SelectedItem = item;
            HotkeyListBox.ScrollIntoView(item);
            HotkeyListBox.FocusItemOrList();
            StartHotkeyRecording(item);
        }, DispatcherPriority.Input);
    }

    private void Settings_SettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => RefreshFrom(e.Current.Settings));
            return;
        }
        RefreshFrom(e.Current.Settings);
    }

    private void RefreshFrom(UserSettings settings)
    {
        if (_closed) return;
        _loading = true;
        try
        {
            LoadControls(settings);
            BuildHotkeys();
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadControls(UserSettings settings)
    {
        SelectCombo(CloseBehaviorCombo, settings.HideToTrayOnAltF4 ? "hide" : "close");
        SelectCombo(DefaultQualityCombo, settings.DefaultPlayQuality);
        SelectCombo(DefaultPlayModeCombo, (settings.DefaultPlayMode ?? PlayMode.Sequential).ToString());
        ThirdPartyPlayerPathBox.Text = settings.ThirdPartyPlayerPath ?? string.Empty;
        LyricAnnounceCheckBox.IsChecked = settings.LyricAutoAnnounce;
        LyricScrollCheckBox.IsChecked = settings.LyricAutoScroll;
        DownloadPathBox.Text = settings.DownloadPath?.Trim() ?? string.Empty;
        SelectCombo(DownloadQualityCombo, settings.DefaultDownloadQuality);
        DownloadLyricCheckBox.IsChecked = settings.DownloadLyric;
        DownloadNameQualityCheckBox.IsChecked = settings.DownloadNameIncludeQuality;
        SelectCombo(DownloadNameOrderCombo, settings.DownloadNameOrder.ToString());
        EnableVoiceAnnouncementCheckBox.IsChecked = settings.EnableVoiceAnnouncement;
        SelectCombo(VoiceCombo, settings.AccessibilitySettings.AnnouncementVoiceId);
        SelectCombo(SpeechRateCombo, settings.AccessibilitySettings.AnnouncementRate.ToString("0.##"));
    }

    private void InitializeVoiceControls()
    {
        VoiceCombo.Items.Add(new ComboBoxItem { Content = "自动检测（推荐）", Tag = string.Empty });
        foreach (var voice in _tts.GetAvailableVoices())
            VoiceCombo.Items.Add(new ComboBoxItem { Content = $"{voice.Name}（{voice.Culture}）", Tag = voice.Id });
        SpeechBackendText.Text = $"朗读后端：{_tts.ActiveBackendName}（自动检测）";
    }

    private static void SelectCombo(ComboBox combo, string? tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedIndex >= 0) ShowCategory(CategoryList.SelectedIndex);
    }

    private void ShowCategory(int selected)
    {
        var panels = new FrameworkElement[]
        {
            GeneralPanel, PlaybackPanel, LyricsPanel, DownloadPanel,
            StoragePanel, AccessibilityPanel, HotkeysPanel
        };
        for (var index = 0; index < panels.Length; index++)
            panels[index].Visibility = index == selected ? Visibility.Visible : Visibility.Collapsed;
        ContentScroller.ScrollToTop();
    }

    private bool Edit(Action<UserSettings> edit)
    {
        var result = _settings.Edit(edit);
        if (result.Accepted) return true;
        Announce(string.Join(" ", result.Errors), AnnouncementKind.Error);
        return false;
    }

    private void CloseBehaviorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Edit(settings => settings.HideToTrayOnAltF4 = SelectedTag(CloseBehaviorCombo, "close") == "hide");
    }

    private void DefaultQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) Edit(settings => settings.DefaultPlayQuality = SelectedTag(DefaultQualityCombo, "128k"));
    }

    private void DefaultPlayModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (Enum.TryParse<PlayMode>(SelectedTag(DefaultPlayModeCombo, "Sequential"), out var mode))
            Edit(settings => settings.DefaultPlayMode = mode);
    }

    private void LyricAnnounceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Edit(settings => settings.LyricAutoAnnounce = LyricAnnounceCheckBox.IsChecked == true);
    }

    private void LyricScrollCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Edit(settings => settings.LyricAutoScroll = LyricScrollCheckBox.IsChecked == true);
    }

    private void DownloadQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) Edit(settings => settings.DefaultDownloadQuality = SelectedTag(DownloadQualityCombo, "128k"));
    }

    private void DownloadSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Edit(settings =>
        {
            settings.DownloadLyric = DownloadLyricCheckBox.IsChecked == true;
            settings.DownloadNameIncludeQuality = DownloadNameQualityCheckBox.IsChecked == true;
        });
    }

    private void DownloadNameOrderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Edit(settings => settings.DownloadNameOrder = SelectedTag(DownloadNameOrderCombo, "ArtistTitle") == "TitleArtist"
            ? DownloadNameOrder.TitleArtist
            : DownloadNameOrder.ArtistTitle);
    }

    private void VoiceAnnouncementCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = EnableVoiceAnnouncementCheckBox.IsChecked == true;
        if (Edit(settings => settings.EnableVoiceAnnouncement = enabled))
            Announce(
                enabled ? "已开启语音朗读提示" : "已关闭语音朗读提示",
                AnnouncementKind.Status,
                "settings",
                enabled ? AnnouncementSpeech.FollowPreference : AnnouncementSpeech.Always);
    }

    private void VoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) Edit(settings => settings.AccessibilitySettings.AnnouncementVoiceId = SelectedTag(VoiceCombo, string.Empty));
    }

    private void SpeechRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !double.TryParse(SelectedTag(SpeechRateCombo, "1"), out var rate)) return;
        Edit(settings => settings.AccessibilitySettings.AnnouncementRate = rate);
    }

    private void BrowseDownloadPathBtn_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择歌曲下载目录",
            SelectedPath = _downloads.ResolveDownloadDirectory(_settings.Current.Settings.DownloadPath)
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        DownloadPathBox.Text = dialog.SelectedPath;
        if (CommitDownloadPath()) Announce($"下载目录已设置为 {dialog.SelectedPath}", AnnouncementKind.Success);
    }

    private void BrowseThirdPartyPlayerBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "播放器程序|*.exe|所有文件|*.*",
            Title = "选择第三方播放器程序",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        ThirdPartyPlayerPathBox.Text = dialog.FileName;
        if (CommitThirdPartyPlayerPath()) Announce($"第三方播放器已设置为 {dialog.FileName}", AnnouncementKind.Success);
    }

    private void DownloadPathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_loading && !_allowClose) CommitDownloadPath();
    }

    private void ThirdPartyPlayerPathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_loading && !_allowClose) CommitThirdPartyPlayerPath();
    }

    private void SettingsPathBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (ReferenceEquals(sender, DownloadPathBox)) CommitDownloadPath();
        else CommitThirdPartyPlayerPath();
    }

    private bool CommitDownloadPath() => CommitPath(
        DownloadPathBox,
        settings => settings.DownloadPath = DownloadPathBox.Text.Trim());

    private bool CommitThirdPartyPlayerPath() => CommitPath(
        ThirdPartyPlayerPathBox,
        settings => settings.ThirdPartyPlayerPath = ThirdPartyPlayerPathBox.Text.Trim());

    private bool CommitPath(TextBox input, Action<UserSettings> edit)
    {
        var result = _settings.Edit(edit);
        if (result.Accepted) return true;
        FocusInvalidPath(input, result.Errors);
        return false;
    }

    private bool CommitPendingPaths()
    {
        var download = DownloadPathBox.Text.Trim();
        var player = ThirdPartyPlayerPathBox.Text.Trim();
        var result = _settings.Edit(settings =>
        {
            settings.DownloadPath = download;
            settings.ThirdPartyPlayerPath = player;
        });
        if (result.Accepted) return true;
        var input = result.Errors.Any(error => error.StartsWith("下载目录", StringComparison.Ordinal))
            ? DownloadPathBox
            : ThirdPartyPlayerPathBox;
        FocusInvalidPath(input, result.Errors);
        return false;
    }

    private void FocusInvalidPath(TextBox input, IReadOnlyList<string> errors)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            input.Focus();
            input.SelectAll();
        }, DispatcherPriority.Input);
        Announce(string.Join(" ", errors), AnnouncementKind.Error);
    }

    private void BrowseDataDirBtn_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择数据目录（将保存数据库与日志）",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var result = _settings.ChangeDataDirectory(dialog.SelectedPath);
        DataDirBox.Text = result.Path;
        Announce(result.Message, result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
    }

    private void ClearCacheBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = _downloads.ClearTemporaryFiles();
        Announce(result.DeletedFiles == 0
            ? "没有可清理的缓存文件。"
            : $"已清理缓存 {result.DeletedFiles} 个文件，释放 {FormatBytes(result.FreedBytes)}。",
            AnnouncementKind.Success);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes} B";
    }

    private void BuildHotkeys()
    {
        var selected = HotkeyListBox.SelectedItem as HotkeySettingItem;
        _hotkeys.Clear();
        foreach (var hotkey in _settings.GetHotkeys())
        {
            _hotkeys.Add(new HotkeySettingItem
            {
                ActionId = hotkey.ActionId,
                ActionName = hotkey.IsGlobal ? hotkey.Name : $"{hotkey.Name}（局部）",
                IsGlobal = hotkey.IsGlobal,
                IsEnabled = hotkey.IsEnabled,
                ShortcutText = FormatHotkey(hotkey),
                HasConflict = hotkey.IsGlobal && hotkey.IsEnabled && (PlatformKey)hotkey.Key != PlatformKey.None
                    && !_settings.IsGlobalHotkeyRegistered(hotkey)
            });
        }
        if (selected is not null)
            HotkeyListBox.SelectedItem = _hotkeys.FirstOrDefault(item =>
                item.ActionId == selected.ActionId && item.IsGlobal == selected.IsGlobal);
    }

    private static string FormatHotkey(Hotkey hotkey)
    {
        if (!hotkey.IsEnabled || (PlatformKey)hotkey.Key == PlatformKey.None) return "未设置";
        var parts = new List<string>();
        if (((PlatformModifiers)hotkey.Modifiers & PlatformModifiers.Control) != 0) parts.Add("Ctrl");
        if (((PlatformModifiers)hotkey.Modifiers & PlatformModifiers.Alt) != 0) parts.Add("Alt");
        if (((PlatformModifiers)hotkey.Modifiers & PlatformModifiers.Shift) != 0) parts.Add("Shift");
        if (((PlatformModifiers)hotkey.Modifiers & PlatformModifiers.Windows) != 0) parts.Add("Win");
        parts.Add(((PlatformKey)hotkey.Key) switch
        {
            PlatformKey.Space => "空格",
            PlatformKey.Left => "左方向键",
            PlatformKey.Right => "右方向键",
            PlatformKey.Up => "上方向键",
            PlatformKey.Down => "下方向键",
            PlatformKey.MediaPlayPause => "播放媒体键",
            PlatformKey.MediaStop => "停止媒体键",
            PlatformKey.MediaNextTrack => "下一曲媒体键",
            PlatformKey.MediaPreviousTrack => "上一曲媒体键",
            _ => ((Key)hotkey.Key).ToString()
        });
        return string.Join("+", parts);
    }

    private void HotkeyListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (HotkeyListBox.SelectedItem is not HotkeySettingItem item) return;
        if (e.Key is Key.Enter or Key.Space)
        {
            StartHotkeyRecording(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            _ = ClearHotkeyAsync(item);
            e.Handled = true;
        }
    }

    private void StartHotkeyRecording(HotkeySettingItem item)
    {
        _recording = item;
        UpdateInputSuppression();
        Announce(
            $"请按下{item.ActionName}的快捷键，Esc 取消，Delete 清除",
            AnnouncementKind.Status,
            "hotkey-recording");

    }

    private async Task ClearHotkeyAsync(HotkeySettingItem item)
    {
        var result = await _settings.ClearHotkeyAsync(item.ActionId, item.IsGlobal);
        PresentHotkeyResult(result, null, $"{item.ActionName}快捷键已清除");
    }

    private async void ResetHotkeysBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = await _settings.ResetHotkeysAsync();
        PresentHotkeyResult(result, null, "快捷键已恢复默认");
    }

    private async void HandleHotkeyRecording(KeyEventArgs e)
    {
        var item = _recording!;
        if (e.Key == Key.Escape)
        {
            EndRecording("已取消，快捷键未更改");
            return;
        }
        if (e.Key == Key.Delete)
        {
            _recording = null;
            UpdateInputSuppression();
            await ClearHotkeyAsync(item);
            RefocusHotkeyList();
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        var result = await _settings.ReplaceHotkeyAsync(item.ActionId, item.IsGlobal, (PlatformKey)key, (PlatformModifiers)Keyboard.Modifiers);
        _recording = null;
        UpdateInputSuppression();
        var saved = _settings.GetHotkeys().First(hotkey =>
            hotkey.ActionId == item.ActionId && hotkey.IsGlobal == item.IsGlobal);
        PresentHotkeyResult(result, item, $"{item.ActionName}功能，{FormatHotkey(saved)}热键设置完成");
        RefocusHotkeyList();
    }

    private void EndRecording(string message)
    {
        _recording = null;
        UpdateInputSuppression();
        Announce(message, AnnouncementKind.Status, "hotkey-recording");
        RefocusHotkeyList();
    }

    private void RefocusHotkeyList()
    {
        HotkeyListBox.FocusItemOrList();
    }

    private void PresentHotkeyResult(HotkeyEditResult result, HotkeySettingItem? item, string successMessage)
    {
        BuildHotkeys();
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
        Announce(
            message,
            result.Applied ? AnnouncementKind.Success : AnnouncementKind.Error,
            "hotkey-recording");
        if (result.Applied && item is not null)
            new HotkeyConfirmDialog(message) { Owner = this }.ShowDialog();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is not null)
        {
            HandleHotkeyRecording(e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Tab && MoveAcrossCategoryBoundary(
                (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
        {
            e.Handled = true;
            return;
        }
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
        if (HotkeyListBox.IsKeyboardFocusWithin && e.Key is Key.Enter or Key.Space or Key.Delete) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase { IsReadOnly: false } or PasswordBox) return;
        if (_handleHotkey(e)) e.Handled = true;
    }

    private void Window_FocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        var next = e.NewFocus as DependencyObject;
        var nextCategory = next is not null && IsDescendantOf(next, CategoryList);
        var previousCategory = _lastFocusedElement is not null
            && IsDescendantOf(_lastFocusedElement, CategoryList);
        var announceReturn = nextCategory && _hasTrackedFocus && !previousCategory;
        _lastFocusedElement = next;
        _hasTrackedFocus = next is not null;

        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (announceReturn
                    && CategoryList.IsKeyboardFocusWithin
                    && CategoryList.SelectedItem is ListBoxItem selected)
                {
                    Announce(
                        AutomationProperties.GetName(selected),
                        AnnouncementKind.Status,
                        "settings",
                        AnnouncementSpeech.None);
                }

                UpdateInputSuppression();
            },
            DispatcherPriority.Input);
    }

    private void UpdateInputSuppression()
    {
        if (_closed) return;
        var focused = Keyboard.FocusedElement as DependencyObject;
        var editing = focused is System.Windows.Controls.Primitives.TextBoxBase { IsReadOnly: false } or PasswordBox
            && IsDescendantOf(focused, this);
        var suppressed = _recording is not null || editing;
        if (suppressed == _inputSuppressed) return;
        _inputSuppressed = suppressed;
        _setInputSuppressed(suppressed);
    }

    private static bool IsDescendantOf(DependencyObject? current, DependencyObject ancestor)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            var visualParent = System.Windows.Media.VisualTreeHelper.GetParent(current);
            current = visualParent ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    internal bool MoveAcrossCategoryBoundary(bool reverse)
    {
        FrameworkElement? firstContent = CategoryList.SelectedIndex switch
        {
            0 => CloseBehaviorCombo,
            1 => DefaultQualityCombo,
            2 => LyricAnnounceCheckBox,
            3 => DownloadPathBox,
            4 => DataDirBox,
            5 => EnableVoiceAnnouncementCheckBox,
            6 => HotkeyListBox,
            _ => null
        };
        if (!reverse && CategoryList.IsKeyboardFocusWithin)
            return firstContent is ListBox list
                ? list.FocusItemOrList()
                : firstContent?.Focus() == true;
        if (reverse && firstContent?.IsKeyboardFocusWithin == true)
            return FocusSelectedCategory(announceReturnFocus: true);
        return false;
    }

    private bool FocusSelectedCategory(bool announceReturnFocus = false)
    {
        if (announceReturnFocus && CategoryList.SelectedItem is ListBoxItem selected)
        {
            _lastFocusedElement = selected;
            _hasTrackedFocus = true;
        }

        if (!CategoryList.FocusItemOrList()) return false;
        if (announceReturnFocus
            && CategoryList.SelectedItem is ListBoxItem selectedItem)
        {
            Announce(
                AutomationProperties.GetName(selectedItem),
                AnnouncementKind.Status,
                "settings",
                AnnouncementSpeech.None);
        }
        return true;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_savingClose || !CommitPendingPaths()) return;
        _savingClose = true;
        CloseSaveAttempts++;
        var result = await _settings.SaveAsync(_settings.Current);
        if (!result.Succeeded)
            Announce("设置保存失败，当前修改仍然生效，将在退出应用时重试。", AnnouncementKind.Error);
        _allowClose = true;
        _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.Normal);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        Loaded -= Window_Loaded;
        _settings.SettingsChanged -= Settings_SettingsChanged;
        if (_inputSuppressed) _setInputSuppressed(false);
        _inputSuppressed = false;
        _recording = null;
    }

    private void Announce(
        string text,
        AnnouncementKind kind = AnnouncementKind.Status,
        string? activityId = "settings",
        AnnouncementSpeech speech = AnnouncementSpeech.FollowPreference) =>
        _announcer.Announce(new(text, kind, activityId, speech));
}
