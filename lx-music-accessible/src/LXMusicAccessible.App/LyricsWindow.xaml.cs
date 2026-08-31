using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>
/// Presents lyrics in a read-only text viewer so screen-reader browse mode can read
/// lines naturally and select or copy freely; keeps the copy button, the local live
/// region, and auto-scroll-to-current-line highlighting.
/// ESC/Alt+F4/close hides the reusable window until application shutdown.
/// </summary>
public partial class LyricsWindow : Window
{
    private const char LineSeparator = '\n';
    private readonly Func<KeyEventArgs, bool> _handleHotkey;
    private readonly LyricsWorkflow _workflow;
    private IReadOnlyList<LyricsLineState>? _presentedLines;
    private string? _contentKey;
    private List<int> _lineOffsets = [];
    private bool _allowClose;

    public LyricsWindow(LyricsWorkflow workflow, Func<KeyEventArgs, bool> handleHotkey)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(handleHotkey);
        InitializeComponent();
        _workflow = workflow;
        _handleHotkey = handleHotkey;
        _workflow.StateChanged += Workflow_StateChanged;
        Present(_workflow.Current);
    }

    public void ScrollToLine(int index)
    {
        if (index < 0 || index >= _lineOffsets.Count) return;
        // Deferred so the new Text has completed layout before measuring the line rect.
        Dispatcher.BeginInvoke(
            () => RevealLine(index),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void FocusLyricsView()
    {
        Dispatcher.BeginInvoke(
            LyricsTextBox.Focus,
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Workflow_StateChanged(object? sender, LyricsState state)
    {
        if (Dispatcher.CheckAccess()) PresentAndAnnounce(state);
        else _ = Dispatcher.BeginInvoke(() => PresentAndAnnounce(state));
    }

    private void PresentAndAnnounce(LyricsState state)
    {
        if (Present(state)
            && IsVisible
            && (state.Status != LyricsLoadStatus.Ready || state.CurrentIndex >= 0))
        {
            _workflow.AnnounceCurrentLine();
        }
    }

    private bool Present(LyricsState state)
    {
        TitleText.Text = string.IsNullOrEmpty(state.Music?.Title)
            ? "歌词"
            : $"歌词：{state.Music.Title}";
        var key = state.Music is null
            ? $"status:{state.Status}:{state.Message}"
            : $"{state.Music.Source}|{state.Music.Id}:{state.Status}:{state.Lines.Count}";
        var contentChanged = !ReferenceEquals(_presentedLines, state.Lines)
            || !string.Equals(_contentKey, key, StringComparison.Ordinal);
        if (contentChanged)
        {
            _contentKey = key;
            _presentedLines = state.Lines;
            var displayLines = state.Lines.Count == 0
                ? [state.Message]
                : state.Lines.Select(line => line.DisplayText).ToList();
            LyricsTextBox.Text = string.Join(LineSeparator, displayLines);
            RebuildLineOffsets(displayLines);
            CopyLyricsButton.IsEnabled = state.Status == LyricsLoadStatus.Ready && state.Lines.Count > 0;
        }

        if (state.AutoScroll && state.CurrentIndex >= 0)
            ScrollToLine(state.CurrentIndex);
        return contentChanged;
    }

    private void RebuildLineOffsets(IReadOnlyList<string> displayLines)
    {
        _lineOffsets = new List<int>(displayLines.Count);
        var offset = 0;
        foreach (var line in displayLines)
        {
            _lineOffsets.Add(offset);
            offset += line.Length + 1; // single LineSeparator character between lines
        }
    }

    /// <summary>Selects the current line (visual highlight) and keeps it inside the viewport.</summary>
    private void RevealLine(int index)
    {
        if (index < 0 || index >= _lineOffsets.Count) return;
        var start = _lineOffsets[index];
        var end = index + 1 < _lineOffsets.Count
            ? _lineOffsets[index + 1] - 1
            : LyricsTextBox.Text.Length;
        LyricsTextBox.Select(start, Math.Max(0, end - start));
        LyricsTextBox.CaretIndex = start;
        var rect = LyricsTextBox.GetRectFromCharacterIndex(start, true);
        if (rect.Height > 0)
        {
            var target = rect.Top - (LyricsTextBox.ViewportHeight / 2d);
            LyricsTextBox.ScrollToVerticalOffset(Math.Max(0, target));
        }
    }

    private void CopyLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        CopyLyricsToClipboard();
    }

    /// <summary>Copies the lyric lines to the clipboard and reports the result through the live region.</summary>
    private void CopyLyricsToClipboard()
    {
        if (!CopyLyricsButton.IsEnabled || LyricsTextBox.Text.Length == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(LyricsTextBox.Text);
            SetLiveRegion("歌词已复制");
        }
        catch (Exception)
        {
            SetLiveRegion("歌词复制失败，剪贴板被占用，请稍后重试");
        }
    }

    /// <summary>Updates the live region text so screen readers announce it while the window is focused.</summary>
    private void SetLiveRegion(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        LiveRegion.Text = text;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            CopyLyricsToClipboard();
            e.Handled = true;
            return;
        }

        if (_handleHotkey(e)) e.Handled = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    public void PrepareForAppShutdown()
    {
        _allowClose = true;
        _workflow.StateChanged -= Workflow_StateChanged;
    }
}