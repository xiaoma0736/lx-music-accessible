using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>Regression coverage for GitHub issue #77: configured download targets and owned storage paths.</summary>
public sealed class Issue77StoragePathTests
{
    [Fact]
    public async Task Configured_download_path_is_used_for_single_download()
    {
        await using var harness = new DownloadHarness();
        var configured = Path.Combine(harness.Root, "configured");
        var source = Path.Combine(harness.Root, "source.mp3");
        File.WriteAllBytes(source, "ID3"u8.ToArray());
        harness.Workflow.ApplyPreferences(new(
            true,
            DownloadNameOrder.ArtistTitle,
            false,
            TargetDirectory: configured));

        var task = await harness.Workflow.DownloadAsync(new(
            new MusicInfo
            {
                Id = "single",
                Source = "Local",
                Title = "single",
                Artist = "artist",
                PlayUrl = source
            }));
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.NotNull(task.SavePath);
        Assert.Equal(configured, Path.GetDirectoryName(task.SavePath)!);
        Assert.True(File.Exists(task.SavePath));
    }

    [Fact]
    public async Task Open_download_directory_uses_the_configured_download_path()
    {
        await using var harness = new DownloadHarness();
        var configured = Path.Combine(harness.Root, "configured");
        harness.Workflow.ApplyPreferences(new(
            true,
            DownloadNameOrder.ArtistTitle,
            false,
            TargetDirectory: configured));

        var result = harness.Workflow.OpenDownloadDirectory();

        Assert.True(result.Succeeded, result.Message);
        var invocation = Assert.Single(harness.Launcher.Invocations);
        Assert.Equal("explorer.exe", invocation.FileName);
        Assert.Contains(configured, invocation.Arguments, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Configured_download_path_is_used_for_playlist_batch_download()
    {
        await using var harness = new DownloadHarness();
        var configured = Path.Combine(harness.Root, "configured");
        var source = Path.Combine(harness.Root, "source.mp3");
        File.WriteAllBytes(source, "ID3"u8.ToArray());
        harness.Workflow.ApplyPreferences(new(
            false,
            DownloadNameOrder.TitleArtist,
            false,
            TargetDirectory: configured));
        var songs = Enumerable.Range(1, 2).Select(index => new MusicInfo
        {
            Id = $"batch-{index}",
            Source = "Local",
            Title = $"song {index}",
            Artist = "artist",
            PlayUrl = source
        }).ToArray();

        var result = await harness.Workflow.DownloadListAsync(new(
            songs,
            "歌单",
            Subdirectory: "playlist"));

        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Cancelled);
        Assert.Equal(2, result.Completed);
        Assert.All(result.Tasks, task => Assert.Equal(DownloadTaskStatus.Completed, task.Status));
        Assert.All(result.Tasks, task => Assert.True(File.Exists(task.SavePath)));
        Assert.All(result.Tasks, task =>
            Assert.True(harness.SamePath(
                Path.Combine(configured, "playlist"),
                Path.GetDirectoryName(task.SavePath)!)));
    }

    [Fact]
    public async Task Configured_root_target_is_refused_and_does_not_create_a_root_file()
    {
        await using var harness = new DownloadHarness();
        var source = Path.Combine(harness.Root, "source.mp3");
        File.WriteAllBytes(source, "ID3"u8.ToArray());
        var root = Path.GetPathRoot(Path.GetFullPath(harness.Root))!;
        harness.Workflow.ApplyPreferences(new(
            false,
            DownloadNameOrder.ArtistTitle,
            false,
            TargetDirectory: root));

        var task = await harness.Workflow.DownloadAsync(new(
            new MusicInfo
            {
                Id = "unsafe",
                Source = "Local",
                Title = "unsafe",
                Artist = "artist",
                PlayUrl = source
            }));

        Assert.Equal(DownloadTaskStatus.Failed, task.Status);
        Assert.Contains("目录", task.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(root, "artist - unsafe.mp3")));
        Assert.False(File.Exists(Path.Combine(root, "unsafe.mp3")));
    }

    [Theory]
    [InlineData("relative-downloads")]
    [InlineData("bad\0path")]
    public async Task Relative_or_invalid_explicit_targets_are_refused(string target)
    {
        await using var harness = new DownloadHarness();
        var source = Path.Combine(harness.Root, "source.mp3");
        File.WriteAllBytes(source, "ID3"u8.ToArray());

        var task = await harness.Workflow.DownloadAsync(new(
            new MusicInfo
            {
                Id = "unsafe",
                Source = "Local",
                Title = "unsafe",
                Artist = "artist",
                PlayUrl = source
            },
            TargetDirectory: target));

        Assert.Equal(DownloadTaskStatus.Failed, task.Status);
        Assert.Contains("目录", task.ErrorMessage);
    }

