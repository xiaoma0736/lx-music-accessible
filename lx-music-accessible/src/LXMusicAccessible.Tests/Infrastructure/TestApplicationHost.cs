using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using LXMusicAccessible.App;
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

internal sealed class TestApplicationHost : IAsyncDisposable
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

    public static async Task<TestApplicationHost> StartAsync(
        Action<IServiceCollection>? configureServices = null)
    {
        var dataDirectory = Directory.CreateTempSubdirectory(TestDirectoryPrefix).FullName;

        var httpHandler = new RejectingHttpMessageHandler();
        var announcer = new RecordingAnnouncer();
        var speech = new RecordingTtsAdapter();
        var processLauncher = new RecordingProcessLauncher();
        var audioPlayer = new RecordingAudioPlayer();
        IHost? host = null;

        try
        {
            host = ApplicationHost.CreateBuilder(dataDirectory)
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices((_, services) =>
                {
                    services.Replace(ServiceDescriptor.Singleton(
                        new HttpClient(httpHandler, disposeHandler: false)
                        {
                            Timeout = Timeout.InfiniteTimeSpan
                        }));
                    services.Replace(ServiceDescriptor.Singleton<MusicHttpClient>(sp =>
                        new MusicHttpClient(
                            sp.GetRequiredService<ILogger<MusicHttpClient>>(),
                            httpHandler)));
                    services.Replace(ServiceDescriptor.Singleton(
                        new ScriptNetworkClient(httpHandler, ResolveFixtureAddressAsync)));
                    services.Replace(ServiceDescriptor.Singleton<IAccessibleAnnouncer>(announcer));
                    services.Replace(ServiceDescriptor.Singleton<ITtsService>(speech));
                    services.Replace(ServiceDescriptor.Singleton<IProcessLauncher>(processLauncher));
                    services.Replace(ServiceDescriptor.Singleton<IAudioPlayer>(audioPlayer));
                    services.Replace(ServiceDescriptor.Singleton(
                        new LocalSearchHistoryStore(Path.Combine(dataDirectory, "file-storage"))));
                    configureServices?.Invoke(services);
                })
                .Build();

            await host.StartAsync();
            var migration = await host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            if (!migration.Succeeded)
                throw new InvalidOperationException(migration.Message);

            return new TestApplicationHost(
                host,
                dataDirectory,
                httpHandler,
                announcer,
                speech,
                processLauncher,
                audioPlayer);
        }
        catch
        {
            if (host is not null)
            {
                try { await host.StopAsync(); }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"Test host stop failed: {exception.GetType().Name}");
                }
                await ApplicationHost.DisposeHostAsync(host);
            }

            SqliteConnection.ClearAllPools();
            DeleteOwnedDirectory(dataDirectory);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await _host.StopAsync();
        }
        finally
        {
            await ApplicationHost.DisposeHostAsync(_host);
            SqliteConnection.ClearAllPools();
            DeleteOwnedDirectory(DataDirectory);
        }
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        var testRoot = Path.GetFullPath(Path.GetTempPath());
        var fullDirectory = Path.GetFullPath(directory);
        var relative = Path.GetRelativePath(testRoot, fullDirectory);

        if (relative == "."
            || relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || !Path.GetFileName(fullDirectory).StartsWith(TestDirectoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete non-test directory: {fullDirectory}");
        }

        if (Directory.Exists(fullDirectory))
        {
            Directory.Delete(fullDirectory, recursive: true);
        }
    }

    private static Task<IPAddress[]> ResolveFixtureAddressAsync(
        string host,
        CancellationToken cancellationToken)
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

    public void Announce(AccessibleAnnouncement announcement)
    {
        Announcements.Add(announcement);
        _messages.Add(announcement.Text);
    }

}

internal sealed class RecordingTtsAdapter : ITtsService
{
    public List<string> Messages { get; } = new();
    public bool IsAvailable => true;
    public string ActiveBackendName => "Test speech recorder";

    public Task SpeakAsync(string text)
    {
        Messages.Add(text);
        return Task.CompletedTask;
    }

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
        ArgumentNullException.ThrowIfNull(startInfo);
        _invocations.Add(new ProcessInvocation(
            startInfo.FileName,
            startInfo.Arguments,
            startInfo.UseShellExecute));
    }
}

internal sealed class RecordingAudioPlayer : IAudioPlayer
{
    private AudioPlayerState _state = AudioPlayerState.Stopped;

    public double Volume { get; set; }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration => TimeSpan.Zero;
    public int CurrentOutputDeviceIndex => -1;

    public AudioPlayerState State
    {
        get => _state;
        set
        {
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<AudioPlayerState>? StateChanged;
    public event EventHandler? PlaybackCompleted { add { } remove { } }

    public Task LoadAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task PlayAsync()
    {
        State = AudioPlayerState.Playing;
        return Task.CompletedTask;
    }

    public void Pause() => State = AudioPlayerState.Paused;
    public void Stop() => State = AudioPlayerState.Stopped;
    public string? GetActualQuality() => null;
    public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => Array.Empty<OutputDeviceInfo>();
    public bool SwitchOutputDevice(int index) => false;
    public void SeekTo(TimeSpan position) => Position = position;
    public void Dispose() { }
}
