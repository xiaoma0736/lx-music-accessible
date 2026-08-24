using System.Windows;

namespace LXMusicAccessible.App;

/// <summary>
/// 快捷键设置完成确认对话框（单个“确定”按钮，回车/Esc/点击均可关闭）。
/// </summary>
public partial class HotkeyConfirmDialog : Window
{
    public HotkeyConfirmDialog(string message)
    {
        InitializeComponent();
        Title = "快捷键设置完成";
        MessageText.Text = message;
        Loaded += (_, _) => OkButton.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
