using System.Threading;
using LXMusicAccessible.App;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class PlaylistLoginHostTests
{
    [Fact]
    public async Task Host_registers_one_login_factory_and_one_qr_module()
    {
        await using var host = await TestApplicationHost.StartAsync();

        Assert.Same(
            host.Services.GetRequiredService<IPlaylistLoginWindowFactory>(),
            host.Services.GetRequiredService<IPlaylistLoginWindowFactory>());
        Assert.Same(
            host.Services.GetRequiredService<KugouQrLoginService>(),
            host.Services.GetRequiredService<KugouQrLoginService>());
    }

    [Fact]
    public async Task Controlled_login_dialog_updates_the_same_import_flow_without_network_or_early_disposal()
    {
        var provider = new ControlledPlaylistProvider();
        ControlledLoginWindowFactory? dialogs = null;

        await using (var host = await TestApplicationHost.StartAsync(services =>
        {
            services.RemoveAll<IPlaylistImportProvider>();
            services.AddSingleton<IPlaylistImportProvider>(provider);
            services.Replace(ServiceDescriptor.Singleton<IPlaylistLoginWindowFactory>(sp =>
                dialogs = new ControlledLoginWindowFactory(
                    sp.GetRequiredService<IPlaylistImportService>(),
                    sp.GetRequiredService<IAccessibleAnnouncer>(),
                    sp.GetRequiredService<IDataService>(),
                    sp.GetRequiredService<MusicHttpClient>(),
                    sp.GetRequiredService<ILogger<WebLoginWindow>>())));
        }))
        {
            var service = host.Services.GetRequiredService<IPlaylistImportService>();
            var factory = Assert.IsType<ControlledLoginWindowFactory>(
                host.Services.GetRequiredService<IPlaylistLoginWindowFactory>());

            Assert.Same(service, factory.Service);
            Assert.Same(host.Announcer, factory.Announcer);
            Assert.Same(host.Services.GetRequiredService<IDataService>(), factory.Data);
            Assert.Same(host.Services.GetRequiredService<MusicHttpClient>(), factory.Http);
            Assert.Same(host.Services.GetRequiredService<ILogger<WebLoginWindow>>(), factory.Logger);
            Assert.Same(service, host.Services.GetRequiredService<IPlaylistImportService>());

            var login = await RunOnStaThread(async () =>
            {
                var owner = new System.Windows.Window { ShowInTaskbar = false };
                owner.Show();
                var window = host.Services.GetRequiredService<ApplicationWindowFactory>()
                    .CreatePlaylistImport(owner, _ => false, _ => { }, _ => { });
                window.Show();
                try { return await window.LoginWithWebDialogAsync(); }
                finally
                {
                    window.Close();
                    owner.Close();
                }
            });

            Assert.True(login.Succeeded);
            Assert.Equal("受控账号", login.AccountName);
            Assert.Equal("导入个人歌单", factory.LastOwnerTitle);
            Assert.Equal("受控账号", service.GetAccount("netease")?.DisplayName);

            var playlist = Assert.Single(await service.GetMyPlaylistsAsync("netease"));
            var import = await service.ImportPlaylistAsync("netease", playlist);
            var stored = Assert.Single(
                await host.Services.GetRequiredService<IDataService>().GetPlaylistsAsync(),
                item => item.Id == import.PlaylistId);

            Assert.Equal(2, (await host.Services.GetRequiredService<IDataService>()
                .GetPlaylistItemsAsync(stored.Id)).Count);
            Assert.Empty(host.HttpHandler.Requests);
            Assert.Equal(0, factory.DisposeCount);
        }

        Assert.NotNull(dialogs);
        Assert.Equal(1, dialogs.DisposeCount);
    }

    private static Task<T> RunOnStaThread<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action().GetAwaiter().GetResult()); }
            catch (Exception exception) { completion.SetException(exception); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class ControlledLoginWindowFactory(
        IPlaylistImportService service,
        IAccessibleAnnouncer announcer,
        IDataService data,
        MusicHttpClient http,
        ILogger<WebLoginWindow> logger) : IPlaylistLoginWindowFactory, IDisposable
    {
        public IPlaylistImportService Service => service;
        public IAccessibleAnnouncer Announcer => announcer;
        public IDataService Data => data;
        public MusicHttpClient Http => http;
        public ILogger<WebLoginWindow> Logger => logger;
        public int DisposeCount { get; private set; }
        public string? LastOwnerTitle { get; private set; }

        public PlaylistLoginMethod? SelectLoginMethod(string platform, System.Windows.Window? owner) =>
            PlaylistLoginMethod.Web;

        public PlaylistLoginDialogResult ShowWebLogin(string platform, System.Windows.Window? owner)
        {
            Assert.NotNull(owner);
            LastOwnerTitle = owner.Title;
            var account = service.LoginWithCookieAsync(platform, "controlled-cookie")
                .GetAwaiter().GetResult();
            return new PlaylistLoginDialogResult(account is not null, account?.DisplayName);
        }

        public bool ShowKugouQrLogin(System.Windows.Window? owner) => false;

        public void Dispose() => DisposeCount++;
    }

    private sealed class ControlledPlaylistProvider : IPlaylistImportProvider
    {
        private bool _loggedIn;

        public string PlatformId => "netease";
        public string PlatformName => "受控平台";

        public Task<AccountInfo?> LoginByCookieAsync(string cookieText)
        {
            Assert.Equal("controlled-cookie", cookieText);
            _loggedIn = true;
            return Task.FromResult<AccountInfo?>(new AccountInfo
            {
                Platform = PlatformId,
                Uid = "controlled-user",
                DisplayName = "受控账号",
                AuthData = cookieText,
                ExpireTime = DateTime.Now.AddDays(1)
            });
        }

        public Task LoadAccountAsync(AccountInfo account)
        {
            _loggedIn = true;
            return Task.CompletedTask;
        }

        public Task LogoutAsync()
        {
            _loggedIn = false;
            return Task.CompletedTask;
        }

        public Task<List<RemotePlaylist>> GetMyPlaylistsAsync()
        {
            Assert.True(_loggedIn);
            return Task.FromResult(new List<RemotePlaylist>
            {
                new()
                {
                    Platform = PlatformId,
                    RemoteId = "playlist-1",
                    Name = "受控歌单",
                    TrackCount = 2
                }
            });
        }

        public Task<List<MusicInfo>> GetPlaylistSongsAsync(RemotePlaylist playlist)
        {
            Assert.True(_loggedIn);
            return Task.FromResult(new List<MusicInfo>
            {
                new() { Id = "song-1", Title = "歌曲一", Artist = "歌手", Source = PlatformId },
                new() { Id = "song-2", Title = "歌曲二", Artist = "歌手", Source = PlatformId }
            });
        }
    }
}
