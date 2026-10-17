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
    private readonly IPlaybackCacheCleanup? _playbackCache;
    private readonly Func<KeyEventArgs, bool> _handleHotkey;

    private readonly ListBox _hotkeys = WinFormsUi.ListBox("快捷键", 2);
    private readonly ComboBox _closeBehavior = WinFormsUi.Combo("关闭行为", 1, "关闭应用", "隐藏到托盘");
    private static readonly string[] DefaultQualityIds = ["128k", "192k", "320k", "flac", "hires"];
    private static readonly string[] DefaultQualityLabels = DefaultQualityIds.Select(QualityCatalog.Label).ToArray();

    private readonly ComboBox _quality = WinFormsUi.Combo("默认播放音质", 2, DefaultQualityLabels);
    private readonly ComboBox _playMode = WinFormsUi.Combo("默认播放模式", 3, "顺序播放", "列表循环", "单曲循环", "随机播放");
    private readonly TextBox _playerPath = new();
    private readonly CheckBox _lyricAnnounce = new() { Text = "自动播报歌词" };
    private readonly CheckBox _lyricScroll = new() { Text = "自动滚动歌词" };
    private readonly TextBox _downloadPath = new();
    private readonly ComboBox _downloadQuality = WinFormsUi.Combo("默认下载音质", 7, DefaultQualityLabels);
    private readonly CheckBox _downloadLyric = new() { Text = "同时下载歌词" };
    private readonly CheckBox _nameQuality = new() { Text = "文件名包含音质" };
    private readonly ComboBox _nameOrder = WinFormsUi.Combo("文件名顺序", 10, "歌手 - 标题", "标题 - 歌手");
    private readonly NumericUpDown _batchDelay = new()
    {
        Minimum = 0,
        Maximum = 60,
        Value = 2,
        DecimalPlaces = 0
    };
    private readonly NumericUpDown _downloadSpeed = new()
    {
        Minimum = 0,
        Maximum = 100000,
        Value = 0,
        DecimalPlaces = 0
    };
    private readonly TextBox _dataDir = new() { ReadOnly = true };
    private readonly CheckBox _voiceEnabled = new() { Text = "启用语音朗读提示" };
    private readonly Label _backend = new() { AutoSize = true, Name = "SpeechBackend", TabStop = false };
    private readonly ComboBox _voice = WinFormsUi.Combo("朗读声音", 13);
    private readonly ComboBox _rate = WinFormsUi.Combo("朗读语速", 14, "0.5", "0.75", "1", "1.25", "1.5", "2");
    private readonly GroupBox _speechScope = new()
    {
        Name = "SpeechScope",
        AccessibleName = "语音提示范围",
        Text = "语音提示范围",
        TabStop = false
    };
    private readonly CheckBox _announcePlaybackState = new() { Text = "播放状态", UseVisualStyleBackColor = true };
    private readonly CheckBox _announceTrackChange = new() { Text = "切歌播报", UseVisualStyleBackColor = true };
    private readonly CheckBox _announceVolume = new() { Text = "音量变化", UseVisualStyleBackColor = true };
    private readonly CheckBox _announceSeek = new() { Text = "快进快退位置", UseVisualStyleBackColor = true };
    private readonly CheckBox _announceWindowVisibility = new() { Text = "窗口显隐", UseVisualStyleBackColor = true };
    private readonly CheckBox _announcePlaybackMode = new() { Text = "播放模式", UseVisualStyleBackColor = true };
    private readonly CheckBox _announceAudioDevice = new() { Text = "播放设备", UseVisualStyleBackColor = true };
    private HotkeySettingItem? _recording;
    private bool _loading = true;

    public SettingsForm(
        IAccessibleAnnouncer announcer,
        SettingsWorkflow settings,
        ITtsService tts,
        IDownloadWorkflow downloads,
        Func<KeyEventArgs, bool> handleHotkey,
        Hotkey? reconfigure = null,
        IPlaybackCacheCleanup? playbackCache = null)
    {
        _announcer = announcer;
        _settings = settings;
        _tts = tts;
        _downloads = downloads;
        _playbackCache = playbackCache;
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
        // The voice list has to exist before the stored settings are read into the controls: the saved
        // voice identifier can only be selected against the list, and a list built afterwards left the
        // combo box on its first row and overwrote the choice the user had made.
        PopulateVoices();
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
        _backend.Text = "朗读后端：" + tts.ActiveBackendName + "（自动检测）";
    }

    internal bool IsRecordingHotkey => _recording is not null;

    /// <summary>
    /// Every row carries the native voice identifier behind its display text, so 朗读声音 stores the value
    /// that the speech backend can resolve and selects the same row again when the window is reopened.
    /// The first row means "let the backend choose" and stores no identifier.
    /// </summary>
    private void PopulateVoices()
    {
        _voice.Items.Add(new ObjectBox<VoiceInfo>("自动检测（推荐）", new VoiceInfo()));
        foreach (var voice in _tts.GetAvailableVoices())
            _voice.Items.Add(new ObjectBox<VoiceInfo>(voice.Name + "（" + voice.Culture + "）", voice));
    }

    private string SelectedVoiceId() =>
        _voice.SelectedItem is ObjectBox<VoiceInfo> box ? box.Value.Id : string.Empty;

    private int VoiceIndexOf(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId)) return 0;
        for (var index = 1; index < _voice.Items.Count; index++)
        {
            if (_voice.Items[index] is ObjectBox<VoiceInfo> box
                && string.Equals(box.Value.Id, voiceId, StringComparison.Ordinal)) return index;
        }

        return 0;
    }

    private TabPage CreateGeneral()
    {
        var page = new TabPage("常规") { Padding = new Padding(12) };
        var caption = WinFormsUi.Caption("关闭行为：", 1);
        var playerCaption = WinFormsUi.Caption("第三方播放器路径：", 3);
        var browse = new Button { Text = "浏览", Width = 80 };
        WinFormsUi.Name(browse, "浏览第三方播放器", 4);
        caption.SetBounds(12, 18, 0, 0);
        _closeBehavior.SetBounds(100, 14, 180, 24);
        WinFormsUi.Name(_playerPath, "第三方播放器路径", 2);
        playerCaption.SetBounds(12, 55, 0, 0);
        _playerPath.SetBounds(12, 75, 560, 24);
        browse.SetBounds(580, 74, 80, 26);
        _playerPath.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            CommitPath(settings => settings.ThirdPartyPlayerPath = _playerPath.Text.Trim());
        };
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
        _playMode.SelectedIndexChanged += (_, _) => Apply(settings => settings.DefaultPlayMode = PlayModeFromIndex(_playMode.SelectedIndex));
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
        WinFormsUi.Name(_downloadPath, "下载目录", 2);
        WinFormsUi.Name(_downloadLyric, "同时下载歌词", 8);
        WinFormsUi.Name(_nameQuality, "文件名包含音质", 9);
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
        var delayCaption = WinFormsUi.Caption("批量下载间隔：", 11);
        var speedCaption = WinFormsUi.Caption("下载限速：", 13);
        WinFormsUi.Name(_batchDelay, "批量下载间隔", 12);
        WinFormsUi.Name(_downloadSpeed, "下载限速", 14);
        _batchDelay.AccessibleName = "批量下载间隔（秒）";
        _downloadSpeed.AccessibleName = "下载限速（KB/s，0 表示不限速）";
        delayCaption.SetBounds(12, 206, 0, 0);
        _batchDelay.SetBounds(12, 226, 180, 24);
        speedCaption.SetBounds(12, 262, 0, 0);
        _downloadSpeed.SetBounds(12, 282, 180, 24);
        page.Controls.AddRange(new Control[] { pathCaption, _downloadPath, browse, qualityCaption, _downloadQuality, _downloadLyric, _nameQuality, orderCaption, _nameOrder, delayCaption, _batchDelay, speedCaption, _downloadSpeed });
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择歌曲下载目录", SelectedPath = _downloads.ResolveDownloadDirectory(_downloadPath.Text) };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _downloadPath.Text = dialog.SelectedPath;
            if (Apply(settings => settings.DownloadPath = dialog.SelectedPath)) Announce("下载目录已设置为 " + dialog.SelectedPath, "settings", AnnouncementKind.Success);
        };
        _downloadPath.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            CommitDownloadPath();
        };
        _downloadQuality.SelectedIndexChanged += (_, _) => Apply(settings => settings.DefaultDownloadQuality = QualityId(_downloadQuality.Text, "128k"));
        _downloadLyric.CheckedChanged += (_, _) => Apply(settings => settings.DownloadLyric = _downloadLyric.Checked);
        _nameQuality.CheckedChanged += (_, _) => Apply(settings => settings.DownloadNameIncludeQuality = _nameQuality.Checked);
        _nameOrder.SelectedIndexChanged += (_, _) => Apply(settings => settings.DownloadNameOrder = _nameOrder.SelectedIndex == 1 ? DownloadNameOrder.TitleArtist : DownloadNameOrder.ArtistTitle);
        _batchDelay.ValueChanged += (_, _) =>
        {
            if (Apply(settings => settings.BatchDownloadDelaySeconds = (int)_batchDelay.Value))
                Announce($"批量下载间隔已设置为 {_batchDelay.Value} 秒", "settings", AnnouncementKind.Status);
        };
        _downloadSpeed.ValueChanged += (_, _) =>
        {
            var requestedKbps = (int)_downloadSpeed.Value;
            if (Apply(settings => settings.DownloadSpeedLimitKbps = requestedKbps))
            {
                var acceptedKbps = _settings.Current.Settings.DownloadSpeedLimitKbps;
                if (_downloadSpeed.Value != acceptedKbps)
                {
                    _loading = true;
                    _downloadSpeed.Value = acceptedKbps;
                    _loading = false;
                }
                Announce(acceptedKbps == 0 ? "下载限速已关闭" : $"下载限速已设置为 {acceptedKbps} KB/s", "settings", AnnouncementKind.Status);
            }
        };
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
        WinFormsUi.Name(_dataDir, "数据目录", 1);
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
            var external = _downloads.ClearTemporaryFiles();
            var playback = _playbackCache?.ClearPlaybackCache() ?? new(0, 0, 0);
            var deleted = external.DeletedFiles + playback.DeletedFiles;
            var skipped = external.SkippedFiles + playback.SkippedFiles;
            var freedBytes = external.FreedBytes + playback.FreedBytes;
            string message;
            if (deleted == 0 && skipped == 0)
                message = "没有可清理的缓存文件。";
            else
            {
                message = $"已清理缓存 {deleted} 个文件，释放 {FormatBytes(freedBytes)}。";
                if (skipped > 0)
                    message += $"{skipped} 个文件被占用，已保留。";
            }
            Announce(message, "settings", AnnouncementKind.Success);
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
        WinFormsUi.Name(_announcePlaybackState, "播放状态", 15);
        _announcePlaybackState.Name = "AnnouncePlaybackState";
        WinFormsUi.Name(_announceTrackChange, "切歌播报", 16);
        _announceTrackChange.Name = "AnnounceTrackChange";
        WinFormsUi.Name(_announceVolume, "音量变化", 17);
        _announceVolume.Name = "AnnounceVolume";
        WinFormsUi.Name(_announceSeek, "快进快退位置", 18);
        _announceSeek.Name = "AnnounceSeek";
        WinFormsUi.Name(_announceWindowVisibility, "窗口显隐", 19);
        _announceWindowVisibility.Name = "AnnounceWindowVisibility";
        WinFormsUi.Name(_announcePlaybackMode, "播放模式", 20);
        _announcePlaybackMode.Name = "AnnouncePlaybackMode";
        WinFormsUi.Name(_announceAudioDevice, "播放设备", 21);
        _announceAudioDevice.Name = "AnnounceAudioDevice";
        _speechScope.SetBounds(12, 140, 520, 150);
        _announcePlaybackState.SetBounds(15, 25, 230, 24);
        _announceTrackChange.SetBounds(265, 25, 230, 24);
        _announceVolume.SetBounds(15, 55, 230, 24);
        _announceSeek.SetBounds(265, 55, 230, 24);
        _announceWindowVisibility.SetBounds(15, 85, 230, 24);
        _announcePlaybackMode.SetBounds(265, 85, 230, 24);
        _announceAudioDevice.SetBounds(15, 115, 230, 24);
        _speechScope.Controls.AddRange(new Control[]
        {
            _announcePlaybackState, _announceTrackChange, _announceVolume, _announceSeek,
            _announceWindowVisibility, _announcePlaybackMode, _announceAudioDevice
        });
        page.Controls.AddRange(new Control[] { _voiceEnabled, _backend, voiceCaption, _voice, rateCaption, _rate, _speechScope });
        _voiceEnabled.CheckedChanged += (_, _) =>
        {
            if (Apply(settings => settings.EnableVoiceAnnouncement = _voiceEnabled.Checked))
                Announce(_voiceEnabled.Checked ? "已开启语音朗读提示" : "已关闭语音朗读提示", "settings", AnnouncementKind.Status, AnnouncementSpeech.Always);
        };
        _voice.SelectedIndexChanged += (_, _) => Apply(settings => settings.AccessibilitySettings.AnnouncementVoiceId = SelectedVoiceId());
        _rate.SelectedIndexChanged += (_, _) => { if (double.TryParse(_rate.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)) Apply(settings => settings.AccessibilitySettings.AnnouncementRate = rate); };
        _announcePlaybackState.CheckedChanged += (_, _) => Apply(settings => settings.AccessibilitySettings.AnnouncePlaybackState = _announcePlaybackState.Checked);
        _announceTrackChange.CheckedChanged += (_, _) => Apply(settings => settings.AccessibilitySettings.AnnounceTrackChange = _announceTrackChange.Checked);
        _announceVolume.CheckedChanged += (_, _) => Apply(settings => settings.AccessibilitySettings.AnnounceVolume = _announceVolume.Checked);
        _announceSeek.CheckedChanged += (_, _) => Apply(settings => settings.AccessibilitySettings.AnnounceSeek = _announceSeek.Checked);
        _announceWindowVisibility.CheckedChanged += (_, _) => Apply(settings => settings.AccessibilitySettings.AnnounceWindowVisibility = _announceWindowVisibility.Checked);
        _announcePlaybackMode.CheckedChanged += (_, _) => Apply(settings => settings.AccessibilitySettings.AnnouncePlaybackMode = _announcePlaybackMode.Checked);
        _announceAudioDevice.CheckedChanged += (_, _) => Apply(settings => settings.AccessibilitySettings.AnnounceAudioDevice = _announceAudioDevice.Checked);
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
        _playMode.SelectedIndex = PlayModeIndex(settings.DefaultPlayMode);
        _playerPath.Text = settings.ThirdPartyPlayerPath ?? string.Empty;
        _lyricAnnounce.Checked = settings.LyricAutoAnnounce;
        _lyricScroll.Checked = settings.LyricAutoScroll;
        _downloadPath.Text = settings.DownloadPath ?? string.Empty;
        _downloadQuality.SelectedIndex = Math.Min(QualityIndex(settings.DefaultDownloadQuality), _downloadQuality.Items.Count - 1);
        _downloadLyric.Checked = settings.DownloadLyric;
        _nameQuality.Checked = settings.DownloadNameIncludeQuality;
        _nameOrder.SelectedIndex = settings.DownloadNameOrder == DownloadNameOrder.TitleArtist ? 1 : 0;
        _batchDelay.Value = Clamp(settings.BatchDownloadDelaySeconds, 0, 60);
        _downloadSpeed.Value = Clamp(settings.DownloadSpeedLimitKbps, 0, 100000);
        _dataDir.Text = _settings.DataDirectory;
        _voiceEnabled.Checked = settings.EnableVoiceAnnouncement;
        _rate.Text = settings.AccessibilitySettings.AnnouncementRate.ToString("0.##", CultureInfo.InvariantCulture);
        _voice.SelectedIndex = VoiceIndexOf(settings.AccessibilitySettings.AnnouncementVoiceId);
        _announcePlaybackState.Checked = settings.AccessibilitySettings.AnnouncePlaybackState;
        _announceTrackChange.Checked = settings.AccessibilitySettings.AnnounceTrackChange;
        _announceVolume.Checked = settings.AccessibilitySettings.AnnounceVolume;
        _announceSeek.Checked = settings.AccessibilitySettings.AnnounceSeek;
        _announceWindowVisibility.Checked = settings.AccessibilitySettings.AnnounceWindowVisibility;
        _announcePlaybackMode.Checked = settings.AccessibilitySettings.AnnouncePlaybackMode;
        _announceAudioDevice.Checked = settings.AccessibilitySettings.AnnounceAudioDevice;
    }

    private static int Clamp(int value, int minimum, int maximum) => value < minimum ? minimum : value > maximum ? maximum : value;

    private static int PlayModeIndex(PlayMode? mode) => mode switch
    {
        PlayMode.Loop => 1,
        PlayMode.SingleLoop => 2,
        PlayMode.Shuffle => 3,
        _ => 0
    };

    private static PlayMode PlayModeFromIndex(int index) => index switch
    {
        1 => PlayMode.Loop,
        2 => PlayMode.SingleLoop,
        3 => PlayMode.Shuffle,
        _ => PlayMode.Sequential
    };

    private static int QualityIndex(string? quality)
    {
        var id = QualityCatalog.Normalize(quality);
        var index = Array.IndexOf(DefaultQualityIds, id);
        if (index >= 0) return index;
        return id is "flac24bit" ? Array.IndexOf(DefaultQualityIds, "hires") : 0;
    }

    private static string QualityId(string display, string fallback)
    {
        var index = Array.IndexOf(DefaultQualityLabels, display);
        return index >= 0 ? DefaultQualityIds[index] : fallback;
    }

    private bool Apply(Action<UserSettings> edit)
    {
        if (_loading) return false;
        var result = _settings.Edit(edit);
        if (result.Accepted) return true;
        Announce(string.Join(" ", result.Errors), "settings", AnnouncementKind.Error);
        return false;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.#} KB";
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private void CommitPath(Action<UserSettings> edit, bool returnFocusOnError = true)
    {
        if (_loading) return;
        var result = _settings.Edit(edit);
        if (!result.Accepted)
        {
            if (returnFocusOnError) _playerPath.Focus();
            Announce(string.Join(" ", result.Errors), "settings", AnnouncementKind.Error);
            return;
        }
        var committed = _playerPath.Text.Trim();
        Announce(committed.Length == 0
            ? "第三方播放器路径已清空"
            : "第三方播放器路径已保存：" + committed, "settings", AnnouncementKind.Success);
    }

    private void CommitDownloadPath()
    {
        if (!Apply(settings => settings.DownloadPath = _downloadPath.Text.Trim())) return;
        var committed = _downloadPath.Text.Trim();
        Announce(committed.Length == 0
            ? "下载目录已清空"
            : "下载目录已设置为 " + committed, "settings", AnnouncementKind.Success);
    }

    /// <summary>
    /// A typed path only becomes a setting through an explicit command: Enter in the box, picking a file in
    /// the browse dialog, or closing the window. Moving focus away from a text box must not run work, so
    /// tabbing past the box no longer writes a half-finished value, and a path that was typed but never
    /// confirmed is still collected when the window closes instead of being silently lost.
    /// </summary>
    private void CommitTypedPaths()
    {
        var playerPath = _playerPath.Text.Trim();
        if (!string.Equals(playerPath, _settings.Current.Settings.ThirdPartyPlayerPath ?? string.Empty, StringComparison.Ordinal))
            CommitPath(settings => settings.ThirdPartyPlayerPath = playerPath, returnFocusOnError: false);
        var downloadPath = _downloadPath.Text.Trim();
        if (!string.Equals(downloadPath, _settings.Current.Settings.DownloadPath ?? string.Empty, StringComparison.Ordinal))
            CommitDownloadPath();
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
        CommitTypedPaths();
        var result = await _settings.SaveAsync(_settings.Current);
        if (!result.Succeeded) Announce("设置保存失败，将在应用退出时重试", "settings", AnnouncementKind.Error);
    }

    protected void Announce(string message, string activityId, AnnouncementKind kind = AnnouncementKind.Status, AnnouncementSpeech speech = AnnouncementSpeech.FollowPreference) =>
        _announcer.Announce(new AccessibleAnnouncement(message, kind, activityId, speech));
}
