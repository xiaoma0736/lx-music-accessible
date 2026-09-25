using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>Seven native settings categories; ordinary edits apply immediately and close persists.</summary>
public class SettingsForm : Form
{
    private readonly IAccessibleAnnouncer _announcer;
    private readonly SettingsWorkflow _settings;
    private readonly ITtsService _tts;
    private readonly IDownloadWorkflow _downloads;
    private readonly Func<KeyEventArgs, bool> _handleHotkey;
    private readonly ListBox _hotkeys = WinFormsUi.ListBox("快捷键", 20);
    private readonly ComboBox _closeBehavior = WinFormsUi.Combo("关闭行为", 1, "关闭应用", "隐藏到托盘");
    private readonly ComboBox _quality = WinFormsUi.Combo("默认播放音质", 2, "标准", "较高", "极高", "无损", "Hi-Res");
    private readonly ComboBox _playMode = WinFormsUi.Combo("默认播放模式", 3, "顺序播放", "列表循环", "单曲循环", "随机播放");
    private readonly TextBox _playerPath = new() { AccessibleName = "第三方播放器路径" };
    private readonly CheckBox _lyricAnnounce = new() { Text = "自动播报歌词" };
    private readonly CheckBox _lyricScroll = new() { Text = "自动滚动歌词" };
    private readonly TextBox _downloadPath = new() { AccessibleName = "下载目录" };
    private readonly ComboBox _downloadQuality = WinFormsUi.Combo("默认下载音质", 7, "标准", "较高", "极高", "无损", "Hi-Res");
    private readonly CheckBox _downloadLyric = new() { Text = "同时下载歌词" };
    private readonly CheckBox _nameQuality = new() { Text = "文件名包含音质" };
    private readonly ComboBox _nameOrder = WinFormsUi.Combo("文件名顺序", 10, "歌手 - 标题", "标题 - 歌手");
    private readonly TextBox _dataDir = new() { ReadOnly = true, AccessibleName = "数据目录" };
    private readonly CheckBox _voiceEnabled = new() { Text = "启用语音朗读提示" };
    private readonly Label _backend = new() { AutoSize = true, TabStop = false };
    private readonly ComboBox _voice = WinFormsUi.Combo("朗读声音", 13);
    private readonly ComboBox _rate = WinFormsUi.Combo("朗读语速", 14, "0.5", "0.75", "1", "1.25", "1.5", "2");
    private HotkeySettingItem? _recording;
    private bool _loading = true;

    public SettingsForm(
        IAccessibleAnnouncer announcer,
        SettingsWorkflow settings,
        ITtsService tts,
        IDownloadWorkflow downloads,
        Func<KeyEventArgs, bool> handleHotkey,
        Action<bool> setInputSuppressed,
        Hotkey? reconfigure = null)
    {
        _announcer = announcer;
        _settings = settings;
        _tts = tts;
        _downloads = downloads;
        _handleHotkey = handleHotkey;
        Text = "设置";
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        Size = new Size(850, 640);
        var tabs = new TabControl { Dock = DockStyle.Fill, TabIndex = 0, AccessibleName = "设置分类" };
        tabs.TabPages.Add(CreateGeneral());
        tabs.TabPages.Add(CreatePlayback());
        tabs.TabPages.Add(CreateLyrics());
        tabs.TabPages.Add(CreateDownload());
        tabs.TabPages.Add(CreateStorage());
        tabs.TabPages.Add(CreateAccessibility());
        tabs.TabPages.Add(CreateHotkeys());
        Controls.Add(tabs);
        KeyPreview = true;
        LoadControls(settings.Current.Settings);
        BuildHotkeys();
        tabs.SelectedIndex = reconfigure is null ? 0 : 6;
        _loading = false;
        settings.SettingsChanged += (_, e) => BeginInvoke(() => { _loading = true; LoadControls(e.Current.Settings); BuildHotkeys(); _loading = false; });
        Shown += (_, _) =>
        {
            if (reconfigure is null) { tabs.Focus(); return; }
            var item = _hotkeys.Items.Cast<ObjectBox<HotkeySettingItem>>().FirstOrDefault(item => item.Value.ActionId == reconfigure.ActionId && item.Value.IsGlobal == reconfigure.IsGlobal);
            if (item is null) return;
            _hotkeys.SelectedItem = item;
            _hotkeys.Focus();
            StartRecording(item.Value);
        };
        _voice.Items.Add("自动检测（推荐）");
        foreach (var voice in tts.GetAvailableVoices()) _voice.Items.Add($"{voice.Name}（{voice.Culture}）");
        _voice.SelectedIndex = 0;
        _backend.Text = "朗读后端：" + tts.ActiveBackendName + "（自动检测）";
    }

