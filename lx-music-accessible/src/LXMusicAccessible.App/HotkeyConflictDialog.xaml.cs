using System.Windows;
using System.Windows.Input;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.App;

/// <summary>
/// 全局快捷键注册冲突提示对话框：列出被其他程序占用、无法全局注册的快捷键，
/// 提供“重新设置”（直接进入该快捷键的重新录制）与“保留（仅程序内生效）”。
/// </summary>
public partial class HotkeyConflictDialog : Window
{
    private readonly Hotkey? _firstFailed;

    /// <summary>用户选择“重新设置”时，需要重新录制的快捷键（默认第一个失败项）</summary>
    public Hotkey? SelectedHotkey { get; private set; }

    public HotkeyConflictDialog(IReadOnlyList<Hotkey> failedHotkeys)
    {
        InitializeComponent();
        Title = "快捷键冲突提示";
        var list = (failedHotkeys ?? System.Array.Empty<Hotkey>())
            .Where(h => h != null && !string.IsNullOrEmpty(h.Name))
            .ToList();
        _firstFailed = list.FirstOrDefault();
        if (list.Count == 0)
        {
            MessageText.Text = "部分全局快捷键无法注册。";
            DetailText.Text = "可尝试重新设置这些快捷键。";
        }
        else if (list.Count == 1)
        {
            MessageText.Text = $"“{_firstFailed!.Name}”快捷键已被其他程序占用，无法全局注册。";
            DetailText.Text = string.Empty;
        }
        else
        {
            MessageText.Text = $"有 {list.Count} 个全局快捷键被其他程序占用，无法全局注册：";
            DetailText.Text = string.Join("\n", list.Select(h => $"{h.Name}：{Format(h)}"));
        }
        Loaded += (_, _) => ReregisterButton.Focus();
    }

    private void Reregister_Click(object sender, RoutedEventArgs e)
    {
        SelectedHotkey = _firstFailed;
        DialogResult = true;
    }

    private void Keep_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string Format(Hotkey h)
    {
        var parts = new List<string>();
        if (((PlatformModifiers)h.Modifiers & PlatformModifiers.Control) != 0) parts.Add("Ctrl");
        if (((PlatformModifiers)h.Modifiers & PlatformModifiers.Alt) != 0) parts.Add("Alt");
        if (((PlatformModifiers)h.Modifiers & PlatformModifiers.Shift) != 0) parts.Add("Shift");
        if (((PlatformModifiers)h.Modifiers & PlatformModifiers.Windows) != 0) parts.Add("Win");
        parts.Add((PlatformKey)h.Key == PlatformKey.Space ? "空格" : h.Key.ToString());
        return string.Join("+", parts);
    }
}
