using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.App;

/// <summary>Stores a native list item while making the whole row one screen-reader unit.</summary>
public sealed class ObjectBox<T>(string name, T value)
{
    public string Name { get; } = name;
    public T Value { get; } = value;
    public override string ToString() => Name;
}

internal static class WinFormsUi
{
    public static void Name(Control control, string name, int tabIndex, bool tabStop = true)
    {
        control.Name = name;
        control.AccessibleName = name;
        control.TabStop = tabStop;
        control.TabIndex = tabIndex;
    }

    public static Label Caption(string text, int tabIndex) => new()
    {
        Text = text,
        AutoSize = true,
        TabStop = false,
        TabIndex = tabIndex
    };

    public static ListBox ListBox(string name, int tabIndex) => new()
    {
        Name = name,        AccessibleName = name,
        TabStop = true,
        TabIndex = tabIndex,
        IntegralHeight = false,
        HorizontalScrollbar = true
    };

    public static ComboBox Combo(string name, int tabIndex, params string[] values)
    {
        var combo = new ComboBox
        {
            Name = name,
            AccessibleName = name,
            DropDownStyle = ComboBoxStyle.DropDownList,
            TabStop = true,
            TabIndex = tabIndex,
            Width = 150
        };
        combo.Items.AddRange(values);
        if (values.Length > 0) combo.SelectedIndex = 0;
        return combo;
    }

    public static T? Selected<T>(ListBox list) => list.SelectedItem is ObjectBox<T> box ? box.Value : default;

    public static IReadOnlyList<MusicInfo> Songs(ListBox list)
    {
        var selected = list.SelectedItems.Cast<object>().OfType<ObjectBox<MusicInfo>>().Select(item => item.Value).ToArray();
        return selected.Length > 0 ? selected : Selected<MusicInfo>(list) is { } song ? new[] { song } : Array.Empty<MusicInfo>();
    }

    public static void FillSongs(ListBox list, IEnumerable<MusicInfo> songs)
    {
        var selected = Selected<MusicInfo>(list)?.Id;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var song in songs) list.Items.Add(new ObjectBox<MusicInfo>(song.AccessibleName, song));
        list.EndUpdate();
        if (list.Items.Count > 0)
        {
            var index = selected is null ? 0 : Enumerable.Range(0, list.Items.Count)
                .Where(i => list.Items[i] is ObjectBox<MusicInfo> box && box.Value.Id == selected).DefaultIfEmpty(-1).First();
            list.SelectedIndex = index >= 0 ? index : 0;
        }
    }

    public static void Error(Control owner, Exception exception, string operation) => MessageBox.Show(
        owner.FindForm() ?? owner,
        operation + "失败：" + exception.Message,
        "落雪音乐无障碍版",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);

    public static bool Confirm(Control owner, string message, string title = "确认操作") => MessageBox.Show(
        owner.FindForm() ?? owner,
        message,
        title,
        MessageBoxButtons.YesNo,
        MessageBoxIcon.Question,
        MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public static string? AskText(Control owner, string title, string label, string? value = null)
    {
        using var form = new TextInputForm(title, new[] { (label, value ?? string.Empty) });
        return form.ShowDialog(owner.FindForm() ?? owner) == DialogResult.OK ? form.Values[0] : null;
    }
}

