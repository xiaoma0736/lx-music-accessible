using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class DeclaredQualityDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "LXMusicAccessible.DeclaredQuality-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _client;
    private readonly StubHttpHandler _http = new();
    private readonly BackgroundWorkHost _background;
    private readonly FakeSource _source = new();

    public DeclaredQualityDownloadTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        _client = new HttpClient(_http) { Timeout = Timeout.InfiniteTimeSpan };
        _background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
        Workflow = new DownloadWorkflow(
            NullLogger<DownloadWorkflow>.Instance,
            _source,
            _client,
            _background,
            new RecordingAnnouncer(),
            Path.Combine(_root, "temporary"),
            new RecordingProcessLauncher());
        Workflow.ApplyPreferences(new(true, DownloadNameOrder.ArtistTitle, true));
        ShortTimeoutWorkflow = new DownloadWorkflow(
            NullLogger<DownloadWorkflow>.Instance,
            _source,
            _client,
            _background,
            new RecordingAnnouncer(),
            Path.Combine(_root, "temporary-short"),
            new RecordingProcessLauncher(),
            new DownloadThrottle(),
            resolutionTimeout: TimeSpan.FromMilliseconds(200),
            responseHeaderTimeout: TimeSpan.FromMilliseconds(200),
            readStallTimeout: TimeSpan.FromMilliseconds(200),
            lyricTimeout: TimeSpan.FromMilliseconds(200));
        ShortTimeoutWorkflow.ApplyPreferences(new(true, DownloadNameOrder.ArtistTitle, true, 0));
    }

    private DownloadWorkflow Workflow { get; }
    private DownloadWorkflow ShortTimeoutWorkflow { get; }
    private StubHttpHandler Http => _http;
    private string Target => Path.Combine(_root, "target");

    [Fact]
    public async Task Declared_quality_downloads_the_exact_selected_quality_without_authenticity_check()
    {
        _source.Qualities = ["128k", "flac"];
        var url = "https://download.invalid/declared-flac-flac";
        Http.Responses[url] = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
        };

        var task = await Workflow.DownloadAsync(new(Song("declared-flac"), "flac", Target));

        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Equal("flac", task.Quality);
        Assert.Equal("flac", task.ActualQuality);
        Assert.Null(task.QualityFallback);
        Assert.Equal(1, _source.QualityCalls);
        Assert.Equal(1, _source.UrlCalls);
        Assert.Single(Http.Requests);
        Assert.True(File.Exists(task.SavePath));
    }

    [Fact]
    public async Task Undeclared_quality_fails_before_resolving_a_url()
    {
        _source.Qualities = ["128k"];

        var task = await Workflow.DownloadAsync(new(Song("undeclared"), "flac", Target));

        Assert.Equal(DownloadTaskStatus.Failed, task.Status);
        Assert.Contains("音源未声明所选音质", task.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, _source.QualityCalls);
        Assert.Equal(0, _source.UrlCalls);
        Assert.Empty(Http.Requests);
        Assert.Empty(Directory.EnumerateFiles(Target, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Exact_quality_failure_does_not_fall_back_to_another_quality()
    {
        _source.Qualities = ["128k", "320k"];
        Http.Responses["https://download.invalid/exact-320k"] = new(HttpStatusCode.ServiceUnavailable);

        var task = await Workflow.DownloadAsync(new(Song("exact-320k"), "320k", Target));

        Assert.Equal(DownloadTaskStatus.Failed, task.Status);
        Assert.Equal(1, _source.QualityCalls);
        Assert.Equal(1, _source.UrlCalls);
        Assert.Single(Http.Requests);
        Assert.Empty(Directory.EnumerateFiles(Target, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Album_waits_for_active_single_and_queued_cancel_does_not_remain_waiting()
    {
        _source.Qualities = ["128k"];
        var releaseFirst = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.HoldUrl("active-single", releaseFirst.Task);
        Http.Responses["https://download.invalid/active-single-128k"] = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2 })
        };

        var singlePending = Workflow.DownloadAsync(new(Song("active-single"), "128k", Target));
        var first = await WaitForTaskAsync("active-single", DownloadTaskStatus.Downloading);

        var albumPending = Workflow.DownloadListAsync(new(
            [Song("queued-1"), Song("queued-2")],
            "album",
            "128k",
            TargetDirectory: Target));
        var queued = await WaitForTaskAsync("queued-1", DownloadTaskStatus.Pending);

        Assert.Equal(DownloadTaskStatus.Downloading, first.Status);
        Assert.Equal(DownloadTaskStatus.Pending, queued.Status);
        Workflow.Cancel(queued);

        releaseFirst.TrySetResult(null);
        await singlePending;
        var albumResult = await albumPending;

        Assert.Equal(DownloadTaskStatus.Completed, first.Status);
        Assert.Equal(DownloadTaskStatus.Cancelled, queued.Status);
        Assert.Equal((2, 0, 1, 1, 0),
            (albumResult.Requested, albumResult.Completed, albumResult.Failed, albumResult.Cancelled, albumResult.Skipped));
    }
    [Fact]
    public async Task Batch_continues_after_an_active_single_finishes()
    {
        _source.Qualities = ["128k"];
        var releaseSingle = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.HoldUrl("active-single", releaseSingle.Task);
        Http.Responses["https://download.invalid/active-single-128k"] = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2 })
        };
        Http.Responses["https://download.invalid/batch-1-128k"] = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 3, 4 })
        };
        Http.Responses["https://download.invalid/batch-2-128k"] = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 5, 6 })
        };

        var singlePending = Workflow.DownloadAsync(new(Song("active-single"), "128k", Target));
        var batchPending = Workflow.DownloadListAsync(new(
            [Song("batch-1"), Song("batch-2")],
            "batch",
            "128k",
            TargetDirectory: Target));
        var queued = await WaitForTaskAsync("batch-1", DownloadTaskStatus.Pending);
        Assert.Equal(DownloadTaskStatus.Pending, queued.Status);

        releaseSingle.TrySetResult(null);
        var single = await singlePending;
        var batch = await batchPending;
        var batchOne = Workflow.Tasks.Single(task => task.Music.Id == "batch-1");
        var batchTwo = Workflow.Tasks.Single(task => task.Music.Id == "batch-2");

        Assert.Equal(DownloadTaskStatus.Completed, single.Status);
        Assert.Equal(DownloadTaskStatus.Completed, batchOne.Status);
        Assert.Equal(DownloadTaskStatus.Completed, batchTwo.Status);
        Assert.Equal((2, 2, 0, 0, 0),
            (batch.Requested, batch.Completed, batch.Failed, batch.Cancelled, batch.Skipped));
    }
    [Fact]
    public async Task Batch_proceeds_when_the_active_download_resolution_stalls()
    {
        _source.Qualities = ["128k"];
        var stalled = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.HoldUrl("stalled-single", stalled.Task);
        Http.Responses["https://download.invalid/batch-ready-1-128k"] = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1 })
        };
        Http.Responses["https://download.invalid/batch-ready-2-128k"] = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 2 })
        };

        var singlePending = ShortTimeoutWorkflow.DownloadAsync(new(Song("stalled-single"), "128k", Target));
        var first = await WaitForTaskAsync(ShortTimeoutWorkflow.Tasks, "stalled-single", DownloadTaskStatus.Downloading);
        var batchPending = ShortTimeoutWorkflow.DownloadListAsync(new(
            [Song("batch-ready-1"), Song("batch-ready-2")],
            "batch",
            "128k",
            TargetDirectory: Target));
        var queued = await WaitForTaskAsync(ShortTimeoutWorkflow.Tasks, "batch-ready-1", DownloadTaskStatus.Pending);

        var single = await singlePending;
        var batch = await batchPending;

        Assert.Equal(DownloadTaskStatus.Failed, single.Status);
        Assert.Contains("超时", single.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(DownloadTaskStatus.Completed, queued.Status);
        Assert.Equal(DownloadTaskStatus.Completed, ShortTimeoutWorkflow.Tasks.Single(task => task.Music.Id == "batch-ready-2").Status);
        Assert.Equal((2, 2, 0, 0, 0),
            (batch.Requested, batch.Completed, batch.Failed, batch.Cancelled, batch.Skipped));
    }
    [Fact]
    public async Task Batch_completes_when_lyric_lookup_stalls()
    {
        _source.Qualities = ["128k"];
        var stalledLyric = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.HoldLyric("lyric-stall", stalledLyric.Task);
        Http.Responses["https://download.invalid/lyric-stall-128k"] = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2 })
        };

        var batchPending = ShortTimeoutWorkflow.DownloadListAsync(new(
            [Song("lyric-stall")],
            "album",
            "128k",
            TargetDirectory: Target));

        var batch = await batchPending;
        var task = Assert.Single(batch.Tasks);

        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.True(File.Exists(task.SavePath));
        Assert.Null(task.LyricPath);
        Assert.DoesNotContain(Directory.EnumerateFiles(Target, "*", SearchOption.AllDirectories), path => path.EndsWith(".download", StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public async Task Manual_redownload_replaces_previous_audio_and_lyric()
    {
        _source.Qualities = ["128k"];
        _source.ReturnLyric = true;
        var url = "https://download.invalid/manual-128k";
        Http.Responses[url] = new(HttpStatusCode.OK) { Content = new ByteArrayContent("old-audio"u8.ToArray()) };

        var task = await Workflow.DownloadAsync(new(Song("manual"), "128k", Target));
        Assert.True(File.Exists(task.SavePath), "audio missing");
        Assert.True(File.Exists(task.LyricPath), "lyric missing");
        var oldAudio = await Task.Run(() => File.ReadAllText(task.SavePath!));
        var oldLyric = await Task.Run(() => File.ReadAllText(task.LyricPath!));

        task.SavePath = Path.Combine(Target, "locked-audio.mp3") + Path.DirectorySeparatorChar;
        task.LyricPath = Path.Combine(Target, "locked-lrc.lrc") + Path.DirectorySeparatorChar;
        Http.Responses[url] = new(HttpStatusCode.OK) { Content = new ByteArrayContent("new-audio"u8.ToArray()) };
        Assert.True(await Workflow.RedownloadAsync(task));

        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.True(File.Exists(task.SavePath));
        Assert.True(File.Exists(task.LyricPath));
        Assert.DoesNotContain(Directory.EnumerateFiles(Target, "*", SearchOption.AllDirectories), path => path.EndsWith(".download", StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public async Task External_playback_materializes_a_declared_quality_without_authenticity_check()
    {
        _source.Qualities = ["128k", "flac"];
        var url = "https://download.invalid/external-declared-flac";
        Http.Responses[url] = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
        };

        var target = await Workflow.PrepareExternalPlaybackAsync(Song("external-declared"), "flac");

        Assert.NotNull(target);
        Assert.True(File.Exists(target.Value));
        Assert.Equal(1, _source.UrlCalls);
        Assert.Single(Http.Requests);
        Workflow.ClearTemporaryFiles();
        Assert.False(File.Exists(target.Value));
    }

    [Fact]
    public async Task External_playback_does_not_resolve_an_undeclared_quality()
    {
        _source.Qualities = ["128k"];

        var target = await Workflow.PrepareExternalPlaybackAsync(Song("external-undeclared"), "flac");

        Assert.Null(target);
        Assert.Equal(0, _source.UrlCalls);
        Assert.Empty(Http.Requests);
        Assert.Empty(Directory.EnumerateFiles(Target, "*", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        ShortTimeoutWorkflow.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Workflow.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _background.Dispose();
        _client.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Task<DownloadTask> WaitForTaskAsync(string songId, DownloadTaskStatus status) =>
        WaitForTaskAsync(Workflow.Tasks, songId, status);

    private static async Task<DownloadTask> WaitForTaskAsync(
        ReadOnlyObservableCollection<DownloadTask> tasks,
        string songId,
        DownloadTaskStatus status)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var task = tasks.FirstOrDefault(item => item.Music.Id == songId);
            if (task is not null && task.Status == status) return task;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Task {songId} did not reach {status}.");
    }
    private static MusicInfo Song(string id) => new()
    {
        Id = id,
        Source = "qq",
        Title = "Song",
        Artist = "Artist"
    };

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        public Dictionary<string, HttpResponseMessage> Responses { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var prototype))
            {
                var bytes = await prototype.Content.ReadAsByteArrayAsync();
                var response = new HttpResponseMessage(prototype.StatusCode)
                {
                    Content = new ByteArrayContent(bytes)
                };
                foreach (var header in prototype.Content.Headers)
                    response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                return await Task.Run(() => response, cancellationToken);
            }
            return await Task.Run(
                () => new HttpResponseMessage(HttpStatusCode.NotFound),
                cancellationToken);
        }
    }

    private sealed class FakeSource : IDownloadSource
    {
        public IReadOnlyList<string> Qualities { get; set; } = ["128k"];
        private int _qualityCalls; public int QualityCalls => _qualityCalls;
        private int _urlCalls; public int UrlCalls => _urlCalls;
        private readonly Dictionary<string, Task> _heldUrls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Task> _heldLyrics = new(StringComparer.Ordinal);
        public bool ReturnLyric { get; set; }
        public bool FailAudioOnce { get; set; }
        public Task<IReadOnlyList<string>> GetQualitiesAsync(
            MusicInfo music,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _qualityCalls);
            return Task.FromResult(Qualities);
        }

        public void HoldLyric(string songId, Task wait)
        {
            _heldLyrics[songId] = wait;
        }

        public void HoldUrl(string songId, Task wait)
        {
            _heldUrls[songId] = wait;
        }

        public async Task<string?> GetUrlAsync(
            MusicInfo music,
            string quality,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _urlCalls);
            var failAudio = FailAudioOnce;
            FailAudioOnce = false;
            if (_heldUrls.TryGetValue(music.Id, out var wait))
            {
                await wait.WaitAsync(cancellationToken);
            }
            return failAudio
                ? $"https://download.invalid/{music.Id}-{quality}-failed"
                : $"https://download.invalid/{music.Id}-{quality}";
        }

        public async Task<LyricResult?> GetLyricAsync(
            MusicInfo music,
            CancellationToken cancellationToken)
        {
            if (_heldLyrics.TryGetValue(music.Id, out var wait))
                await wait.WaitAsync(cancellationToken);
            if (!ReturnLyric) return null;
            return new LyricResult
            {
                SourceId = music.Source,
                Lyric = new Lyric
                {
                    Lines = [new LyricLine { Time = TimeSpan.FromSeconds(1), Text = "lyric" }]
                }
            };
        }
    }
}