    internal bool IsRecordingHotkey => _recording is not null;

    private TabPage CreateGeneral()
    {
        var page = new TabPage("常规") { Padding = new Padding(12) };
        var caption = WinFormsUi.Caption("关闭行为：", 1);
        var playerCaption = WinFormsUi.Caption("第三方播放器路径：", 3);
        var browse = new Button { Text = "浏览", Width = 80 };
        WinFormsUi.Name(browse, "浏览第三方播放器", 4);
        caption.SetBounds(12, 18, 0, 0);
        _closeBehavior.SetBounds(100, 14, 180, 24);
        playerCaption.SetBounds(12, 55, 0, 0);
        _playerPath.SetBounds(12, 75, 560, 24);
        browse.SetBounds(580, 74, 80, 26);
        _playerPath.LostFocus += (_, _) => CommitPath(settings => settings.ThirdPartyPlayerPath = _playerPath.Text.Trim());
        browse.Click += (_, _) => BrowsePath(_playerPath, "选择第三方播放器", "程序|*.exe|所有文件|*.*", settings => settings.ThirdPartyPlayerPath = _playerPath.Text.Trim());
        page.Controls.AddRange(new Control[] { caption, _closeBehavior, playerCaption, _playerPath, browse });
        _closeBehavior.SelectedIndexChanged += (_, _) => Apply(settings => settings.HideToTrayOnAltF4 = _closeBehavior.SelectedIndex == 1);
        return page;
    }

    private TabPage CreatePlayback()
    {
        var page = new TabPage("播放") { Padding = new Padding(12) };
        var qualityCaption = WinFormsUi.Caption("默认播放音质：", 1);
        var modeCaption = WinFormsUi.Caption("默认播放模式：", 3);
        qualityCaption.SetBounds(12, 18, 0, 0);
        _quality.SetBounds(120, 14, 180, 24);
        modeCaption.SetBounds(12, 55, 0, 0);
        _playMode.SetBounds(120, 51, 180, 24);
        page.Controls.AddRange(new Control[] { qualityCaption, _quality, modeCaption, _playMode });
        _quality.SelectedIndexChanged += (_, _) => Apply(settings => settings.DefaultPlayQuality = QualityId(_quality.Text, "128k"));
        _playMode.SelectedIndexChanged += (_, _) => Apply(settings => settings.DefaultPlayMode = (PlayMode)_playMode.SelectedIndex);
        return page;
    }

    private TabPage CreateLyrics()
    {
        var page = new TabPage("歌词") { Padding = new Padding(12) };
        _lyricAnnounce.SetBounds(12, 15, 300, 24);
        _lyricScroll.SetBounds(12, 50, 300, 24);
        WinFormsUi.Name(_lyricAnnounce, "自动播报歌词", 1);
        WinFormsUi.Name(_lyricScroll, "自动滚动歌词", 2);
        page.Controls.AddRange(new Control[] { _lyricAnnounce, _lyricScroll });
        _lyricAnnounce.CheckedChanged += (_, _) => Apply(settings => settings.LyricAutoAnnounce = _lyricAnnounce.Checked);
        _lyricScroll.CheckedChanged += (_, _) => Apply(settings => settings.LyricAutoScroll = _lyricScroll.Checked);
        return page;
    }

