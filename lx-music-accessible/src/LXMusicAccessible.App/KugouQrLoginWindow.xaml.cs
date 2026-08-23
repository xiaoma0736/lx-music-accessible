using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.App;

/// <summary>
/// 酷狗 APP 扫码登录窗口（无障碍：语音播报 + 键盘操作）。
/// 显示官方二维码，用户用手机酷狗APP扫码并在手机上确认，程序轮询 login_qr_check 获取客户端 token+userid 完成登录。
/// </summary>
public partial class KugouQrLoginWindow : Window
{
    private readonly IPlaylistImportService _service;
    private readonly IAccessibilityService _announce;
    private readonly KugouQrLoginService _qr;
    private readonly DispatcherTimer _timer;
    private string _qrKey = string.Empty;
    private bool _succeeded;
    private int _failCount;

    public KugouQrLoginWindow(
        IPlaylistImportService service,
        IAccessibilityService announce,
        MusicHttpClient httpClient,
        ILogger<KugouQrLoginService> logger)
    {
        InitializeComponent();
        _service = service;
        _announce = announce;
        _qr = new KugouQrLoginService(httpClient, logger);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await PollOnceAsync();

        Loaded += async (_, _) => await LoadQrAsync();
    }

    private void Announce(string message)
    {
        try { _announce.Announce(message); }
        catch { /* 播报失败不阻塞 */ }
    }

    private async Task LoadQrAsync()
    {
        StatusText.Text = "正在获取二维码，请稍候。";
        Announce("正在获取酷狗登录二维码，请稍候。");
        var key = await _qr.GetQrKeyAsync();
        if (key == null || string.IsNullOrEmpty(key.QrImage))
        {
            StatusText.Text = "获取二维码失败，请检查网络后按刷新二维码按钮重试。";
            Announce("获取二维码失败，请检查网络后按刷新二维码按钮重试。");
            return;
        }
        _qrKey = key.QrKey;
        SetQrImage(key.QrImage);
        StatusText.Text = "二维码已生成。请用手机酷狗APP扫描二维码，然后在手机上确认登录。";
        Announce("二维码已生成。请用手机酷狗APP扫描二维码，然后在手机上确认登录。");
        _failCount = 0;
        _timer.Start();
    }

    private void SetQrImage(string dataUri)
    {
        try
        {
            var base64 = dataUri.Contains(",") ? dataUri[(dataUri.IndexOf(',') + 1)..] : dataUri;
            var bytes = Convert.FromBase64String(base64);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new System.IO.MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            QrImage.Source = bmp;
        }
        catch { /* 图片解码失败忽略，用户可点刷新重试 */ }
    }

    private async Task PollOnceAsync()
    {
        if (_succeeded || string.IsNullOrEmpty(_qrKey)) return;
        var r = await _qr.CheckStatusAsync(_qrKey);
        if (r.Status == 4)
        {
            _succeeded = true;
            _timer.Stop();
            await OnSuccessAsync(r.UserId, r.Token);
        }
        else if (r.Status == 2)
        {
            StatusText.Text = "已扫码，请在手机上确认登录。";
            Announce("已扫码，请在手机上确认登录。");
        }
        else if (r.Status == 0)
        {
            _timer.Stop();
            StatusText.Text = "二维码已过期，请按刷新二维码按钮重新获取。";
            Announce("二维码已过期，请按刷新二维码按钮重新获取。");
        }
        else if (!string.IsNullOrEmpty(r.Error))
        {
            _failCount++;
            if (_failCount >= 3)
            {
                _timer.Stop();
                StatusText.Text = "网络异常，请按刷新二维码按钮重试。";
                Announce("网络异常，请按刷新二维码按钮重试。");
            }
        }
    }

    private async Task OnSuccessAsync(string userId, string token)
    {
        StatusText.Text = "登录成功，正在保存登录状态...";
        Announce("扫码登录成功，正在保存登录状态，请稍候。");
        try
        {
            // 酷狗 Provider 会从 cookie 中解析 userid/token 并调用客户端歌单接口校验
            var cookie = $"userid={userId}; token={token}";
            var acc = await _service.LoginWithCookieAsync("kg", cookie);
            if (acc == null)
            {
                StatusText.Text = "登录状态保存失败，请重试。";
                Announce("登录状态保存失败，请重试。");
                _succeeded = false;
                _timer.Start();
                return;
            }
            StatusText.Text = $"登录成功：{acc.DisplayName}。";
            Announce($"登录成功：{acc.DisplayName}。");
            DialogResult = true;
        }
        catch (Exception)
        {
            var correlationId = LoginDiagnostics.CreateCorrelationId();
            var message = LoginDiagnostics.CreateUserMessage(
                LoginDiagnosticOutcome.Exception, LoginDiagnosticError.Unexpected, correlationId);
            StatusText.Text = message;
            Announce(message);
            _succeeded = false;
        }
    }

    private async void RefreshQrBtn_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _succeeded = false;
        await LoadQrAsync();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _timer.Stop();
            DialogResult = false;
            e.Handled = true;
        }
    }
}
