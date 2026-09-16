using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LXMusicAccessible.App;

public enum PlaylistLoginMethod
{
    Web,
    Cookie,
    KugouQr
}

/// <summary>Native login-method list.</summary>
public class PlaylistLoginMethodForm : Form
{
    private readonly ListBox _methods = WinFormsUi.ListBox("登录方式", 1);
    public PlaylistLoginMethod? SelectedMethod { get; private set; }

    public PlaylistLoginMethodForm(string platform)
    {
        Text = PlatformName(platform) + "登录方式";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 260);
        _methods.SetBounds(12, 12, 335, 160);
        var use = new Button { Text = "使用所选方式", DialogResult = DialogResult.OK };
        WinFormsUi.Name(use, "使用所选方式", 2);
        use.SetBounds(220, 185, 125, 32);
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        WinFormsUi.Name(cancel, "取消", 3);
        cancel.SetBounds(90, 185, 100, 32);
        foreach (var method in Methods(platform))
            _methods.Items.Add(new ObjectBox<PlaylistLoginMethod>(MethodText(method), method));
        _methods.SelectedIndex = 0;
        Controls.AddRange(new Control[] { _methods, use, cancel });
        AcceptButton = use;
        CancelButton = cancel;
        _methods.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; AcceptButton.PerformClick(); } };
        FormClosed += (_, _) => SelectedMethod = DialogResult == DialogResult.OK && DialogResult == DialogResult.OK ? WinFormsUi.Selected<PlaylistLoginMethod>(_methods) : null;
        Shown += (_, _) => _methods.Focus();
    }

    private static string PlatformName(string platform) => platform switch
    {
        "netease" => "网易云",
        "qq" => "QQ音乐",
        "kg" => "酷狗",
        "kw" => "酷我",
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };

    private static IEnumerable<PlaylistLoginMethod> Methods(string platform) => platform switch
    {
        "netease" or "qq" => new[] { PlaylistLoginMethod.Web, PlaylistLoginMethod.Cookie },
        "kg" => new[] { PlaylistLoginMethod.Cookie, PlaylistLoginMethod.KugouQr },
        _ => new[] { PlaylistLoginMethod.Web }
    };

    private static string MethodText(PlaylistLoginMethod method) => method switch
    {
        PlaylistLoginMethod.Web => "网页登录",
        PlaylistLoginMethod.Cookie => "粘贴 Cookie 登录",
        _ => "酷狗 APP 扫码登录"
    };
}

