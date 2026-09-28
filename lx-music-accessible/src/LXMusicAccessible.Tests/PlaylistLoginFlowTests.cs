using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Core.MusicApi;
using Microsoft.Extensions.Logging.Abstractions;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>The platform command, the login-method window it opens, and what each sign-in path reports back.</summary>
public sealed class PlaylistLoginFlowTests
{
    [Fact]
    public void Platform_without_saved_account_offers_login_methods_for_that_platform()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var login = new ScriptedLoginFactory();
            using var form = CreateForm(new FakeImportService(), login);
            Click(form, "网易云音乐");
            await WaitFor(() => login.Platforms.Count == 1, "the login chooser");

            Assert.Equal(new[] { "netease" }, login.Platforms);
            Assert.Same(form, login.Owners.Single());
            Assert.Equal("已选择平台，请登录或加载歌单。", Status(form));
            Assert.Same(Required(form, "网易云音乐"), form.ActiveControl);
        });
    }

    [Fact]
    public void Valid_saved_account_hides_cookie_login_controls_and_loads_playlists()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var service = new FakeImportService
            {
                Account = new AccountInfo { Platform = "netease", DisplayName = "Tester" }
            };
            service.Playlists.Add(new RemotePlaylist { Name = "Favorite" });
            using var form = CreateForm(service, new ScriptedLoginFactory(service));
            Click(form, "网易云音乐");
            await WaitFor(() => service.PlaylistCalls == 1, "playlist verification");

            Assert.False(Required(form, "CookieCaption").Visible);
            Assert.False(Required(form, "Cookie").Visible);
            Assert.False(Required(form, "使用 Cookie 登录").Visible);
            Assert.Contains("已加载个人歌单", Status(form), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Cookie_choice_focuses_the_cookie_box_and_names_the_command_that_finishes_it()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var login = new ScriptedLoginFactory { Method = PlaylistLoginMethod.Cookie };
            using var form = CreateForm(new FakeImportService(), login);
            Click(form, "QQ音乐");
            await WaitFor(() => login.Platforms.Count == 1, "the login chooser");

            Assert.Equal("qq", login.Platforms.Single());
            Assert.Same(Required(form, "Cookie"), form.ActiveControl);
            Assert.Contains("使用 Cookie 登录", Status(form), StringComparison.Ordinal);
            Assert.Equal(0, login.WebCalls);
            Assert.Equal(0, login.QrCalls);
        });
    }

    [Fact]
    public void Web_login_success_verifies_the_new_session_and_loads_playlists()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var service = new FakeImportService();
            service.Playlists.Add(new RemotePlaylist { Name = "Favorite" });
            var login = new ScriptedLoginFactory(service)
            {
                Method = PlaylistLoginMethod.Web,
                WebResult = new PlaylistLoginDialogResult(true, "Tester")
            };
            using var form = CreateForm(service, login);
            Click(form, "网易云音乐");
            await WaitFor(() => service.PlaylistCalls == 1, "playlist verification");

            Assert.Equal(1, login.WebCalls);
            Assert.Equal(0, login.QrCalls);
            Assert.Equal("已登录：Tester", ((Label)Required(form, "ImportAccountStatus")).Text);
            Assert.Contains("已加载个人歌单", Status(form), StringComparison.Ordinal);
            Assert.Same(Required(form, "个人歌单"), form.ActiveControl);
            Assert.Single(((ListBox)Required(form, "个人歌单")).Items.Cast<object>());
        });
    }

    [Fact]
    public void Web_login_that_the_platform_never_confirms_stays_on_the_platform_command()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var login = new ScriptedLoginFactory { Method = PlaylistLoginMethod.Web, WebResult = new PlaylistLoginDialogResult(false) };
            using var form = CreateForm(new FakeImportService(), login);
            Click(form, "酷我音乐");
            await WaitFor(() => login.Platforms.Count == 1, "the failed web login");

            Assert.Contains("网页登录未完成", Status(form), StringComparison.Ordinal);
            Assert.Contains("未登录", Status(form), StringComparison.Ordinal);
            Assert.Same(Required(form, "酷我音乐"), form.ActiveControl);
        });
    }

    [Fact]
    public void Kugou_qr_success_verifies_the_new_session_and_loads_playlists()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var service = new FakeImportService();
            service.Playlists.Add(new RemotePlaylist { Name = "Kugou list" });
            var login = new ScriptedLoginFactory(service)
            {
                Method = PlaylistLoginMethod.KugouQr,
                QrResult = true
            };
            using var form = CreateForm(service, login);
            Click(form, "酷狗音乐");
            await WaitFor(() => service.PlaylistCalls == 1, "playlist verification");

            Assert.Equal(1, login.QrCalls);
            Assert.Equal(0, login.WebCalls);
            Assert.Equal("已登录：KugouUser", ((Label)Required(form, "ImportAccountStatus")).Text);
            Assert.Same(Required(form, "个人歌单"), form.ActiveControl);
        });
    }

    [Fact]
    public void Cached_platform_login_is_verified_and_loads_playlists_without_login_method()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var service = new FakeImportService
            {
                Account = new AccountInfo { Platform = "netease", DisplayName = "Tester" }
            };
            service.Playlists.Add(new RemotePlaylist { Name = "Cached list" });
            var login = new ScriptedLoginFactory(service);
            using var form = CreateForm(service, login);
            Click(form, "网易云音乐");
            await WaitFor(() => service.PlaylistCalls == 1, "the saved login check");

            Assert.Empty(login.Platforms);
            Assert.Equal(0, login.WebCalls);
            Assert.Equal(0, login.QrCalls);
            Assert.Contains("已加载个人歌单", Status(form), StringComparison.Ordinal);
            var playlists = Assert.IsType<ListBox>(Required(form, "个人歌单"));
            Assert.Single(playlists.Items.Cast<object>());
            Assert.Same(playlists, form.ActiveControl);
        });
    }

    [Fact]
    public void Invalid_cached_login_falls_back_to_the_login_method()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var service = new FakeImportService
            {
                Account = new AccountInfo { Platform = "qq", DisplayName = "Old session" },
                PlaylistException = new InvalidOperationException("session expired")
            };
            var login = new ScriptedLoginFactory(service) { Method = PlaylistLoginMethod.Cookie };
            using var form = CreateForm(service, login);
            Click(form, "QQ音乐");
            await WaitFor(() => login.Platforms.Count == 1, "the login fallback");

            Assert.Equal(1, service.PlaylistCalls);
            Assert.Contains("已保存登录状态无效", Status(form), StringComparison.Ordinal);
            Assert.Same(Required(form, "Cookie"), form.ActiveControl);
        });
    }

    [Fact]
    public void Login_method_lists_offer_the_ways_the_user_guide_promises_per_platform()
    {
        WinFormsTestRunner.Run(() =>
        {
            // All four offer the website and the cookie paste, Kugou additionally the in-app QR window.
            Assert.Equal(new[] { "网页登录", "粘贴 Cookie 登录" }, MethodsOf("netease"));
            Assert.Equal(new[] { "网页登录", "粘贴 Cookie 登录" }, MethodsOf("qq"));
            Assert.Equal(new[] { "网页登录", "粘贴 Cookie 登录", "酷狗 APP 扫码登录" }, MethodsOf("kg"));
            Assert.Equal(new[] { "网页登录", "粘贴 Cookie 登录" }, MethodsOf("kw"));
        });

        static string[] MethodsOf(string platform)
        {
            using var form = new PlaylistLoginMethodForm(platform);
            var list = Assert.IsType<ListBox>(form.Controls.Find("登录方式", true).Single());
            return list.Items.Cast<ObjectBox<PlaylistLoginMethod>>().Select(item => item.Name).ToArray();
        }
    }

    [Fact]
    public void Web_login_waits_for_the_session_cookie_the_platform_actually_sets()
    {
        Assert.True(WebLoginForm.HasLoginCookies("netease", "MUSIC_U=abc; other=1"));
        Assert.False(WebLoginForm.HasLoginCookies("netease", "other=1; NSC=abc"));
        Assert.True(WebLoginForm.HasLoginCookies("qq", "uin=123456; qm_keyst=key"));
        Assert.True(WebLoginForm.HasLoginCookies("qq", "uin=123456; qqmusic_key=key"));
        // An anonymous visit already leaves uin=0 behind, which must not read as a signed-in session.
        Assert.False(WebLoginForm.HasLoginCookies("qq", "uin=0; qm_keyst=; NSC=abc"));
        Assert.True(WebLoginForm.HasLoginCookies("kg", "userid=99; token=t-1; dfid=-"));
        Assert.False(WebLoginForm.HasLoginCookies("kg", "userid=99; token="));
        // The Kugou web page writes different names from the APP scan: KugooID plus t. Both are a session.
        Assert.True(WebLoginForm.HasLoginCookies("kg", "KugooID=88; t=abc; dfid=-"));
        Assert.False(WebLoginForm.HasLoginCookies("kg", "KugooID=0; t=abc"));
        Assert.True(WebLoginForm.HasLoginCookies("kw", "userid=7; websid=w-1"));
        Assert.False(WebLoginForm.HasLoginCookies("kw", "userid=7"));
    }

    [Fact]
    public void Every_platform_opens_its_own_site_instead_of_a_shared_fallback()
    {
        Assert.Equal("https://music.163.com/", WebLoginForm.LoginUrl("netease"));
        Assert.Equal("https://y.qq.com/", WebLoginForm.LoginUrl("qq"));
        Assert.Equal("https://www.kugou.com/", WebLoginForm.LoginUrl("kg"));
        Assert.Equal("https://www.kuwo.cn/", WebLoginForm.LoginUrl("kw"));
        Assert.Equal(4, new[] { "netease", "qq", "kg", "kw" }.Select(WebLoginForm.LoginUrl).Distinct().Count());
    }

    /// <summary>
    /// A stored browser session makes returning users fast, but the window still has to offer the command that
    /// ends it: without 清除登录记录 there is no way onto a second account, so the toolbar is the contract.
    /// </summary>
    [Fact]
    public void Web_login_window_offers_the_recovery_commands_around_the_page()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var form = new WebLoginForm(
                "kg", new FakeImportService(), new ScriptedLoginFactory(), NullLogger<WebLoginForm>.Instance);
            string[] names = { "\u5b8c\u6210\u767b\u5f55", "\u5237\u65b0\u9875\u9762", "\u6e05\u9664\u767b\u5f55\u8bb0\u5f55", "\u53d6\u6d88" };
            for (var i = 0; i < names.Length; i++)
            {
                var button = (Button)Required(form, names[i]);
                Assert.Equal(i + 1, button.TabIndex);
                Assert.True(button.TabStop);
                Assert.Equal(names[i], button.AccessibleName);
                Assert.Equal(names[i], button.Text);
            }
            // Escape is the documented way out of every window, and it has to stay the cancel command.
            Assert.Same(Required(form, names[names.Length - 1]), form.CancelButton);
        });
    }

    [Fact]
    public void Web_login_browser_is_quiesced_until_WebView2_initialization_completes()
    {
        WinFormsTestRunner.Run(() =>
        {
            // Initialization runs while a screen reader is already attached. Leaving the browser visible
            // during that STA wait lets the reader query a not-yet-connected accessibility child and
            // turns every platform's login window into "initialization failed".
            using var form = new RecordingInitializationForm(
                "netease", new FakeImportService(), new ScriptedLoginFactory());
            form.Show();
            Application.DoEvents();

            Assert.True(form.BrowserWasQuiescedDuringInitialization, "WebView2 was exposed before initialization completed.");
            Assert.False(form.BrowserIsQuiesced, "WebView2 remained hidden after initialization completed.");
        });
    }
    [Fact]
    public void Refresh_does_not_touch_the_browser_after_initialization_fails()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var form = new FailedInitializationForm(
                "netease", new FakeImportService(), new ScriptedLoginFactory());
            form.Show();
            Application.DoEvents();

            var status = (Label)Required(form, "WebLoginStatus");
            Assert.Contains("浏览器组件初始化失败", status.Text, StringComparison.Ordinal);

            // This used to call WebView2.Reload() on an uninitialized instance and crash the app.
            ((Button)Required(form, "刷新页面")).PerformClick();
            Application.DoEvents();

            Assert.Contains("尚未初始化完成", status.Text, StringComparison.Ordinal);
        });
    }

    private sealed class FailedInitializationForm : WebLoginForm
    {
        public FailedInitializationForm(
            string platform,
            IPlaylistImportService service,
            IAccessibleAnnouncer announcer)
            : base(platform, service, announcer, NullLogger<WebLoginForm>.Instance)
        {
        }

        protected internal override Task InitializeBrowserAsync()
            => Task.FromException(new InvalidOperationException("WebView2 initialization failed"));
    }
    private static PlaylistImportForm CreateForm(FakeImportService service, ScriptedLoginFactory login)
    {
        var form = new PlaylistImportForm(service, login, login, _ => { });
        form.Show();
        Application.DoEvents();
        return form;
    }

    private static void Click(Form form, string name)
    {
        ((Button)Required(form, name)).PerformClick();
        Application.DoEvents();
    }

    private static async Task WaitFor(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("The awaited condition was not reached: " + description);
    }

    private static string Status(Form form) => ((Label)Required(form, "ImportStatus")).Text;

    private static Control Required(Control root, string name)
    {
        var matches = root.Controls.Find(name, true);
        Assert.True(matches.Length == 1, name + " count=" + matches.Length);
        return matches[0];
    }

    private sealed class ScriptedLoginFactory : IPlaylistLoginWindowFactory, IAccessibleAnnouncer
    {
        private readonly FakeImportService? _service;

        public ScriptedLoginFactory(FakeImportService? service = null) => _service = service;

        public List<string> Platforms { get; } = new();
        public List<Form?> Owners { get; } = new();
        public List<AccessibleAnnouncement> Announcements { get; } = new();
        public bool SpeechEnabled { get; set; }
        public PlaylistLoginMethod? Method { get; set; }
        public PlaylistLoginDialogResult WebResult { get; set; } = new(false);
        public bool QrResult { get; set; }
        public int WebCalls { get; private set; }
        public int QrCalls { get; private set; }

        public PlaylistLoginMethod? SelectLoginMethod(string platform, Form? owner)
        {
            Platforms.Add(platform);
            Owners.Add(owner);
            return Method;
        }

        public PlaylistLoginDialogResult ShowWebLogin(string platform, Form? owner)
        {
            WebCalls++;
            if (WebResult.Succeeded && _service is not null)
                _service.Account = new AccountInfo { Platform = platform, DisplayName = WebResult.AccountName };
            return WebResult;
        }

        public bool ShowKugouQrLogin(Form? owner)
        {
            QrCalls++;
            if (QrResult && _service is not null)
                _service.Account = new AccountInfo { Platform = "kg", DisplayName = "KugouUser" };
            return QrResult;
        }

        public void Announce(AccessibleAnnouncement announcement) => Announcements.Add(announcement);
    }

    private sealed class RecordingInitializationForm : WebLoginForm
    {
        public bool BrowserWasQuiescedDuringInitialization { get; private set; }

        public RecordingInitializationForm(
            string platform,
            IPlaylistImportService service,
            IAccessibleAnnouncer announcer)
            : base(platform, service, announcer, NullLogger<WebLoginForm>.Instance)
        {
        }

        protected internal override Task InitializeBrowserAsync()
        {
            BrowserWasQuiescedDuringInitialization = BrowserIsQuiesced;
            return Task.CompletedTask;
        }
    }
    private sealed class FakeImportService : IPlaylistImportService
    {
        public AccountInfo? Account { get; set; }
        public List<RemotePlaylist> Playlists { get; } = new();
        public Exception? PlaylistException { get; set; }
        public int PlaylistCalls { get; private set; }
        public IReadOnlyList<IPlaylistImportProvider> Providers => Array.Empty<IPlaylistImportProvider>();
        public IPlaylistImportProvider? GetProvider(string platformId) => null;
        public Task EnsureAccountsLoadedAsync() => Task.CompletedTask;
        public AccountInfo? GetAccount(string platformId) => Account;
        public Task<AccountInfo?> LoginWithCookieAsync(string platformId, string cookieText) => Task.FromResult(Account);
        public Task<AccountInfo?> LoginWithBrowserResultAsync(string platformId, string cookieText, string? displayName, List<RemotePlaylist>? playlists) => Task.FromResult(Account);
        public Task LogoutAsync(string platformId) => Task.CompletedTask;
        public Task<List<RemotePlaylist>> GetMyPlaylistsAsync(string platformId)
        {
            PlaylistCalls++;
            return PlaylistException is null
                ? Task.FromResult(Playlists.ToList())
                : Task.FromException<List<RemotePlaylist>>(PlaylistException);
        }

        public Task<List<MusicInfo>> GetPlaylistSongsPreviewAsync(string platformId, RemotePlaylist remote) => Task.FromResult(new List<MusicInfo>());
        public Task<PlaylistImportResult> ImportPlaylistAsync(string platformId, RemotePlaylist remote, Action<string>? announce = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlaylistImportResult>> ImportPlaylistsAsync(string platformId, IReadOnlyList<RemotePlaylist> remotes, Action<string>? announce = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
