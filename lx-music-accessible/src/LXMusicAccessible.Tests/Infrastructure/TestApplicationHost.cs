using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Tests.Infrastructure;

internal sealed class TestApplicationHost : IAsyncDisposable, IDisposable
{
    private const string TestDirectoryPrefix = "LXMusicAccessible.Tests-";
    private readonly IHost _host;
    private bool _disposed;

    private TestApplicationHost(
        IHost host,
        string dataDirectory,
        RejectingHttpMessageHandler httpHandler,
        RecordingAnnouncer announcer,
        RecordingTtsAdapter speech,
        RecordingProcessLauncher processLauncher,
        RecordingAudioPlayer audioPlayer)
    {
        _host = host;
        DataDirectory = dataDirectory;
        HttpHandler = httpHandler;
        Announcer = announcer;
        Speech = speech;
        ProcessLauncher = processLauncher;
        AudioPlayer = audioPlayer;
    }

    public IServiceProvider Services => _host.Services;
    public string DataDirectory { get; }
    public RejectingHttpMessageHandler HttpHandler { get; }
    public RecordingAnnouncer Announcer { get; }
    public RecordingTtsAdapter Speech { get; }
    public RecordingProcessLauncher ProcessLauncher { get; }
    public RecordingAudioPlayer AudioPlayer { get; }

    public static async Task<TestApplicationHost> StartAsync(Action<IServiceCollection>? configureServices = null)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), TestDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var httpHandler = new RejectingHttpMessageHandler();
        var announcer = new RecordingAnnouncer();
        var speech = new RecordingTtsAdapter();
        var processLauncher = new RecordingProcessLauncher();
        var audioPlayer = new RecordingAudioPlayer();
        IHost? host = null;
        try
        {
            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.ClearProviders())
                .UseDefaultServiceProvider((_, options) => { options.ValidateOnBuild = true; options.ValidateScopes = true; })
                .ConfigureServices((_, services) =>
                {
                    services.Replace(ServiceDescriptor.Singleton(new HttpClient(httpHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }));
                    services.Replace(ServiceDescriptor.Singleton<MusicHttpClient>(sp => new MusicHttpClient(sp.GetRequiredService<ILogger<MusicHttpClient>>(), httpHandler)));
                    services.Replace(ServiceDescriptor.Singleton(new ScriptNetworkClient(httpHandler, ResolveFixtureAddressAsync)));
                    services.Replace(ServiceDescriptor.Singleton<IAccessibleAnnouncer>(announcer));
                    services.Replace(ServiceDescriptor.Singleton<ITtsService>(speech));
                    services.Replace(ServiceDescriptor.Singleton<IProcessLauncher>(processLauncher));
                    services.Replace(ServiceDescriptor.Singleton<IAudioPlayer>(audioPlayer));
                    services.AddSingleton<NeteaseMusicService>();
                    services.AddSingleton<QqMusicService>();
                    services.AddSingleton<KugouMusicService>();
                    services.AddSingleton<KuwoMusicService>();
                    services.AddSingleton<MiguMusicService>();
                    services.AddSingleton<ScriptMusicSource>();
                    services.AddSingleton<IDataService>(sp => new DataService(sp.GetRequiredService<ILogger<DataService>>(), dataDirectory));
                    services.AddSingleton(sp => new DatabaseInitializer(sp.GetRequiredService<ILogger<DatabaseInitializer>>(), dataDirectory));
                    services.AddSingleton<AudioPlayer>();
                    services.AddSingleton<BackgroundWorkHost>();
                    services.AddSingleton<IPlaybackSourceAdapter, MusicPlaybackSourceAdapter>();
                    services.AddSingleton(sp => new MusicPlayerService(sp.GetRequiredService<ILogger<MusicPlayerService>>(), sp.GetRequiredService<IAudioPlayer>(), sp.GetRequiredService<IPlaybackSourceAdapter>(), sp.GetRequiredService<IAccessibleAnnouncer>(), sp.GetRequiredService<BackgroundWorkHost>(), sp.GetRequiredService<HttpClient>(), Path.Combine(dataDirectory, "cache")));
                    services.AddSingleton<IPlaybackCacheCleanup>(sp => sp.GetRequiredService<MusicPlayerService>());
                    services.AddSingleton<IPlaybackCoordinator>(sp => sp.GetRequiredService<MusicPlayerService>());
                    services.AddSingleton<IAudioPlaybackControl>(sp => sp.GetRequiredService<MusicPlayerService>());
                    services.AddSingleton<IExactPlaybackCoordinator>(sp => sp.GetRequiredService<MusicPlayerService>());
                    services.AddSingleton<MusicSourceService>();
                    services.AddSingleton<IManualSourceGateway>(sp => sp.GetRequiredService<MusicSourceService>());
                    services.AddSingleton<IManualSourceDiscoveryWorkflow, ManualSourceDiscoveryWorkflow>();
                    services.AddSingleton<IMusicSourceService>(sp => sp.GetRequiredService<MusicSourceService>());
                    services.AddSingleton<IMusicBrowseSource>(sp => sp.GetRequiredService<MusicSourceService>());
                    services.AddSingleton(_ => new LocalSearchHistoryStore(Path.Combine(dataDirectory, "file-storage")));
                    services.AddSingleton<MusicBrowseWorkflow>();
                    services.AddSingleton<SavedPlaylistWorkflow>();
                    services.AddSingleton<IDownloadWorkflow, DownloadWorkflow>();
                    services.AddSingleton<ILyricService, LyricService>();
                    services.AddSingleton<LyricsWorkflow>();
                    services.AddSingleton<LxScriptEngine>();
                    services.AddSingleton<SettingsWorkflow>();
                    services.AddSingleton<IPlaylistImportService, PlaylistImportService>();
                    services.AddSingleton<IPlaylistImportProvider, NeteasePlaylistProvider>();
                    services.AddSingleton<IPlaylistImportProvider, QqPlaylistProvider>();
                    services.AddSingleton<IPlaylistImportProvider, KugouPlaylistProvider>();
                    services.AddSingleton<IPlaylistImportProvider, KuwoPlaylistProvider>();
                    services.AddSingleton<KugouQrLoginService>();
                    configureServices?.Invoke(services);
                }).Build();
            await host.StartAsync();
            var migration = await host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            if (!migration.Succeeded) throw new InvalidOperationException(migration.Message);
            return new TestApplicationHost(host, dataDirectory, httpHandler, announcer, speech, processLauncher, audioPlayer);
        }
        catch
        {
            if (host != null) await StopHostAsync(host);
            SqliteConnection.ClearAllPools();
            DeleteOwnedDirectory(dataDirectory);
            throw;
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();


    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await StopHostAsync(_host); }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteOwnedDirectory(DataDirectory);
        }
    }

    private static async Task StopHostAsync(IHost host)
    {
        try { await host.StopAsync(); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine("Test host stop failed: " + exception.GetType().Name); }
        if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync(); else host.Dispose();
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var testRoot = Path.GetFullPath(Path.GetTempPath());
        var relative = FrameworkCompatibility.GetRelativePath(testRoot, fullDirectory);
        if (relative == "." || relative.StartsWith("..") || Path.IsPathRooted(relative) || !Path.GetFileName(fullDirectory).StartsWith(TestDirectoryPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to delete non-test directory: " + fullDirectory);
        if (Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, true);
    }

    private static Task<IPAddress[]> ResolveFixtureAddressAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
    }
}