    [Fact]
    public async Task File_used_as_download_target_is_refused_and_preserved()
    {
        await using var harness = new DownloadHarness();
        var source = Path.Combine(harness.Root, "source.mp3");
        File.WriteAllBytes(source, "ID3"u8.ToArray());
        var blocked = Path.Combine(harness.Root, "blocked");
        File.WriteAllText(blocked, "user-content");

        var task = await harness.Workflow.DownloadAsync(new(
            new MusicInfo
            {
                Id = "blocked",
                Source = "Local",
                Title = "blocked",
                Artist = "artist",
                PlayUrl = source
            },
            TargetDirectory: blocked));

        Assert.Equal(DownloadTaskStatus.Failed, task.Status);
        Assert.Contains("目录", task.ErrorMessage);
        Assert.Equal("user-content", File.ReadAllText(blocked));
    }

    [Fact]
    public async Task Normalized_configured_download_path_commits_to_the_canonical_directory()
    {
        await using var harness = new DownloadHarness();
        var configured = Path.Combine(harness.Root, "configured");
        var normalized = configured + Path.DirectorySeparatorChar;
        var source = Path.Combine(harness.Root, "source.mp3");
        File.WriteAllBytes(source, "ID3"u8.ToArray());
        harness.Workflow.ApplyPreferences(new(
            false,
            DownloadNameOrder.ArtistTitle,
            false,
            TargetDirectory: normalized));

        var task = await harness.Workflow.DownloadAsync(new(
            new MusicInfo
            {
                Id = "normalized",
                Source = "Local",
                Title = "normalized",
                Artist = "artist",
                PlayUrl = source
            }));

        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Equal(configured, Path.GetDirectoryName(task.SavePath));
    }

    [Fact]
    public void Data_directory_change_migrates_all_persisted_user_content()
    {
        using var fixture = new DataDirectoryFixture();
        fixture.CreateCurrentContent();

        var workflow = fixture.CreateWorkflow();
        var result = workflow.ChangeDataDirectoryInRoot(fixture.SelectedParent, fixture.LocalAppData);

        Assert.True(result.Succeeded, result.Message);
        var target = result.Path;
        Assert.Equal(
            Path.Combine(fixture.SelectedParent, AppDataPaths.DefaultDirName),
            target);
        Assert.Equal(target, AppDataPaths.ResolveDataDir(fixture.LocalAppData));
        Assert.True(File.Exists(Path.Combine(target, "lxmusic.db")));
        Assert.True(File.Exists(Path.Combine(target, "lxmusic.db-wal")));
        Assert.True(File.Exists(Path.Combine(target, "logs", "app.log")));
        Assert.True(File.Exists(Path.Combine(target, "file-storage", "search_history.json")));
        Assert.True(File.Exists(Path.Combine(target, "trusted", "local-source.js")));
        Assert.True(File.Exists(Path.Combine(target, "playlists", "local.json")));
        Assert.True(AppDataOwnership.TryRead(target, out _));
        Assert.True(File.Exists(Path.Combine(
            fixture.LocalAppData,
            AppDataPaths.DefaultDirName,
            "data_dir.json")));
    }