/// <summary>WebView2 login host with explicit completion; cookies stay on the user's machine.</summary>
public class WebLoginForm : Form
{
    private readonly IPlaylistImportService _service;
    private readonly IAccessibleAnnouncer _announcer;
    private readonly ILogger<WebLoginForm> _logger;
    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill, AccessibleName = "登录网页", TabStop = true, TabIndex = 4 };
    private readonly Label _status = new() { Text = "正在打开登录页...", Dock = DockStyle.Bottom, AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft, TabStop = false };
    public string? LastAccountName { get; private set; }

    public WebLoginForm(string platform, IPlaylistImportService service, IAccessibleAnnouncer announcer, ILogger<WebLoginForm> logger)
    {
        _service = service;
        _announcer = announcer;
        _logger = logger;
        Text = PlatformName(platform) + "网页登录";
        Size = new Size(1050, 760);
        StartPosition = FormStartPosition.CenterParent;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, FlowDirection = FlowDirection.LeftToRight, TabStop = false };
        var done = new Button { Text = "完成登录", Width = 100 };
        var refresh = new Button { Text = "刷新页面", Width = 100 };
        var cancel = new Button { Text = "取消", Width = 80 };
        WinFormsUi.Name(done, "完成登录", 1);
        WinFormsUi.Name(refresh, "刷新页面", 2);
        WinFormsUi.Name(cancel, "取消", 3);
        top.Controls.AddRange(new Control[] { done, refresh, cancel });
        Controls.Add(_browser);
        Controls.Add(_status);
        Controls.Add(top);
        refresh.Click += (_, _) => _browser.Reload();
        cancel.Click += (_, _) => Close();
        done.Click += async (_, _) => await CompleteAsync(platform);
        Shown += async (_, _) =>
        {
            try
            {
                await _browser.EnsureCoreWebView2Async();
                _browser.CoreWebView2.CookieManager.DeleteAllCookies();
                _browser.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    _status.Text = "登录完成后选择完成登录。若页面需要手动操作，请先完成网页验证。";
                    Announce(_status.Text, "web-login");
                };
                _browser.Source = new Uri(LoginUrl(platform));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebView2 initialization failed");
                _status.Text = "浏览器组件初始化失败。请使用 Cookie 登录。";
                Announce(_status.Text, "web-login", AnnouncementKind.Error);
            }
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }
    private async Task CompleteAsync(string platform)
    {
        if (_browser.CoreWebView2 is null) return;
        try
        {
            var cookies = await _browser.CoreWebView2.CookieManager.GetCookiesAsync(LoginUrl(platform));
            var cookieText = string.Join("; ", cookies.Select(cookie => cookie.Name + "=" + cookie.Value));
            if (string.IsNullOrWhiteSpace(cookieText))
            {
                _status.Text = "没有读取到登录 Cookie。";
                Announce(_status.Text, "web-login", AnnouncementKind.Error);
                return;
            }
            var account = await _service.LoginWithBrowserResultAsync(platform, cookieText, null, null);
            if (account is null)
            {
                _status.Text = "登录状态保存失败，请确认已在网页中登录。";
                Announce(_status.Text, "web-login", AnnouncementKind.Error);
                return;
            }
            LastAccountName = account.DisplayName ?? account.Platform;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web login completion failed");
            _status.Text = "读取登录结果失败。";
            Announce(_status.Text, "web-login", AnnouncementKind.Error);
        }
    }

    private static string LoginUrl(string platform) => platform switch
    {
        "netease" => "https://music.163.com/",
        "qq" => "https://y.qq.com/",
        "kw" => "https://www.kuwo.cn/",
        _ => "https://music.163.com/"
    };

    private static string PlatformName(string platform) => platform switch
    {
        "netease" => "网易云",
        "qq" => "QQ音乐",
        "kw" => "酷我",
        _ => platform
    };

    private void Announce(string message, string activity, AnnouncementKind kind = AnnouncementKind.Status) =>
        _announcer.Announce(new AccessibleAnnouncement(message, kind, activity));
}