/// <summary>Native WinForms UIA notification plus optional TTS, preserving one announcement pipeline.</summary>
public sealed class WinFormsAccessibleAnnouncer : IAccessibleAnnouncer
{
    private readonly ITtsService? _speech;
    private readonly ILogger<WinFormsAccessibleAnnouncer> _logger;
    private Control? _liveRegion;
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 350 };
    private readonly Dictionary<string, AccessibleAnnouncement> _pendingProgress = new(StringComparer.Ordinal);
    private readonly HashSet<string> _terminal = new(StringComparer.Ordinal);

    public WinFormsAccessibleAnnouncer(IEnumerable<ITtsService> speech, ILogger<WinFormsAccessibleAnnouncer> logger)
    {
        _speech = speech.FirstOrDefault();
        _logger = logger;
        _progressTimer.Tick += (_, _) =>
        {
            _progressTimer.Stop();
            var pending = _pendingProgress.Values.ToArray();
            _pendingProgress.Clear();
            foreach (var announcement in pending) Deliver(announcement);
        };
    }

    public bool SpeechEnabled { get; set; } = true;

    public void Attach(Control liveRegion) => _liveRegion = liveRegion;

    public void Announce(AccessibleAnnouncement announcement)
    {
        if (string.IsNullOrWhiteSpace(announcement.Text) || _liveRegion is null || _liveRegion.IsDisposed) return;
        if (_liveRegion.InvokeRequired)
        {
            _liveRegion.BeginInvoke(() => Announce(announcement));
            return;
        }
        if (announcement.Kind == AnnouncementKind.Progress)
        {
            _pendingProgress[announcement.ActivityId ?? announcement.Text] = announcement;
            _progressTimer.Stop();
            _progressTimer.Start();
            return;
        }
        if (announcement.Kind is AnnouncementKind.Success or AnnouncementKind.Error && !string.IsNullOrEmpty(announcement.ActivityId))
        {
            _pendingProgress.Remove(announcement.ActivityId);
            if (!_terminal.Add(announcement.ActivityId)) return;
        }
        Deliver(announcement);
    }

    private void Deliver(AccessibleAnnouncement announcement)
    {
        var target = _liveRegion!;
        target.Text = announcement.Text;
        target.AccessibleName = announcement.Text;
        try
        {
            target.AccessibilityObject.RaiseAutomationNotification(
                System.Windows.Forms.Automation.AutomationNotificationKind.Other,
                System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent,
                announcement.Text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Accessible notification delivery failed");
        }
        if (ShouldSpeak(announcement)) _ = SpeakAsync(announcement);
    }

    private bool ShouldSpeak(AccessibleAnnouncement announcement) =>
        _speech?.IsAvailable == true
        && (SpeechEnabled || announcement.Speech == AnnouncementSpeech.Always)
        && AccessibleSpeechPolicy.ShouldSpeak(announcement);

    private async Task SpeakAsync(AccessibleAnnouncement announcement)
    {
        try
        {
            if (announcement.Speech == AnnouncementSpeech.Queued) await _speech!.SpeakQueuedAsync(announcement.Text);
            else await _speech!.SpeakAsync(announcement.Text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Optional TTS announcement failed");
        }
    }
}

/// <summary>Generic input dialog using native labels and TextBox controls.</summary>
public sealed class TextInputForm : Form
{
    public IReadOnlyList<string> Values { get; }

    public TextInputForm(string title, IReadOnlyList<(string Label, string Value)> inputs)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new System.Drawing.Size(460, 80 + inputs.Count * 64);
        var values = new List<string>(new string[inputs.Count]);
        Values = values;
        for (var index = 0; index < inputs.Count; index++)
        {
            var caption = Caption(inputs[index].Label, index * 2 + 1);
            caption.SetBounds(12, 10 + index * 64, 0, 0);
            var box = new TextBox { Text = inputs[index].Value };
            WinFormsUi.Name(box, inputs[index].Label.TrimEnd('：', ':'), index * 2 + 2);
            box.SetBounds(12, 30 + index * 64, 430, 23);
            Controls.Add(caption);
            Controls.Add(box);
            values[index] = box.Text;
            var captured = index;
            box.TextChanged += (_, _) => values[captured] = box.Text;
            if (index == 0) box.SelectAll();
        }
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK };
        WinFormsUi.Name(ok, "确定", inputs.Count * 2 + 1);
        ok.SetBounds(280, ClientSize.Height - 36, 75, 28);
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        WinFormsUi.Name(cancel, "取消", inputs.Count * 2 + 2);
        cancel.SetBounds(365, ClientSize.Height - 36, 75, 28);
        Controls.Add(ok);
        Controls.Add(cancel);
        CancelButton = cancel;
        AcceptButton = ok;
    }

    private static Label Caption(string text, int tabIndex) => new()
    {
        Text = text,
        AutoSize = true,
        TabStop = false,
        TabIndex = tabIndex
    };
}
