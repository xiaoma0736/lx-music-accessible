using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LXMusicAccessible.App;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace LXMusicAccessible.Tests;

public sealed class SourceModuleAuditTests
{
    private const string CustomPlatformScript = """
        /**
         * @name Audit source
         * @version 1.0.0
         */
        lx.on(lx.EVENT_NAMES.request, function () {
          return Promise.resolve('https://fixture.invalid/audit-audio.mp3');
        });
        lx.send(lx.EVENT_NAMES.inited, {
          sources: {
            custom: { type: 'music', actions: ['musicUrl'], qualitys: ['128k', 'flac'] }
          }
        });
        """;

    [Fact]
    public async Task Disabled_default_script_is_not_used_for_playback()
    {
        using var host = await TestApplicationHost.StartAsync();
        var sources = host.Services.GetRequiredService<MusicSourceService>();
        var path = Path.Combine(host.DataDirectory, "audit-source.js");
        File.WriteAllText(path, CustomPlatformScript);

        await sources.InitializeScriptSourcesAsync();
        var imported = Assert.IsType<MusicSource>(await sources.ImportFromFileAsync(path));
        Assert.True(imported.IsDefault);
        await sources.DisableSourceAsync(imported);
        Assert.True(imported.IsDefault);
        Assert.False(imported.IsEnabled);

        var method = typeof(MusicSourceService).GetMethod(
            "GetPlayUrlCoreAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<PlaybackSourceResolution?>>(method!.Invoke(
            sources,
            ["audit-id", "custom", "128k", null, CancellationToken.None]));
        var url = (await task)?.Url;

