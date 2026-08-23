using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>
/// 使用说明窗口：无障碍友好的只读文档查看器（Markdown 源文档）。
/// 文档内容从程序目录 Docs\使用说明.md 加载；展示前做轻量 Markdown→纯文本转换
/// （去掉 # 标题标记、** 加粗、表格竖线），并按“## ”一级标题解析出章节列表，
/// 可在“章节”下拉框中分章节跳转。文件缺失时显示内置提示。
/// </summary>
public partial class UserGuideWindow : Window
{
    /// <summary>章节：标题 + 在展示文本中的起始字符偏移（用于跳转）</summary>
    private readonly List<(string Title, int Offset)> _chapters = new();
    private readonly IAccessibleAnnouncer _accessibilityService;
    private bool _suppressJump;

    public UserGuideWindow(IAccessibleAnnouncer accessibilityService)
    {
        _accessibilityService = accessibilityService;
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var raw = LoadGuideContent();
        var display = BuildDisplay(raw);
        GuideText.Text = display;
        PopulateChapters(display);

        // 打开后焦点落入正文开头，方便读屏从第一行开始朗读。
        // 用 Dispatcher 延迟到窗口真正激活后再聚焦，确保键盘焦点确实落在正文上。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            GuideText.Focus();
            GuideText.CaretIndex = 0;
            GuideText.ScrollToHome();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>把 Markdown 源文档转为适合读屏的纯文本展示，并顺带记录“## ”章节在展示文本中的偏移</summary>
    private string BuildDisplay(string raw)
    {
        var sb = new StringBuilder();
        _chapters.Clear();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
                _chapters.Add((line.Substring(3).Trim(), sb.Length));
            sb.AppendLine(ToPlainText(line));
        }
        return sb.ToString();
    }

    /// <summary>轻量 Markdown→纯文本：去掉 # 标题标记、表格竖线与分隔行、** 加粗</summary>
    private static string ToPlainText(string line)
    {
        var s = line;
        // 标题标记
        if (s.StartsWith("### ", StringComparison.Ordinal)) s = s.Substring(4);
        else if (s.StartsWith("## ", StringComparison.Ordinal)) s = s.Substring(3);
        else if (s.StartsWith("# ", StringComparison.Ordinal)) s = s.Substring(2);
        // 表格分隔行（仅含 | - : 空格）→ 空行
        var t = s.Trim();
        if (t.Length > 0 && t.All(c => c == '|' || c == '-' || c == ':' || c == ' '))
            return string.Empty;
        // 表格数据行：去掉首尾竖线，内部竖线换成两个空格
        if (s.StartsWith("|") && s.EndsWith("|"))
            s = s.Trim().Trim('|').Replace("|", "  ");
        // 加粗标记
        s = s.Replace("**", string.Empty);
        return s;
    }

    /// <summary>用解析到的章节填充“章节”下拉框（分章节查看）</summary>
    private void PopulateChapters(string display)
    {
        _suppressJump = true;
        try
        {
            ChapterCombo.ItemsSource = _chapters.Select(c => c.Title).ToList();
            ChapterCombo.SelectedIndex = -1;
        }
        finally
        {
            _suppressJump = false;
        }
    }

    /// <summary>章节下拉框选择：把正文滚动到对应章节开头并播报章节名</summary>
    private void ChapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressJump) return;
        var idx = ChapterCombo.SelectedIndex;
        if (idx < 0 || idx >= _chapters.Count) return;

        var target = Math.Clamp(_chapters[idx].Offset, 0, GuideText.Text.Length);
        GuideText.CaretIndex = target;
        // 滚动到目标所在行（WPF TextBox 无 ScrollToCaret，按行号滚动）
        var line = GuideText.Text.AsSpan(0, target).Count('\n');
        GuideText.ScrollToLine(line);

        var title = _chapters[idx].Title;
        _accessibilityService.Announce(new AccessibleAnnouncement(
            $"已跳转到章节：{title}",
            AnnouncementKind.Status,
            $"guide:{idx}"));
    }

    private static string LoadGuideContent()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Docs", "使用说明.md");
            if (File.Exists(path))
                return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return $"使用说明文档读取失败：{ex.Message}\n\n请检查程序目录下的 Docs\\使用说明.md 文件。";
        }
        return "使用说明文档不存在。\n\n请检查程序目录下的 Docs\\使用说明.md 文件。";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
