using System.Collections.Generic;
using System.Windows;

namespace LXMusicAccessible.App;

/// <summary>
/// 歌曲详情对话框：以列表逐条展示（读屏可上下方向键逐条浏览），回车/Esc 关闭。
/// </summary>
public partial class SongDetailDialog : Window
{
    public SongDetailDialog(string songTitle, IReadOnlyList<string> detailLines)
    {
        InitializeComponent();
        TitleText.Text = $"歌曲详情：{songTitle}";
        foreach (var line in detailLines)
            DetailsList.Items.Add(line);
        Loaded += (_, _) =>
        {
            if (DetailsList.Items.Count > 0)
            {
                DetailsList.SelectedIndex = 0;
                DetailsList.ScrollIntoView(DetailsList.Items[0]);
                DetailsList.Focus();
            }
            else
            {
                Close();
            }
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}