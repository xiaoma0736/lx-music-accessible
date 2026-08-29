using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using LXMusicAccessible.App;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

[Collection("WPF accessibility semantics")]
public sealed class PlaylistImportWindowTests
{
    [Fact]
    public void Main_window_has_one_tool_menu_entry_and_no_embedded_import_page()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "LXMusicAccessible.App", "MainWindow.xaml"));
        var mainCode = File.ReadAllText(Path.Combine(root, "src", "LXMusicAccessible.App", "MainWindow.xaml.cs"));
        var import = File.ReadAllText(Path.Combine(root, "src", "LXMusicAccessible.App", "PlaylistImportWindow.xaml"));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            main,
            "Header=\"导入个人歌单\\(_I\\)\"").Cast<System.Text.RegularExpressions.Match>());
        Assert.DoesNotContain("<TabItem Header=\"导入个人歌单\"", main, StringComparison.Ordinal);
        var tools = XDocument.Load(Path.Combine(root, "src", "LXMusicAccessible.App", "MainWindow.xaml"))
            .Descendants()
            .Single(element => (string?)element.Attribute("Header") == "工具(_T)");
        Assert.Equal(
            ["下载管理(_D)", "导入个人歌单(_I)", "Separator", "音源管理(_M)", "设置(_S)"],
            tools.Elements().Select(element =>
                element.Name.LocalName == "Separator"
                    ? "Separator"
                    : (string?)element.Attribute("Header")));
        Assert.Contains("<Window", import, StringComparison.Ordinal);
        Assert.Contains("Title=\"导入个人歌单\"", import, StringComparison.Ordinal);
        Assert.Contains("WindowStartupLocation=\"CenterOwner\"", import, StringComparison.Ordinal);
        Assert.Contains("ResizeMode=\"CanResize\"", import, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"关闭\"", import, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", import, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"WebLoginBtn\"", import, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"CookieLoginBtn\"", import, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"KugouQrLoginBtn\"", import, StringComparison.Ordinal);
        Assert.Contains("_externalSongLists.Contains(list)", mainCode, StringComparison.Ordinal);
        Assert.Contains("DetachSongListCapabilities", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_login_method_window_maps_all_platforms_and_focuses_the_first_native_item()
    {
        await RunOnStaThreadAsync(() =>
        {
            var owner = new Window { ShowInTaskbar = false };
            owner.Show();
            try
            {
                var cases = new[]
                {
                    ("netease", "网易云登录方式", new[] { "网页登录", "粘贴 Cookie 登录" }),
                    ("qq", "QQ音乐登录方式", new[] { "网页登录", "粘贴 Cookie 登录" }),
                    ("kg", "酷狗登录方式", new[] { "粘贴 Cookie 登录", "酷狗 APP 扫码登录" }),
                    ("kw", "酷我登录方式", new[] { "网页登录" })
                };

                foreach (var (platform, title, methods) in cases)
                {
                    var window = new PlaylistLoginMethodWindow(platform) { Owner = owner };
                    try
                    {
                        window.Show();
                        Pump(window.Dispatcher);

                        Assert.Equal(title, window.Title);
                        Assert.Same(owner, window.Owner);
                        Assert.Equal(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
                        Assert.Equal(methods, window.MethodsList.Items.Cast<object>().Select(item => item.ToString()));
                        Assert.Equal("登录方式", new ListBoxAutomationPeer(window.MethodsList).GetName());
                        var first = Assert.IsType<ListBoxItem>(
                            window.MethodsList.ItemContainerGenerator.ContainerFromIndex(0));
                        Assert.Equal(0, window.MethodsList.SelectedIndex);
                        Assert.Same(first, Keyboard.FocusedElement);
                        Assert.Null(window.SelectedMethod);

                        if (methods.Length > 1)
                        {
                            RaiseKey(first, Key.Down, Keyboard.KeyDownEvent);
                            Pump(window.Dispatcher);
                            var second = Assert.IsType<ListBoxItem>(
                                window.MethodsList.ItemContainerGenerator.ContainerFromIndex(1));
                            Assert.Equal(1, window.MethodsList.SelectedIndex);
                            Assert.Same(second, Keyboard.FocusedElement);
                            Assert.Null(window.SelectedMethod);
                        }

                        Assert.True(((UIElement)Keyboard.FocusedElement!).MoveFocus(
                            new TraversalRequest(FocusNavigationDirection.Next)));
                        Assert.Same(window.UseSelectedMethodBtn, Keyboard.FocusedElement);
                        Assert.True(window.UseSelectedMethodBtn.MoveFocus(
                            new TraversalRequest(FocusNavigationDirection.Previous)));
                        Assert.True(window.MethodsList.IsKeyboardFocusWithin);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
            finally
            {
                owner.Close();
            }
        });
    }

    [Fact]
    public async Task Login_method_window_requires_explicit_confirmation_and_cancel_has_no_result()
    {
        await RunOnStaThreadAsync(() =>
        {
            var owner = new Window { ShowInTaskbar = false };
            owner.Show();
            try
            {
                var enter = new PlaylistLoginMethodWindow("netease") { Owner = owner };
                enter.Loaded += (_, _) => enter.Dispatcher.BeginInvoke(() =>
                {
                    var first = Assert.IsType<ListBoxItem>(
                        enter.MethodsList.ItemContainerGenerator.ContainerFromIndex(0));
                    RaiseKey(first, Key.Enter, Keyboard.KeyDownEvent);
                }, DispatcherPriority.Input);
                Assert.True(enter.ShowDialog());
                Assert.Equal(PlaylistLoginMethod.Web, enter.SelectedMethod);

                var button = new PlaylistLoginMethodWindow("kg") { Owner = owner };
                button.Loaded += (_, _) => button.Dispatcher.BeginInvoke(() =>
                    button.UseSelectedMethodBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                    DispatcherPriority.Input);
                Assert.True(button.ShowDialog());
                Assert.Equal(PlaylistLoginMethod.Cookie, button.SelectedMethod);

                var escape = new PlaylistLoginMethodWindow("qq") { Owner = owner };
                escape.Loaded += (_, _) => escape.Dispatcher.BeginInvoke(
                    () => RaisePreviewKey(escape, Key.Escape), DispatcherPriority.Input);
                Assert.NotEqual(true, escape.ShowDialog());
                Assert.Null(escape.SelectedMethod);

                var nativeClose = new PlaylistLoginMethodWindow("kw") { Owner = owner };
                nativeClose.Loaded += (_, _) => nativeClose.Dispatcher.BeginInvoke(
                    () => SystemCommands.CloseWindow(nativeClose), DispatcherPriority.Input);
                Assert.NotEqual(true, nativeClose.ShowDialog());
                Assert.Null(nativeClose.SelectedMethod);
            }
            finally
            {
                owner.Close();
            }
        });
    }

    [Fact]
    public async Task New_window_uses_default_platform_focus_and_releases_song_hooks_when_closed()
    {
        await RunOnStaThreadAsync(() =>
        {
            var service = new ControlledImportService();
            var background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            var attached = 0;
            var detached = 0;
            var window = new PlaylistImportWindow(
                service,
                new RecordingAnnouncer(),
                new RecordingLoginWindows(),
                background,
                _ => false,
                _ => attached++,
                _ => detached++);
            try
            {
                window.Show();
                Pump(window.Dispatcher);

                Assert.Same(window.PlatformNeteaseBtn, Keyboard.FocusedElement);
                Assert.Equal("网易云，选择登录方式",
                    new ButtonAutomationPeer(window.PlatformNeteaseBtn).GetName());
                Assert.Equal("QQ音乐，选择登录方式",
                    new ButtonAutomationPeer(window.PlatformQqBtn).GetName());
                Assert.Equal("酷狗，选择登录方式",
                    new ButtonAutomationPeer(window.PlatformKgBtn).GetName());
                Assert.Equal("酷我，选择登录方式",
                    new ButtonAutomationPeer(window.PlatformKwBtn).GetName());
                Assert.Equal("已登录：网易账号", window.AccountStatusText.Text);
                Assert.Equal(1, attached);
                Assert.Equal(0, service.OperationCount);

                window.Close();
                Assert.Equal(1, detached);
            }
            finally
            {
                if (window.IsVisible) window.Close();
                background.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public async Task Preview_escape_returns_to_playlist_list_before_top_level_escape_closes()
    {
        await RunOnStaThreadAsync(() =>
        {
            var service = new ControlledImportService();
            var background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            var window = CreateWindow(service, background);
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                window.LoadPlaylistsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window.Dispatcher);
                window.MyPlaylistsList.SelectedIndex = 0;
                RaiseKey(window.MyPlaylistsList, Key.Enter, Keyboard.KeyDownEvent);
                Pump(window.Dispatcher);
                Assert.Equal(Visibility.Visible, window.SongPreviewArea.Visibility);

                RaisePreviewKey(window, Key.Escape);
                Assert.True(window.IsVisible);
                Assert.Equal(Visibility.Collapsed, window.SongPreviewArea.Visibility);

                RaisePreviewKey(window, Key.Escape);
                Assert.False(window.IsVisible);
            }
            finally
            {
                if (window.IsVisible) window.Close();
                background.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public async Task Empty_playlist_and_preview_results_focus_their_list_shells()
    {
        await RunOnStaThreadAsync(() =>
        {
            var background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            try
            {
                var emptyPlaylists = CreateWindow(
                    new ControlledImportService { EmptyPlaylists = true }, background);
                emptyPlaylists.Show();
                Pump(emptyPlaylists.Dispatcher);
                Assert.True(emptyPlaylists.LoadPlaylistsBtn.Focus());
                emptyPlaylists.LoadPlaylistsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(emptyPlaylists.Dispatcher);
                Assert.Same(emptyPlaylists.MyPlaylistsList, Keyboard.FocusedElement);
                emptyPlaylists.Close();

                var emptyPreview = CreateWindow(
                    new ControlledImportService { EmptyPreview = true }, background);
                emptyPreview.Show();
                Pump(emptyPreview.Dispatcher);
                Assert.True(emptyPreview.LoadPlaylistsBtn.Focus());
                emptyPreview.LoadPlaylistsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(emptyPreview.Dispatcher);
                var playlist = Assert.IsType<ListBoxItem>(
                    emptyPreview.MyPlaylistsList.ItemContainerGenerator.ContainerFromIndex(0));
                RaiseKey(playlist, Key.Enter, Keyboard.KeyDownEvent);
                Pump(emptyPreview.Dispatcher);
                Assert.Same(emptyPreview.PlaylistSongsList, Keyboard.FocusedElement);
                emptyPreview.Close();
            }
            finally
            {
                background.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public async Task Playlist_loading_does_not_take_focus_back_after_the_user_leaves()
    {
        await RunOnStaThreadAsync(() =>
        {
            var service = new ControlledImportService { DelayPlaylists = true };
            var background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            var window = CreateWindow(service, background);
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(window.Dispatcher));
                Assert.True(window.LoadPlaylistsBtn.Focus());
                window.LoadPlaylistsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window.Dispatcher, () => service.PlaylistsStarted.Task.IsCompleted);
                Assert.True(window.LogoutBtn.Focus());
                service.ReleasePlaylists.SetResult();
                PumpUntil(window.Dispatcher, () => window.MyPlaylistsList.Items.Count == 1);

                Assert.Same(window.LogoutBtn, Keyboard.FocusedElement);
                Assert.Equal(0, window.MyPlaylistsList.SelectedIndex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
                if (window.IsVisible) window.Close();
                background.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public async Task Active_import_close_can_continue_then_cancel_and_wait_for_the_delayed_fetch()
    {
        await RunOnStaThreadAsync(() =>
        {
            var service = new ControlledImportService { DelayBatch = true };
            var background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            var announcer = new RecordingAnnouncer();
            var decisions = new Queue<PlaylistImportCloseDecision>([
                PlaylistImportCloseDecision.ContinueImport,
                PlaylistImportCloseDecision.CancelImportAndClose]);
            var window = new PlaylistImportWindow(
                service,
                announcer,
                new RecordingLoginWindows(),
                background,
                _ => false,
                _ => { },
                _ => { },
                _ => decisions.Dequeue());
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                window.LoadPlaylistsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window.Dispatcher);
                window.MyPlaylistsList.SelectedItems.Add(window.MyPlaylistsList.Items[0]);
                window.ImportSelectedBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(service.BatchStarted.Task.IsCompleted);

                window.Close();
                Assert.True(window.IsVisible);
                Assert.False(service.CancellationObserved);

                window.Close();
                Assert.True(window.IsVisible);
                Assert.Equal("正在取消", window.ImportStatusText.Text);
                Assert.Equal("正在取消", AutomationProperties.GetItemStatus(window.ImportStatusText));
                Assert.Contains("正在取消", announcer.Messages);

                service.ReleaseBatch.SetResult();
                PumpUntil(window.Dispatcher, () => !window.IsVisible);
                Assert.True(service.CancellationObserved);
            }
            finally
            {
                if (window.IsVisible) window.Close();
                background.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public async Task Playback_hotkeys_are_delegated_but_cookie_editing_suppresses_them()
    {
        await RunOnStaThreadAsync(() =>
        {
            var background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            var routed = 0;
            var loginWindows = new RecordingLoginWindows
            {
                MethodToSelect = PlaylistLoginMethod.Cookie
            };
            var window = new PlaylistImportWindow(
                new ControlledImportService(),
                new RecordingAnnouncer(),
                loginWindows,
                background,
                _ => { routed++; return true; },
                _ => { },
                _ => { });
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                window.PlatformNeteaseBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.CookieBox.Focus();
                RaisePreviewKey(window, Key.F5);
                Assert.Equal(0, routed);

                window.PlatformNeteaseBtn.Focus();
                RaisePreviewKey(window, Key.F5);
                Assert.Equal(1, routed);
            }
            finally
            {
                window.Close();
                background.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public async Task Platform_action_uses_selected_method_once_and_restores_the_expected_focus()
    {
        await RunOnStaThreadAsync(() =>
        {
            var service = new ControlledImportService();
            var background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            var dialogs = new RecordingLoginWindows
            {
                MethodToSelect = PlaylistLoginMethod.Cookie
            };
            var window = new PlaylistImportWindow(
                service, new RecordingAnnouncer(), dialogs, background,
                _ => false, _ => { }, _ => { });
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                window.PlatformKgBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window.Dispatcher);

                Assert.Equal("kg", dialogs.LastMethodPlatform);
                Assert.Same(window, dialogs.LastOwner);
                Assert.Equal(1, dialogs.MethodSelectionCount);
                Assert.Equal(0, dialogs.WebLoginCount);
                Assert.Equal(0, dialogs.QrLoginCount);
                Assert.Equal(0, service.OperationCount);
                Assert.Equal(Visibility.Visible, window.CookieArea.Visibility);
                Assert.Same(window.CookieBox, Keyboard.FocusedElement);

                dialogs.MethodToSelect = null;
                window.PlatformQqBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window.Dispatcher);
                Assert.Same(window.PlatformQqBtn, Keyboard.FocusedElement);

                dialogs.MethodToSelect = PlaylistLoginMethod.Web;
                dialogs.WebResult = new PlaylistLoginDialogResult(true, "测试账号");
                window.PlatformNeteaseBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window.Dispatcher);
                Assert.Equal(1, dialogs.WebLoginCount);
                Assert.Same(window, dialogs.LastOwner);
                Assert.Same(window.LoadPlaylistsBtn, Keyboard.FocusedElement);

                dialogs.MethodToSelect = PlaylistLoginMethod.KugouQr;
                dialogs.QrResult = false;
                window.PlatformKgBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window.Dispatcher);
                Assert.Equal(1, dialogs.QrLoginCount);
                Assert.Same(window.PlatformKgBtn, Keyboard.FocusedElement);
            }
            finally
            {
                window.Close();
                background.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public async Task Cookie_submission_focuses_load_on_success_and_the_platform_on_failure()
    {
        await RunOnStaThreadAsync(() =>
        {
            var background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            try
            {
                var successService = new ControlledImportService
                {
                    CookieLoginResult = new AccountInfo
                    {
                        Platform = "qq",
                        DisplayName = "QQ账号"
                    }
                };
                var successAnnouncer = new RecordingAnnouncer();
                var success = new PlaylistImportWindow(
                    successService, successAnnouncer,
                    new RecordingLoginWindows { MethodToSelect = PlaylistLoginMethod.Cookie },
                    background, _ => false, _ => { }, _ => { });
                success.Show();
                Pump(success.Dispatcher);
                success.PlatformQqBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                success.CookieBox.Text = "valid-cookie";
                success.CookieSubmitBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(success.Dispatcher, () => success.AccountStatusText.Text == "已登录：QQ账号");

                Assert.Equal(1, successService.CookieLoginCount);
                Assert.Same(success.LoadPlaylistsBtn, Keyboard.FocusedElement);
                Assert.Contains("登录成功：QQ账号。按 Tab 到“获取我的歌单”按钮。", successAnnouncer.Messages);
                success.Close();

                var failureService = new ControlledImportService();
                var failure = new PlaylistImportWindow(
                    failureService, new RecordingAnnouncer(),
                    new RecordingLoginWindows { MethodToSelect = PlaylistLoginMethod.Cookie },
                    background, _ => false, _ => { }, _ => { });
                failure.Show();
                Pump(failure.Dispatcher);
                failure.PlatformQqBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                failure.CookieBox.Text = "expired-cookie";
                failure.CookieSubmitBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(failure.Dispatcher);

                Assert.Equal(1, failureService.CookieLoginCount);
                Assert.Same(failure.PlatformQqBtn, Keyboard.FocusedElement);
                failure.Close();
            }
            finally
            {
                background.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public async Task Import_progress_completion_and_errors_are_accessibly_reported()
    {
        await RunOnStaThreadAsync(() =>
        {
            var background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            try
            {
                var successAnnouncer = new RecordingAnnouncer();
                var success = new PlaylistImportWindow(
                    new ControlledImportService(), successAnnouncer, new RecordingLoginWindows(), background,
                    _ => false, _ => { }, _ => { });
                success.Show();
                Pump(success.Dispatcher);
                StartFirstImport(success);
                PumpUntil(success.Dispatcher, () => success.ImportStatusText.Text.Contains("导入完成", StringComparison.Ordinal));
                Assert.Contains("正在导入歌单一", successAnnouncer.Messages);
                Assert.Contains(successAnnouncer.Messages, message => message.Contains("导入完成", StringComparison.Ordinal));
                success.Close();

                var errorAnnouncer = new RecordingAnnouncer();
                var failure = new PlaylistImportWindow(
                    new ControlledImportService { BatchFailure = new InvalidOperationException("受控失败") },
                    errorAnnouncer, new RecordingLoginWindows(), background,
                    _ => false, _ => { }, _ => { });
                failure.Show();
                Pump(failure.Dispatcher);
                StartFirstImport(failure);
                PumpUntil(failure.Dispatcher, () => failure.ImportStatusText.Text == "导入失败");
                Assert.Contains("导入失败：受控失败", errorAnnouncer.Messages);
                failure.Close();
            }
            finally
            {
                background.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    private static void StartFirstImport(PlaylistImportWindow window)
    {
        window.LoadPlaylistsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window.Dispatcher);
        window.MyPlaylistsList.SelectedItems.Add(window.MyPlaylistsList.Items[0]);
        window.ImportSelectedBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static PlaylistImportWindow CreateWindow(
        IPlaylistImportService service,
        BackgroundWorkHost background) =>
        new(service, new RecordingAnnouncer(), new RecordingLoginWindows(), background,
            _ => false, _ => { }, _ => { });

    private static void RaisePreviewKey(UIElement element, Key key)
        => RaiseKey(element, key, Keyboard.PreviewKeyDownEvent);

    private static void RaiseKey(UIElement element, Key key, RoutedEvent routedEvent)
    {
        var source = PresentationSource.FromVisual(element);
        Assert.NotNull(source);
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = routedEvent
        });
    }

    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            dispatcher);
        var poll = new DispatcherTimer(
            TimeSpan.FromMilliseconds(10),
            DispatcherPriority.Background,
            (_, _) => { if (condition()) frame.Continue = false; },
            dispatcher);
        timeout.Start();
        poll.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        poll.Stop();
        Assert.True(condition());
    }

    private static Task RunOnStaThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            if (failure is null) completion.SetResult();
            else completion.SetException(failure);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "verify.ps1"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class RecordingAnnouncer : IAccessibleAnnouncer
    {
        public bool SpeechEnabled { get; set; }
        public List<string> Messages { get; } = [];
        public void Announce(AccessibleAnnouncement announcement) => Messages.Add(announcement.Text);
    }

    private sealed class RecordingLoginWindows : IPlaylistLoginWindowFactory
    {
        public PlaylistLoginMethod? MethodToSelect { get; set; }
        public PlaylistLoginDialogResult WebResult { get; set; }
        public bool QrResult { get; set; }
        public string? LastMethodPlatform { get; private set; }
        public Window? LastOwner { get; private set; }
        public int MethodSelectionCount { get; private set; }
        public int WebLoginCount { get; private set; }
        public int QrLoginCount { get; private set; }

        public PlaylistLoginMethod? SelectLoginMethod(string platform, Window? owner)
        {
            LastMethodPlatform = platform;
            LastOwner = owner;
            MethodSelectionCount++;
            return MethodToSelect;
        }

        public PlaylistLoginDialogResult ShowWebLogin(string platform, Window? owner)
        {
            LastOwner = owner;
            WebLoginCount++;
            return WebResult;
        }

        public bool ShowKugouQrLogin(Window? owner)
        {
            LastOwner = owner;
            QrLoginCount++;
            return QrResult;
        }
    }

    private sealed class ControlledImportService : IPlaylistImportService
    {
        private AccountInfo? _cookieAccount;
        public bool EmptyPlaylists { get; init; }
        public bool EmptyPreview { get; init; }
        public bool DelayPlaylists { get; init; }
        public bool DelayBatch { get; init; }
        public Exception? BatchFailure { get; init; }
        public AccountInfo? CookieLoginResult { get; init; }
        public int OperationCount { get; private set; }
        public int CookieLoginCount { get; private set; }
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource BatchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBatch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PlaylistsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePlaylists { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<IPlaylistImportProvider> Providers => [];
        public IPlaylistImportProvider? GetProvider(string platformId) => null;
        public Task EnsureAccountsLoadedAsync() => Task.CompletedTask;
        public AccountInfo? GetAccount(string platformId) =>
            _cookieAccount?.Platform == platformId
                ? _cookieAccount
                : platformId == "netease"
                    ? new AccountInfo { Platform = platformId, DisplayName = "网易账号" }
                    : null;
        public Task<AccountInfo?> LoginWithCookieAsync(string platformId, string cookieText)
        {
            CookieLoginCount++;
            _cookieAccount = CookieLoginResult;
            return Task.FromResult(_cookieAccount);
        }
        public Task<AccountInfo?> LoginWithBrowserResultAsync(string platformId, string cookieText, string? displayName, List<RemotePlaylist>? playlists) =>
            throw new NotSupportedException();
        public Task LogoutAsync(string platformId) => Task.CompletedTask;
        public async Task<List<RemotePlaylist>> GetMyPlaylistsAsync(string platformId)
        {
            OperationCount++;
            PlaylistsStarted.TrySetResult();
            if (DelayPlaylists) await ReleasePlaylists.Task;
            if (EmptyPlaylists) return [];
            return
            [
                new() { Platform = platformId, RemoteId = "one", Name = "歌单一", TrackCount = 1 }
            ];
        }
        public Task<List<MusicInfo>> GetPlaylistSongsPreviewAsync(string platformId, RemotePlaylist remote)
        {
            OperationCount++;
            if (EmptyPreview) return Task.FromResult(new List<MusicInfo>());
            return Task.FromResult(new List<MusicInfo>
            {
                new() { Id = "song", Title = "歌曲", Artist = "歌手", Source = platformId }
            });
        }
        public Task<PlaylistImportResult> ImportPlaylistAsync(string platformId, RemotePlaylist remote, Action<string>? announce = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async Task<IReadOnlyList<PlaylistImportResult>> ImportPlaylistsAsync(
            string platformId,
            IReadOnlyList<RemotePlaylist> remotes,
            Action<string>? announce = null,
            CancellationToken cancellationToken = default)
        {
            OperationCount++;
            BatchStarted.SetResult();
            if (DelayBatch) await ReleaseBatch.Task;
            announce?.Invoke("正在导入歌单一");
            if (BatchFailure is not null) throw BatchFailure;
            try { cancellationToken.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
            return remotes.Select(remote => new PlaylistImportResult
            {
                PlaylistId = remote.RemoteId,
                PlaylistName = remote.Name,
                ImportedCount = 1
            }).ToList();
        }
    }
}
