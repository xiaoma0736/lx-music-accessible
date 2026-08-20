using System.Windows;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>
/// 赞助二维码窗口：方便用手机直接扫描（盲人用户通常由身边人协助扫码）。所有功能均免费，赞助纯属自愿支持。
/// 扫码完成后关闭本窗口即可，不会影响任何功能。
/// </summary>
public partial class SponsorQrWindow : Window
{
    private readonly IAccessibilityService _announce;

    public SponsorQrWindow()
    {
        InitializeComponent();
        _announce = ServiceProvider.GetRequiredService<IAccessibilityService>();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _announce.Announce("赞助二维码已显示。本软件所有功能均免费，如蒙赞助支持，请用手机扫描窗口中放大的二维码；扫描完成后按 Esc 或“关闭”按钮关闭本窗口。");
        }
        catch { /* 播报失败不阻塞 */ }
        CloseBtn.Focus();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
