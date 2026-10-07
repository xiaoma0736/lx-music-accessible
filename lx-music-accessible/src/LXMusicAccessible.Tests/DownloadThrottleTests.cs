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
    public async Task Batch_cancellation_during_transfer_stops_remaining_song()
    {
        await using var harness = new DownloadHarness();
        harness.Workflow.ApplyPreferences(new(true, DownloadNameOrder.ArtistTitle, true, 2, 32));
        Uri url = harness.Source.Register("blocking");
        harness.Source.Register("remaining");
        var stream = new BlockingReadStream(1024);
        harness.Http.Register(HttpMethod.Get, url, () => StreamResponse(stream, 1024));
        using var cancellation = new CancellationTokenSource();

        var pending = harness.Workflow.DownloadListAsync(
            new DownloadListRequest([Song("blocking"), Song("remaining")], "取消", TargetDirectory: harness.Target),
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

    private static HttpResponseMessage AudioResponse(int size)
    {
        var bytes = new byte[Math.Max(size, 4)];
        "ID3"u8.CopyTo(bytes);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage StreamResponse(Stream stream, int length)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        content.Headers.ContentLength = length;
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
        private readonly IReadOnlyList<string> _qualities = ["128k"];

        public Uri Register(string id)
        {
            var url = new Uri($"https://downloads.invalid/{id}.mp3");
            _urls[id] = url;
            return url;
        }

        public Task<IReadOnlyList<string>> GetQualitiesAsync(
            string source,
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
            return Task.FromResult(_urls.TryGetValue(music.Id, out var url) ? url.AbsoluteUri : null);
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

    private sealed class BlockingReadStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly TaskCompletionSource<object?> _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _first = true;

        public BlockingReadStream(int length) => _bytes = new byte[length];

        public Task FirstRead => _firstRead.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.LongLength;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_first)
            {
                _first = false;
                var length = Math.Min(count, _bytes.Length);
                Array.Copy(_bytes, 0, buffer, offset, length);
                _firstRead.TrySetResult(null);
                return Task.FromResult(length);
            }
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(_ => 0, cancellationToken);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