    private TabPage CreateDownload()
    {
        var page = new TabPage("下载") { Padding = new Padding(12) };
        var pathCaption = WinFormsUi.Caption("下载目录：", 1);
        var browse = new Button { Text = "浏览", Width = 80 };
        WinFormsUi.Name(browse, "浏览下载目录", 3);
        var qualityCaption = WinFormsUi.Caption("默认下载音质：", 4);
        pathCaption.SetBounds(12, 18, 0, 0);
        _downloadPath.SetBounds(12, 38, 560, 24);
        browse.SetBounds(580, 37, 80, 26);
        qualityCaption.SetBounds(12, 76, 0, 0);
        _downloadQuality.SetBounds(110, 72, 180, 24);
        _downloadLyric.SetBounds(12, 108, 300, 24);
        _nameQuality.SetBounds(12, 140, 300, 24);
        var orderCaption = WinFormsUi.Caption("文件名顺序：", 9);
        orderCaption.SetBounds(12, 174, 0, 0);
        _nameOrder.SetBounds(105, 170, 180, 24);
        page.Controls.AddRange(new Control[] { pathCaption, _downloadPath, browse, qualityCaption, _downloadQuality, _downloadLyric, _nameQuality, orderCaption, _nameOrder });
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择歌曲下载目录", SelectedPath = _downloads.ResolveDownloadDirectory(_downloadPath.Text) };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _downloadPath.Text = dialog.SelectedPath;
            if (Apply(settings => settings.DownloadPath = dialog.SelectedPath)) Announce("下载目录已设置为 " + dialog.SelectedPath, "settings", AnnouncementKind.Success);
        };
        _downloadPath.LostFocus += (_, _) => Apply(settings => settings.DownloadPath = _downloadPath.Text.Trim());
        _downloadQuality.SelectedIndexChanged += (_, _) => Apply(settings => settings.DefaultDownloadQuality = QualityId(_downloadQuality.Text, "128k"));
        _downloadLyric.CheckedChanged += (_, _) => Apply(settings => settings.DownloadLyric = _downloadLyric.Checked);
        _nameQuality.CheckedChanged += (_, _) => Apply(settings => settings.DownloadNameIncludeQuality = _nameQuality.Checked);
        _nameOrder.SelectedIndexChanged += (_, _) => Apply(settings => settings.DownloadNameOrder = _nameOrder.SelectedIndex == 1 ? DownloadNameOrder.TitleArtist : DownloadNameOrder.ArtistTitle);
        return page;
    }

    private TabPage CreateStorage()
    {
        var page = new TabPage("存储与缓存") { Padding = new Padding(12) };
        var caption = WinFormsUi.Caption("数据目录：", 1);
        var browse = new Button { Text = "更改", Width = 80 };
        WinFormsUi.Name(browse, "更改数据目录", 2);
        var clear = new Button { Text = "清理缓存", Width = 100 };
        WinFormsUi.Name(clear, "清理缓存", 3);
        caption.SetBounds(12, 18, 0, 0);
        _dataDir.SetBounds(12, 38, 560, 24);
        browse.SetBounds(580, 37, 80, 26);
        clear.SetBounds(12, 74, 100, 28);
        page.Controls.AddRange(new Control[] { caption, _dataDir, browse, clear });
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择数据目录" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var result = _settings.ChangeDataDirectory(dialog.SelectedPath);
            _dataDir.Text = result.Path;
            Announce(result.Message, "settings", result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
        };
        clear.Click += (_, _) =>
        {
            var result = _downloads.ClearTemporaryFiles();
            Announce(result.DeletedFiles == 0 ? "没有可清理的缓存文件。" : $"已清理缓存 {result.DeletedFiles} 个文件。", "settings", AnnouncementKind.Success);
        };
        return page;
    }

    private TabPage CreateAccessibility()
    {
        var page = new TabPage("无障碍") { Padding = new Padding(12) };
        _voiceEnabled.SetBounds(12, 15, 300, 24);
        _backend.SetBounds(12, 50, 0, 0);
        var voiceCaption = WinFormsUi.Caption("朗读声音：", 4);
        var rateCaption = WinFormsUi.Caption("朗读语速：", 6);
        WinFormsUi.Name(_voiceEnabled, "启用语音朗读提示", 1);
        _backend.TabStop = false;
        voiceCaption.SetBounds(12, 78, 0, 0);
        _voice.SetBounds(90, 74, 250, 24);
        rateCaption.SetBounds(12, 112, 0, 0);
        _rate.SetBounds(90, 108, 120, 24);
        page.Controls.AddRange(new Control[] { _voiceEnabled, _backend, voiceCaption, _voice, rateCaption, _rate });
        _voiceEnabled.CheckedChanged += (_, _) =>
        {
            if (Apply(settings => settings.EnableVoiceAnnouncement = _voiceEnabled.Checked))
                Announce(_voiceEnabled.Checked ? "已开启语音朗读提示" : "已关闭语音朗读提示", "settings", AnnouncementKind.Status, AnnouncementSpeech.Always);
        };
        _voice.SelectedIndexChanged += (_, _) => Apply(settings => settings.AccessibilitySettings.AnnouncementVoiceId = _voice.SelectedIndex == 0 ? string.Empty : _voice.Text);
        _rate.SelectedIndexChanged += (_, _) => { if (double.TryParse(_rate.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)) Apply(settings => settings.AccessibilitySettings.AnnouncementRate = rate); };
        return page;
    }

    private TabPage CreateHotkeys()
    {
        var page = new TabPage("快捷键") { Padding = new Padding(12) };
        var hint = WinFormsUi.Caption("选择后按 Enter 录制，Delete 清除，Esc 取消录制。", 1);
        hint.SetBounds(12, 15, 0, 0);
        _hotkeys.SetBounds(12, 40, 620, 380);
        var reset = new Button { Text = "恢复默认", Width = 100 };
        WinFormsUi.Name(reset, "恢复默认快捷键", 3);
        reset.SetBounds(12, 430, 100, 28);
        page.Controls.AddRange(new Control[] { hint, _hotkeys, reset });
        _hotkeys.KeyDown += (_, e) =>
        {
            if (_hotkeys.SelectedItem is not ObjectBox<HotkeySettingItem> box) return;
            if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; StartRecording(box.Value); }
            else if (e.KeyCode == Keys.Delete) { e.Handled = true; _ = ClearHotkey(box.Value); }
        };
        reset.Click += async (_, _) =>
        {
            var result = await _settings.ResetHotkeysAsync();
            Announce(result.Applied ? "快捷键已恢复默认" : StatusMessage("快捷键恢复默认", result), "settings", result.Applied ? AnnouncementKind.Success : AnnouncementKind.Error);
        };
        return page;
    }

    private void LoadControls(UserSettings settings)
    {
        _closeBehavior.SelectedIndex = settings.HideToTrayOnAltF4 ? 1 : 0;
        _quality.SelectedIndex = Math.Min(QualityIndex(settings.DefaultPlayQuality), _quality.Items.Count - 1);
        _playMode.SelectedIndex = (int)(settings.DefaultPlayMode ?? PlayMode.Sequential);
        _playerPath.Text = settings.ThirdPartyPlayerPath ?? string.Empty;
        _lyricAnnounce.Checked = settings.LyricAutoAnnounce;
        _lyricScroll.Checked = settings.LyricAutoScroll;
        _downloadPath.Text = settings.DownloadPath ?? string.Empty;
        _downloadQuality.SelectedIndex = Math.Min(QualityIndex(settings.DefaultDownloadQuality), _downloadQuality.Items.Count - 1);
        _downloadLyric.Checked = settings.DownloadLyric;
        _nameQuality.Checked = settings.DownloadNameIncludeQuality;
        _nameOrder.SelectedIndex = settings.DownloadNameOrder == DownloadNameOrder.TitleArtist ? 1 : 0;
        _dataDir.Text = _settings.DataDirectory;
        _voiceEnabled.Checked = settings.EnableVoiceAnnouncement;
        _rate.Text = settings.AccessibilitySettings.AnnouncementRate.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static int QualityIndex(string? quality) => quality switch
    {
        "192k" => 1,
        "320k" => 2,
        "flac" => 3,
        "flac24bit" or "hires" => 4,
        _ => 0
    };

    private static string QualityId(string display, string fallback) => display switch
    {
        "较高" => "192k",
        "极高" => "320k",
        "无损" => "flac",
        "Hi-Res" => "hires",
        _ => fallback
    };

    private bool Apply(Action<UserSettings> edit)
    {
        if (_loading) return false;
        var result = _settings.Edit(edit);
        if (result.Accepted) return true;
        Announce(string.Join(" ", result.Errors), "settings", AnnouncementKind.Error);
        return false;
    }

    private void CommitPath(Action<UserSettings> edit)
    {
        if (_loading) return;
        var result = _settings.Edit(edit);
        if (!result.Accepted)
        {
            _playerPath.Focus();
            Announce(string.Join(" ", result.Errors), "settings", AnnouncementKind.Error);
        }
    }

    private void BrowsePath(TextBox target, string title, string filter, Action<UserSettings> edit)
    {
        using var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        target.Text = dialog.FileName;
        Apply(edit);
        Announce("路径已设置为 " + dialog.FileName, "settings", AnnouncementKind.Success);
    }

    private void BuildHotkeys()
    {
        var selected = WinFormsUi.Selected<HotkeySettingItem>(_hotkeys);
        _hotkeys.Items.Clear();
        foreach (var hotkey in _settings.GetHotkeys())
        {
            var display = hotkey.IsGlobal ? hotkey.Name : hotkey.Name + "（局部）";
            var text = $"{display}，{FormatHotkey(hotkey)}";
            if (hotkey.IsGlobal && hotkey.IsEnabled && !_settings.IsGlobalHotkeyRegistered(hotkey)) text += "，未注册";
            var settingItem = new HotkeySettingItem
            {
                ActionId = hotkey.ActionId,
                ActionName = hotkey.Name,
                IsGlobal = hotkey.IsGlobal,
                IsEnabled = hotkey.IsEnabled
            };
            _hotkeys.Items.Add(new ObjectBox<HotkeySettingItem>(text, settingItem));
        }
        if (selected is not null)
        {
            var match = _hotkeys.Items.Cast<ObjectBox<HotkeySettingItem>>().FirstOrDefault(item => item.Value.ActionId == selected.ActionId && item.Value.IsGlobal == selected.IsGlobal);
            if (match is not null) _hotkeys.SelectedItem = match;
        }
    }

    private static string FormatHotkey(Hotkey hotkey)
    {
        if (!hotkey.IsEnabled || (PlatformKey)hotkey.Key == PlatformKey.None) return "未设置";
        var modifiers = (PlatformModifiers)hotkey.Modifiers;
        var parts = new List<string>();
        if ((modifiers & PlatformModifiers.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & PlatformModifiers.Alt) != 0) parts.Add("Alt");
        if ((modifiers & PlatformModifiers.Shift) != 0) parts.Add("Shift");
        if ((modifiers & PlatformModifiers.Windows) != 0) parts.Add("Win");
        parts.Add(((PlatformKey)hotkey.Key).ToString());
        return string.Join("+", parts);
    }

    private void StartRecording(HotkeySettingItem item)
    {
        _recording = item;
        Announce($"请按{item.ActionName}的快捷键，Esc 取消，Delete 清除", "hotkey-recording");
    }

    private async Task ClearHotkey(HotkeySettingItem item)
    {
        _recording = null;
        var result = await _settings.ClearHotkeyAsync(item.ActionId, item.IsGlobal);
        Announce(result.Applied ? item.ActionName + "快捷键已清除" : StatusMessage(item.ActionName + "快捷键", result), "hotkey-recording", result.Applied ? AnnouncementKind.Success : AnnouncementKind.Error);
        _hotkeys.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_recording is null)
        {
            if (_handleHotkey(e)) { e.Handled = true; return; }
            if (e.KeyCode == Keys.Escape) Close();
            return;
        }
        if (e.KeyCode == Keys.Escape) { EndRecording("已取消快捷键录制"); return; }
        if (e.KeyCode == Keys.Delete) { var clearing = _recording; _ = ClearHotkey(clearing); return; }
        if (e.KeyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
        // The recorder sees Forms key numbers while native registration and dispatch need Win32 virtual
        // keys, so translate once here instead of persisting a number that binds the wrong native key.
        var key = KeyboardKeys.FromVirtualKey((uint)(e.KeyCode & Keys.KeyCode));
        if (key == PlatformKey.None) { EndRecording("此按键不能注册为快捷键"); return; }
        var modifiers = (e.Control ? PlatformModifiers.Control : PlatformModifiers.None) | (e.Alt ? PlatformModifiers.Alt : PlatformModifiers.None) | (e.Shift ? PlatformModifiers.Shift : PlatformModifiers.None);
        var target = _recording;
        _recording = null;
        _ = ReplaceHotkey(target, key, modifiers);
    }

    private async Task ReplaceHotkey(HotkeySettingItem item, PlatformKey key, PlatformModifiers modifiers)
    {
        var result = await _settings.ReplaceHotkeyAsync(item.ActionId, item.IsGlobal, key, modifiers);
        Announce(EditMessage(item, result), "hotkey-recording", result.Applied ? AnnouncementKind.Success : AnnouncementKind.Error);
        _hotkeys.Focus();
    }

    private static string EditMessage(HotkeySettingItem item, HotkeyEditResult result) =>
        StatusMessage((item.IsGlobal ? "全局" : string.Empty) + item.ActionName + "快捷键", result);

    /// <summary>
    /// One accessible sentence per edit outcome, so a rejected or rolled-back edit is never silent:
    /// conflicts name the other actions, a registration failure states the previous binding is intact,
    /// and a save failure says the binding works but the choice was not persisted.
    /// </summary>
    private static string StatusMessage(string subject, HotkeyEditResult result) => result.Status switch
    {
        HotkeyEditStatus.Applied => subject + "已更新",
        HotkeyEditStatus.Unchanged => subject + "未发生变化",
        HotkeyEditStatus.Invalid => subject + "无效：该按键不能注册为快捷键",
        HotkeyEditStatus.Conflict => subject + "与其他快捷键冲突：" + DescribeHotkeys(result.Conflicts),
        HotkeyEditStatus.RegistrationFailed => subject + "注册失败：系统可能已占用该按键，原有设置保持不变",
        HotkeyEditStatus.RollbackFailed => subject + "注册失败且无法恢复原有快捷键，请重新设置",
        HotkeyEditStatus.PersistenceFailed => subject + "已生效但保存失败，退出前请检查设置",
        _ => subject + "更新失败"
    };

    private static string DescribeHotkeys(IReadOnlyList<Hotkey> hotkeys) =>
        hotkeys.Count == 0 ? "无" : string.Join("、", hotkeys.Select(hotkey => string.IsNullOrWhiteSpace(hotkey.Name) ? hotkey.ActionId : hotkey.Name));

    private void EndRecording(string message)
    {
        _recording = null;
        Announce(message, "hotkey-recording");
        _hotkeys.Focus();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        var result = await _settings.SaveAsync(_settings.Current);
        if (!result.Succeeded) Announce("设置保存失败，将在应用退出时重试", "settings", AnnouncementKind.Error);
    }

    protected void Announce(string message, string activityId, AnnouncementKind kind = AnnouncementKind.Status, AnnouncementSpeech speech = AnnouncementSpeech.FollowPreference) =>
        _announcer.Announce(new AccessibleAnnouncement(message, kind, activityId, speech));
}
