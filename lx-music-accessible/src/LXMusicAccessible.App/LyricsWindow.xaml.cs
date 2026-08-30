using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>
/// Presents lyric lines in the accessible list workflow and keeps local copy/live-region support.
/// ESC/Alt+F4/close hides the reusable window until application shutdown.
/// </summary>
public partial class LyricsWindow : Window
{
    private readonly Func<KeyEventArgs, bool> _handleHotkey;
    private readonly LyricsWorkflow _workflow;
    private readonly ObservableCollection<LyricsLineItem> _lines = [];
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
        LyricsList.ItemsSource = _lines;
        _workflow.StateChanged += Workflow_StateChanged;
        Present(_workflow.Current);
    }

    public void ScrollToLine(int index)
    {
        if (index < 0 || index >= LyricsList.Items.Count) return;
        LyricsList.ScrollIntoView(LyricsList.Items[index]);
    }

    public void FocusLyricsList()
    {
        Dispatcher.BeginInvoke(
            LyricsList.FocusItemOrList,
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
            _lines.Clear();
            if (state.Lines.Count == 0)
            {
                _lines.Add(new LyricsLineItem(state.Message, state.Message));
            }
            else
            {
                foreach (var line in state.Lines)
                    _lines.Add(new LyricsLineItem(line.DisplayText, line.AccessibleText));
            }
            CopyLyricsButton.IsEnabled = state.Status == LyricsLoadStatus.Ready && state.Lines.Count > 0;
        }

        for (var index = 0; index < _lines.Count; index++)
            _lines[index].IsCurrent = index == state.CurrentIndex;
        if (state.AutoScroll && state.CurrentIndex >= 0)
            ScrollToLine(state.CurrentIndex);
        return contentChanged;
    }

    private void CopyLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        CopyLyricsToClipboard();
    }

    /// <summary>Copies the lyric lines to the clipboard and reports the result through the live region.</summary>
    private void CopyLyricsToClipboard()
    {
        if (!CopyLyricsButton.IsEnabled || _lines.Count == 0) return;
        var text = string.Join(Environment.NewLine, _lines.Select(line => line.DisplayText));
        try
        {
            System.Windows.Clipboard.SetText(text);
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

    private sealed class LyricsLineItem(string displayText, string accessibleText) : INotifyPropertyChanged
    {
        private bool _isCurrent;

        public string DisplayText { get; } = displayText;
        public string AccessibleText { get; } = accessibleText;

        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value) return;
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}