using System;
using System.Collections.Generic;
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
    }

    private DownloadWorkflow Workflow { get; }
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
        Assert.Equal(QualityCatalog.Label("flac"), task.ActualQuality);
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
        Workflow.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _background.Dispose();
        _client.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
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
            if (Responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var response))
                return await Task.Run(() => response, cancellationToken);
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

        public Task<IReadOnlyList<string>> GetQualitiesAsync(
            MusicInfo music,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _qualityCalls);
            return Task.FromResult(Qualities);
        }

        public Task<string?> GetUrlAsync(
            MusicInfo music,
            string quality,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _urlCalls);
            return Task.FromResult<string?>($"https://download.invalid/{music.Id}-{quality}");
        }

        public Task<LyricResult?> GetLyricAsync(
            MusicInfo music,
            CancellationToken cancellationToken) => Task.FromResult<LyricResult?>(null);
    }
}
