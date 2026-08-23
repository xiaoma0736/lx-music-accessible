using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LXMusicAccessible.Services;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using Serilog;

namespace LXMusicAccessible.App;

/// <summary>
/// 服务提供者 - 管理所有依赖注入配置
/// </summary>
public static class ServiceProvider
{
    private static IServiceProvider? _serviceProvider;
    private static readonly object _lock = new object();

    /// <summary>
    /// 获取服务提供者实例（单例模式）
    /// </summary>
    public static IServiceProvider GetServiceProvider()
    {
        if (_serviceProvider != null)
        {
            return _serviceProvider;
        }

        lock (_lock)
        {
            if (_serviceProvider != null)
            {
                return _serviceProvider;
            }

            _serviceProvider = ConfigureServices();
            return _serviceProvider;
        }
    }

    /// <summary>
    /// 配置所有服务
    /// </summary>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 配置日志（Debug + Serilog 文件，便于排查导入问题）
        services.AddLogging(configure =>
        {
            configure.AddDebug();
            if (Log.Logger != null)
                configure.AddSerilog(Log.Logger, dispose: false);
        });
        
        // 核心服务
        services.AddSingleton<IAccessibilityService, AccessibilityService>();
        services.AddSingleton<IMusicPlayerService, MusicPlayerService>();
        services.AddSingleton<ITtsService, RoutingTtsService>();
        services.AddSingleton<IHotkeyManager, HotkeyManagerService>();
        services.AddSingleton<IProcessLauncher, SystemProcessLauncher>();
        var dataDir = AppDataPaths.ResolveDataDir();
        services.AddSingleton<IDataService>(sp => new DataService(sp.GetRequiredService<ILogger<DataService>>(), dataDir));
        services.AddSingleton<IDataStorageService, LocalFileDataStorageService>();
        
        // 音乐源服务
        services.AddSingleton<IMusicSourceService, MusicSourceService>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<ILyricService, LyricService>();
        services.AddSingleton<ScriptMusicSource>();
        
        // 音乐平台API服务
        services.AddSingleton<MusicHttpClient>();
        services.AddSingleton<NeteaseMusicService>();
        services.AddSingleton<QqMusicService>();
        services.AddSingleton<KugouMusicService>();
        services.AddSingleton<KuwoMusicService>();
        services.AddSingleton<MiguMusicService>();
        // 个人歌单导入（登录 + 歌单拉取）
        services.AddSingleton<IPlaylistImportService, PlaylistImportService>();
        services.AddSingleton<IPlaylistImportProvider, NeteasePlaylistProvider>();
        services.AddSingleton<IPlaylistImportProvider, QqPlaylistProvider>();
        services.AddSingleton<IPlaylistImportProvider, KugouPlaylistProvider>();
        services.AddSingleton<IPlaylistImportProvider, KuwoPlaylistProvider>();
        
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 获取指定服务
    /// </summary>
    public static T? GetService<T>() where T : class
    {
        var provider = GetServiceProvider();
        return provider.GetService<T>();
    }

    /// <summary>
    /// 获取必需的服务
    /// </summary>
    public static T GetRequiredService<T>() where T : class
    {
        var provider = GetServiceProvider();
        return provider.GetRequiredService<T>();
    }

    /// <summary>
    /// 清理服务提供者
    /// </summary>
    public static void Cleanup()
    {
        lock (_lock)
        {
            if (_serviceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
            _serviceProvider = null;
        }
    }
}
