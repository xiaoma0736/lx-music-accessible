using System.Windows;

namespace LXMusicAccessible.App;

/// <summary>
/// 通用文本输入对话框（支持一个或两个输入框）
/// </summary>
public partial class TextInputDialog : Window
{
    public string? Value => InputBox1.Text;
    public string? Value2 => InputBox2.Text;

    public TextInputDialog(string title, string label1, string value1, string? label2 = null, string? value2 = null)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        Label1.Content = label1;
        // Label.Target moves focus but does not populate the WPF peer Name.
        System.Windows.Automation.AutomationProperties.SetName(InputBox1, ToAccessibleTaskName(label1));
        InputBox1.Text = value1 ?? string.Empty;
        if (!string.IsNullOrEmpty(label2))
        {
            Label2.Content = label2;
            System.Windows.Automation.AutomationProperties.SetName(InputBox2, ToAccessibleTaskName(label2));
            Label2.Visibility = Visibility.Visible;
            InputBox2.Text = value2 ?? string.Empty;
            InputBox2.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => { InputBox1.Focus(); InputBox1.SelectAll(); };
    }

    private static string ToAccessibleTaskName(string label)
    {
        var taskName = label.Trim().TrimEnd('：', ':').TrimEnd();
        return taskName.Length > 0 ? taskName : "内容";
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