        Assert.Null(url);
        Assert.Empty(host.HttpHandler.Requests);
    }

    [Fact]
    public void Disabled_default_source_is_announced_as_disabled()
    {
        var source = new MusicSource { Id = "default", Name = "Default", IsDefault = true, IsEnabled = false, TrustState = ScriptTrustState.UserApproved };
        Assert.Equal("\u9ed8\u8ba4\uff0c\u5df2\u7981\u7528", source.StatusText);
    }

    [Fact]
    public async Task Removing_the_default_source_selects_an_enabled_replacement()
    {
        using var host = await TestApplicationHost.StartAsync();
        var sources = host.Services.GetRequiredService<MusicSourceService>();
        await sources.InitializeScriptSourcesAsync();

        var firstPath = Path.Combine(host.DataDirectory, "first.js");
        File.WriteAllText(firstPath, CustomPlatformScript);
        var first = Assert.IsType<MusicSource>(await sources.ImportFromFileAsync(firstPath));

        var secondPath = Path.Combine(host.DataDirectory, "second.js");
        File.WriteAllText(secondPath, CustomPlatformScript.Replace("Audit source", "Second audit source"));
        var second = Assert.IsType<MusicSource>(await sources.ImportFromFileAsync(secondPath));

        Assert.Equal(second.Id, sources.GetDefaultSource()?.Id);

        Assert.True(await sources.RemoveSourceAsync(second));

        var replacement = sources.GetDefaultSource();
        Assert.NotNull(replacement);
        Assert.Equal(first.Id, replacement!.Id);
        Assert.True(replacement.IsEnabled);
    }

    [Theory]
    [InlineData("128k")]
    [InlineData("320k")]
    [InlineData("flac")]
    [InlineData("flac24bit")]
    [InlineData("standard")]
    [InlineData("high")]
    [InlineData("lossless")]
    public async Task Json_url_request_accepts_original_lx_quality_ids(string quality)
    {
        var expectedQuality = quality switch
        {
            "standard" => "128k", "high" => "320k", "lossless" => "flac24bit", _ => quality
        };
        var requestUrl = $"https://api.example.test/wy?br={expectedQuality}";
        const string response = """{"data":{"url":"https://cdn.example.test/audio.m4a"}}""";
        const string json = """
        {
          "name": "Quality source", "version": "1.0",
          "platforms": {
            "wy": {
              "url": {
                "url": "https://api.example.test/wy",
                "params": { "br": "{quality}" },
                "qualities": {
                  "128k": "128k", "320k": "320k",
                  "flac": "flac", "flac24bit": "flac24bit"
                },
                "result": { "url": "$.data.url" }
              }
            }
          }
        }
        """;
        var inner = new RejectingHttpMessageHandler();
        inner.RegisterText(HttpMethod.Get, new Uri(requestUrl), response);
        using var recorder = new AuditRecordingHttpHandler(inner);
        using var network = new ScriptNetworkClient(
            recorder,
            (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("93.184.216.34") }));
        var source = new JsonMusicSource(JsonSourceDefinitionParser.Parse(json), network);

        var url = await source.GetPlayUrlAsync("netease", "fixture-id", quality);

        Assert.Equal("https://cdn.example.test/audio.m4a", url);
        var request = Assert.Single(recorder.Requests);
        Assert.Equal(new Uri(requestUrl), request.RequestUri);
    }

    [Fact]
    public async Task Bad_fast_path_url_falls_back_to_the_real_script_engine()
    {
        using var host = await TestApplicationHost.StartAsync();
        var sources = host.Services.GetRequiredService<MusicSourceService>();
        const string script = """
            /**
             * @name Fast path fallback
             * @version 1.0.0
             */
            var API_URL = 'https://fixture.invalid/api';
            // The API form contains songId=${songId} for the simple fast path.
            var MUSIC_QUALITY = { custom: ['128k'] };
            lx.on(lx.EVENT_NAMES.request, function () {
              return Promise.resolve('https://fixture.invalid/audio.mp3');
            });
            lx.send(lx.EVENT_NAMES.inited, {
              sources: {
                custom: { type: 'music', actions: ['musicUrl'], qualitys: ['128k'] }
              }
            });
            """;
        var path = Path.Combine(host.DataDirectory, "fast-path-fallback.js");
        File.WriteAllText(path, script);

        host.HttpHandler.Register(
            HttpMethod.Get,
            new Uri("https://fixture.invalid/api/url?source=custom&songId=audit-id&quality=128k"),
            () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"url\":\"https://fixture.invalid/not-playable.mp3\"}")
            });
        host.HttpHandler.Register(
            HttpMethod.Get,
            new Uri("https://fixture.invalid/not-playable.mp3"),
            () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("not audio")
            });
        host.HttpHandler.Register(
            HttpMethod.Get,
            new Uri("https://fixture.invalid/audio.mp3"),
            () => new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
            {
                Content = new StringContent("audio", System.Text.Encoding.UTF8, "audio/mpeg")
            });

        await sources.InitializeScriptSourcesAsync();
        var imported = Assert.IsType<MusicSource>(await sources.ImportFromFileAsync(path));

        var method = typeof(MusicSourceService).GetMethod(
            "GetPlayUrlCoreAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<PlaybackSourceResolution?>>(method!.Invoke(
            sources,
            ["audit-id", "custom", "128k", null, CancellationToken.None]));
        var result = (await task)?.Url;

        Assert.Contains(
            (HttpMethod.Get.Method, "https://fixture.invalid/audio.mp3"),
            host.HttpHandler.Requests);
    }

    [Fact]
    public async Task Network_import_is_delegated_to_the_validated_source_service_once()
    {
        var announcer = new RecordingSourceAnnouncer();
        var sources = new RouteRecordingSourceService
        {
            Imported = new MusicSource
            {
                Id = "network_imported",
                Name = "Network source",
                ScriptContent = "validated persisted content"
            }
        };
        using var form = new SourceManagerForm(announcer, sources, _ => false);

        var method = typeof(SourceManagerForm).GetMethod(
            "ImportSourceAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(form, ["https://fixture.invalid/source.js"]));
        await task;

        var request = Assert.Single(sources.UrlImports);
        Assert.Equal("https://fixture.invalid/source.js", request);
        Assert.Empty(sources.FileImports);
        Assert.Equal("validated persisted content", sources.Imported.ScriptContent);
        Assert.Contains("Network source", Assert.Single(announcer.Messages));
    }

    private sealed class AuditRecordingHttpHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner;

        public AuditRecordingHttpHandler(HttpMessageHandler inner) { _inner = new HttpMessageInvoker(inner, disposeHandler: false); }
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await _inner.SendAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingSourceAnnouncer : IAccessibleAnnouncer
    {
        public List<AccessibleAnnouncement> Announcements { get; } = new();
        public List<string> Messages { get; } = new();
        public bool SpeechEnabled { get; set; } = true;
        public void Announce(AccessibleAnnouncement announcement)
        {
            Announcements.Add(announcement);
            Messages.Add(announcement.Text);
        }
    }

    private sealed class RouteRecordingSourceService : IMusicSourceService
    {
        private readonly List<MusicSource> _sources = new();
        private EventHandler? _changed;

        public event EventHandler? SourcesChanged
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public event EventHandler<LxScriptUpdateAlertEventArgs>? ScriptUpdateAlert
        {
            add { }
            remove { }
        }

        public List<string> UrlImports { get; } = new();
        public List<string> FileImports { get; } = new();
        public MusicSource? Imported { get; set; }
        public MusicSourceImportStatus DetailedStatus { get; set; } = MusicSourceImportStatus.Imported;

        public async Task<MusicSource?> ImportFromUrlAsync(string url, CancellationToken cancellationToken = default) =>
            (await ImportFromUrlDetailedAsync(url, cancellationToken))?.Source;

        public Task<MusicSourceImportResult?> ImportFromUrlDetailedAsync(string url, CancellationToken cancellationToken = default)
        {
            UrlImports.Add(url);
            if (Imported is null) return Task.FromResult<MusicSourceImportResult?>(null);
            if (!_sources.Any(source => source.Id == Imported.Id)) _sources.Add(Imported);
            return Task.FromResult<MusicSourceImportResult?>(new(Imported, DetailedStatus));
        }

        public async Task<MusicSource?> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default) =>
            (await ImportFromFileDetailedAsync(filePath, cancellationToken))?.Source;

        public Task<MusicSourceImportResult?> ImportFromFileDetailedAsync(string filePath, CancellationToken cancellationToken = default)
        {
            FileImports.Add(filePath);
            if (Imported is null) return Task.FromResult<MusicSourceImportResult?>(null);
            if (!_sources.Any(source => source.Id == Imported.Id)) _sources.Add(Imported);
            return Task.FromResult<MusicSourceImportResult?>(new(Imported, DetailedStatus));
        }

        public List<MusicSource> GetAllSources() => _sources.ToList();
        public List<MusicSource> GetEnabledSources() => _sources.Where(source => source.IsEnabled).ToList();
        public MusicSource? GetDefaultSource() => _sources.FirstOrDefault(source => source.IsDefault);

        public void Replace(params MusicSource[] source)
        {
            _sources.Clear();
            _sources.AddRange(source);
            _changed?.Invoke(this, EventArgs.Empty);
        }

        public Task<ScriptActivationResult> EnableSourceAsync(MusicSource source, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScriptActivationResult(true, ScriptActivationStatus.Activated, "\u5df2\u542f\u7528"));
        public void DisableSource(MusicSource source) { }
        public Task<bool> DisableSourceAsync(MusicSource source, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void RemoveSource(MusicSource source) { }
        public Task<bool> RemoveSourceAsync(MusicSource source, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> UpdateSourceAsync(MusicSource source) => Task.FromResult(true);
        public Task<bool> RefreshSourceAsync(MusicSource source, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<MusicSourceService.MusicSourceRefreshResult> RefreshSourceDetailedAsync(MusicSource source, CancellationToken cancellationToken = default) => Task.FromResult(new MusicSourceService.MusicSourceRefreshResult(true, false, source.TrustState, source.IsEnabled));
        public Task<bool> TestSourceAsync(MusicSource source) => Task.FromResult(true);
        public void SetDefaultSource(string id) { }
        public Task<bool> SetDefaultSourceAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<LxLyricRaw?> GetLyricFromScriptsAsync(MusicInfo music, CancellationToken cancellationToken = default) => Task.FromResult<LxLyricRaw?>(null);
        public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page = 1, int pageSize = 20, string searchType = "song", string sourceId = "all", CancellationToken cancellationToken = default) => Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public Task<string?> GetPlayUrlAsync(string musicId, string source, string quality = "128k", CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> GetPlayUrlAsync(MusicInfo music, string source, string quality = "128k", CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId = "netease", CancellationToken cancellationToken = default) => Task.FromResult(new List<PlaylistCategory>());
        public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page = 1, int pageSize = 30, string sourceId = "netease", CancellationToken cancellationToken = default) => Task.FromResult(PagedResult<Playlist>.Empty(page, pageSize));
        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public Task<List<Ranking>> GetRankingsAsync(string sourceId = "netease", CancellationToken cancellationToken = default) => Task.FromResult(new List<Ranking>());
        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public IMusicApi? GetApi(string id) => null;
        public Task<List<string>> GetQualitiesForSourceAsync(string sourceId) => Task.FromResult(new List<string>());
        public Task InitializeScriptSourcesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PersistSourcesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PersistSourcesOrThrowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
