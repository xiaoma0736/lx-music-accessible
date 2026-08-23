using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace LXMusicAccessible.App;

/// <summary>
/// 独立歌词窗口：显示当前歌曲歌词并支持逐句浏览；播放局部热键（F1~F12、Shift+F7/F8）
/// 在本窗口内仍然生效（由主窗口执行动作）。ESC/Alt+F4/关闭按钮 = 隐藏窗口，重开时复用。
/// </summary>
public partial class LyricsWindow : Window
{
    private readonly Func<KeyEventArgs, bool> _handleHotkey;
    private bool _allowClose; // 应用退出时置 true，允许真正关闭

    public LyricsWindow(Func<KeyEventArgs, bool> handleHotkey)
    {
        ArgumentNullException.ThrowIfNull(handleHotkey);
        InitializeComponent();
        _handleHotkey = handleHotkey;
    }

    /// <summary>设置歌词列表内容（共享主窗口的歌词行集合，IsCurrent 高亮通过绑定自动跟随）</summary>
    public void SetLyrics(IEnumerable lines)
    {
        LyricsList.ItemsSource = lines;
    }

    /// <summary>滚动并高亮到指定行（不移动选中项，不影响读屏逐行浏览）</summary>
    public void ScrollToLine(int idx)
    {
        if (idx < 0 || idx >= LyricsList.Items.Count) return;
        LyricsList.ScrollIntoView(LyricsList.Items[idx]);
    }

    /// <summary>设置窗口标题中的歌曲名</summary>
    public void SetTitle(string title)
    {
        TitleText.Text = string.IsNullOrEmpty(title) ? "歌词" : $"歌词：{title}";
    }

    /// <summary>把焦点放进歌词列表（有选中/当前行时聚焦该行容器，否则聚焦列表本身）；
    /// 延迟到布局完成后执行，确保打开窗口后焦点可靠落入列表（修复“查看歌词后焦点不落列表”）。</summary>
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
            if (item != null)
                item.Focus();
            else
                LyricsList.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
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
        // 其余按键先交给主窗口处理播放局部热键（F1~F12、Shift+F7/F8 等）
        if (_handleHotkey(e))
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
