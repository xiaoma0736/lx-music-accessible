using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class DownloadWorkflowTests
{
    [Fact]
    public async Task Success_uses_bounded_progress_and_leaves_only_atomic_final_files()
    {
        using var harness = new DownloadHarness();
        var url = harness.Source.Register("success");
        harness.Source.RegisterLyric("success");
        harness.Http.Register(HttpMethod.Get, url, () => AudioResponse(240_000));

        var task = await harness.Workflow.DownloadAsync(new(
            Song("success", "title", "artist"),
            TargetDirectory: harness.Target));

        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.NotNull(task.SavePath);
        Assert.True(File.Exists(task.SavePath));
        Assert.NotNull(task.LyricPath);
        Assert.True(File.Exists(task.LyricPath));
        Assert.Equal("mp3", task.ActualQuality);
        Assert.DoesNotContain(EnumerateFiles(harness.Target), IsTemporary);
        Assert.Equal(2, EnumerateFiles(harness.Target).Length);

        var announcements = harness.Announcer.Announcements
            .Where(item => item.ActivityId == $"download:{task.Id}")
            .ToArray();
        Assert.Equal(AnnouncementKind.Busy, announcements.First().Kind);
        Assert.Equal(AnnouncementKind.Success, announcements.Last().Kind);
        Assert.InRange(announcements.Count(item => item.Kind == AnnouncementKind.Progress), 1, 3);
    }

    [Fact]
    public async Task Cancellation_removes_the_partial_file_and_reports_cancelled()
    {
        using var harness = new DownloadHarness();
        var url = harness.Source.Register("cancel");
        var stream = new BlockingReadStream();
        harness.Http.Register(HttpMethod.Get, url, () =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            content.Headers.ContentLength = 256;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var pending = harness.Workflow.DownloadAsync(new(
            Song("cancel"),
            TargetDirectory: harness.Target));
        await stream.FirstRead.WaitAsync(TimeSpan.FromSeconds(5));
        var task = Assert.Single(harness.Workflow.Tasks);
        harness.Workflow.Cancel(task);
        task = await pending;

        Assert.Equal(DownloadTaskStatus.Cancelled, task.Status);
        Assert.Empty(EnumerateFiles(harness.Target));
        Assert.Contains(
            harness.Announcer.Announcements,
            item => item.Kind == AnnouncementKind.Error && item.Text.Contains("取消"));
    }

    [Fact]
    public async Task Clear_completed_removes_every_terminal_task_and_preserves_active_downloads()
    {
        using var harness = new DownloadHarness();
        var completedUrl = harness.Source.Register("completed");
        harness.Http.Register(HttpMethod.Get, completedUrl, () => AudioResponse(64));
        var completed = await harness.Workflow.DownloadAsync(new(
            Song("completed"),
            TargetDirectory: harness.Target));

        harness.Source.Register("failed");
        var failed = await harness.Workflow.DownloadAsync(new(
            Song("failed"),
            TargetDirectory: harness.Target));

        var cancelledUrl = harness.Source.Register("cancelled");
        var cancelledStream = new BlockingReadStream();
        harness.Http.Register(HttpMethod.Get, cancelledUrl, () =>
        {
            var content = new StreamContent(cancelledStream);
            content.Headers.ContentLength = 256;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var cancelling = harness.Workflow.DownloadAsync(new(
            Song("cancelled"),
            TargetDirectory: harness.Target));
        await cancelledStream.FirstRead.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Workflow.Cancel(harness.Workflow.Tasks.Single(task => task.Music.Id == "cancelled"));
        var cancelled = await cancelling;

        var activeUrl = harness.Source.Register("active");
        var activeStream = new BlockingReadStream();
        harness.Http.Register(HttpMethod.Get, activeUrl, () =>
        {
            var content = new StreamContent(activeStream);
            content.Headers.ContentLength = 256;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var activeDownload = harness.Workflow.DownloadAsync(new(
            Song("active"),
            TargetDirectory: harness.Target));
        await activeStream.FirstRead.WaitAsync(TimeSpan.FromSeconds(5));
        var active = harness.Workflow.Tasks.Single(task => task.Music.Id == "active");

        Assert.Equal(DownloadTaskStatus.Completed, completed.Status);
        Assert.Equal(DownloadTaskStatus.Failed, failed.Status);
        Assert.Equal(DownloadTaskStatus.Cancelled, cancelled.Status);
        Assert.Equal(DownloadTaskStatus.Downloading, active.Status);

        harness.Workflow.ClearCompleted();

        Assert.Same(active, Assert.Single(harness.Workflow.Tasks));
        harness.Workflow.Cancel(active);
        await activeDownload;
    }

    [Fact]
    public async Task Network_failure_removes_temporary_files_and_can_be_retried()
    {
        using var harness = new DownloadHarness();
        harness.Source.SetQualities("128k", "320k");
        var url = harness.Source.Register("retry", "320k");
        harness.Http.Register(HttpMethod.Get, url, () =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var task = await harness.Workflow.DownloadAsync(new(
            Song("retry"),
            "320k",
            TargetDirectory: harness.Target));

        Assert.Equal(DownloadTaskStatus.Failed, task.Status);
        Assert.Empty(EnumerateFiles(harness.Target));

        harness.Http.Register(HttpMethod.Get, url, () => Mp3Response(320, 32));
        Assert.True(await harness.Workflow.RetryAsync(task));
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Equal("320k", task.Quality);
        Assert.Equal("320k", task.ActualQuality);
        Assert.Null(task.QualityFallback);
        Assert.Single(EnumerateFiles(harness.Target));
        Assert.DoesNotContain(EnumerateFiles(harness.Target), IsTemporary);
    }

    [Fact]
    public async Task Requested_lossless_quality_does_not_accept_a_lowest_quality_mp3_payload()
    {
        using var harness = new DownloadHarness();
        harness.Source.SetQualities("128k", "320k", "flac");
        var url = harness.Source.Register("quality-mismatch", "flac");
        harness.Http.Register(HttpMethod.Get, url, () => AudioResponse(64));

        var task = await harness.Workflow.DownloadAsync(new(
            Song("quality-mismatch"),
            "flac",
            harness.Target));

        Assert.Equal(DownloadTaskStatus.Failed, task.Status);
        Assert.Contains("音质", task.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(EnumerateFiles(harness.Target));
    }

    [Fact]
    public async Task Unsupported_requested_quality_uses_an_explicit_verified_fallback()
    {
        using var harness = new DownloadHarness();
        harness.Source.SetQualities("128k", "flac");
        var selected = harness.Source.Register("quality-fallback", "flac");
        var fallback = harness.Source.Register("quality-fallback", "128k");
        harness.Http.Register(HttpMethod.Get, selected, () => Mp3Response(128, 64));
        harness.Http.Register(HttpMethod.Get, fallback, () => Mp3Response(128, 64));

        var task = await harness.Workflow.DownloadAsync(new(
            Song("quality-fallback"),
            "flac",
            harness.Target));

        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Equal("128k", task.Quality);
        Assert.Equal("128k", task.ActualQuality);
        Assert.Equal("请求 flac，已回退到 128k", task.QualityFallback);
        Assert.Contains("回退", task.AccessibleStatus, StringComparison.Ordinal);
        Assert.Contains(
            harness.Announcer.Announcements,
            item => item.Kind == AnnouncementKind.Success
                && item.Text.Contains("回退", StringComparison.Ordinal));
        Assert.Equal(2, harness.Http.Requests.Count);

        Assert.True(await harness.Workflow.RetryAsync(task));
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Equal("128k", task.Quality);
        Assert.Equal("请求 flac，已回退到 128k", task.QualityFallback);
        Assert.Equal(4, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task Conflict_and_dangerous_names_stay_inside_the_target_without_overwriting()
    {
        using var harness = new DownloadHarness();
        var url = harness.Source.Register("name");
        harness.Http.Register(HttpMethod.Get, url, () => AudioResponse(64));
        var existing = Path.Combine(harness.Target, "artist - title [mp3].mp3");
        await Task.Run(() => File.WriteAllText(existing, "user-content"));

        var conflict = await harness.Workflow.DownloadAsync(new(
            Song("name", "title", "artist"),
            TargetDirectory: harness.Target));
        Assert.EndsWith("artist - title [mp3](1).mp3", conflict.SavePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("user-content", await Task.Run(() => File.ReadAllText(existing)));

        var dangerousUrl = harness.Source.Register("dangerous-name");
        harness.Http.Register(HttpMethod.Get, dangerousUrl, () => AudioResponse(64));
        var dangerous = await harness.Workflow.DownloadAsync(new(
            Song("dangerous-name", "../CON:<escape>", "..\\.."),
            TargetDirectory: harness.Target));

        Assert.Equal(DownloadTaskStatus.Completed, dangerous.Status);
        Assert.True(IsWithin(harness.Target, dangerous.SavePath!));
        Assert.DoesNotContain("..", Path.GetFileName(dangerous.SavePath));
        Assert.DoesNotContain(EnumerateFiles(harness.Target), IsTemporary);
    }

    [Fact]
    public async Task Root_system_and_reparse_targets_are_refused_before_http()
    {
        using var harness = new DownloadHarness();
        var url = harness.Source.Register("unsafe");
        harness.Http.Register(HttpMethod.Get, url, () => AudioResponse(64));

        var driveRoot = Path.GetPathRoot(harness.Root)!;
        var rootTask = await harness.Workflow.DownloadAsync(new(
            Song("unsafe"),
            TargetDirectory: driveRoot));
        Assert.Equal(DownloadTaskStatus.Failed, rootTask.Status);

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            var systemTask = await harness.Workflow.DownloadAsync(new(
                Song("unsafe"),
                TargetDirectory: windows));
            Assert.Equal(DownloadTaskStatus.Failed, systemTask.Status);
        }
        Assert.Empty(harness.Http.Requests);

        var outside = CreateTestDirectory("LXMusicAccessible.T24-link-target-");
        var link = Path.Combine(harness.Root, "linked-downloads");
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException)
            {
                return;
            }

            var linkedTask = await harness.Workflow.DownloadAsync(new(
                Song("unsafe"),
                TargetDirectory: link));
            Assert.Equal(DownloadTaskStatus.Failed, linkedTask.Status);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
            Assert.Empty(harness.Http.Requests);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            DeleteTestDirectory(outside, "LXMusicAccessible.T24-link-target-");
        }
    }

    [Fact]
    public async Task List_download_reuses_single_rules_and_reports_partial_failure_once()
    {
        using var harness = new DownloadHarness();
        var goodUrl = harness.Source.Register("good");
        var badUrl = harness.Source.Register("bad");
        harness.Http.Register(HttpMethod.Get, goodUrl, () => AudioResponse(64));
        harness.Http.Register(HttpMethod.Get, badUrl, () =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await harness.Workflow.DownloadListAsync(new(
            [Song("good", "one"), Song("bad", "two")],
            "测试列表",
            TargetDirectory: harness.Target,
            Subdirectory: "../CON:album"));

        Assert.False(result.Succeeded);
        Assert.True(result.PartiallySucceeded);
        Assert.Equal((2, 1, 1, 0, 0),
            (result.Requested, result.Completed, result.Failed, result.Cancelled, result.Skipped));
        Assert.Equal(
            new[] { DownloadTaskStatus.Completed, DownloadTaskStatus.Failed },
            result.Tasks.Select(task => task.Status));
        Assert.Single(EnumerateFiles(harness.Target));
        Assert.DoesNotContain(EnumerateFiles(harness.Target), IsTemporary);
        var summaries = harness.Announcer.Announcements
            .Where(item => item.ActivityId?.StartsWith("download-list:", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(2, summaries.Length);
        Assert.Equal(AnnouncementKind.Busy, summaries[0].Kind);
        Assert.Equal(AnnouncementKind.Error, summaries[1].Kind);
        Assert.Contains("成功 1 首，失败 1 首", summaries[1].Text);
    }

    [Fact]
    public async Task List_cancellation_is_host_owned_and_marks_unstarted_songs_as_skipped()
    {
        using var harness = new DownloadHarness();
        var firstUrl = harness.Source.Register("first");
        harness.Source.Register("never-started");
        var stream = new BlockingReadStream();
        harness.Http.Register(HttpMethod.Get, firstUrl, () =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentLength = 256;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var cancellation = new CancellationTokenSource();

        var pending = harness.Workflow.DownloadListAsync(new(
            [Song("first"), Song("never-started")],
            "取消列表",
            TargetDirectory: harness.Target),
            cancellation.Token);
        await stream.FirstRead.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await pending;

        Assert.Equal((0, 0, 1, 1),
            (result.Completed, result.Failed, result.Cancelled, result.Skipped));
        Assert.Single(harness.Http.Requests);
        Assert.Empty(EnumerateFiles(harness.Target));
        Assert.Equal(0, harness.ActiveBackgroundWork);
    }

    [Fact]
    public async Task External_playback_uses_controlled_http_and_owned_temporary_cleanup()
    {
        using var harness = new DownloadHarness();
        var url = harness.Source.Register("external");
        harness.Http.Register(HttpMethod.Get, url, () => AudioResponse(64));

        var target = await harness.Workflow.PrepareExternalPlaybackAsync(
            Song("external"),
            "128k");

        Assert.NotNull(target);
        Assert.True(File.Exists(target.Value));
        Assert.True(IsWithin(harness.Root, target.Value));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.GetDirectoryName(target.Value)!),
            IsTemporary);

        var cleanup = harness.Workflow.ClearTemporaryFiles();
        Assert.Equal(1, cleanup.DeletedFiles);
        Assert.False(File.Exists(target.Value));
    }

    [Fact]
    public async Task External_playback_does_not_pass_a_detected_lower_quality_url_to_the_player()
    {
        using var harness = new DownloadHarness();
        harness.Source.SetQualities("128k", "flac");
        var url = harness.Source.Register("external-low", "flac");
        harness.Http.Register(HttpMethod.Get, url, () => Mp3Response(128, 64));

        var target = await harness.Workflow.PrepareExternalPlaybackAsync(
            Song("external-low"),
            "flac");

        Assert.Null(target);
        Assert.Empty(EnumerateFiles(harness.Target));
        Assert.Contains(
            harness.Announcer.Announcements,
            item => item.Kind == AnnouncementKind.Error
                && item.ActivityId?.StartsWith("download-external:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task External_player_launch_is_owned_by_the_download_workflow()
    {
        using var harness = new DownloadHarness();
        var player = Path.Combine(harness.Root, "player.exe");
        var audio = Path.Combine(harness.Root, "song.mp3");
        await Task.Run(() => File.WriteAllText(player, "fixture"));
        await Task.Run(() => File.WriteAllText(audio, "fixture"));

        var result = await harness.Workflow.PlayExternallyAsync(new(
            player,
            new MusicInfo { Id = "local", Title = "Song", Source = "Local", PlayUrl = audio },
            "128k"));

        Assert.True(result.Succeeded);
        var invocation = Assert.Single(harness.ProcessLauncher.Invocations);
        Assert.Equal(player, invocation.FileName);
        Assert.Contains(audio, invocation.Arguments, StringComparison.Ordinal);
    }

    private static HttpResponseMessage AudioResponse(int size)
    {
        var bytes = new byte[Math.Max(size, 4)];
        "ID3"u8.CopyTo(bytes);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Mp3Response(int bitrate, int size)
    {
        var bytes = new byte[Math.Max(size, 4)];
        bytes[0] = 0xFF;
        bytes[1] = 0xFB;
        bytes[2] = bitrate switch
        {
            128 => 0x90,
            192 => 0xB0,
            320 => 0xE0,
            _ => throw new ArgumentOutOfRangeException(nameof(bitrate))
        };
        bytes[3] = 0x64;
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static MusicInfo Song(
        string id,
        string title = "title",
        string artist = "artist") => new()
    {
        Id = id,
        Source = "test",
        Title = title,
        Artist = artist
    };

    private static bool IsTemporary(string path) =>
        Path.GetFileName(path).EndsWith(".download", StringComparison.OrdinalIgnoreCase);

    private static string[] EnumerateFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray()
            : [];

    private static bool IsWithin(string root, string path)
    {
        var relative = FrameworkCompatibility.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != "."
            && !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string CreateTestDirectory(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    private static void DeleteTestDirectory(string directory, string prefix)
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var full = Path.GetFullPath(directory);
        if (!IsWithin(tempRoot, full)
            || !Path.GetFileName(full).StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete non-test directory: {full}");
        }
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private sealed class DownloadHarness : IAsyncDisposable
    {
        private const string Prefix = "LXMusicAccessible.T24-";
        private readonly HttpClient _client;
        private readonly BackgroundWorkHost _background;

        public DownloadHarness()
        {
            Root = CreateTestDirectory(Prefix);
            Target = Directory.CreateDirectory(Path.Combine(Root, "downloads")).FullName;
            Http = new RejectingHttpMessageHandler();
            _client = new HttpClient(Http) { Timeout = Timeout.InfiniteTimeSpan };
            _background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            Source = new ControlledDownloadSource();
            Announcer = new RecordingAnnouncer();
            ProcessLauncher = new RecordingProcessLauncher();
            Workflow = new DownloadWorkflow(
                NullLogger<DownloadWorkflow>.Instance,
                Source,
                _client,
                _background,
                Announcer,
                Path.Combine(Root, "temporary"),
                ProcessLauncher);
            Workflow.ApplyPreferences(new(true, DownloadNameOrder.ArtistTitle, true));
        }

        public string Root { get; }
        public string Target { get; }
        public RejectingHttpMessageHandler Http { get; }
        public ControlledDownloadSource Source { get; }
        public RecordingAnnouncer Announcer { get; }
        public RecordingProcessLauncher ProcessLauncher { get; }
        public DownloadWorkflow Workflow { get; }
        public int ActiveBackgroundWork => _background.ActiveCount;

        public async ValueTask DisposeAsync()
        {
            await Workflow.DisposeAsync();
            await _background.DisposeAsync();
            _client.Dispose();
            DeleteTestDirectory(Root, Prefix);
        }
    }

    private sealed class ControlledDownloadSource : IDownloadSource
    {
        private readonly Dictionary<string, Uri> _urls = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Id, string Quality), Uri> _qualityUrls = new();
        private readonly HashSet<string> _lyrics = new(StringComparer.Ordinal);
        private IReadOnlyList<string> _qualities = ["128k"];

        public Uri Register(string id)
        {
            var uri = new Uri($"https://downloads.invalid/{id}.mp3");
            _urls[id] = uri;
            return uri;
        }

        public Uri Register(string id, string quality)
        {
            var uri = new Uri($"https://downloads.invalid/{id}-{quality}.mp3");
            _qualityUrls[(id, quality)] = uri;
            return uri;
        }

        public void SetQualities(params string[] qualities) => _qualities = qualities;

        public void RegisterLyric(string id) => _lyrics.Add(id);

        public Task<IReadOnlyList<string>> GetQualitiesAsync(
            MusicInfo music,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_qualities);
        }

        public Task<string?> GetUrlAsync(
            MusicInfo music,
            string quality,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _qualityUrls.TryGetValue((music.Id, quality), out var qualityUri)
                    ? qualityUri.AbsoluteUri
                    : _urls.TryGetValue(music.Id, out var uri)
                        ? uri.AbsoluteUri
                        : null);
        }

        public Task<LyricResult?> GetLyricAsync(
            MusicInfo music,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_lyrics.Contains(music.Id)
                ? new LyricResult
                {
                    SourceId = music.Source,
                    Lyric = new Lyric
                    {
                        Lines =
                        [
                            new LyricLine
                            {
                                Time = TimeSpan.FromSeconds(1),
                                Text = "test lyric"
                            }
                        ]
                    }
                }
                : null);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private bool _first = true;
        private readonly TaskCompletionSource<object?> _firstRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstRead => _firstRead.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private async Task<int> ReadCoreAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            if (_first)
            {
                _first = false;
                var bytes = new byte[] { 0x49, 0x44, 0x33, 0x2D, 0x70, 0x61, 0x72, 0x74 };
                Array.Copy(bytes, 0, buffer, offset, Math.Min(count, bytes.Length));
                _firstRead.TrySetResult(null);
                return bytes.Length;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => ReadCoreAsync(buffer, offset, count, cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
