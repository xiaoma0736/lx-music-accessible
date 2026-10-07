using System.Reflection;
using System.Threading;
using System.Net.Http;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Core.MusicApi;
using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>
/// Shutdown ownership audit for resources that are easy to miss because they are not child controls:
/// shell timers, announcement progress timers, and native global hotkey registrations.
/// </summary>
public sealed class ShutdownResourceLifecycleTests
{
    [Fact]
    public void Disposing_the_shell_stops_its_status_timer()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var shell = RuntimePlaybackSearchHistoryTests.CreateShellForLifecycle();
            var clock = Assert.IsType<Label>(shell.Controls.Find("ClockText", true).Single());
            var before = clock.Text;
            shell.Dispose();

            // The timer ticks once per second. A disposed form must not leave it alive and updating a
            // disposed control after host release.
            await Task.Delay(1100);
            Application.DoEvents();

            Assert.Equal(before, clock.Text);
        });
    }

    [Fact]
    public void Announcement_progress_timer_is_released_when_announcer_is_disposed()
    {
        using var announcer = new WinFormsAccessibleAnnouncer(
            Array.Empty<ITtsService>(),
            NullLogger<WinFormsAccessibleAnnouncer>.Instance);
        Assert.True(announcer is IDisposable, "the announcer must be owned by Host disposal");
        var timer = (System.Windows.Forms.Timer)typeof(WinFormsAccessibleAnnouncer)
            .GetField("_progressTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(announcer)!;
        var disposed = false;
        timer.Disposed += (_, _) => disposed = true;

        announcer.Dispose();

        Assert.True(disposed, "the announcement progress timer survived disposal");
    }
    [Fact]
    public void Disposing_settings_workflow_releases_native_global_hotkeys_and_save_gate()
    {
        WinFormsTestRunner.Run(() =>
        {
            var window = new Control();
            var handle = window.Handle;
            try
            {
                var hotkeys = new CountingHotkeyAdapter();
                var workflow = new SettingsWorkflow(
                    new EmptySettingsStore(),
                    new HotkeyManagerService(
                        NullLogger<HotkeyManagerService>.Instance,
                        hotkeys),
                    NullLogger<SettingsWorkflow>.Instance);
                workflow.LoadAsync(handle).GetAwaiter().GetResult();
                Assert.True(hotkeys.Registered.Count > 0, "the fixture did not register a global hotkey");

                workflow.Dispose();

                Assert.Empty(hotkeys.Registered);
                Assert.Throws<ObjectDisposedException>(() =>
                    workflow.SaveAsync(workflow.Current).GetAwaiter().GetResult());
            }
            finally { window.Dispose(); }
        });
    }

    [Fact]
    public void Disposing_web_login_form_disposes_login_check_timer()
    {
        WinFormsTestRunner.Run(() =>
        {
            var form = new WebLoginForm(
                "netease",
                new EmptyImportService(),
                new RecordingAnnouncer(),
                NullLogger<WebLoginForm>.Instance);
            AssertDisposedWithForm(GetOwnedTimer(form, "_loginCheck"), form);
        });
    }

    [Fact]
    public void Disposing_kugou_login_form_disposes_qr_polling_timer()
    {
        WinFormsTestRunner.Run(() =>
        {
            var form = new KugouQrLoginForm(
                new EmptyImportService(),
                new RecordingAnnouncer(),
                new KugouQrLoginService(
                    new MusicHttpClient(NullLogger<MusicHttpClient>.Instance),
                    NullLogger<KugouQrLoginService>.Instance));
            AssertDisposedWithForm(GetOwnedTimer(form, "_timer"), form);
        });
    }

    [Fact]
    public async Task Complete_exit_ends_the_message_loop_when_host_release_has_already_disposed_shell()
    {
        WinFormsShell? shell = null;
        WinFormsDesktopIntegration? desktop = null;
        var shellShown = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loopExited = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                shell = RuntimePlaybackSearchHistoryTests.CreateShellForLifecycle();
                desktop = new WinFormsDesktopIntegration(new RecordingAnnouncer());
                desktop.Attach(shell);
                shell.Show();
                shellShown.SetResult(null);
                Application.Run(shell);
                loopExited.SetResult(null);
            }
            catch (Exception exception)
            {
                shellShown.TrySetException(exception);
                loopExited.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            await shellShown.Task;
            var disposed = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            shell!.BeginInvoke(() =>
            {
                shell.Dispose();
                disposed.SetResult(null);
            });
            await disposed.Task;

            // Production disposal order can dispose the Host-owned main form before the shutdown
            // workflow asks the desktop seam to complete exit. That must still end Application.Run.
            desktop!.CompleteExit();
            var completed = await Task.WhenAny(loopExited.Task, Task.Delay(500));
            if (completed != loopExited.Task)
            {
                Application.Exit();
                await loopExited.Task;
                Assert.Fail("CompleteExit left Application.Run alive after the shell was disposed.");
            }
        }
        finally
        {
            Application.Exit();
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static System.Windows.Forms.Timer GetOwnedTimer(Form form, string fieldName) =>
        (System.Windows.Forms.Timer)form.GetType()
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(form)!;

    private static void AssertDisposedWithForm(System.Windows.Forms.Timer timer, Form form)
    {
        var disposed = false;
        timer.Disposed += (_, _) => disposed = true;

        form.Dispose();

        Assert.True(disposed, form.GetType().Name + " left its long-lived timer alive.");
    }

    [Fact]
    public void Production_composition_owns_long_lived_disposables_for_host_release()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LXMusicAccessible.Lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        IServiceCollection? services = null;
        try
        {
            var builder = ApplicationHost.CreateBuilder(directory, collection => services = collection);
            using var host = builder.Build();

            Assert.Contains(services!, item => item.ServiceType == typeof(SettingsWorkflow));
            foreach (var (serviceType, ownedType) in new[]
                     {
                         (typeof(HttpClient), typeof(HttpClient)),
                         (typeof(MusicHttpClient), typeof(MusicHttpClient)),
                         (typeof(ScriptNetworkClient), typeof(ScriptNetworkClient)),
                         (typeof(ScriptMusicSource), typeof(ScriptMusicSource)),
                         (typeof(IAccessibleAnnouncer), typeof(WinFormsAccessibleAnnouncer)),
                         (typeof(IAudioPlayer), typeof(AudioPlayer)),
                         (typeof(BackgroundWorkHost), typeof(BackgroundWorkHost)),
                         (typeof(MusicPlayerService), typeof(MusicPlayerService)),
                         (typeof(IDownloadWorkflow), typeof(DownloadWorkflow)),
                         (typeof(LyricsWorkflow), typeof(LyricsWorkflow)),
                         (typeof(ITtsService), typeof(RoutingTtsService)),
                         (typeof(IManualSourceDiscoveryWorkflow), typeof(ManualSourceDiscoveryWorkflow)),
                         (typeof(SettingsWorkflow), typeof(SettingsWorkflow)),
                         (typeof(GlobalHotkeyMessageWindow), typeof(GlobalHotkeyMessageWindow)),
                         (typeof(WinFormsShell), typeof(WinFormsShell)),
                         (typeof(DesktopLifecycleWorkflow), typeof(DesktopLifecycleWorkflow)),
                         (typeof(WinFormsDesktopIntegration), typeof(WinFormsDesktopIntegration))
                     })
            {
                var descriptor = Assert.Single(services!, item => item.ServiceType == serviceType);
                Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
                var implementationType = descriptor.ImplementationType ?? ownedType;
                Assert.True(
                    typeof(IDisposable).IsAssignableFrom(implementationType) ||
                        typeof(IAsyncDisposable).IsAssignableFrom(implementationType),
                    serviceType.Name + " is not disposable and is not documented as process-owned.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class EmptySettingsStore : ISettingsStore
    {
        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<UserSettings?>(null);
        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CountingHotkeyAdapter : IGlobalHotkeyAdapter
    {
        public List<int> Registered { get; } = new();

        public int LastRegisterError { get; private set; }

        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers)
        {
            Registered.Add(id);
            return true;
        }

        public bool Unregister(IntPtr windowHandle, int id)
        {
            Registered.Remove(id);
            return true;
        }
    }

    private sealed class EmptyImportService : IPlaylistImportService
    {
        public IReadOnlyList<IPlaylistImportProvider> Providers => Array.Empty<IPlaylistImportProvider>();
        public IPlaylistImportProvider? GetProvider(string platformId) => null;
        public Task EnsureAccountsLoadedAsync() => Task.CompletedTask;
        public AccountInfo? GetAccount(string platformId) => null;
        public Task<AccountInfo?> LoginWithCookieAsync(string platformId, string cookieText) => Task.FromResult<AccountInfo?>(null);
        public Task<AccountInfo?> LoginWithBrowserResultAsync(string platformId, string cookieText, string? displayName, List<RemotePlaylist>? playlists) => Task.FromResult<AccountInfo?>(null);
        public Task LogoutAsync(string platformId) => Task.CompletedTask;
        public Task<List<RemotePlaylist>> GetMyPlaylistsAsync(string platformId) => Task.FromResult(new List<RemotePlaylist>());
        public Task<List<MusicInfo>> GetPlaylistSongsPreviewAsync(string platformId, RemotePlaylist remote) => Task.FromResult(new List<MusicInfo>());
        public Task<PlaylistImportResult> ImportPlaylistAsync(string platformId, RemotePlaylist remote, Action<string>? announce = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlaylistImportResult());
        public Task<IReadOnlyList<PlaylistImportResult>> ImportPlaylistsAsync(string platformId, IReadOnlyList<RemotePlaylist> remotes, Action<string>? announce = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaylistImportResult>>(Array.Empty<PlaylistImportResult>());
    }
}