internal sealed class RecordingAnnouncer : IAccessibleAnnouncer
{
    private readonly List<string> _messages = new();
    public IReadOnlyList<string> Messages => _messages;
    public List<AccessibleAnnouncement> Announcements { get; } = new();
    public bool SpeechEnabled { get; set; } = true;
    public void Announce(AccessibleAnnouncement announcement) { Announcements.Add(announcement); _messages.Add(announcement.Text); }
}

internal sealed class RecordingTtsAdapter : ITtsService
{
    public List<string> Messages { get; } = new();
    public bool IsAvailable => true;
    public string ActiveBackendName => "Test speech recorder";
    public Task SpeakAsync(string text) { Messages.Add(text); return Task.CompletedTask; }
    public Task SpeakQueuedAsync(string text) => SpeakAsync(text);
    public void Stop() { }
    public void SetRate(double rate) { }
    public void SetVoice(string voiceId) { }
    public List<VoiceInfo> GetAvailableVoices() => new();
}

internal sealed record ProcessInvocation(string FileName, string Arguments, bool UseShellExecute);

internal sealed class RecordingProcessLauncher : IProcessLauncher
{
    private readonly List<ProcessInvocation> _invocations = new();
    public IReadOnlyList<ProcessInvocation> Invocations => _invocations;
    public void Start(ProcessStartInfo startInfo)
    {
        FrameworkCompatibility.ThrowIfNull(startInfo);
        _invocations.Add(new ProcessInvocation(startInfo.FileName, startInfo.Arguments, startInfo.UseShellExecute));
    }
}

internal sealed class RecordingAudioPlayer : IAudioPlayer
{
    private AudioPlayerState _state = AudioPlayerState.Stopped;
    public double Volume { get; set; }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration => TimeSpan.Zero;
    public int CurrentOutputDeviceIndex => -1;
    public AudioPlayerState State { get => _state; set { _state = value; StateChanged?.Invoke(this, value); } }
    public event EventHandler<AudioPlayerState>? StateChanged;
    public event EventHandler? PlaybackCompleted { add { } remove { } }
    public Task LoadAsync(string url, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task PlayAsync() { State = AudioPlayerState.Playing; return Task.CompletedTask; }
    public void Pause() => State = AudioPlayerState.Paused;
    public void Stop() => State = AudioPlayerState.Stopped;
    public string? GetActualQuality() => null;
    public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => Array.Empty<OutputDeviceInfo>();
    public bool SwitchOutputDevice(int index) => false;
    public void SeekTo(TimeSpan position) => Position = position;
    public void Dispose() { }
}
