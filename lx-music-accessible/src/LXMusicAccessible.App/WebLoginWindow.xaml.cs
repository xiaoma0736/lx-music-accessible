using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Services;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.App;

/// <summary>
/// 网页登录窗口（无障碍）：内嵌 WebView2 打开平台官网，用户直接在页面内登录。
/// 自动检测登录状态：周期性抓取 Cookie 并校验，一旦检测到已登录便自动关闭网页；
/// 自动检测失败时，用户可手动按“完成并获取Cookie”再检测一遍，成功同样自动关闭网页。
/// 登录成功后由父窗口弹窗提示“Cookie获取成功”。
/// 支持 网易云 / QQ音乐 / 酷狗 / 酷我 四个平台。
/// </summary>
public partial class WebLoginWindow : Window
{
    private readonly IPlaylistImportService _service;
    private readonly IAccessibleAnnouncer _announce;
    private readonly ILogger<WebLoginWindow> _logger;
    private readonly string _platform;

    private DispatcherTimer? _autoDetectTimer;
    private bool _checking;
    private bool _loginSucceeded;
    private string _loginActivityId = string.Empty;

    /// <summary>登录成功后的账号昵称（供父窗口弹窗提示）</summary>
    public string? LastAccountName { get; private set; }

    public WebLoginWindow(
        string platform,
        IPlaylistImportService service,
        IAccessibleAnnouncer announce,
        ILogger<WebLoginWindow> logger)
    {
        InitializeComponent();
        _platform = platform;
        PlatformNameText.Text = GetPlatformName(platform);
        _service = service;
        _announce = announce;
        _logger = logger;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void Announce(
        string message,
        AnnouncementKind kind = AnnouncementKind.Status,
        string? activityId = null)
    {
        _announce.Announce(new AccessibleAnnouncement(message, kind, activityId));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
            if (Browser.CoreWebView2 == null)
                throw new InvalidOperationException("浏览器组件未初始化");
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.IsWebMessageEnabled = false;
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Browser.CoreWebView2.SourceChanged += OnSourceChanged;
            Browser.Source = new Uri(GetPlatformUrl(_platform));
            StatusText.Text = "正在打开登录页面...";
            Announce($"正在打开{GetPlatformName(_platform)}登录页面，请稍候。");
            StartAutoDetect();
        }
        catch (Exception)
        {
            var correlationId = LoginDiagnostics.CreateCorrelationId();
            StatusText.Text = "网页组件初始化失败";
            _logger.LogWarning("网页登录组件初始化失败：{Diagnostic}",
                LoginDiagnostics.CreateSafeSummary(_platform, string.Empty,
                    LoginDiagnosticOutcome.Exception, LoginDiagnosticError.BrowserUnavailable, correlationId));
            Announce(LoginDiagnostics.CreateUserMessage(
                LoginDiagnosticOutcome.Exception, LoginDiagnosticError.BrowserUnavailable, correlationId));
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopAutoDetect();
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!e.IsSuccess)
            {
                StatusText.Text = "页面加载失败，可按“刷新页面”重试";
                return;
            }
            StatusText.Text = "请在该页面中登录账号，本窗口会自动检测登录状态；登录完成后也可按 Tab 到“完成并获取Cookie”按钮回车手动确认。";
            if (IsInitialLoad)
            {
                IsInitialLoad = false;
                Announce($"页面加载完成。请在该页面中登录{GetPlatformName(_platform)}账号，本窗口会自动检测登录状态并自动关闭，无需再按任何按钮。");
            }
            Browser.Focus();
        }));
    }

    private bool IsInitialLoad = true;

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            StatusText.Text = $"当前地址：{Browser.Source?.Host ?? ""}";
        }));
    }

    // ============ 自动检测登录状态 ============

    private void StartAutoDetect()
    {
        StopAutoDetect();
        _autoDetectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoDetectTimer.Tick += async (_, _) => await AutoDetectOnceAsync();
        _autoDetectTimer.Start();
    }

    private void StopAutoDetect()
    {
        if (_autoDetectTimer != null)
        {
            _autoDetectTimer.Stop();
            _autoDetectTimer = null;
        }
    }

    private async Task AutoDetectOnceAsync()
    {
        if (_checking || _loginSucceeded) return;
        _checking = true;
        try
        {
            var cookie = await CollectCookiesAsync();
            if (!LooksLoggedIn(_platform, cookie)) return; // 尚未登录，继续等待
            _loginActivityId = $"web-login:{Guid.NewGuid():N}";
            await TryLoginAsync(cookie, auto: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("网页登录轮询失败: Platform={Platform}, ErrorType={ErrorType}",
                _platform, ex.GetType().Name);
        }
        finally
        {
            _checking = false;
        }
    }

    // ============ 完成并获取 Cookie（手动再检测一遍） ============

    private async void DoneBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_loginSucceeded) return;
        DoneBtn.IsEnabled = false;
        StopAutoDetect(); // 手动检测期间暂停自动检测，避免并发
        _loginActivityId = $"web-login:{Guid.NewGuid():N}";
        var cookie = string.Empty;
        try
        {
            Announce("正在检测登录Cookie并校验，请稍候。", AnnouncementKind.Busy, _loginActivityId);
            cookie = await CollectCookiesAsync();
            if (string.IsNullOrEmpty(cookie))
            {
                Announce("尚未获取到登录Cookie，请确认已在页面中完成登录后再试。");
                return; // 保持网页打开，继续等待
            }
            var ok = await TryLoginAsync(cookie, auto: false);
            if (!ok)
            {
                var correlationId = LoginDiagnostics.CreateCorrelationId();
                var reportPath = LoginDiagnosticReportStore.Write(
                    System.IO.Path.Combine(AppDataPaths.ResolveDataDir(), "logs"), _platform, cookie,
                    LoginDiagnosticOutcome.Failure, LoginDiagnosticError.LoginRejected, correlationId);
                var safeMessage = LoginDiagnostics.CreateUserMessage(
                    LoginDiagnosticOutcome.Failure, LoginDiagnosticError.LoginRejected, correlationId);
                _logger.LogWarning("网页登录失败：{Diagnostic}",
                    LoginDiagnostics.CreateSafeSummary(_platform, cookie, LoginDiagnosticOutcome.Failure,
                        LoginDiagnosticError.LoginRejected, correlationId));
                Announce(safeMessage, AnnouncementKind.Error, _loginActivityId);

                if (reportPath == null)
                {
                    MessageBox.Show(this, safeMessage + "\n\n诊断报告保存失败。",
                        "获取Cookie失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    var delete = MessageBox.Show(this,
                        safeMessage + $"\n\n诊断报告已保存到：\n{reportPath}" +
                        "\n\n报告不应包含 Cookie、令牌、授权头或其他凭据。是否立即删除该报告？",
                        "获取Cookie失败", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                    if (delete == MessageBoxResult.Yes && !LoginDiagnosticReportStore.Delete(reportPath))
                        MessageBox.Show(this,
                            $"报告删除失败，请在文件资源管理器中手动删除：\n{reportPath}",
                            "删除诊断报告失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception)
        {
            var correlationId = LoginDiagnostics.CreateCorrelationId();
            LoginDiagnosticReportStore.Write(
                System.IO.Path.Combine(AppDataPaths.ResolveDataDir(), "logs"), _platform, cookie,
                LoginDiagnosticOutcome.Exception, LoginDiagnosticError.Unexpected, correlationId);
            _logger.LogWarning("网页登录异常：{Diagnostic}",
                LoginDiagnostics.CreateSafeSummary(_platform, cookie, LoginDiagnosticOutcome.Exception,
                    LoginDiagnosticError.Unexpected, correlationId));
            Announce(LoginDiagnostics.CreateUserMessage(
                LoginDiagnosticOutcome.Exception, LoginDiagnosticError.Unexpected, correlationId),
                AnnouncementKind.Error,
                _loginActivityId);
        }
        finally
        {
            if (!_loginSucceeded)
            {
                DoneBtn.IsEnabled = true;
                StartAutoDetect(); // 恢复自动检测
            }
        }
    }

    /// <summary>校验 Cookie；成功则自动关闭网页并返回 true（父窗口负责弹窗提示）</summary>
    private async Task<bool> TryLoginAsync(string cookie, bool auto)
    {
        if (_loginSucceeded) return true; // 已成功则直接返回，避免并发重复处理
        AccountInfo? acc;
        if (string.Equals(_platform, "kw", StringComparison.OrdinalIgnoreCase))
        {
            // 酷我接口有反爬（Secret 头 + UUIDv1 reqId），只能在页面内拉取个人歌单；
            // 登录本身以 userid/websid Cookie 判定，歌单由页面内脚本拉取后随账号一并保存。
            var br = await FetchKuwoPlaylistsAsync();
            var remote = ToRemotePlaylists(br);
            acc = await _service.LoginWithBrowserResultAsync(_platform, cookie, br?.DisplayName, remote);
            if (acc != null && br != null && !br.Ok && remote == null)
                _logger.LogWarning("酷我登录成功但未能在页面内获取个人歌单，接口状态：{Statuses}",
                    br.Results == null ? "(无)" : string.Join(",", br.Results.Select(r => r.Status?.ToString() ?? "unknown")));
        }
        else
        {
            acc = await _service.LoginWithCookieAsync(_platform, cookie);
        }
        if (acc == null) return false;

        _loginSucceeded = true;
        LastAccountName = acc.DisplayName;
        StopAutoDetect();
        DoneBtn.IsEnabled = false;
        if (auto)
        {
            StatusText.Text = $"已自动检测到登录状态，登录成功：{acc.DisplayName}。正在关闭网页...";
            Announce($"已自动检测到登录状态，Cookie获取成功，登录账号：{acc.DisplayName}。", AnnouncementKind.Success, _loginActivityId);
        }
        else
        {
            StatusText.Text = $"Cookie获取成功，登录成功：{acc.DisplayName}。正在关闭网页...";
            Announce($"Cookie获取成功，登录成功：{acc.DisplayName}。", AnnouncementKind.Success, _loginActivityId);
        }
        DialogResult = true; // 自动关闭网页
        return true;
    }

    // ============ 酷我（kw）专用：页面内拉取个人歌单 ============

    /// <summary>把页面内拉取到的酷我歌单转换成通用远程歌单模型</summary>
    private static List<RemotePlaylist>? ToRemotePlaylists(KuwoBrowserResult? br)
    {
        if (br?.Playlists == null || br.Playlists.Count == 0) return null;
        return br.Playlists.Select(p => new RemotePlaylist
        {
            Platform = "kw",
            RemoteId = p.Id,
            Name = p.Name,
            TrackCount = p.Total,
            IsLiked = p.IsLiked,
            IsCollected = p.IsCollected,
            CoverUrl = p.Pic
        }).ToList();
    }

    /// <summary>
    /// 酷我反爬（Secret 校验）只能在页面上下文中通过：
    /// 请求头 Secret=f(Hm_Iuvt cookie 值, cookie 名)，reqId 必须是 UUID v1，且需带 Accept 头。
    /// 此脚本在已登录的 www.kuwo.cn 页面内依次尝试多个个人歌单接口，返回第一个有歌单数据的成功结果。
    /// 注意：ExecuteScriptAsync 不会等待异步 Promise，故脚本先把结果写入 window.__kwFetch，再在 C# 侧轮询读取。
    /// </summary>
    private async Task<KuwoBrowserResult?> FetchKuwoPlaylistsAsync()
    {
        if (Browser.CoreWebView2 == null) return null;
        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(KuwoFetchScript);
            // 轮询读取页面内异步拉取结果（脚本完成后写入 window.__kwFetch）
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(200);
                var json = await Browser.CoreWebView2.ExecuteScriptAsync(
                    "window.__kwFetch && window.__kwFetch.done ? { done: true, result: window.__kwFetch.result, error: window.__kwFetch.error } : null");
                if (string.IsNullOrEmpty(json) || json == "null") continue;
                var envelope = JsonSerializer.Deserialize<KuwoFetchEnvelope>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (envelope == null || !envelope.Done) continue;
                if (!string.IsNullOrEmpty(envelope.Error))
                {
                    _logger.LogWarning("酷我页面内拉取个人歌单出错：{Error}", envelope.Error);
                    return null;
                }
                if (string.IsNullOrEmpty(envelope.Result))
                {
                    _logger.LogWarning("酷我页面内拉取个人歌单：未返回数据");
                    return null;
                }
                return JsonSerializer.Deserialize<KuwoBrowserResult>(envelope.Result,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            _logger.LogWarning("酷我页面内拉取个人歌单超时");
            return null;
        }
        catch (Exception)
        {
            _logger.LogWarning("酷我页面内拉取个人歌单失败：{Diagnostic}",
                LoginDiagnostics.CreateSafeSummary(_platform, string.Empty, LoginDiagnosticOutcome.Exception,
                    LoginDiagnosticError.Unexpected, LoginDiagnostics.CreateCorrelationId()));
            return null;
        }
    }

    /// <summary>酷我页面内拉取歌单的 JS 脚本（同步启动，异步结果写入 window.__kwFetch；算法来自 www.kuwo.cn 前端）</summary>
    private const string KuwoFetchScript = """
        (() => {
          window.__kwFetch = { done: false, result: null, error: null };
          const NAME = 'Hm_Iuvt_cdb524f42f23cer9b268564v7y735ewrq2324';
          const getCookie = function (t) {
            const n = document.cookie;
            const i = n.indexOf(t + '=');
            if (i === -1) return null;
            const s = i + t.length + 1;
            const e = n.indexOf(';', s);
            return unescape(n.substring(s, e === -1 ? n.length : e));
          };
          const secretOf = function (t, e) {
            if (e == null || e.length <= 0) return null;
            let n = '';
            for (let i = 0; i < e.length; i++) n += e.charCodeAt(i).toString();
            const r = Math.floor(n.length / 5);
            const o = parseInt(n.charAt(r) + n.charAt(2 * r) + n.charAt(3 * r) + n.charAt(4 * r) + n.charAt(5 * r));
            const c = Math.ceil(e.length / 2);
            const l = Math.pow(2, 31) - 1;
            if (o < 2) return null;
            let d = Math.round(1e9 * Math.random()) % 1e8;
            for (n += d; n.length > 10;) n = (parseInt(n.substring(0, 10)) + parseInt(n.substring(10, n.length))).toString();
            n = (o * n + c) % l;
            let f = '', h = '';
            for (let i = 0; i < t.length; i++) {
              const v = parseInt(t.charCodeAt(i) ^ Math.floor((n / l) * 255));
              h += (v < 16 ? '0' + v.toString(16) : v.toString(16));
              n = (o * n + c) % l;
            }
            d = d.toString(16);
            while (d.length < 8) d = '0' + d;
            return h + d;
          };
          const uuidv1 = function () {
            const m = new Date().getTime();
            let tl = (m & 0xffffffff).toString(16); while (tl.length < 8) tl = '0' + tl;
            let tm = ((m / 1000) & 0xffff).toString(16); while (tm.length < 4) tm = '0' + tm;
            let th = (((m / 4294967296) & 0x0fff) + 0x1000).toString(16); while (th.length < 4) th = '0' + th;
            let cs = (0x8000 | Math.floor(Math.random() * 0x3fff)).toString(16);
            let nd = Math.floor(Math.random() * 0xffffffff).toString(16).padStart(8, '0') + Math.floor(Math.random() * 0xffff).toString(16).padStart(4, '0');
            return tl + '-' + tm + '-' + th + '-' + cs + '-' + nd;
          };
          const readNuxtUser = function () {
            try {
              const u = (window.__NUXT__ && window.__NUXT__.state && window.__NUXT__.state.userInfo && window.__NUXT__.state.userInfo.userInfo) || {};
              return u;
            } catch (e) { return {}; }
          };
          const run = async () => {
            try {
              const tok = getCookie(NAME);
              const userid = getCookie('userid') || '';
              const sid = getCookie('sid') || getCookie('websid') || '';
              const username = getCookie('username') || '';
              const nuxtUser = readNuxtUser();
              const nuxtName = nuxtUser.uname || nuxtUser.username || nuxtUser.nickname || nuxtUser.nick || '';
              const secret = tok ? secretOf(tok, NAME) : null;
              const results = [];
              const mkUrl = function (path, extra) {
                let q = 'pn=1&rn=100&httpsStatus=1&reqId=' + uuidv1() + '&plat=web_www&from=';
                if (userid) q += '&loginUid=' + encodeURIComponent(userid) + '&loginSid=' + encodeURIComponent(sid);
                if (extra) q += '&' + extra;
                return 'https://www.kuwo.cn' + path + '?' + q;
              };
              const candidates = [
                { path: '/api/www/playlist/getUserPlayList', extra: '' },
                { path: '/api/www/playlist/getUserPlayList', extra: 'uid=' + userid },
                { path: '/api/www/playlist/getMyPlayList', extra: '' },
                { path: '/api/www/playlist/getMyPlaylist', extra: '' },
                { path: '/api/www/user/playlist', extra: '' },
                { path: '/api/www/playlist/getUserPlaylist', extra: '' },
                { path: '/api/www/user/getUserPlayList', extra: '' },
                { path: '/api/www/playlist/getUserPlayListInfo', extra: '' }
              ];
              const pickPlaylists = function (j) {
                const d = (j && j.data) || null;
                if (!d) return null;
                let arr = null;
                if (Array.isArray(d)) arr = d;
                else if (d.data && Array.isArray(d.data)) arr = d.data;
                else if (d.list && Array.isArray(d.list)) arr = d.list;
                if (!arr) return null;
                const items = arr.filter(function (it) { return it && it.id != null && (it.name || it.title); });
                if (!items.length) return null;
                return items.map(function (it) {
                  const total = it.total != null ? it.total : (it.num != null ? it.num : (it.musicNum != null ? it.musicNum : 0));
                  const name = it.name || it.title || '';
                  const id = String(it.id);
                  const pic = it.pic || it.img || it.pic300 || it.img700 || '';
                  const isCollected = !!it.isCollected || it.collect == 1;
                  const isLiked = !!it.isLiked || /love|favorite|liked/i.test(id) || /我喜欢的/.test(name);
                  return { id: id, name: name, total: parseInt(total) || 0, pic: pic, isCollected: isCollected, isLiked: isLiked };
                });
              };
              let okResult = null;
              for (const cd of candidates) {
                try {
                  const url = mkUrl(cd.path, cd.extra);
                  const resp = await fetch(url, { headers: { 'Secret': secret, 'Referer': location.origin + '/', 'Accept': 'application/json, text/plain, */*' } });
                  const text = await resp.text();
                  results.push({ path: cd.path + (cd.extra ? '?' + cd.extra : ''), status: resp.status, body: text.substring(0, 200) });
                  let j = null;
                  try { j = JSON.parse(text); } catch (e) { j = null; }
                  if (j && (j.code === 200 || j.success === true)) {
                    const pls = pickPlaylists(j);
                    if (pls && pls.length) { okResult = { path: cd.path, playlists: pls }; break; }
                  }
                } catch (e) {
                  results.push({ path: cd.path, err: String((e && e.message) || e) });
                }
              }
              const displayName = username || nuxtName || ('酷我用户' + userid);
              if (okResult) {
                window.__kwFetch = { done: true, result: JSON.stringify({ ok: true, displayName: displayName, apiPath: okResult.path, playlists: okResult.playlists, results: results }), error: null };
              } else {
                window.__kwFetch = { done: true, result: JSON.stringify({ ok: false, displayName: displayName, results: results }), error: null };
              }
            } catch (e) {
              window.__kwFetch = { done: true, result: null, error: String((e && e.message) || e) };
            }
          };
          run();
          return 'started';
        })()
        """;

    private async Task<string> CollectCookiesAsync()
    {
        if (Browser.CoreWebView2 == null) return string.Empty;

        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 来源1：DevTools Protocol 全量 Cookie（含 HttpOnly、跨所有域）
        await CollectFromDevToolsAsync(parts, seen);
        // 来源2：CookieManager 按域名（与来源1合并，避免任一来源遗漏关键登录令牌）
        await CollectFromCookieManagerAsync(parts, seen);

        var cookie = string.Join("; ", parts);
        _logger.LogInformation("网页登录 Cookie 采集完成：{Diagnostic}",
            LoginDiagnostics.CreateSafeSummary(_platform, cookie, LoginDiagnosticOutcome.Success,
                LoginDiagnosticError.None, LoginDiagnostics.CreateCorrelationId()));
        return cookie;
    }

    private async Task CollectFromDevToolsAsync(List<string> parts, HashSet<string> seen)
    {
        try
        {
            var json = await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.getAllCookies", "{}");
            if (string.IsNullOrEmpty(json)) return;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("cookies", out var arr) || arr.ValueKind != JsonValueKind.Array) return;

            var hosts = GetCookieHosts(_platform);
            foreach (var c in arr.EnumerateArray())
            {
                var name = GetJsonString(c, "name");
                var value = GetJsonString(c, "value");
                var domain = GetJsonString(c, "domain");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(domain)) continue;
                if (!DomainMatches(hosts, domain)) continue;
                if (!seen.Add(name)) continue;
                parts.Add($"{name}={value}");
            }
        }
        catch (Exception)
        {
            _logger.LogWarning("DevTools 获取 Cookie 失败：{Diagnostic}",
                LoginDiagnostics.CreateSafeSummary(_platform, string.Empty, LoginDiagnosticOutcome.Exception,
                    LoginDiagnosticError.CookieCollectionFailed, LoginDiagnostics.CreateCorrelationId()));
        }
    }

    private async Task CollectFromCookieManagerAsync(List<string> parts, HashSet<string> seen)
    {
        if (Browser.CoreWebView2 == null) return;
        foreach (var uri in GetCookieDomains(_platform))
        {
            try
            {
                var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(uri);
                foreach (var c in cookies)
                {
                    if (string.IsNullOrEmpty(c.Name)) continue;
                    if (!seen.Add(c.Name)) continue;
                    parts.Add($"{c.Name}={c.Value}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Cookie 域读取失败: Platform={Platform}, ErrorType={ErrorType}",
                    _platform, ex.GetType().Name);
            }
        }
    }

    private static string[] GetCookieHosts(string platform) =>
        GetCookieDomains(platform).Select(u => new Uri(u).Host).ToArray();

    private static bool DomainMatches(string[] hosts, string cookieDomain)
    {
        var d = cookieDomain?.TrimStart('.')?.ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(d)) return false;
        return hosts.Any(h =>
        {
            var host = h.ToLowerInvariant();
            return host == d
                || host.EndsWith("." + d, StringComparison.Ordinal)
                || d.EndsWith("." + host, StringComparison.Ordinal);
        });
    }

    private static string GetJsonString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? string.Empty;
        return string.Empty;
    }

    /// <summary>快速判断 Cookie 是否已呈现“已登录”特征（避免未登录时频繁联网校验）</summary>
    private static bool LooksLoggedIn(string platform, string cookie)
    {
        if (string.IsNullOrEmpty(cookie)) return false;
        switch (platform)
        {
            case "netease":
                return cookie.Contains("MUSIC_U=", StringComparison.OrdinalIgnoreCase);
            case "qq":
                var uin = GetCookieValue(cookie, "uin");
                var hasUin = !string.IsNullOrEmpty(uin) && uin != "0";
                var hasKey = cookie.Contains("qm_keyst=", StringComparison.OrdinalIgnoreCase)
                          || cookie.Contains("qqmusic_key=", StringComparison.OrdinalIgnoreCase);
                return hasUin && hasKey;
            case "kg":
                return !string.IsNullOrEmpty(GetCookieValue(cookie, "KugooID"))
                    && !string.IsNullOrEmpty(GetCookieValue(cookie, "t"));
            case "kw":
                var kwUid = GetCookieValue(cookie, "userid");
                return !string.IsNullOrEmpty(kwUid) && kwUid != "0"
                    && !string.IsNullOrEmpty(GetCookieValue(cookie, "websid"));
            default:
                return cookie.Length > 10;
        }
    }

    private static string GetCookieValue(string cookie, string name)
    {
        foreach (var part in cookie.Split(';', StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }

        return string.Empty;
    }

    // ============ 辅助 ============

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 != null)
            Browser.CoreWebView2.Reload();
        Announce("正在刷新页面。");
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        StopAutoDetect();
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            StopAutoDetect();
            DialogResult = false;
            e.Handled = true;
        }
    }

    private static string GetPlatformName(string platform) => platform switch
    {
        "netease" => "网易云音乐",
        "qq" => "QQ音乐",
        "kg" => "酷狗音乐",
        "kw" => "酷我音乐",
        _ => "音乐平台"
    };

    private static string GetPlatformUrl(string platform) => platform switch
    {
        "netease" => "https://music.163.com/",
        "qq" => "https://y.qq.com/",
        "kg" => "https://www.kugou.com/",
        "kw" => "https://www.kuwo.cn/",
        _ => "https://music.163.com/"
    };

    private static string[] GetCookieDomains(string platform) => platform switch
    {
        "netease" => new[] { "https://music.163.com/" },
        "qq" => new[] { "https://y.qq.com/", "https://c.y.qq.com/", "https://u.y.qq.com/", "https://graph.qq.com/", "https://ptlogin2.qq.com/", "https://ssl.ptlogin2.qq.com/", "https://xui.ptlogin2.qq.com/", "https://qq.com/" },
        "kg" => new[] { "https://www.kugou.com/", "https://kugou.com/", "https://passport.kugou.com/", "https://login.kugou.com/" },
        "kw" => new[] { "https://www.kuwo.cn/", "https://kuwo.cn/" },
        _ => new[] { "https://music.163.com/" }
    };
}

/// <summary>酷我页面内拉取个人歌单的结果</summary>
public class KuwoBrowserResult
{
    public bool Ok { get; set; }
    public string? DisplayName { get; set; }
    public string? ApiPath { get; set; }
    public List<KuwoBrowserPlaylist>? Playlists { get; set; }
    public List<KuwoEndpointResult>? Results { get; set; }
}

/// <summary>酷我页面内拉取到的单个歌单</summary>
public class KuwoBrowserPlaylist
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Total { get; set; }
    public string? Pic { get; set; }
    public bool IsLiked { get; set; }
    public bool IsCollected { get; set; }
}

/// <summary>酷我歌单接口探测结果（用于诊断）</summary>
public class KuwoEndpointResult
{
    public string Path { get; set; } = string.Empty;
    public int? Status { get; set; }
    public string? Body { get; set; }
    public string? Err { get; set; }
}

/// <summary>酷我页面内异步拉取脚本的执行信封（C# 轮询读取 window.__kwFetch）</summary>
public class KuwoFetchEnvelope
{
    public bool Done { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
}
