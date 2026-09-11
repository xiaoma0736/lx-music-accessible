using System.Net.Http;
using LXMusicAccessible.Core.MusicApi;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class JsonSourceDefinitionTests
{
    private const string ValidJson = """
    {
      "name": "残像音源",
      "version": "1.0.1",
      "author": "残像API",
      "description": "declarative source",
      "platforms": {
        "qq": {
          "search": {
            "method": "GET",
            "url": "https://api.example.test/qq",
            "params": { "msg": "{keyword}" },
            "result": {
              "list": "$.data.songs",
              "title": "$.name",
              "artist": "$.artist",
              "coverUrl": "$.cover",
              "sourceId": "$.song_id"
            }
          },
          "url": {
            "url": "https://api.example.test/qq",
            "params": { "id": "{sourceId}", "br": "{quality}" },
            "qualities": { "standard": "128", "high": "320", "lossless": "flac" },
            "result": { "url": "$.data.play_url" }
          },
          "lyric": {
            "url": "https://api.example.test/qq",
            "params": { "id": "{sourceId}" },
            "result": { "lyric": "$.data.lyrics" }
          }
        },
        "wy": {
          "url": {
            "url": "https://api.example.test/wy",
            "params": { "id": "{sourceId}", "br": "{quality}" },
            "qualities": { "standard": "standard", "high": "exhigh", "lossless": "lossless" },
            "result": { "url": "$.data.url" }
          }
        }
      }
    }
    """;

    [Fact]
    public void Parse_extracts_metadata_platforms_qualities_and_templates()
    {
        var manifest = JsonSourceDefinitionParser.Parse(ValidJson);

        Assert.Equal("残像音源", manifest.Name);
        Assert.Equal("1.0.1", manifest.Version);
        Assert.Equal("残像API", manifest.Author);
        Assert.Equal("declarative source", manifest.Description);
        Assert.Equal(["qq", "netease"], manifest.Platforms.Keys.Select(MapPlatform).ToArray());
        Assert.Equal("https://api.example.test/qq", manifest.Platforms["qq"].Search!.Url);
        Assert.Equal("{keyword}", manifest.Platforms["qq"].Search!.Params["msg"]);
        Assert.Equal("flac", manifest.Platforms["qq"].Url!.Qualities["lossless"]);
    }

    [Fact]
    public void StageFromContent_detects_json_manifest()
    {
        using var source = new ScriptMusicSource(NullLogger<ScriptMusicSource>.Instance);
        var info = source.StageFromContent(ValidJson, "json-source", "fixture", true);

        Assert.NotNull(info);
        Assert.Equal(LxSourceType.JsonManifest, info!.Kind);
        Assert.Equal("残像音源", info.Name);
        Assert.Equal("1.0.1", info.Version);
        Assert.NotNull(info.JsonSource);
        Assert.Equal(["qq", "netease"], info.SupportedSources);
        Assert.Contains("128k", info.QualityMap["qq"]);
        Assert.Contains("flac24bit", info.QualityMap["qq"]);
    }

    [Theory]
    [InlineData("[]", "json_manifest_invalid")]
    [InlineData("""{"name":"x","version":"1.0"}""", "json_manifest_invalid")]
    [InlineData("""{"name":"x","version":"","platforms":{"qq":{"url":{"url":"https://api.example.test/"}}}}""", "json_manifest_invalid")]
    [InlineData("""{"name":"x","version":"1.0","platforms":{"bad":{"url":{"url":"https://api.example.test/"}}}}""", "json_manifest_invalid")]
    [InlineData("""{"name":"x","version":"1.0","platforms":{"qq":{"url":{"url":"ftp://api.example.test/"}}}}""", "json_manifest_url_invalid")]
    [InlineData("""{"name":"x","version":"1.0","platforms":{"qq":{"url":{"method":"TRACE","url":"https://api.example.test/"}}}}""", "json_manifest_method_unsupported")]
    public void Invalid_manifest_is_rejected(string json, string expectedCode)
    {
        var exception = Assert.Throws<JsonSourceManifestException>(
            () => JsonSourceDefinitionParser.Parse(json));
        Assert.Equal(expectedCode, exception.Code);

        using var source = new ScriptMusicSource(NullLogger<ScriptMusicSource>.Instance);
        Assert.Null(source.StageFromContent(json, "bad-json", "fixture", true));
        Assert.Equal(expectedCode, source.LastFailureCode);
    }

    private static string MapPlatform(string platform) => platform.ToLowerInvariant() switch
    {
        "wy" => "netease",
        "mg" => "migu",
        _ => platform.ToLowerInvariant()
    };
}
