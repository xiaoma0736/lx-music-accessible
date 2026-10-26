using System.Net;
using System.Net.Http;
using System.Text;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class JsonSourceLifecycleTests
{
    private const string FullJson = """
    {
      "name": "Lifecycle JSON", "version": "1.0.1", "author": "Fixture", "description": "lifecycle",
      "platforms": {
        "wy": {
          "search": {
            "method": "GET",
            "url": "https://api.example.test/search",
            "params": { "msg": "{keyword}" },
            "result": {
              "list": "$.data.songs", "title": "$.name", "artist": "$.artist",
              "sourceId": "$.song_id", "duration": "$.interval"
            }
          },
          "url": {
            "method": "GET",
            "url": "https://api.example.test/wy?id={sourceId}&br={quality}",
            "qualities": { "standard": "standard", "high": "exhigh", "lossless": "lossless" },
            "result": { "url": "$.data.url" }
          },
          "lyric": {
            "method": "GET",
            "url": "https://api.example.test/lyric",
            "params": { "id": "{sourceId}" },
            "result": { "lyric": "$.data.lyric", "tlyric": "$.data.tlyric" }
          }
        }
      }
    }
    """;

    [Fact]
    public async Task Json_manifest_stage_enable_and_register_runtime()
    {
        using var app = await TestApplicationHost.StartAsync();
        var scriptSource = app.Services.GetRequiredService<ScriptMusicSource>();
        var sources = app.Services.GetRequiredService<IMusicSourceService>();
        var info = scriptSource.StageFromContent(FullJson, "json-runtime", "fixture", true);
        Assert.NotNull(info);
        Assert.Equal(LxSourceType.JsonManifest, info!.Kind);

        var source = new MusicSource
        {
            Id = info.Id,
            Name = info.Name,
            Version = info.Version ?? "1.0",
            ScriptUrl = "fixture",
            ScriptContent = info.RawScript,
            ContentSha256 = ScriptMusicSource.ComputeSha256(info.RawScript!),
            TrustState = ScriptTrustState.Untrusted,
            Type = SourceType.Local,
            IsEnabled = false
        };
        var activation = await ((MusicSourceService)sources).EnableSourceAsync(source);
        Assert.True(activation.Succeeded);

        Assert.True(source.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, source.TrustState);
        Assert.NotNull(((MusicSourceService)sources).GetScriptSource().GetJsonSource(source.Id));
    }

    [Fact]
    public async Task Json_manifest_is_persisted_and_restored()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync([JsonSource("restored-json", FullJson)]);
        var first = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        await first.InitializeScriptSourcesAsync();

        var restored = Assert.Single(first.GetAllSources(), source => source.Id == "restored-json");
        Assert.True(restored.IsEnabled);
        Assert.NotNull(first.GetScriptSource().GetJsonSource(restored.Id));

        var second = (MusicSourceService)ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);
        await second.InitializeScriptSourcesAsync();
        var persisted = Assert.Single(second.GetAllSources(), source => source.Id == "restored-json");
        Assert.True(persisted.IsEnabled);
        Assert.NotNull(second.GetScriptSource().GetJsonSource(persisted.Id));
    }

    [Fact]
    public async Task Enabled_json_source_resolves_play_url_before_built_in_source()
    {
        using var app = await TestApplicationHost.StartAsync();
        app.HttpHandler.RegisterText(
            HttpMethod.Get,
            new Uri("https://api.example.test/wy?id=123&br=exhigh"),
            """{"data":{"url":"https://cdn.example.test/audio.mp3"}}""");
        app.HttpHandler.Register(HttpMethod.Get, new Uri("https://cdn.example.test/audio.mp3"), () =>
        {
            var content = new StringContent("audio-bytes", Encoding.UTF8, "audio/mpeg");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        await app.Services.GetRequiredService<IDataService>().SaveMusicSourcesAsync(
            [JsonSource("priority-json", FullJson)]);
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        await sources.InitializeScriptSourcesAsync();

        var music = new MusicInfo { Id = "123", Title = "Song", Source = "netease" };
        var url = await sources.GetPlayUrlAsync(music, "netease", "320k");

        Assert.Equal("https://cdn.example.test/audio.mp3", url);
        Assert.Contains(
            (HttpMethod.Get.Method, "https://api.example.test/wy?id=123&br=exhigh"),
            app.HttpHandler.Requests.Select(request => (request.Method, request.Uri)));
    }

    [Fact]
    public async Task Platform_and_quality_mismatches_never_reach_json_api()
    {
        const string json = """
        {
          "name": "Mismatch JSON", "version": "1.0",
          "platforms": {
            "wy": {
              "url": {
                "method": "GET",
                "url": "https://api.example.test/wy",
                "qualities": { "high": "exhigh" },
                "result": { "url": "$.data.url" }
              }
            }
          }
        }
        """;
        using var app = await TestApplicationHost.StartAsync();
        await app.Services.GetRequiredService<IDataService>().SaveMusicSourcesAsync(
            [JsonSource("mismatch-json", json)]);
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        await sources.InitializeScriptSourcesAsync();

        var music = new MusicInfo { Id = "123", Title = "Song", Source = "qq" };
        Assert.Null(await sources.GetPlayUrlAsync(music, "qq", "320k"));
        Assert.Null(await sources.GetPlayUrlAsync(music, "netease", "128k"));
        Assert.DoesNotContain(app.HttpHandler.Requests, request =>
            request.Uri.StartsWith("https://api.example.test/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Json_search_runs_on_requested_platform_and_sets_source()
    {
        using var app = await TestApplicationHost.StartAsync();
        app.HttpHandler.RegisterText(
            HttpMethod.Get,
            new Uri("https://api.example.test/search?msg=Sun%20Flower"),
            """
            {"data":{"songs":[{"name":"Song","artist":"Artist","song_id":"1","interval":213}]}}
            """);
        await app.Services.GetRequiredService<IDataService>().SaveMusicSourcesAsync(
            [JsonSource("search-json", FullJson)]);
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        await sources.InitializeScriptSourcesAsync();

        var page = await sources.SearchAsync("Sun Flower", sourceId: "netease");

        var song = Assert.Single(page.Items);
        Assert.Equal("1", song.Id);
        Assert.Equal("Song", song.Title);
        Assert.Equal("Artist", song.Artist);
        Assert.Equal("netease", song.Source);
        Assert.Equal("3:33", song.Duration);
    }

    [Fact]
    public async Task Lyric_lookup_maps_json_translation()
    {
        using var app = await TestApplicationHost.StartAsync();
        app.HttpHandler.RegisterText(
            HttpMethod.Get,
            new Uri("https://api.example.test/lyric?id=123"),
            """{"data":{"lyric":"Lyric","tlyric":"Translation"}}""");
        await app.Services.GetRequiredService<IDataService>().SaveMusicSourcesAsync(
            [JsonSource("lyric-json", FullJson)]);
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        await sources.InitializeScriptSourcesAsync();
        var music = new MusicInfo { Id = "123", Title = "Song", Source = "netease" };

        var lyric = await sources.GetLyricFromScriptsAsync(music);

        Assert.NotNull(lyric);
        Assert.Equal("Lyric", lyric!.Lyric);
        Assert.Equal("Translation", lyric.Tlyric);
    }

    private static MusicSource JsonSource(string id, string json) => new()
    {
        Id = id,
        Name = "Lifecycle JSON",
        Version = "1.0.1",
        ScriptUrl = "https://fixture.invalid/source.json",
        ScriptContent = json,
        ContentSha256 = ScriptMusicSource.ComputeSha256(json),
        TrustState = ScriptTrustState.UserApproved,
        Type = SourceType.Network,
        IsEnabled = true,
        Priority = 10
    };
}
