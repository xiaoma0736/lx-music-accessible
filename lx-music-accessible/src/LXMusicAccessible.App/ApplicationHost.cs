using System.IO;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.App;

/// <summary>
/// Builds the service graph used by the desktop application.
/// Tests use this same builder and replace only external adapters.
/// </summary>
public static class ApplicationHost
{
    public static IHostBuilder CreateBuilder(string dataDirectory)
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
                services.AddSingleton<IDataStorageService, LocalFileDataStorageService>();
                services.AddSingleton<IHotkeyManager, HotkeyManagerService>();
                services.AddSingleton<IProcessLauncher, SystemProcessLauncher>();
            });
    }
}
