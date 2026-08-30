using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace LXMusicAccessible.App;

/// <summary>
/// 独立歌词窗口：以只读文本查看器显示当前歌曲完整歌词，播放时自动滚动并高亮当前句；
/// 支持“复制歌词”按钮或 Ctrl+Shift+C 一键复制到剪贴板；播放局部热键（F1~F12、Shift+F7/F8）
/// 在本窗口内仍然生效（由主窗口执行动作）。ESC/Alt+F4/关闭按钮 = 隐藏窗口，重开时复用。
/// </summary>
public partial class LyricsWindow : Window
{
    private readonly MainWindow _owner;
    private bool _allowClose; // 应用退出时置 true，允许真正关闭

    public LyricsWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
    }

    /// <summary>设置歌词全文文本；占位内容（暂无歌词/加载中）时禁用复制按钮</summary>
    public void SetLyrics(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        LyricsText.Text = text ?? string.Empty;
        CopyLyricsButton.IsEnabled = trimmed.Length > 0
            && trimmed != "暂无歌词"
            && trimmed != "歌词加载中...";
    }

    /// <summary>滚动并高亮到指定行：按换行定位第 idx 行，选中该行文本并滚动到可见（不打断读屏全文阅读）</summary>
    public void ScrollToLine(int idx)
    {
        if (idx < 0 || string.IsNullOrEmpty(LyricsText.Text)) return;
        var text = LyricsText.Text;
        var start = 0;
        var lineIdx = 0;
        while (lineIdx < idx && start < text.Length)
        {
            var nl = text.IndexOf('\n', start);
            if (nl < 0) { start = text.Length; break; }
            start = nl + 1;
            lineIdx++;
        }
        var end = text.IndexOf('\n', start);
        var len = (end < 0 ? text.Length : end) - start;
        if (len < 0) len = 0;
        LyricsText.Select(start, len);
        LyricsText.ScrollToLine(idx);
    }

    /// <summary>更新歌词窗口的实时区域（读屏在窗口聚焦时播报）</summary>
    public void SetLiveRegion(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        LiveRegion.Text = text;
    }

    /// <summary>设置窗口标题中的歌曲名</summary>
    public void SetTitle(string title)
    {
        TitleText.Text = string.IsNullOrEmpty(title) ? "歌词" : $"歌词：{title}";
    }

    /// <summary>把焦点放进歌词文本查看器（当前句已在 ScrollToLine 中选中，聚焦后读屏会朗读当前句）；
    /// 延迟到布局完成后执行，确保打开窗口后焦点可靠落入文本查看器。</summary>
    public void FocusLyricsList()
    {
        Dispatcher.BeginInvoke(new Action(() => LyricsText.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CopyLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        CopyLyricsToClipboard();
    }

    /// <summary>一键复制整首歌词到剪贴板，并通过实时区域给读屏反馈</summary>
    private void CopyLyricsToClipboard()
    {
        if (string.IsNullOrEmpty(LyricsText.Text) || !CopyLyricsButton.IsEnabled) return;
        try
        {
            System.Windows.Clipboard.SetText(LyricsText.Text);
            SetLiveRegion("歌词已复制");
        }
        catch { /* 剪贴板被占用等异常：静默处理，不影响歌词查看 */ }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // ESC：关闭（隐藏）歌词窗口
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }
        // Ctrl+Shift+C：一键复制整首歌词到剪贴板
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            CopyLyricsToClipboard();
            e.Handled = true;
            return;
        }
        // 其余按键先交给主窗口处理播放局部热键（F1~F12、Shift+F7/F8 等）
        if (_owner.TryHandleLyricsWindowHotkey(e))
            e.Handled = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    /// <summary>应用退出前调用：允许真正关闭，避免阻挡退出</summary>
    public void PrepareForAppShutdown()
    {
        _allowClose = true;
    }
}
