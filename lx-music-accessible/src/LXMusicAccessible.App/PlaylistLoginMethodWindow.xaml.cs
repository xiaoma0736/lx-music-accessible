using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace LXMusicAccessible.App;

public enum PlaylistLoginMethod
{
    Web,
    Cookie,
    KugouQr
}

internal sealed record PlaylistLoginMethodOption(PlaylistLoginMethod Method, string Name)
{
    public override string ToString() => Name;
}

public partial class PlaylistLoginMethodWindow : Window
{
    private bool _confirmed;

    internal PlaylistLoginMethodWindow(string platform)
    {
        InitializeComponent();
        Title = GetPlatformName(platform) + "登录方式";
        MethodsList.ItemsSource = GetMethods(platform);
        MethodsList.SelectedIndex = 0;
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            MethodsList.FocusItemOrList, DispatcherPriority.Input);
    }

    public PlaylistLoginMethod? SelectedMethod { get; private set; }

    private static string GetPlatformName(string platform) => platform switch
    {
        "netease" => "网易云",
        "qq" => "QQ音乐",
        "kg" => "酷狗",
        "kw" => "酷我",
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };

    private static PlaylistLoginMethodOption[] GetMethods(string platform) => platform switch
    {
        "netease" or "qq" =>
        [
            new(PlaylistLoginMethod.Web, "网页登录"),
            new(PlaylistLoginMethod.Cookie, "粘贴 Cookie 登录")
        ],
        "kg" =>
        [
            new(PlaylistLoginMethod.Cookie, "粘贴 Cookie 登录"),
            new(PlaylistLoginMethod.KugouQr, "酷狗 APP 扫码登录")
        ],
        "kw" => [new(PlaylistLoginMethod.Web, "网页登录")],
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };

    private void MethodsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Confirm();
    }

    private void UseSelectedMethodBtn_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (_confirmed || MethodsList.SelectedItem is not PlaylistLoginMethodOption option) return;
        _confirmed = true;
        SelectedMethod = option.Method;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape && e.SystemKey != Key.F4) return;
        e.Handled = true;
        Close();
    }
}
