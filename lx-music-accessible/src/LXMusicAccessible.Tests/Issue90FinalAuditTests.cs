using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class Issue90FinalAuditTests
{
    [Fact]
    public async Task Active_download_rejects_restart_instead_of_replacing_it()
    {
        using var root = new TemporaryDirectory();
        var handler = new RejectingHttpMessageHandler();
        using var http = new HttpClient(handler);
        await using var background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
        var source = new BlockingSource();
        handler.Register(HttpMethod.Get, new Uri("https://download.invalid/audio.mp3"), () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[70_000]) });
        await using var workflow = new DownloadWorkflow(
            NullLogger<DownloadWorkflow>.Instance,
            source,
            http,
            background,
            new RecordingAnnouncer(),
            root.Path,
            new RecordingProcessLauncher());

        var pending = workflow.DownloadAsync(new(Song("active"), TargetDirectory: root.Path));
        var task = await WaitAsync(workflow, source);
        var rejected = await workflow.RedownloadAsync(task, root.Path).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(rejected);
        Assert.Equal(DownloadTaskStatus.Downloading, task.Status);
        source.Release();
        await pending;
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
    }

    [Fact]
    public async Task Changed_subscription_never_silently_trusts_or_enables_the_remote_script()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        const string oldScript = """
            /**
             * @name Old test source
             * @version 1.0.0
             */
            lx.on(lx.EVENT_NAMES.request, function () {
              return Promise.resolve('https://fixture.invalid/old.mp3');
            });
            """;
        const string newScript = """
            /**
             * @name New test source
             * @version 2.0.0
             */
            lx.on(lx.EVENT_NAMES.request, function () {
              return Promise.resolve('https://fixture.invalid/new.mp3');
            });
            """;
        const string subscriptionUrl = "https://raw.githubusercontent.com/Macrohard0001/lx-ikun-music-sources/main/V260817/%E6%8E%A8%E8%8D%90/HYWmusic_beta_%E5%85%AC%E7%9B%8A%E6%B5%8B%E8%AF%95.js";
        await data.SaveMusicSourcesAsync([new MusicSource
        {
            Id = "hyw",
            Name = "Old test source",
            Version = "1.0.0",
            ScriptUrl = subscriptionUrl,
            ScriptContent = oldScript,
            ContentSha256 = ScriptMusicSource.ComputeSha256(oldScript),
            TrustState = ScriptTrustState.UserApproved,
            Type = SourceType.Network,
            IsEnabled = true,
            Priority = 10
        }]);
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(subscriptionUrl), newScript);
        var sources = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);

        await sources.InitializeScriptSourcesAsync();

        var updated = Assert.Single(sources.GetAllSources(), source => source.Id == "hyw");
        Assert.Equal(ScriptTrustState.Untrusted, updated.TrustState);
        Assert.False(updated.IsEnabled);
        var persisted = Assert.Single(await data.GetMusicSourcesAsync(), source => source.Id == "hyw");
        Assert.Equal(ScriptTrustState.Untrusted, persisted.TrustState);
        Assert.False(persisted.IsEnabled);
    }

    [Fact]
    public async Task Runtime_database_uses_WAL_busy_timeout_and_foreign_keys()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        await data.SetSettingValueAsync("issue90", "ready");
        using var connection = new SqliteConnection($"Data Source={Path.Combine(app.DataDirectory, "lxmusic.db")}");
        await connection.OpenAsync();

        Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode"));
        Assert.Equal(1L, await ScalarAsync(connection, "PRAGMA foreign_keys"));
        var runtime = File.ReadAllText(RepositoryPath("src", "LXMusicAccessible.Data", "DataService.cs"));
        Assert.Contains("PRAGMA busy_timeout = 5000", runtime, StringComparison.Ordinal);
        Assert.Contains("PRAGMA journal_mode = WAL", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_data_directory_copy_does_not_leave_partial_target_or_activate_it()
    {
        using var fixture = new Issue90DataDirectoryFixture();
        fixture.CreateCurrentContent();
        var currentPointer = Path.Combine(fixture.LocalAppData, AppDataPaths.DefaultDirName, AppDataPaths.PointerFileName);
        Directory.CreateDirectory(Path.Combine(fixture.SelectedParent, AppDataPaths.DefaultDirName));
        File.WriteAllText(Path.Combine(fixture.SelectedParent, AppDataPaths.DefaultDirName, "blocked.txt"), "existing");

        var workflow = fixture.CreateWorkflow();
        var result = workflow.ChangeDataDirectoryInRoot(fixture.SelectedParent, fixture.LocalAppData);

        Assert.False(result.Succeeded);
        Assert.Equal(fixture.Current, AppDataPaths.ResolveDataDir(fixture.LocalAppData));
        Assert.True(File.Exists(Path.Combine(fixture.SelectedParent, AppDataPaths.DefaultDirName, "blocked.txt")));
        Assert.False(Directory.EnumerateFiles(
                Path.Combine(fixture.SelectedParent, AppDataPaths.DefaultDirName),
                "lxmusic.db", SearchOption.AllDirectories).Any(),
            "A failed copy left database content behind.");
    }

    [Fact]
    public void Replacement_promotion_rolls_back_audio_when_lyric_target_is_invalid()
    {
        var root = Path.Combine(Path.GetTempPath(), "issue90-replacement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var newAudio = Path.Combine(root, "new.audio");
            var newLyric = Path.Combine(root, "new.lrc");
            var oldAudio = Path.Combine(root, "old.audio");
            var invalidLyricTarget = newAudio;
            File.WriteAllBytes(newAudio, [1, 2, 3]);
            File.WriteAllBytes(newLyric, [4, 5, 6]);
            File.WriteAllBytes(oldAudio, [7, 8, 9]);

            var method = typeof(DownloadWorkflow).GetMethod("PromoteReplacement", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            Assert.Throws<TargetInvocationException>(() => method!.Invoke(null, [newAudio, newLyric, oldAudio, invalidLyricTarget]));
            Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(oldAudio));
            Assert.Empty(Directory.EnumerateFiles(root, ".lxmusic-backup-*"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
    [Fact]
    public void Shell_documents_search_shortcuts_without_stale_delete_clearing_claims()
    {
        var guide = File.ReadAllText(RepositoryPath("KEYBOARD_NAVIGATION_GUIDE.md"));
        Assert.Contains("`Ctrl+F`", guide, StringComparison.Ordinal);
        Assert.Contains("`Ctrl+Delete` 删除当前记录", guide, StringComparison.Ordinal);
        Assert.Contains("`Alt+Delete` 清空全部", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void Tray_restore_preserves_window_state_and_focused_control()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = new WinFormsShell(CreateShellServices());
            using var integration = new WinFormsDesktopIntegration(new RecordingAnnouncer());
            integration.Attach(shell);
            shell.Show();
            Application.DoEvents();
            var focused = shell.Controls.Find("搜索关键词", true).Single();
            shell.ActiveControl = focused;
            shell.WindowState = FormWindowState.Maximized;
            Application.DoEvents();

            Assert.True(integration.HideToTray());
            Application.DoEvents();
            Assert.False(shell.Visible);
            Assert.Equal(FormWindowState.Minimized, shell.WindowState);

            var target = integration.RestoreWindow();
            Application.DoEvents();
            Assert.Equal(DesktopFocusTarget.PreviousControl, target);
            Assert.True(shell.Visible);
            Assert.Equal(FormWindowState.Maximized, shell.WindowState);
            Assert.True(focused.Focused || shell.ActiveControl == focused);
        });
    }

    [Fact]
    public void Playlist_logout_is_guarded_against_reentry()
    {
        WinFormsTestRunner.Run(() =>
        {
            var service = new SlowLogoutService();
            using var form = new PlaylistImportForm(service, new RecordingAnnouncer(), new RecordingLoginFactory(), _ => { });
            form.Show();
            Application.DoEvents();
            typeof(PlaylistImportForm).GetField("_platform", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, "qq");
            var logout = Assert.IsType<Button>(form.Controls.Find("退出登录", true).Single());
            logout.PerformClick();
            logout.PerformClick();
            logout.PerformClick();

            Assert.Equal(1, service.LogoutCalls);
        });
    }

    [Fact]
    public void Release_manifest_records_the_complete_framework_payload()
    {
        var script = File.ReadAllText(RepositoryPath("build_release.ps1"));
        Assert.Contains("payloadScope = \"complete-framework-dependent-application-payload\"", script, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem -LiteralPath $application -File -Recurse", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$payloadFiles = foreach ($relativePath in $requiredPayload | Sort-Object)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_net48_diagnostics_match_documented_installer_behavior()
    {
        var installer = File.ReadAllText(RepositoryPath("packaging", "inno", "LXMusicAccessible.iss"));
        Assert.Contains("net48-diagnostics.txt", installer, StringComparison.Ordinal);
        Assert.Contains("https://dotnet.microsoft.com/download/dotnet-framework/net48", installer, StringComparison.Ordinal);
        Assert.Contains("installed=", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requiredRelease=", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Abort;", installer, StringComparison.Ordinal);

        var readme = File.ReadAllText(RepositoryPath("README.md"));
        Assert.Contains("官方下载地址", readme, StringComparison.Ordinal);
        Assert.Contains("net48-diagnostics.txt", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void TestConsole_returns_nonzero_when_any_smoke_check_fails()
    {
        var source = File.ReadAllText(RepositoryPath("src", "LXMusicAccessible.TestConsole", "Program.cs"));
        Assert.Contains("static async Task<int> Main", source, StringComparison.Ordinal);
        Assert.Contains("return fail == 0 ? 0 : 1;", source, StringComparison.Ordinal);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync();
    }

    private static MusicInfo Song(string id) => new()
    {
        Id = id,
        Source = "test",
        Title = "Song " + id,
        Artist = "Artist"
    };

    private static async Task<DownloadTask> WaitAsync(DownloadWorkflow workflow, BlockingSource source)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var task = workflow.Tasks.FirstOrDefault();
            if (task is not null && task.Status == DownloadTaskStatus.Downloading) return task;
            await Task.Delay(25);
        }
        source.Release();
        throw new TimeoutException("The download did not become active.");
    }

    private static string RepositoryPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src")
            directory = directory.Parent;
        var root = directory?.Parent ?? throw new InvalidOperationException("Could not locate repository root.");
        return Path.Combine(new[] { root.FullName }.Concat(parts).ToArray());
    }

    private static WinFormsShellServices CreateShellServices() => new()
    {
        Announcer = new RecordingAnnouncer(),
        BrowseSource = new FakeBrowseSource(),
        Downloads = new NullDownloadWorkflow()
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "issue90-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }

    private sealed class BlockingSource : IDownloadSource
    {
        private readonly TaskCompletionSource<string?> _url = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _url.TrySetResult("https://download.invalid/audio.mp3");
        public Task<IReadOnlyList<string>> GetQualitiesAsync(MusicInfo music, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["128k"]);
        public Task<string?> GetUrlAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) =>
            _url.Task.WaitAsync(cancellationToken);
        public Task<LyricResult?> GetLyricAsync(MusicInfo music, CancellationToken cancellationToken = default) =>
            Task.FromResult<LyricResult?>(null);
    }

    private sealed class NullDownloadWorkflow : IDownloadWorkflow
    {
        public ReadOnlyObservableCollection<DownloadTask> Tasks { get; } = new([]);
        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new DownloadTask());
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DownloadListResult(0, 0, 0, 0, 0, [], "ok"));
        public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> RedownloadAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void Cancel(DownloadTask task) { }
        public void Pause(DownloadTask task) { }
        public void Resume(DownloadTask task) { }
        public bool Delete(DownloadTask task, bool deleteSourceFile) => true;
    public Task<bool> DeleteAsync(DownloadTask task, bool deleteSourceFile) => Task.FromResult(Delete(task, deleteSourceFile));
        public void ClearCompleted() { }
        public string ResolveDownloadDirectory(string? requested = null) => System.IO.Path.GetTempPath();
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) => Task.FromResult<ExternalPlaybackTarget?>(null);
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ExternalPlaybackResult(true, "ok"));
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }

    private sealed class SlowLogoutService : IPlaylistImportService
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LogoutCalls { get; private set; }
        public void Release() => _release.TrySetResult(true);
        public IReadOnlyList<IPlaylistImportProvider> Providers => [];
        public IPlaylistImportProvider? GetProvider(string platformId) => null;
        public Task EnsureAccountsLoadedAsync() => Task.CompletedTask;
        public AccountInfo? GetAccount(string platformId) => null;
        public Task<AccountInfo?> LoginWithCookieAsync(string platformId, string cookieText) => Task.FromResult<AccountInfo?>(null);
        public Task<AccountInfo?> LoginWithBrowserResultAsync(string platformId, string cookieText, string? displayName, List<RemotePlaylist>? playlists) => Task.FromResult<AccountInfo?>(null);
        public async Task LogoutAsync(string platformId)
        {
            LogoutCalls++;
            await _release.Task;
        }
        public Task<List<RemotePlaylist>> GetMyPlaylistsAsync(string platformId) => Task.FromResult(new List<RemotePlaylist>());
        public Task<List<MusicInfo>> GetPlaylistSongsPreviewAsync(string platformId, RemotePlaylist remote) => Task.FromResult(new List<MusicInfo>());
        public Task<PlaylistImportResult> ImportPlaylistAsync(string platformId, RemotePlaylist remote, Action<string>? announce = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlaylistImportResult());
        public Task<IReadOnlyList<PlaylistImportResult>> ImportPlaylistsAsync(string platformId, IReadOnlyList<RemotePlaylist> remotes, Action<string>? announce = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaylistImportResult>>([]);
    }

    internal sealed class Issue90DataDirectoryFixture : IDisposable
    {
        public Issue90DataDirectoryFixture()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LXMusicAccessible.Issue90Data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            LocalAppData = System.IO.Path.Combine(Root, "localappdata");
            SelectedParent = System.IO.Path.Combine(Root, "selected");
            Directory.CreateDirectory(LocalAppData);
            Directory.CreateDirectory(SelectedParent);
            Current = AppDataPaths.PrepareDataDir(LocalAppData);
        }

        public string Root { get; }
        public string LocalAppData { get; }
        public string SelectedParent { get; }
        public string Current { get; }

        public void CreateCurrentContent()
        {
            File.WriteAllText(System.IO.Path.Combine(Current, "lxmusic.db"), "settings;playlists");
            Directory.CreateDirectory(System.IO.Path.Combine(Current, "logs"));
            File.WriteAllText(System.IO.Path.Combine(Current, "logs", "app.log"), "log");
        }

        public SettingsWorkflow CreateWorkflow() => new(
            new MemorySettingsStore(),
            new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, new NoHotkeyAdapter()),
            NullLogger<SettingsWorkflow>.Instance);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<UserSettings?>(new());
        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoHotkeyAdapter : IGlobalHotkeyAdapter
    {
        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers) => true;
        public bool Unregister(IntPtr windowHandle, int id) => true;
        public int LastRegisterError => 0;
    }
    private sealed class RecordingLoginFactory : IPlaylistLoginWindowFactory
    {
        public PlaylistLoginMethod? SelectLoginMethod(string platform, Form? owner) => null;
        public PlaylistLoginDialogResult ShowWebLogin(string platform, Form? owner) => new(false);
        public bool ShowKugouQrLogin(Form? owner) => false;
    }
}

