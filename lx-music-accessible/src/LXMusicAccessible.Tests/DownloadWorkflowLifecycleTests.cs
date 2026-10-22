using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class DownloadWorkflowLifecycleTests
{
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<byte[]>> _responses = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> Bodies { get; } = new(StringComparer.Ordinal);
        public HashSet<string> BlockSecondRead { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource<object?> FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object?> ReleaseSecondRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var id = request.RequestUri!.AbsolutePath.TrimStart('/').Split('/')[0];
            Bodies.TryGetValue(id, out var bytes);
            bytes ??= [];
            HttpContent content = BlockSecondRead.Contains(id)
                ? new StreamContent(new BlockingSecondReadStream(bytes, FirstRead, ReleaseSecondRead))
                : new ByteArrayContent(bytes);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingSecondReadStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly TaskCompletionSource<object?> _firstRead;
        private readonly TaskCompletionSource<object?> _release;
        private bool _first = true;

        public BlockingSecondReadStream(
            byte[] bytes,
            TaskCompletionSource<object?> firstRead,
            TaskCompletionSource<object?> release)
        {
            _bytes = bytes;
            _firstRead = firstRead;
            _release = release;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.LongLength;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_first)
            {
                _first = false;
                var length = Math.Min(count, _bytes.Length);
                Array.Copy(_bytes, 0, buffer, offset, length);
                _firstRead.TrySetResult(null);
                return length;
            }

            var completed = await Task.WhenAny(
                _release.Task,
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            return completed == _release.Task ? 0 : throw new OperationCanceledException(cancellationToken);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeSource : IDownloadSource
    {
        public IReadOnlyList<string> Qualities { get; set; } = ["128k"];

        public Task<IReadOnlyList<string>> GetQualitiesAsync(MusicInfo music, CancellationToken cancellationToken) =>
            Task.FromResult(Qualities);

        public Task<string?> GetUrlAsync(MusicInfo music, string quality, CancellationToken cancellationToken) =>
            Task.FromResult<string?>($"https://download.test/{music.Id}/{quality}.mp3");

        public Task<LyricResult?> GetLyricAsync(MusicInfo music, CancellationToken cancellationToken) =>
            Task.FromResult<LyricResult?>(null);
    }

    private static DownloadWorkflow CreateWorkflow(FakeSource source, FakeHttpHandler handler) =>
        new(
            NullLogger<DownloadWorkflow>.Instance,
            (IDownloadSource)source,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance),
            new RecordingAnnouncer(),
            Path.Combine(Path.GetTempPath(), "download-lifecycle-" + Guid.NewGuid().ToString("N")),
            new RecordingProcessLauncher());

    private static byte[] Mp3Bytes()
    {
        var bytes = new List<byte> { 0x49, 0x44, 0x33, 0x03, 0, 0, 0, 0, 0, 0 };
        bytes.AddRange(new byte[70_000]);
        bytes[10] = 0xFF;
        bytes[11] = 0xFB;
        bytes[12] = 0x90;
        return bytes.ToArray();
    }

    private static MusicInfo Song(string id) => new()
    {
        Id = id,
        Source = "test",
        Title = "Song " + id,
        Artist = "Artist"
    };

    [Fact]
    public async Task Batch_download_processes_every_song()
    {
        var handler = new FakeHttpHandler();
        handler.Bodies["one"] = Mp3Bytes();
        handler.Bodies["two"] = Mp3Bytes();
        handler.Bodies["three"] = Mp3Bytes();
        await using var workflow = CreateWorkflow(new FakeSource(), handler);

        var result = await workflow.DownloadListAsync(new(
            [Song("one"), Song("two"), Song("three")],
            "全部歌曲"));

        Assert.Equal(3, result.Requested);
        Assert.True(result.Completed == 3, result.Completed + ":" + string.Join("|", result.Tasks.Select(task => task.ErrorMessage)));
        Assert.Equal(0, result.Failed);
        Assert.All(result.Tasks, task => Assert.Equal(DownloadTaskStatus.Completed, task.Status));
        Assert.Equal(3, handler.Requests.Count(request => request.AbsolutePath.EndsWith(".mp3", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Pause_and_resume_complete_a_blocked_download()
    {
        var handler = new FakeHttpHandler();
        var workflow = CreateWorkflow(new FakeSource(), handler);
        var song = Song("blocked");
        handler.Bodies["blocked"] = Mp3Bytes();
        handler.BlockSecondRead.Add("blocked");
        var pending = workflow.DownloadAsync(new(song) { Quality = "128k" });

        var task = await WaitForTaskStatus(workflow.Tasks, song.Id, DownloadTaskStatus.Downloading);
        await handler.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        workflow.Pause(task);
        Assert.Equal(DownloadTaskStatus.Paused, task.Status);

        workflow.Resume(task);
        Assert.Equal(DownloadTaskStatus.Downloading, task.Status);
        handler.ReleaseSecondRead.TrySetResult(null);

        handler.Bodies[song.Id] = Mp3Bytes();

        task = await pending;
        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
    }

    [Fact]
    public async Task Cancel_directly_stops_the_active_operation()
    {
        var handler = new FakeHttpHandler();
        await using var workflow = CreateWorkflow(new FakeSource(), handler);
        var song = Song("cancelling");
        handler.Bodies["cancelling"] = Mp3Bytes();
        handler.BlockSecondRead.Add("cancelling");
        var pending = workflow.DownloadAsync(new(song));

        var task = await WaitForTaskStatus(workflow.Tasks, song.Id, DownloadTaskStatus.Downloading);
        await handler.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        workflow.Cancel(task);
        task = await pending;

        Assert.Equal(DownloadTaskStatus.Cancelled, task.Status);
        Assert.Equal("下载已取消", task.ErrorMessage);
    }

    [Fact]
    public async Task Delete_can_remove_a_completed_task_and_source_file()
    {
        var handler = new FakeHttpHandler();
        handler.Bodies["done"] = Mp3Bytes();
        await using var workflow = CreateWorkflow(new FakeSource(), handler);
        var task = await workflow.DownloadAsync(new(Song("done")));

        Assert.True(File.Exists(task.SavePath));
        Assert.True(workflow.Delete(task, deleteSourceFile: true));

        Assert.DoesNotContain(workflow.Tasks, item => item.Id == task.Id);
        Assert.False(File.Exists(task.SavePath));
    }


    private static async Task<DownloadTask> WaitForTaskStatus(
        ReadOnlyObservableCollection<DownloadTask> tasks,
        string songId,
        DownloadTaskStatus status)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var task = tasks.FirstOrDefault(item => item.Music.Id == songId);
            if (task is not null && task.Status == status) return task;
            await Task.Delay(25);
        }

        throw new TimeoutException($"Download {songId} did not reach {status}.");
    }
}
