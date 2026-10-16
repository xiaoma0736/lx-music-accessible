using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>
/// Owns explicit multi-selection for downloads. Keeping this work out of browsing lists makes Space a
/// stable playback key while batch review and check state remain a separate, predictable task.
/// </summary>
internal sealed class BatchSelectionForm : Form
{
    private readonly IAccessibleAnnouncer _announcer;
    private readonly IReadOnlyList<MusicInfo> _songs;
    private readonly Func<IReadOnlyList<MusicInfo>, Task> _download;
    private readonly CheckedListBox _songsList;
    private readonly Label _status;
    private readonly Button _downloadButton;
    private readonly Button _selectAllButton;
    private readonly Button _clearButton;
    private bool _suppressCheckEvents;

    public BatchSelectionForm(
        IAccessibleAnnouncer announcer,
        IReadOnlyList<MusicInfo> songs,
        string sourceTitle,
        Func<IReadOnlyList<MusicInfo>, Task> download)
    {
        _announcer = announcer;
        _songs = songs;
        _download = download;

        Text = "批量选择下载：" + sourceTitle;
        AccessibleName = Text;
        Font = new Font(Font.Name, 11f);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;

        _songsList = new CheckedListBox
        {
            Name = "BatchSelectionSongs",
            AccessibleName = "批量下载歌曲",
            CheckOnClick = true,
            IntegralHeight = false,
            HorizontalScrollbar = true
        };
        WinFormsUi.Name(_songsList, "批量下载歌曲", 0);
        foreach (var song in songs)
            _songsList.Items.Add(new ObjectBox<MusicInfo>(song.AccessibleName, song));
        _songsList.ItemCheck += (_, e) =>
        {
            if (!_suppressCheckEvents) UpdateStatus();
        };
        _songsList.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Space || e.Control || e.Shift || e.Alt) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            var index = _songsList.SelectedIndex;
            if (index >= 0) SetItemChecked(index, !_songsList.GetItemChecked(index));
        };
        _songsList.KeyDown += (_, e) =>
        {
            if (!e.Control || e.Shift || e.Alt || e.KeyCode != Keys.A) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            SetAllChecked(true);
        };

        _status = new Label
        {
            Name = "BatchSelectionStatus",
            Text = "已选择 0 首",
            AutoSize = true,
            TabStop = false
        };
        _downloadButton = CreateButton("下载选中歌曲", 1, (_, _) => DownloadCheckedForTests());
        _selectAllButton = CreateButton("全选", 2, (_, _) => SetAllChecked(true));
        _clearButton = CreateButton("清空", 3, (_, _) => SetAllChecked(false));
        var closeButton = CreateButton("关闭", 4, (_, _) => Close());

        _songsList.SetBounds(12, 12, 640, 400);
        _status.SetBounds(12, 420, 0, 0);
        _downloadButton.SetBounds(12, 450, 150, 32);
        _selectAllButton.SetBounds(172, 450, 90, 32);
        _clearButton.SetBounds(272, 450, 90, 32);
        closeButton.SetBounds(562, 450, 90, 32);
        Controls.AddRange(new Control[] { _songsList, _status, _downloadButton, _selectAllButton, _clearButton, closeButton });
        AcceptButton = null;
        CancelButton = closeButton;
        UpdateStatus();
    }

    internal CheckedListBox ItemsForTests => _songsList;
    internal string StatusText => _status.Text;
    internal Button ClearButtonForTests => _clearButton;

    internal void SetItemChecked(int index, bool isChecked)
    {
        if (index < 0 || index >= _songsList.Items.Count) return;
        _suppressCheckEvents = true;
        try
        {
            _songsList.SetItemChecked(index, isChecked);
        }
        finally
        {
            _suppressCheckEvents = false;
        }
        UpdateStatus();
    }

    internal void DownloadCheckedForTests()
    {
        var selected = _songsList.CheckedIndices.Cast<int>()
            .Where(IsSongIndex)
            .Select(index => ((ObjectBox<MusicInfo>)_songsList.Items[index]).Value)
            .ToArray();
        if (selected.Length == 0)
        {
            _announcer.Announce(new AccessibleAnnouncement("没有选中歌曲", AnnouncementKind.Status, "batch-selection-empty"));
            return;
        }

        _ = _download(selected);
    }

    private void SetAllChecked(bool isChecked)
    {
        _suppressCheckEvents = true;
        try
        {
            for (var index = 0; index < _songsList.Items.Count; index++)
            {
                if (IsSongIndex(index)) _songsList.SetItemChecked(index, isChecked);
            }
        }
        finally
        {
            _suppressCheckEvents = false;
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var count = _songsList.CheckedIndices.Cast<int>().Count(IsSongIndex);
        _status.Text = $"已选择 {count} 首";
    }

    private bool IsSongIndex(int index) =>
        index >= 0 && index < _songsList.Items.Count && _songsList.Items[index] is ObjectBox<MusicInfo>;

    private static Button CreateButton(string text, int tabIndex, EventHandler onClick)
    {
        var button = new Button { Text = text };
        WinFormsUi.Name(button, text, tabIndex);
        button.Click += onClick;
        return button;
    }
}


