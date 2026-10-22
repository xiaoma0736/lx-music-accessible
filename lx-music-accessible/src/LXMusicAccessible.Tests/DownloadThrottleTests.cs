using System.Collections.ObjectModel;
using System.Diagnostics;
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

public sealed class DownloadThrottleTests
{
    [Fact]
    public async Task Between_song_delay_is_recorded_once_for_more_songs()
    {
        var throttle = new RecordingThrottle();
        var preferences = new DownloadPreferences(
            true,
            DownloadNameOrder.ArtistTitle,
            true,
            2,
            0);

        await throttle.DelayBetweenSongsAsync(preferences, hasMoreSongs: true, CancellationToken.None);
        await throttle.DelayBetweenSongsAsync(preferences, hasMoreSongs: false, CancellationToken.None);

        var delay = Assert.Single(throttle.Delays);
        Assert.Equal(TimeSpan.FromSeconds(2), delay);
    }

    [Fact]
    public async Task Between_song_delay_is_not_recorded_when_already_cancelled()
    {
        var throttle = new RecordingThrottle();
        var preferences = new DownloadPreferences(true, DownloadNameOrder.ArtistTitle, true, 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await throttle.DelayBetweenSongsAsync(preferences, true, cancellation.Token);

        Assert.Empty(throttle.Delays);
    }

    [Fact]
    public async Task Between_song_delay_honors_cancellation()
    {
        var throttle = new DownloadThrottle();
        var preferences = new DownloadPreferences(true, DownloadNameOrder.ArtistTitle, true, 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            throttle.DelayBetweenSongsAsync(preferences, true, cancellation.Token));
    }

    [Fact]
    public async Task Transfer_delay_uses_1024_bytes_per_kilobyte()
    {
        var throttle = new RecordingThrottle();
        var preferences = new DownloadPreferences(true, DownloadNameOrder.ArtistTitle, true, 2, 32);

        await throttle.PaceTransferAsync(preferences, TimeSpan.Zero, 1024, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(1024d / (32 * 1024d)), throttle.Delays.Single());
    }

    [Fact]
    public void Transfer_delay_is_reduced_by_elapsed_transfer_time()
    {
        var elapsed = TimeSpan.FromMilliseconds(20);
        Assert.Equal(TimeSpan.FromMilliseconds(42.5), DownloadThrottle.TransferDelay(32, elapsed, 2048));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Unlimited_transfer_does_not_delay(int limit)
    {
        Assert.Equal(TimeSpan.Zero, DownloadThrottle.TransferDelay(limit, TimeSpan.Zero, 1024));
        Assert.Equal(TimeSpan.Zero, DownloadThrottle.TransferDelay(limit, TimeSpan.FromSeconds(1), 1024));
    }

    [Fact]
    public void Invalid_transfer_sizes_do_not_delay()
    {
        Assert.Equal(TimeSpan.Zero, DownloadThrottle.TransferDelay(32, TimeSpan.Zero, 0));
        Assert.Equal(TimeSpan.Zero, DownloadThrottle.TransferDelay(32, TimeSpan.Zero, -1));
    }

    [Fact]
    public void Transfer_delay_is_never_negative()
    {
        Assert.Equal(TimeSpan.Zero, DownloadThrottle.TransferDelay(32, TimeSpan.FromSeconds(1), 1024));
    }

    [Fact]
    public async Task Batch_failure_continues_and_prepares_next_delay()
    {
        await using var harness = new DownloadHarness();
        harness.Workflow.ApplyPreferences(new(true, DownloadNameOrder.ArtistTitle, true, 2, 0));
        Uri goodUrl = harness.Source.Register("good");
        Uri badUrl = harness.Source.Register("bad");
        harness.Http.Register(HttpMethod.Get, goodUrl, () => AudioResponse(64));
        harness.Http.Register(HttpMethod.Get, badUrl, () => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await harness.Workflow.DownloadListAsync(
            new DownloadListRequest([Song("bad"), Song("good")], "节流", TargetDirectory: harness.Target));

        Assert.Equal((2, 1, 1, 0, 0),
            (result.Requested, result.Completed, result.Failed, result.Cancelled, result.Skipped));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.Zero], harness.Throttle.Delays.ToArray());
        Assert.Equal(2, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task Batch_cancellation_during_resolution_stops_remaining_song()
    {
        await using var harness = new DownloadHarness();
        harness.Workflow.ApplyPreferences(new(true, DownloadNameOrder.ArtistTitle, true, 2, 32));
        harness.Source.BlockUrl("blocking");
        harness.Source.Register("remaining");
        using var cancellation = new CancellationTokenSource();

        var pending = harness.Workflow.DownloadListAsync(
            new DownloadListRequest([Song("blocking"), Song("remaining")], "batch-cancel", TargetDirectory: harness.Target),
            cancellation.Token);

        var downloadTask = await WaitForDownloadingTaskAsync(harness.Workflow.Tasks, "blocking");
        harness.Workflow.Cancel(downloadTask);
        cancellation.Cancel();
        var result = await pending;

        Assert.Equal((0, 0, 1, 1),
            (result.Completed, result.Failed, result.Cancelled, result.Skipped));
        Assert.Empty(harness.Http.Requests);
        Assert.Empty(EnumerateFiles(harness.Target));
        Assert.Equal(0, harness.ActiveBackgroundWork);
    }

    private static async Task<DownloadTask> WaitForDownloadingTaskAsync(
        ReadOnlyObservableCollection<DownloadTask> tasks,
        string songId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var task = tasks.FirstOrDefault(item => item.Music.Id == songId);
            if (task is not null && task.Status == DownloadTaskStatus.Downloading) return task;
            await Task.Delay(25);
        }

        throw new TimeoutException("The blocked batch download did not start.");
    }

    private static HttpResponseMessage AudioResponse(int size)
    {
        var bytes = new byte[Math.Max(size, 4)];
        "ID3"u8.CopyTo(bytes);
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

    private static string[] EnumerateFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray()
            : [];

    private sealed class RecordingThrottle : DownloadThrottle
    {
        public List<TimeSpan> Delays { get; } = new();

        public override Task DelayBetweenSongsAsync(
            DownloadPreferences preferences,
            bool hasMoreSongs,
            CancellationToken cancellationToken)
        {
            if (!hasMoreSongs || cancellationToken.IsCancellationRequested)
                return Task.CompletedTask;
            Delays.Add(TimeSpan.FromSeconds(preferences.BatchDownloadDelaySeconds));
            return Task.CompletedTask;
        }

        public override Task PaceTransferAsync(
            DownloadPreferences preferences,
            TimeSpan elapsed,
            long totalBytes,
            CancellationToken cancellationToken)
        {
            var delay = DownloadThrottle.TransferDelay(preferences.DownloadSpeedLimitKbps, elapsed, totalBytes);
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class DownloadHarness : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly BackgroundWorkHost _background;

        public DownloadHarness()
        {
            Root = Path.Combine(Path.GetTempPath(), "LXMusicAccessible.T74-" + Guid.NewGuid().ToString("N"));
            Target = Path.Combine(Root, "downloads");
            Directory.CreateDirectory(Target);
            Http = new RejectingHttpMessageHandler();
            _client = new HttpClient(Http) { Timeout = Timeout.InfiniteTimeSpan };
            _background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
            Source = new ControlledDownloadSource();
            Announcer = new RecordingAnnouncer();
            Throttle = new RecordingThrottle();
            Workflow = new DownloadWorkflow(
                NullLogger<DownloadWorkflow>.Instance,
                Source,
                _client,
                _background,
                Announcer,
                Root,
                new InfrastructureRecordingProcessLauncher(),
                Throttle);
        }

        public string Root { get; }
        public string Target { get; }
        public RejectingHttpMessageHandler Http { get; }
        public ControlledDownloadSource Source { get; }
        public RecordingAnnouncer Announcer { get; }
        public RecordingThrottle Throttle { get; }
        public DownloadWorkflow Workflow { get; }
        public int ActiveBackgroundWork => _background.ActiveCount;

        public async ValueTask DisposeAsync()
        {
            await Workflow.DisposeAsync();
            await _background.DisposeAsync();
            _client.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ControlledDownloadSource : IDownloadSource
    {
        private readonly Dictionary<string, Uri> _urls = new(StringComparer.Ordinal);
        private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
        private readonly IReadOnlyList<string> _qualities = ["128k"];

        public void BlockUrl(string id)
        {
            _urls[id] = new Uri($"https://downloads.invalid/{id}.mp3");
            _blocked.Add(id);
        }

        public Uri Register(string id)
        {
            var url = new Uri($"https://downloads.invalid/{id}.mp3");
            _urls[id] = url;
            return url;
        }

        public Task<IReadOnlyList<string>> GetQualitiesAsync(MusicInfo music, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_qualities);
        }

        public async Task<string?> GetUrlAsync(
            MusicInfo music,
            string quality,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_urls.TryGetValue(music.Id, out var url)) return null;
            if (!_blocked.Contains(music.Id)) return url.AbsoluteUri;

            var blocked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => blocked.TrySetCanceled(cancellationToken));
            return await blocked.Task;
        }

        public Task<LyricResult?> GetLyricAsync(
            MusicInfo music,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<LyricResult?>(null);
        }
    }

    private sealed class InfrastructureRecordingProcessLauncher : IProcessLauncher
    {
        public List<string> Invocations { get; } = new();

        public void Start(ProcessStartInfo startInfo) => Invocations.Add(startInfo.FileName);
    }

}