/// <summary>Kugou QR login with polling from the existing service.</summary>
public class KugouQrLoginForm : Form
{
    private readonly IPlaylistImportService _service;
    private readonly IAccessibleAnnouncer _announcer;
    private readonly KugouQrLoginService _qr;
    private readonly PictureBox _image = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "登录二维码", TabStop = false };
    private readonly Label _status = new() { Text = "正在获取二维码...", Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, TabStop = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private string _key = string.Empty;
    private int _failures;

    public KugouQrLoginForm(IPlaylistImportService service, IAccessibleAnnouncer announcer, KugouQrLoginService qr)
    {
        _service = service;
        _announcer = announcer;
        _qr = qr;
        Text = "酷狗扫码登录";
        Size = new Size(500, 620);
        StartPosition = FormStartPosition.CenterParent;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, TabStop = false };
        var refresh = new Button { Text = "刷新二维码", Width = 110 };
        var cancel = new Button { Text = "取消", Width = 80 };
        WinFormsUi.Name(refresh, "刷新二维码", 1);
        WinFormsUi.Name(cancel, "取消", 2);
        buttons.Controls.Add(refresh);
        buttons.Controls.Add(cancel);
        Controls.Add(_image);
        Controls.Add(_status);
        Controls.Add(buttons);
        refresh.Click += async (_, _) => await LoadAsync();
        cancel.Click += (_, _) => { _timer.Stop(); DialogResult = DialogResult.Cancel; Close(); };
        _timer.Tick += async (_, _) => await PollAsync();
        Shown += async (_, _) => await LoadAsync();
        FormClosed += (_, _) => _timer.Stop();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }
    private async Task LoadAsync()
    {
        try
        {
            var qr = await _qr.GetQrKeyAsync();
            if (qr?.QrImage is null)
            {
                _status.Text = "获取二维码失败，请检查网络后刷新。";
                Announce(_status.Text, "qr-login", AnnouncementKind.Error);
                return;
            }
            _key = qr.QrKey;
            var base64 = qr.QrImage.Contains(',') ? qr.QrImage[(qr.QrImage.IndexOf(',') + 1)..] : qr.QrImage;
            _image.Image = System.Drawing.Image.FromStream(new MemoryStream(Convert.FromBase64String(base64)));
            _status.Text = "二维码已生成。请用手机酷狗 APP 扫码并在手机上确认。";
            Announce(_status.Text, "qr-login");
            _failures = 0;
            _timer.Start();
        }
        catch (Exception ex)
        {
            _status.Text = "获取二维码失败。";
            Announce(_status.Text, "qr-login", AnnouncementKind.Error);
            WinFormsUi.Error(this, ex, "获取二维码");
        }
    }

    private async Task PollAsync()
    {
        if (string.IsNullOrEmpty(_key)) return;
        var result = await _qr.CheckStatusAsync(_key);
        if (result.Status == 4)
        {
            _timer.Stop();
            await SaveAsync(result.UserId, result.Token);
        }
        else if (result.Status == 2)
        {
            _status.Text = "已扫码，请在手机上确认登录。";
            Announce(_status.Text, "qr-login");
        }
        else if (result.Status == 0)
        {
            _timer.Stop();
            _status.Text = "二维码已过期，请刷新二维码。";
            Announce(_status.Text, "qr-login", AnnouncementKind.Error);
        }
        else if (!string.IsNullOrEmpty(result.Error) && ++_failures >= 3)
        {
            _timer.Stop();
            _status.Text = "网络异常，请刷新二维码。";
            Announce(_status.Text, "qr-login", AnnouncementKind.Error);
        }
    }

    private async Task SaveAsync(string userId, string token)
    {
        try
        {
            var account = await _service.LoginWithCookieAsync("kg", $"userid={userId}; token={token}");
            if (account is null)
            {
                _status.Text = "登录状态保存失败，请重试。";
                Announce(_status.Text, "qr-login", AnnouncementKind.Error);
                _timer.Start();
                return;
            }
            _status.Text = "扫码登录成功：" + account.DisplayName;
            Announce(_status.Text, "qr-login", AnnouncementKind.Success);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "登录状态保存失败。";
            Announce(_status.Text, "qr-login", AnnouncementKind.Error);
            WinFormsUi.Error(this, ex, "保存登录状态");
        }
    }

    private void Announce(string message, string activity, AnnouncementKind kind = AnnouncementKind.Status) =>
        _announcer.Announce(new AccessibleAnnouncement(message, kind, activity));
}

/// <summary>The login-window seam used by playlist import and shell workflows.</summary>
public interface IPlaylistLoginWindowFactory
{
    PlaylistLoginMethod? SelectLoginMethod(string platform, Form? owner);
    PlaylistLoginDialogResult ShowWebLogin(string platform, Form? owner);
    bool ShowKugouQrLogin(Form? owner);
}

public readonly record struct PlaylistLoginDialogResult(bool Succeeded, string? AccountName = null);

public sealed class PlaylistLoginWindowFactory(
    IPlaylistImportService service,
    IAccessibleAnnouncer announcer,
    KugouQrLoginService kugouQr,
    ILogger<WebLoginForm> logger) : IPlaylistLoginWindowFactory
{
    public PlaylistLoginMethod? SelectLoginMethod(string platform, Form? owner)
    {
        using var dialog = new PlaylistLoginMethodForm(platform);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedMethod : null;
    }

    public PlaylistLoginDialogResult ShowWebLogin(string platform, Form? owner)
    {
        using var dialog = new WebLoginForm(platform, service, announcer, logger);
        return new(dialog.ShowDialog(owner) == DialogResult.OK, dialog.LastAccountName);
    }

    public bool ShowKugouQrLogin(Form? owner)
    {
        using var dialog = new KugouQrLoginForm(service, announcer, kugouQr);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}
