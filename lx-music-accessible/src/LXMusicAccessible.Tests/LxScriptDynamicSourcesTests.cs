using System.Net.Http;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class LxScriptDynamicSourcesTests
{
    [Fact]
    public async Task Inited_sources_declare_supported_platforms_and_qualities()
    {
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance);
        var script = CreateScript(
            """
            lx.on(lx.EVENT_NAMES.request, function () {
              return Promise.resolve('https://fixture.invalid/audio.mp3');
            });
            lx.send(lx.EVENT_NAMES.inited, {
              sources: {
                tx: {
                  type: 'music',
                  actions: ['musicUrl', 'lyric'],
                  qualitys: [' 128k ', '128k', '320k']
                },
                wy: {
                  type: 'music',
                  actions: ['lyric', 'musicUrl'],
                  qualitys: ['128k', 'flac']
                }
              }
            });
            """);

        var loaded = await engine.TestLoadAsync(script);

        Assert.True(loaded, engine.LastError);
        Assert.Equal(["qq", "netease"], script.SupportedSources);
        Assert.Equal(["128k", "320k"], script.QualityMap["qq"]);
        Assert.Equal(["128k", "flac"], script.QualityMap["netease"]);
    }

    [Fact]
    public async Task Inited_sources_are_merged_with_static_quality_declarations()
    {
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance);
        const string body = """
            var MUSIC_QUALITY = { tx: ['128k'] };
            lx.on(lx.EVENT_NAMES.request, function () {
              return Promise.resolve('https://fixture.invalid/audio.mp3');
            });
            lx.send(lx.EVENT_NAMES.inited, {
              sources: {
                tx: {
                  type: 'music',
                  actions: ['musicUrl'],
                  qualitys: ['320k']
                },
                wy: {
                  type: 'music',
                  actions: ['musicUrl'],
                  qualitys: ['128k']
                }
              }
            });
            """;
        var script = new ScriptMusicSource(NullLogger<ScriptMusicSource>.Instance)
            .StageFromContent(body, "dynamic-source", "fixture", isLocal: true);
        Assert.NotNull(script);
        Assert.Equal(["qq"], script!.SupportedSources);
        Assert.Equal(["128k"], script.QualityMap["qq"]);

        var loaded = await engine.TestLoadAsync(script);

        Assert.True(loaded, engine.LastError);
        Assert.Equal(["qq", "netease"], script.SupportedSources);
        Assert.Equal(["128k", "320k"], script.QualityMap["qq"]);
        Assert.Equal(["128k"], script.QualityMap["netease"]);
    }

    [Theory]
    [InlineData("lx.send(lx.EVENT_NAMES.inited, { sources: {} });")]
    [InlineData("lx.send(lx.EVENT_NAMES.inited, { sources: { tx: {} } });")]
    [InlineData("lx.send(lx.EVENT_NAMES.inited, { sources: { tx: { type: 'music', qualitys: ['128k'] } } });")]
    [InlineData("lx.send(lx.EVENT_NAMES.inited, { sources: { tx: { type: 'media', actions: ['musicUrl'], qualitys: ['128k'] } } });")]
    [InlineData("lx.send(lx.EVENT_NAMES.inited, { sources: { tx: { type: 'music', actions: 'musicUrl', qualitys: ['128k'] } } });")]
    [InlineData("lx.send(lx.EVENT_NAMES.inited, { sources: { tx: { type: 'music', actions: ['musicUrl'], qualitys: ['', ' ', null, 128] } } });")]
    [InlineData("lx.send(lx.EVENT_NAMES.inited, { sources: { tx: { type: 'music', actions: ['musicUrl'], qualitys: '128k' } } });")]
    public async Task Empty_or_malformed_inited_sources_do_not_declare_support(string declaration)
    {
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance);
        var script = CreateScript(
            """
            lx.on(lx.EVENT_NAMES.request, function () {
              return Promise.resolve('https://fixture.invalid/audio.mp3');
            });
            """ + "\n" + declaration);

        var loaded = await engine.TestLoadAsync(script);

        Assert.True(loaded, engine.LastError);
        Assert.Empty(script.SupportedSources);
        Assert.Empty(script.QualityMap);
    }

    [Fact]
    public async Task First_inited_manifest_is_not_overwritten_by_a_second_manifest()
    {
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance);
        var script = CreateScript(
            """
            lx.on(lx.EVENT_NAMES.request, function () {
              return Promise.resolve('https://fixture.invalid/audio.mp3');
            });
            lx.send(lx.EVENT_NAMES.inited, {
              sources: {
                tx: { type: 'music', actions: ['musicUrl'], qualitys: ['128k'] }
              }
            });
            lx.send(lx.EVENT_NAMES.inited, {
              sources: {
                wy: { type: 'music', actions: ['musicUrl'], qualitys: ['128k'] }
              }
            });
            """);

        var loaded = await engine.TestLoadAsync(script);

        Assert.True(loaded, engine.LastError);
        Assert.Equal(["qq"], script.SupportedSources);
        Assert.Equal(["128k"], script.QualityMap["qq"]);
        Assert.DoesNotContain("netease", script.SupportedSources);
    }

    [Fact]
    public async Task First_unserializable_inited_manifest_is_not_replaced_by_a_second_manifest()
    {
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance);
        var script = CreateScript(
            """
            lx.on(lx.EVENT_NAMES.request, function () {
              return Promise.resolve('https://fixture.invalid/audio.mp3');
            });
            var firstManifest = {};
            firstManifest.self = firstManifest;
            lx.send(lx.EVENT_NAMES.inited, firstManifest);
            lx.send(lx.EVENT_NAMES.inited, {
              sources: {
                tx: { type: 'music', actions: ['musicUrl'], qualitys: ['128k'] }
              }
            });
            """);

        var loaded = await engine.TestLoadAsync(script);

        Assert.True(loaded, engine.LastError);
        Assert.Empty(script.SupportedSources);
        Assert.Empty(script.QualityMap);
    }
    [Fact]
    public async Task Oversized_inited_manifest_is_ignored_entirely()
    {
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance);
        var repeatedQualities = string.Join(",", Enumerable.Repeat("'128k'", 16_000));
        var script = CreateScript($$"""
            lx.on(lx.EVENT_NAMES.request, function () {
              return Promise.resolve('https://fixture.invalid/audio.mp3');
            });
            lx.send(lx.EVENT_NAMES.inited, {
              sources: {
                tx: {
                  type: 'music',
                  actions: ['musicUrl'],
                  qualitys: [{{repeatedQualities}}]
                }
              }
            });
            """);

        var loaded = await engine.TestLoadAsync(script);

        Assert.True(loaded, engine.LastError);
        Assert.Empty(script.SupportedSources);
        Assert.Empty(script.QualityMap);
    }

    [Fact]
    public async Task Manual_source_discovery_finds_runtime_declared_script_sources()
    {
        using var host = await TestApplicationHost.StartAsync();
        var url = new Uri("https://fixture.invalid/dynamic-source.js");
        const string script = """
            /**
             * @name Dynamic script source
             * @version 1.0.0
             */
            lx.on(lx.EVENT_NAMES.request, function () {
              return Promise.resolve('https://fixture.invalid/audio.mp3');
            });
            lx.send(lx.EVENT_NAMES.inited, {
              sources: {
                tx: {
                  type: 'music',
                  actions: ['musicUrl'],
                  qualitys: ['128k', '320k']
                }
              }
            });
            """;
        host.HttpHandler.RegisterText(HttpMethod.Get, url, script);
        var sources = host.Services.GetRequiredService<MusicSourceService>();

        var imported = await sources.ImportFromUrlAsync(url.ToString());
        var target = new MusicInfo
        {
            Id = "qq-1",
            Source = "qq",
            Title = "same title",
            Artist = "same artist"
        };

        var interfaces = sources.GetPlaybackInterfaces(target);

        Assert.NotNull(imported);
        Assert.True(imported!.IsEnabled);
        var scriptInterface = Assert.Single(interfaces, item => item.IsScript);
        Assert.Equal(imported.Id, scriptInterface.Id);
        Assert.Equal("qq", scriptInterface.Platform);
        Assert.Equal(["128k", "320k"], scriptInterface.Qualities);
    }

    private static LxScriptInfo CreateScript(string body) => new()
    {
        Id = "dynamic-source",
        Name = "Dynamic source",
        RawScript = body
    };
}
