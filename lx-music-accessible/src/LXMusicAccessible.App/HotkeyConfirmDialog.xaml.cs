using System.Windows;
using System.Windows.Automation;

namespace LXMusicAccessible.App;

/// <summary>
/// 快捷键设置完成确认对话框（单个“确定”按钮，回车/Esc/点击均可关闭）。
/// 打开时聚焦“确定”按钮，并将其无障碍名称设为完整结果消息，便于读屏播报。
/// </summary>
public partial class HotkeyConfirmDialog : Window
{
    public HotkeyConfirmDialog(string message)
    {
        InitializeComponent();
        Title = "快捷键设置完成";
        MessageText.Text = message;
        AutomationProperties.SetName(OkButton, $"{message}，回车关闭");
        Loaded += (_, _) => OkButton.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
