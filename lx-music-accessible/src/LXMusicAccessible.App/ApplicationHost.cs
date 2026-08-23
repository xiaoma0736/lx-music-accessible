using System.IO;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.App;

/// <summary>The single composition root for the desktop process.</summary>
public static class ApplicationHost
{
    public static ApplicationRuntime Create()
    {
        var dataDirectory = AppDataPaths.ResolveDataDir();
        return CreateRuntime(
            dataDirectory,
            new ProductionStartupInitializer(dataDirectory),
            new WpfStartupFailurePresenter());
    }

    internal static ApplicationRuntime CreateForTesting(
        string dataDirectory,
        IStartupInitializer initializer,
        IStartupFailurePresenter failurePresenter,
        Action<IServiceCollection>? replaceAdapters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return CreateRuntime(
            Path.GetFullPath(dataDirectory),
            initializer,
            failurePresenter,
            replaceAdapters);
    }

    internal static IHostBuilder CreateBuilder(
        string dataDirectory,
        Action<IServiceCollection>? replaceAdapters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var dataDir = Path.GetFullPath(dataDirectory);

        return Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<MusicHttpClient>();
                services.AddSingleton<NeteaseMusicService>();
                services.AddSingleton<QqMusicService>();
                services.AddSingleton<KugouMusicService>();
                services.AddSingleton<KuwoMusicService>();
                services.AddSingleton<MiguMusicService>();
                services.AddSingleton<ScriptNetworkClient>();
                services.AddSingleton<ScriptMusicSource>();

                services.AddSingleton<IAccessibilityService, AccessibilityService>();
                services.AddSingleton<IDataService>(sp =>
                    new DataService(sp.GetRequiredService<ILogger<DataService>>(), dataDir));
                services.AddSingleton(sp => new DatabaseInitializer(
                    sp.GetRequiredService<ILogger<DatabaseInitializer>>(), dataDir));

                services.AddSingleton<IAudioPlayer, AudioPlayer>();
                services.AddSingleton<IMusicPlayerService, MusicPlayerService>();
                services.AddSingleton<IMusicSourceService, MusicSourceService>();
                services.AddSingleton<IDownloadService, DownloadService>();
                services.AddSingleton<ILyricService, LyricService>();
                services.AddSingleton<LxScriptEngine>();
                services.AddSingleton<ITtsService, RoutingTtsService>();
                services.AddSingleton<IDataStorageService>(_ =>
                    new LocalFileDataStorageService(Path.Combine(dataDir, "file-storage")));
                services.AddSingleton<IHotkeyManager, HotkeyManagerService>();
                services.AddSingleton<IProcessLauncher, SystemProcessLauncher>();

                services.AddSingleton<IPlaylistImportService, PlaylistImportService>();
                services.AddSingleton<IPlaylistImportProvider, NeteasePlaylistProvider>();
                services.AddSingleton<IPlaylistImportProvider, QqPlaylistProvider>();
                services.AddSingleton<IPlaylistImportProvider, KugouPlaylistProvider>();
                services.AddSingleton<IPlaylistImportProvider, KuwoPlaylistProvider>();

                services.AddSingleton<PlaylistImportView>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<IApplicationShell, WpfApplicationShell>();

                replaceAdapters?.Invoke(services);
            });
    }

    private static ApplicationRuntime CreateRuntime(
        string dataDirectory,
        IStartupInitializer initializer,
        IStartupFailurePresenter failurePresenter,
        Action<IServiceCollection>? replaceAdapters = null) =>
        new(
            () => CreateBuilder(dataDirectory, replaceAdapters)
                .UseDefaultServiceProvider((_, options) =>
                {
                    options.ValidateOnBuild = true;
                    options.ValidateScopes = true;
                })
                .Build(),
            initializer,
            failurePresenter);
}