    [Fact]
    public async Task Playback_cache_cleanup_removes_abandoned_files_but_preserves_user_content()
    {
        await using var fixture = new PlaybackCacheFixture();
        var abandoned = fixture.CreateCacheFile("lxmusic_pre_abandoned.tmp", 24);
        var retained = fixture.CreateCacheFile("user-export.json", 8);

        var result = ((IPlaybackCacheCleanup)fixture.Player).ClearPlaybackCache();

        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(24, result.FreedBytes);
        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(retained));
    }

    [Fact]
    public async Task Playback_cache_cleanup_preserves_the_active_precache_download()
    {
        await using var fixture = new PlaybackCacheFixture(blockPrecache: true);
        var current = fixture.LocalSong("current");
        var next = fixture.NetworkSong("next", "https://fixture.invalid/next.mp3");
        await fixture.Player.PlayAsync(new([current, next], current));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !Directory.EnumerateFiles(fixture.CacheDirectory, "lxmusic_pre_*.tmp").Any())
            await Task.Delay(20);

        var active = Assert.Single(Directory.EnumerateFiles(fixture.CacheDirectory, "lxmusic_pre_*.tmp"));
        var result = ((IPlaybackCacheCleanup)fixture.Player).ClearPlaybackCache();

        Assert.Equal(0, result.DeletedFiles);
        Assert.True(File.Exists(active));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "verify.ps1")))
                return directory.FullName;
            directory = directory.Parent!;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class DownloadHarness : IAsyncDisposable
    {
        public DownloadHarness()
        {
            Root = Path.Combine(Path.GetTempPath(), "LXMusicAccessible.Issue77-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            Launcher = new RecordingProcessLauncher();
            Workflow = new DownloadWorkflow(
                NullLogger<DownloadWorkflow>.Instance,
                new NotUsedDownloadSource(),
                new HttpClient(new RejectingHttpMessageHandler()) { Timeout = Timeout.InfiniteTimeSpan },
                Background,
                new RecordingAnnouncer(),
                Root,
                Launcher);
        }

        public string Root { get; }
        public BackgroundWorkHost Background { get; }
        public RecordingProcessLauncher Launcher { get; }
        public DownloadWorkflow Workflow { get; }

        public bool SamePath(string left, string right) =>
            string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

        public async ValueTask DisposeAsync()
        {
            await Workflow.DisposeAsync();
            await Background.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class NotUsedDownloadSource : IDownloadSource
    {
        public Task<IReadOnlyList<string>> GetQualitiesAsync(string source, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["128k"]);
        public Task<string?> GetUrlAsync(MusicInfo music, string quality, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Issue 77 path tests use local files.");
        public Task<LyricResult?> GetLyricAsync(MusicInfo music, CancellationToken cancellationToken) =>
            Task.FromResult<LyricResult?>(null);
    }

    private sealed class DataDirectoryFixture : IDisposable
    {
        public DataDirectoryFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "LXMusicAccessible.Issue77Data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            LocalAppData = Path.Combine(Root, "localappdata");
            SelectedParent = Path.Combine(Root, "selected");
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
            File.WriteAllText(Path.Combine(Current, "lxmusic.db"), "settings;sources;playlists;accounts");
            File.WriteAllText(Path.Combine(Current, "lxmusic.db-wal"), "recent commits");
            Directory.CreateDirectory(Path.Combine(Current, "logs"));
            File.WriteAllText(Path.Combine(Current, "logs", "app.log"), "log");
            Directory.CreateDirectory(Path.Combine(Current, "file-storage"));
            File.WriteAllText(Path.Combine(Current, "file-storage", "search_history.json"), "[]");
            Directory.CreateDirectory(Path.Combine(Current, "trusted"));
            File.WriteAllText(Path.Combine(Current, "trusted", "local-source.js"), "// source");
            Directory.CreateDirectory(Path.Combine(Current, "playlists"));
            File.WriteAllText(Path.Combine(Current, "playlists", "local.json"), "[]");
        }

        public SettingsWorkflow CreateWorkflow() => new(
            new MemorySettingsStore(),
            new HotkeyManagerService(
                NullLogger<HotkeyManagerService>.Instance,
                new NoHotkeyAdapter()),
            NullLogger<SettingsWorkflow>.Instance);

        public void Dispose()
        {
            SqliteConnectionClearPools();
            Directory.Delete(Root, recursive: true);
        }

        private static void SqliteConnectionClearPools()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<UserSettings?>(new());
        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoHotkeyAdapter : IGlobalHotkeyAdapter
    {
        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers) => true;
        public bool Unregister(IntPtr windowHandle, int id) => true;
        public int LastRegisterError => 0;
    }

    private sealed class PlaybackCacheFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly HttpClient _httpClient;
        private readonly BackgroundWorkHost _background;
        private readonly MusicPlayerService _player;

        public PlaybackCacheFixture(bool blockPrecache = false)
        {
            _directory = Path.Combine(Path.GetTempPath(), "LXMusicAccessible.Issue77Cache-" + Guid.NewGuid().ToString("N"));
            CacheDirectory = Path.Combine(_directory, "cache");
            _httpClient = new HttpClient(blockPrecache
                ? new BlockingPrecacheHandler()
                : new RejectAllHandler())
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            _player = new MusicPlayerService(
                NullLogger<MusicPlayerService>.Instance,
                new RecordingAudioPlayer(),
                new FixedPlaybackSource("https://fixture.invalid/next.mp3"),
                new RecordingAnnouncer(),
                _background,
                _httpClient,
                CacheDirectory);
        }

        public string CacheDirectory { get; }
        public IPlaybackCoordinator Player => _player;

        public string CreateCacheFile(string name, int length)
        {
            Directory.CreateDirectory(CacheDirectory);
            var path = Path.Combine(CacheDirectory, name);
            File.WriteAllBytes(path, new byte[length]);
            return path;
        }

        public MusicInfo LocalSong(string id) => new()
        {
            Id = id,
            Source = "Local",
            Title = id,
            Artist = "artist",
            PlayUrl = id
        };

        public MusicInfo NetworkSong(string id, string url) => new()
        {
            Id = id,
            Source = "fixture",
            Title = id,
            Artist = "artist",
            PlayUrl = url
        };

        public async ValueTask DisposeAsync()
        {
            await _player.DisposeAsync();
            await _background.DisposeAsync();
            _httpClient.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FixedPlaybackSource(string url) : IPlaybackSourceAdapter
    {
        public IReadOnlyList<string> GetAlternativeSourceIds(string currentSource) => [];
        public Task<string?> ResolveAsync(MusicInfo music, string quality, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(url);
        public Task<IReadOnlyList<MusicInfo>> FindAlternativesAsync(MusicInfo music, string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MusicInfo>>([]);
        public Task<string?> ResolveExactAsync(MusicInfo music, string sourceId, string quality, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(url);
    }

    private sealed class RejectAllHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Unexpected playback HTTP request.");
    }

    private sealed class BlockingPrecacheHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream())
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("audio/mpeg") }
                }
            });
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
