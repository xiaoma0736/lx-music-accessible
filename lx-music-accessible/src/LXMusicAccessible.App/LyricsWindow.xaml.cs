using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>
/// Presents lyric workflow state and owns only focus, browsing, and window-local keys.
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
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (LyricsList.Items.Count == 0)
            {
                LyricsList.Focus();
                return;
            }
            var target = LyricsList.SelectedItem ?? LyricsList.Items[0];
            var item = LyricsList.ItemContainerGenerator.ContainerFromItem(target) as FrameworkElement
                       ?? LyricsList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            if (item != null) item.Focus();
            else LyricsList.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
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
        }

        for (var index = 0; index < _lines.Count; index++)
            _lines[index].IsCurrent = index == state.CurrentIndex;
        if (state.AutoScroll && state.CurrentIndex >= 0)
            ScrollToLine(state.CurrentIndex);
        return contentChanged;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
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
