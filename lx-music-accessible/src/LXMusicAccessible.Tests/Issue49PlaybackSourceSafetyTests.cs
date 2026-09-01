using System.Net.Http;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LXMusicAccessible.Tests;

[Collection("Global network settings")]
public sealed class Issue49PlaybackSourceSafetyTests
{
    private static readonly Uri AudioUri = new("https://source-safety.test/audio.mp3");

    private const string ExplicitNeteaseScript = """
        /** @name Explicit source @version 1.0.0 */
        var MUSIC_QUALITY = { wy: ['128k'] };
        lx.on(lx.EVENT_NAMES.request, function () {
          return Promise.resolve('https://source-safety.test/audio.mp3');
        });
        """;

    private const string UnknownSupportScript = """
        /** @name Unknown support source @version 1.0.0 */
        var MUSIC_QUALITY = {};
        lx.on(lx.EVENT_NAMES.request, function () {
          return Promise.resolve('https://source-safety.test/audio.mp3');
        });
        """;

    [Fact]
    public async Task Script_with_explicit_requested_source_support_resolves_the_song()
    {
        await using var host = await TestApplicationHost.StartAsync();
        host.HttpHandler.Register(HttpMethod.Get, AudioUri, AudioResponse);
        var data = host.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync([TrustedSource(ExplicitNeteaseScript)]);

        var sources = host.Services.GetRequiredService<MusicSourceService>();
        await sources.InitializeScriptSourcesAsync();
        var url = await sources.GetPlayUrlAsync(Music(), "netease", "128k");

        Assert.Equal(AudioUri.AbsoluteUri, url);
        Assert.Contains((HttpMethod.Get.Method, AudioUri.AbsoluteUri), host.HttpHandler.Requests);
    }

    [Fact]
    public async Task Script_without_explicit_requested_source_support_is_not_used()
    {
        await using var host = await TestApplicationHost.StartAsync();
        host.HttpHandler.Register(HttpMethod.Get, AudioUri, AudioResponse);
        var data = host.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync([TrustedSource(UnknownSupportScript)]);

        var sources = host.Services.GetRequiredService<MusicSourceService>();
        await sources.InitializeScriptSourcesAsync();
        var url = await sources.GetPlayUrlAsync(Music(), "netease", "128k");

        Assert.Null(url);
        Assert.DoesNotContain((HttpMethod.Get.Method, AudioUri.AbsoluteUri), host.HttpHandler.Requests);
    }

    private static MusicInfo Music() => new()
    {
        Id = "netease-song-id",
        Source = "netease",
        Title = "同一首歌",
        Artist = "同一位歌手"
    };

    private static MusicSource TrustedSource(string script) => new()
    {
        Id = "script-source",
        Name = "Script source",
        Version = "1.0.0",
        ScriptUrl = "local://script-source",
        ScriptContent = script,
        ContentSha256 = ScriptMusicSource.ComputeSha256(script),
        IsEnabled = true,
        IsDefault = true,
        IsSystemHidden = false,
        Priority = 1,
        Type = SourceType.Custom,
        TrustState = ScriptTrustState.UserApproved
    };

    private static HttpResponseMessage AudioResponse()
    {
        var audio = new byte[16];
        audio[0] = 0x49;
        audio[1] = 0x44;
        audio[2] = 0x33;
        return new(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(audio)
        };
    }
}
