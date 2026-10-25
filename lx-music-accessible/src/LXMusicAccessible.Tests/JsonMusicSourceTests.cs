using System.Net;
using System.Net.Http;
using System.Text;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class JsonMusicSourceTests
{
    private static ScriptNetworkClient CreateNetwork(
        HttpMessageHandler handler,
        IPAddress? address = null) =>
        new(handler, (_, _) => Task.FromResult(new[] { address ?? IPAddress.Parse("93.184.216.34") }));

    private static JsonSourceDefinition ParseManifest(string json) =>
        JsonSourceDefinitionParser.Parse(json);

    [Fact]
    public async Task Get_play_url_sends_mapped_quality_and_extracts_http_result()
    {
        const string json = """
        {
          "name": "JSON source", "version": "1.0",
          "platforms": {
            "wy": {
              "url": {
                "method": "GET",
                "url": "https://api.example.test/wy?id={sourceId}&br={quality}",
                "headers": { "X-Test": "{sourceId}" },
                "qualities": { "standard": "standard", "high": "exhigh", "lossless": "lossless" },
                "result": { "url": "$.data.url" }
              }
            }
          }
        }
        """;
        var inner = new RejectingHttpMessageHandler();
        inner.RegisterText(
            HttpMethod.Get,
            new Uri("https://api.example.test/wy?id=123&br=exhigh"),
            """{"data":{"url":"https://cdn.example.test/audio.m4a"}}""");
        using var recorder = new RecordingHttpHandler(inner);
        using var network = CreateNetwork(recorder);
        var source = new JsonMusicSource(ParseManifest(json), network);

        var url = await source.GetPlayUrlAsync("netease", "123", "320k");

        Assert.Equal("https://cdn.example.test/audio.m4a", url);
        var request = Assert.Single(recorder.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("123", request.Headers.GetValues("X-Test").Single());
    }

    [Fact]
    public async Task Post_json_body_is_sent_and_response_path_is_selected()
    {
        const string json = """
        {
          "name": "JSON source", "version": "1.0",
          "platforms": {
            "qq": {
              "url": {
                "method": "POST",
                "url": "https://api.example.test/url",
                "qualities": { "standard": "128", "high": "320", "lossless": "flac" },
                "body": {
                  "type": "json",
                  "values": { "keyword": "Song Artist", "page": "{page}" }
                },
                "result": { "url": "$.data.url" }
              }
            }
          }
        }
        """;
        var inner = new RejectingHttpMessageHandler();
        inner.RegisterText(
            HttpMethod.Post,
            new Uri("https://api.example.test/url"),
            """{"data":{"url":"https://cdn.example.test/audio.mp3"}}""");
        using var recorder = new RecordingHttpHandler(inner);
        using var network = CreateNetwork(recorder);
        var source = new JsonMusicSource(ParseManifest(json), network);

        var url = await source.GetPlayUrlAsync("qq", "123", "320k");

        Assert.Equal("https://cdn.example.test/audio.mp3", url);
        var request = Assert.Single(recorder.Requests);
        Assert.Equal("application/json", Assert.Single(recorder.ContentTypes));
        var body = Assert.Single(recorder.Bodies);
        Assert.Contains("\"keyword\":\"Song Artist\"", body);
        Assert.Contains("\"page\":\"1\"", body);
    }

    [Fact]
    public async Task Post_form_body_is_url_encoded()
    {
        const string json = """
        {
          "name": "JSON source", "version": "1.0",
          "platforms": {
            "kg": {
              "url": {
                "method": "POST",
                "url": "https://api.example.test/url",
                "qualities": { "standard": "128", "high": "320", "lossless": "flac" },
                "body": {
                  "type": "form",
                  "values": { "keyword": "a b&c", "page": "{page}" }
                },
                "result": { "url": "$.data.url" }
              }
            }
          }
        }
        """;
        var inner = new RejectingHttpMessageHandler();
        inner.RegisterText(
            HttpMethod.Post,
            new Uri("https://api.example.test/url"),
            """{"data":{"url":"https://cdn.example.test/audio.mp3"}}""");
        using var recorder = new RecordingHttpHandler(inner);
        using var network = CreateNetwork(recorder);
        var source = new JsonMusicSource(ParseManifest(json), network);

        var url = await source.GetPlayUrlAsync("kg", "123", "128k");

        Assert.Equal("https://cdn.example.test/audio.mp3", url);
        var request = Assert.Single(recorder.Requests);
        Assert.Equal(
            "application/x-www-form-urlencoded",
            Assert.Single(recorder.ContentTypes));
        Assert.Equal("keyword=a+b%26c&page=1", Assert.Single(recorder.Bodies));
    }

    [Fact]
    public async Task Search_maps_complete_results_and_formats_duration()
    {
        const string json = """
        {
          "name": "JSON source", "version": "1.0",
          "platforms": {
            "qq": {
              "search": {
                "method": "GET",
                "url": "https://api.example.test/search",
                "params": { "msg": "{keyword}", "page": "{page}", "size": "{pageSize}" },
                "result": {
                  "list": "$.data.songs", "title": "$.name", "artist": "$.artist",
                  "album": "$.album", "coverUrl": "$.cover", "duration": "$.interval",
                  "sourceId": "$.song_id"
                }
              }
            }
          }
        }
        """;
        var inner = new RejectingHttpMessageHandler();
        inner.RegisterText(
            HttpMethod.Get,
            new Uri("https://api.example.test/search?msg=Sun%20Flower&page=2&size=10"),
            """
            {"data":{"songs":[
              {"name":"Song","artist":"Artist","album":"Album","song_id":"1","cover":"https://cdn.example.test/a.jpg","interval":213},
              {"artist":"Missing title","song_id":"2"}
            ]}}
            """);
        using var recorder = new RecordingHttpHandler(inner);
        using var network = CreateNetwork(recorder);
        var source = new JsonMusicSource(ParseManifest(json), network);

        var results = await source.SearchAsync("qq", "Sun Flower", 2, 10, "song");

        var song = Assert.Single(results);
        Assert.Equal("1", song.Id);
        Assert.Equal("Song", song.Title);
        Assert.Equal("Artist", song.Artist);
        Assert.Equal("Album", song.Album);
        Assert.Equal("qq", song.Source);
        Assert.Equal("https://cdn.example.test/a.jpg", song.CoverUrl);
        Assert.Equal("3:33", song.Duration);
    }

    [Fact]
    public async Task Lyric_and_cover_paths_are_extracted()
    {
        const string json = """
        {
          "name": "JSON source", "version": "1.0",
          "platforms": {
            "kw": {
              "lyric": {
                "url": "https://api.example.test/lyric",
                "params": { "id": "{sourceId}" },
                "result": { "lyric": "$.data.lyric", "tlyric": "$.data.tlyric", "rlyric": "$.data.rlyric" }
              },
              "pic": {
                "url": "https://api.example.test/pic",
                "params": { "id": "{sourceId}" },
                "result": { "url": "$.data.cover" }
              }
            }
          }
        }
        """;
        var inner = new RejectingHttpMessageHandler();
        inner.RegisterText(
            HttpMethod.Get,
            new Uri("https://api.example.test/lyric?id=123"),
            """{"data":{"lyric":"[00:01]Lyric","tlyric":"[00:01]Translation","rlyric":"[00:01]Roman"}}""");
        inner.RegisterText(
            HttpMethod.Get,
            new Uri("https://api.example.test/pic?id=123"),
            """{"data":{"cover":"https://cdn.example.test/cover.jpg"}}""");
        using var recorder = new RecordingHttpHandler(inner);
        using var network = CreateNetwork(recorder);
        var source = new JsonMusicSource(ParseManifest(json), network);
        var music = new MusicInfo { Id = "123", Title = "Song", Source = "kw" };

        var lyric = await source.GetLyricAsync("kw", music);
        var pic = await source.GetPicAsync("kw", music);

        Assert.Equal("[00:01]Lyric", lyric?.Lyric);
        Assert.Equal("[00:01]Translation", lyric?.Translation);
        Assert.Equal("[00:01]Roman", lyric?.Romanization);
        Assert.Equal("https://cdn.example.test/cover.jpg", pic);
    }

    [Fact]
    public async Task Platform_and_quality_mismatches_do_not_send_requests()
    {
        const string json = """
        {
          "name": "JSON source", "version": "1.0",
          "platforms": {
            "wy": {
              "url": {
                "url": "https://api.example.test/url",
                "qualities": { "high": "exhigh" },
                "result": { "url": "$.data.url" }
              }
            }
          }
        }
        """;
        var inner = new RejectingHttpMessageHandler();
        using var recorder = new RecordingHttpHandler(inner);
        using var network = CreateNetwork(recorder);
        var source = new JsonMusicSource(ParseManifest(json), network);

        Assert.Null(await source.GetPlayUrlAsync("qq", "123", "320k"));
        Assert.Null(await source.GetPlayUrlAsync("netease", "123", "128k"));
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task Http_json_and_unsafe_play_results_are_rejected()
    {
        const string json = """
        {
          "name": "JSON source", "version": "1.0",
          "platforms": {
            "qq": {
              "url": {
                "url": "https://api.example.test/url",
                "qualities": { "standard": "128" },
                "result": { "url": "$.data.url" }
              },
              "pic": {
                "url": "https://api.example.test/pic",
                "result": { "url": "$.data.url" }
              }
            }
          }
        }
        """;
        var inner = new RejectingHttpMessageHandler();
        inner.Register(
            HttpMethod.Get,
            new Uri("https://api.example.test/url"),
            () => new HttpResponseMessage(HttpStatusCode.NotFound));
        inner.RegisterText(
            HttpMethod.Get,
            new Uri("https://api.example.test/pic"),
            """{"data":{"url":"ftp://cdn.example.test/audio.mp3"}}""");
        using var recorder = new RecordingHttpHandler(inner);
        using var network = CreateNetwork(recorder);
        var source = new JsonMusicSource(ParseManifest(json), network);

        Assert.Null(await source.GetPlayUrlAsync("qq", "123", "128k"));
        Assert.Null(await source.GetPicAsync("qq", new MusicInfo { Id = "123" }));
        Assert.Equal(2, recorder.Requests.Count);
    }

    [Fact]
    public async Task Private_target_is_blocked_before_http()
    {
        const string json = """
        {
          "name": "JSON source", "version": "1.0",
          "platforms": {
            "qq": {
              "url": { "url": "http://private.test/url", "result": { "url": "$.url" } }
            }
          }
        }
        """;
        var inner = new RejectingHttpMessageHandler();
        using var recorder = new RecordingHttpHandler(inner);
        using var network = CreateNetwork(recorder, IPAddress.Parse("10.0.0.8"));
        var source = new JsonMusicSource(ParseManifest(json), network);

        Assert.Null(await source.GetPlayUrlAsync("qq", "123", "128k"));
        Assert.Empty(recorder.Requests);
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner;
        private readonly List<HttpRequestMessage> _requests = [];
        private readonly List<string> _bodies = [];
        private readonly List<string?> _contentTypes = [];

        public RecordingHttpHandler(RejectingHttpMessageHandler inner) => _inner = new HttpMessageInvoker(inner, disposeHandler: false);

        public IReadOnlyList<string> Bodies
        {
            get
            {
                lock (_bodies) return _bodies.ToArray();
            }
        }

        public IReadOnlyList<string?> ContentTypes
        {
            get
            {
                lock (_contentTypes) return _contentTypes.ToArray();
            }
        }

        public IReadOnlyList<HttpRequestMessage> Requests
        {
            get
            {
                lock (_requests) return _requests.ToArray();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);
            lock (_contentTypes) _contentTypes.Add(request.Content?.Headers.ContentType?.MediaType);
            lock (_bodies) _bodies.Add(request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            return await _inner.SendAsync(request, cancellationToken);
        }
    }
}