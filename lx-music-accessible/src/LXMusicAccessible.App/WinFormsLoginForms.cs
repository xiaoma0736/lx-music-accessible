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
        FormClosed += (_, _) => SelectedMethod = DialogResult == DialogResult.OK ? WinFormsUi.Selected<PlaylistLoginMethod>(_methods) : null;
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

    /// <summary>
    /// Every platform can be signed in through its own website or by pasting cookies, because both are
    /// implemented and verified for all four; Kugou additionally gets the in-app QR window, which is the
    /// only path that needs no cookie hunting at all. The order is the order the user guide lists.
    /// </summary>
    private static IEnumerable<PlaylistLoginMethod> Methods(string platform) => platform switch
    {
        "kg" => new[] { PlaylistLoginMethod.Web, PlaylistLoginMethod.Cookie, PlaylistLoginMethod.KugouQr },
        _ => new[] { PlaylistLoginMethod.Web, PlaylistLoginMethod.Cookie }
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
    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill, AccessibleName = "登录网页", TabStop = false, TabIndex = 5 };
    private readonly Label _status = new() { Text = "正在打开登录页...", Dock = DockStyle.Bottom, AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Name = "WebLoginStatus", TabStop = false };
    private readonly Button _done = new() { Text = "完成登录", Width = 100 };
    // The user guide promises this window closes by itself once the sign-in worked, so the browser session
    // is watched for the platform's own login cookie. A screen reader user cannot see the page behind the
    // toolbar, and hunting for the confirm button after scanning a QR code on the phone is a dead end; the
    // button stays as the manual fallback for pages that finish later or differently.
    private readonly System.Windows.Forms.Timer _loginCheck = new() { Interval = 2000 };
    private bool _checkingLogin;
    private bool _autoCheckUsed;
    private bool _guided;
    public string? LastAccountName { get; private set; }

    /// <summary>True only while the native browser is being created away from assistive-tech queries.</summary>
    internal bool BrowserIsQuiesced => !_browser.Visible && !_browser.Enabled;

    public WebLoginForm(string platform, IPlaylistImportService service, IAccessibleAnnouncer announcer, ILogger<WebLoginForm> logger)
    {
        _service = service;
        _announcer = announcer;
        _logger = logger;
        Text = PlatformName(platform) + "网页登录";
        Size = new Size(1050, 760);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        // The WebView2 environment creation awaits on the UI thread. A screen reader can query the control
        // during that wait, before its native accessibility bridge exists; keep it out of the exposed and
        // focusable UI until the initialization task has completed.
        _browser.Enabled = false;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, FlowDirection = FlowDirection.LeftToRight, TabStop = false };
        var refresh = new Button { Text = "刷新页面", Width = 100 };
        var clear = new Button { Text = "清除登录记录", Width = 120 };
        var cancel = new Button { Text = "取消", Width = 80 };
        WinFormsUi.Name(_done, "完成登录", 1);
        WinFormsUi.Name(refresh, "刷新页面", 2);
        WinFormsUi.Name(clear, "清除登录记录", 3);
        WinFormsUi.Name(cancel, "取消", 4);
        top.Controls.AddRange(new Control[] { _done, refresh, clear, cancel });
        Controls.Add(_browser);
        Controls.Add(_status);
        Controls.Add(top);
        refresh.Click += (_, _) =>
        {
            // Reloading is the documented recovery for a page that did not finish, but an uninitialized
            // native browser has no page to reload; expose the failure instead of crashing on Reload().
            if (_browser.CoreWebView2 is null)
            {
                _status.Text = "浏览器组件尚未初始化完成，无法刷新。";
                Announce(_status.Text, "web-login", AnnouncementKind.Error);
                return;
            }

            _autoCheckUsed = false;
            _browser.Reload();
        };
        clear.Click += (_, _) =>
        {
            // Reusing the stored session is the fast path, but it also means this window cannot switch
            // accounts by itself. Dropping the cookies and reloading the login page has to be one
            // command here, not a hidden folder the user has to find.
            if (_browser.CoreWebView2 is null) return;
            _browser.CoreWebView2.CookieManager.DeleteAllCookies();
            _guided = false;
            _autoCheckUsed = false;
            _loginCheck.Stop();
            _status.Text = "已清除浏览器登录记录，请在网页中重新登录。";
            Announce(_status.Text, "web-login");
            _browser.CoreWebView2.Navigate(LoginUrl(platform));
        };
        cancel.Click += (_, _) => Close();
        // Escape is the documented way out of every window in this application. A button without a
        // DialogResult still closes the form through its own handler, and the native dialog-key handling
        // reaches it from any of the toolbar buttons without the form having to preview the key.
        CancelButton = cancel;
        _done.Click += async (_, _) => await CompleteAsync(platform);
        _loginCheck.Tick += async (_, _) => await DetectLoginAsync(platform);
        FormClosed += (_, _) => _loginCheck.Stop();
        Shown += async (_, _) =>
        {
            try
            {
                _browser.Hide();
                await InitializeBrowserAsync();
                _browser.Visible = true;
                _browser.Enabled = true;
                _browser.TabStop = true;
                // The browser session deliberately survives this window: a user who is already signed in to
                // the site reaches the app with one Enter press instead of scanning a QR code again, and
                // the clear-session command below is the explicit way onto a different account.
                _browser.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    _status.Text = "请在页面中完成登录，登录后本窗口会自动关闭。需要手动确认时选择“完成登录”。";
                    // Sites redirect more than once; the instruction is the same every time, so it is spoken
                    // once instead of interrupting the user on each navigation.
                    if (!_guided) { _guided = true; Announce(_status.Text, "web-login"); }
                    // One automatic attempt per page load: a session cookie left over from a previous visit
                    // that the platform no longer accepts must not stay the last word for this window.
                    _autoCheckUsed = false;
                    _loginCheck.Start();
                    // The page is the task: keyboard focus belongs in it as soon as it is readable, and
                    // Shift+Tab from its first tab stop returns to 取消 / 清除登录记录 / 刷新页面 / 完成登录.
                    _browser.Focus();
                };
                _browser.Source = new Uri(LoginUrl(platform));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebView2 initialization failed");
                _status.Text = "浏览器组件初始化失败。请使用 Cookie 登录。";
                Announce(_status.Text, "web-login", AnnouncementKind.Error);
                refresh.Focus();
            }
        };
    }

    protected internal virtual Task InitializeBrowserAsync() => _browser.EnsureCoreWebView2Async();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void Dispose(bool disposing)
    {
        // This timer is not a child control, so Form disposal does not own it automatically.
        if (disposing) _loginCheck.Dispose();
        base.Dispose(disposing);
    }
    /// <summary>
    /// Hands the browser session to the import service, which is the only thing that can confirm a login:
    /// it calls the platform and gets the account back. Runs from the automatic watch and from 完成登录.
    /// </summary>
    private async Task CompleteAsync(string platform, bool automatic = false)
    {
        if (_browser.CoreWebView2 is null) return;
        try
        {
            var cookieText = await ReadCookieTextAsync(platform);
            if (string.IsNullOrWhiteSpace(cookieText))
            {
                _status.Text = "没有读取到登录 Cookie。";
                Announce(_status.Text, "web-login", AnnouncementKind.Error);
                return;
            }
            var account = await _service.LoginWithBrowserResultAsync(platform, cookieText, null, null);
            if (account is null)
            {
                _status.Text = automatic
                    ? "网页里已经有了登录记录，但平台没有确认。请在页面中重新登录、按“刷新页面”，或选择“完成登录”。"
                    : "登录状态保存失败，请确认已在网页中登录。";
                Announce(_status.Text, "web-login", AnnouncementKind.Error);
                return;
            }
            _loginCheck.Stop();
            LastAccountName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Platform : account.DisplayName;
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

    /// <summary>
    /// Watches the browser session for the platform's login marker and completes the sign-in on its own.
    /// Only one automatic attempt is made per page load: a marker the platform itself rejects would
    /// otherwise restart the same request every two seconds and repeat the same failure out loud.
    /// </summary>
    private async Task DetectLoginAsync(string platform)
    {
        if (_checkingLogin || _autoCheckUsed || _browser.CoreWebView2 is null) return;
        _checkingLogin = true;
        try
        {
            var cookieText = await ReadCookieTextAsync(platform);
            if (cookieText is null || !HasLoginCookies(platform, cookieText)) return;
            _autoCheckUsed = true;
            _loginCheck.Stop();
            await CompleteAsync(platform, automatic: true);
        }
        catch (Exception ex)
        {
            // The window is still usable: 完成登录 repeats the same read, and the status line says why.
            _logger.LogWarning(ex, "Web login detection failed");
        }
        finally
        {
            _checkingLogin = false;
        }
    }

    private async Task<string?> ReadCookieTextAsync(string platform)
    {
        var cookies = await _browser.CoreWebView2!.CookieManager.GetCookiesAsync(LoginUrl(platform));
        var cookieText = string.Join("; ", cookies.Select(cookie => cookie.Name + "=" + cookie.Value));
        return string.IsNullOrWhiteSpace(cookieText) ? null : cookieText;
    }

    /// <summary>
    /// The cookie each platform only sets after a real sign-in. Waiting for these keeps the window from
    /// closing on tracking cookies that are already there before the user typed anything.
    /// The cookie names are the ones the providers read: the Kugou web page writes KugooID plus t, while the APP scan
    /// writes userid plus token, so both spellings count, and an id of 0 is what an anonymous visit leaves.
    /// </summary>
    internal static bool HasLoginCookies(string platform, string cookieText) => platform switch
    {
        "netease" => HasCookieValue(cookieText, "MUSIC_U"),
        "qq" => HasCookieValue(cookieText, "uin") && !IsCookieValue(cookieText, "uin", "0")
            && (HasCookieValue(cookieText, "qm_keyst") || HasCookieValue(cookieText, "qqmusic_key")),
        "kg" => (HasCookieValue(cookieText, "KugooID") || HasCookieValue(cookieText, "userid"))
            && !IsCookieValue(cookieText, "KugooID", "0") && !IsCookieValue(cookieText, "userid", "0")
            && (HasCookieValue(cookieText, "t") || HasCookieValue(cookieText, "token")),
        "kw" => HasCookieValue(cookieText, "userid") && HasCookieValue(cookieText, "websid"),
        _ => false
    };

    private static bool HasCookieValue(string cookieText, string name)
    {
        foreach (var pair in cookieText.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var item = pair.Trim();
            var equals = item.IndexOf('=');
            if (equals <= 0) continue;
            if (string.Equals(item[..equals], name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item[(equals + 1)..])) return true;
        }
        return false;
    }

    private static bool IsCookieValue(string cookieText, string name, string value)
    {
        foreach (var pair in cookieText.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var item = pair.Trim();
            var equals = item.IndexOf('=');
            if (equals <= 0) continue;
            if (string.Equals(item[..equals], name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item[(equals + 1)..].Trim(), value, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>The official site whose own login button sets the session cookie the platform accepts.</summary>
    internal static string LoginUrl(string platform) => platform switch
    {
        "netease" => "https://music.163.com/",
        "qq" => "https://y.qq.com/",
        "kg" => "https://www.kugou.com/",
        "kw" => "https://www.kuwo.cn/",
        _ => "https://music.163.com/"
    };

    private static string PlatformName(string platform) => platform switch
    {
        "netease" => "网易云",
        "qq" => "QQ音乐",
        "kg" => "酷狗",
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
    private readonly Label _status = new() { Text = "正在获取二维码...", Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Name = "KugouLoginStatus", TabStop = false };
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
        KeyPreview = true;
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
        CancelButton = cancel;
        _timer.Tick += async (_, _) => await PollAsync();
        // Nothing else in the window is interactive, so the reading position starts on the command that can
        // recover a failed QR image and one Tab away from 取消.
        Shown += async (_, _) => { refresh.Focus(); await LoadAsync(); };
        FormClosed += (_, _) => _timer.Stop();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void Dispose(bool disposing)
    {
        // Polling continues after startup, so this owner must release the timer with the form.
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
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
            // The status line and its error announcement already carry the result, and the 刷新二维码 button
            // is the recovery command. A modal dialog on top of that steals the reading position and makes
            // the user dismiss a second copy of the same message, so the reason is reported in place.
            _status.Text = "获取二维码失败：" + ex.Message;
            Announce(_status.Text, "qr-login", AnnouncementKind.Error);
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
