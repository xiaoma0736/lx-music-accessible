using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>
/// Presents lyric workflow state in a full-text read-only viewer and owns only focus,
/// copying, and window-local keys. ESC/Alt+F4/close hides the reusable window until
/// application shutdown.
/// </summary>
public partial class LyricsWindow : Window
{
    private readonly Func<KeyEventArgs, bool> _handleHotkey;
    private readonly LyricsWorkflow _workflow;
    private IReadOnlyList<LyricsLineState>? _presentedLines;
    private string? _contentKey;
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

    /// <summary>Scrolls to and highlights the given zero-based line index without moving keyboard focus.</summary>
    public void ScrollToLine(int index)
    {
        if (index < 0 || string.IsNullOrEmpty(LyricsText.Text)) return;
        var text = LyricsText.Text;
        var start = 0;
        var lineIndex = 0;
        while (lineIndex < index && start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                start = text.Length;
                break;
            }
            start = newline + 1;
            lineIndex++;
        }

        var end = text.IndexOf('\n', start);
        var length = (end < 0 ? text.Length : end) - start;
        if (length < 0) length = 0;
        LyricsText.Select(start, length);
        LyricsText.ScrollToLine(index);
    }

    /// <summary>Moves keyboard focus into the lyrics viewer after layout completes.</summary>
    public void FocusLyricsList()
    {
        Dispatcher.BeginInvoke(
            LyricsText.Focus,
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
            LyricsText.Text = state.Lines.Count == 0
                ? state.Message
                : string.Join(Environment.NewLine, state.Lines.Select(line => line.DisplayText));
            CopyLyricsButton.IsEnabled = state.Status == LyricsLoadStatus.Ready && state.Lines.Count > 0;
        }

        if (state.AutoScroll && state.CurrentIndex >= 0)
            ScrollToLine(state.CurrentIndex);
        return contentChanged;
    }

    private void CopyLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        CopyLyricsToClipboard();
    }

    /// <summary>Copies the full lyrics to the clipboard and reports the result through the live region.</summary>
    private void CopyLyricsToClipboard()
    {
        if (!CopyLyricsButton.IsEnabled || LyricsText.Text.Length == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(LyricsText.Text);
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