using System.Net.Http;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class QualityCandidateSourceTests
{
    private static readonly Uri LowAudio = new("https://quality-candidates.test/low/audio.mp3");
    private static readonly Uri HighAudio = new("https://quality-candidates.test/high/audio.flac");

    [Fact]
    public async Task Returns_only_declared_candidates_from_each_enabled_script_interface()
    {
        using var host = await TestApplicationHost.StartAsync();
        RegisterScripts(host);
        await SaveScriptsAsync(host);
        await host.Services.GetRequiredService<MusicSourceService>().InitializeScriptSourcesAsync();
        var source = (IQualityCandidateSource)host.Services.GetRequiredService<MusicSourceService>();

        var candidates = (await source.GetCandidatesAsync(QualitySong()))
            .Where(candidate => candidate.InterfaceId is "low" or "high")
            .ToArray();

        Assert.Equal(
            [
                new("low", "low", 1, "128k"),
                new("high", "high", 2, "128k"),
                new("high", "high", 2, "320k")
            ],
            candidates);
    }

    [Fact]
    public async Task Builtin_candidates_stay_on_the_current_source_and_declared_qualities()
    {
        using var host = await TestApplicationHost.StartAsync();
        var source = (IQualityCandidateSource)host.Services.GetRequiredService<MusicSourceService>();
        var song = QualitySong();
        song.Source = "netease";
        var candidates = await source.GetCandidatesAsync(song);

        var builtin = candidates.Where(candidate => candidate.InterfaceId == "netease").ToArray();
        Assert.NotEmpty(builtin);
        Assert.All(builtin, candidate => Assert.Equal("netease", candidate.InterfaceId));
        Assert.Equal(["128k", "192k", "320k", "flac", "flac24bit"], builtin.Select(candidate => candidate.Quality).ToArray());
    }

    [Fact]
    public async Task Exact_resolution_targets_the_selected_interface_and_quality()
    {
        using var host = await TestApplicationHost.StartAsync();
        RegisterScripts(host);
        await SaveScriptsAsync(host);
        await host.Services.GetRequiredService<MusicSourceService>().InitializeScriptSourcesAsync();
        var source = (IQualityCandidateSource)host.Services.GetRequiredService<MusicSourceService>();
        var candidates = (await source.GetCandidatesAsync(QualitySong()))
            .Where(candidate => candidate.InterfaceId is "low" or "high")
            .ToArray();

        var low = await source.ResolveAsync(
            QualitySong(),
            candidates.Single(candidate => candidate.InterfaceId == "low" && candidate.Quality == "128k"));
        var high = await source.ResolveAsync(
            QualitySong(),
            candidates.Single(candidate => candidate.InterfaceId == "high" && candidate.Quality == "320k"));

        Assert.Equal(LowAudio.AbsoluteUri, low);
        Assert.Equal(HighAudio.AbsoluteUri, high);
    }

    [Fact]
    public async Task Exact_resolution_does_not_invent_an_unlisted_quality()
    {
        using var host = await TestApplicationHost.StartAsync();
        RegisterScripts(host);
        await SaveScriptsAsync(host);
        await host.Services.GetRequiredService<MusicSourceService>().InitializeScriptSourcesAsync();
        var source = (IQualityCandidateSource)host.Services.GetRequiredService<MusicSourceService>();
        var candidate = new SourceQualityCandidate("low", "low", 1, "320k");

        var url = await source.ResolveAsync(QualitySong(), candidate);

        Assert.Null(url);
        Assert.DoesNotContain(
            host.HttpHandler.Requests,
            request => request.Item2 == LowAudio.AbsoluteUri);
    }

    [Fact]
    public async Task Default_source_restricts_the_menu_candidates_to_that_interface()
    {
        using var host = await TestApplicationHost.StartAsync();
        RegisterScripts(host);
        await SaveScriptsAsync(host);
        var service = host.Services.GetRequiredService<MusicSourceService>();
        await service.InitializeScriptSourcesAsync();
        var source = (IQualityCandidateSource)service;

        var before = await source.GetCandidatesAsync(QualitySong());
        Assert.Contains(before, candidate => candidate.InterfaceId == "low");
        Assert.Contains(before, candidate => candidate.InterfaceId == "high");

        Assert.True(await service.SetDefaultSourceAsync("high"));
        var after = await source.GetCandidatesAsync(QualitySong());

        Assert.All(after, candidate => Assert.Equal("high", candidate.InterfaceId));
        Assert.Equal(["128k", "320k"], after.Select(candidate => candidate.Quality).ToArray());
    }

    [Fact]
    public async Task Default_source_that_cannot_serve_the_song_platform_falls_back_to_all_interfaces()
    {
        using var host = await TestApplicationHost.StartAsync();
        RegisterScripts(host);
        await host.Services.GetRequiredService<IDataService>().SaveMusicSourcesAsync([
            TrustedScript("low", "https://quality-candidates.test/low", ["128k"], 1),
            TrustedScript("high", "https://quality-candidates.test/high", ["128k", "320k"], 2),
            TrustedScript("other-platform", "https://quality-candidates.test/other", ["128k"], 3, platform: "wy")
        ]);
        var service = host.Services.GetRequiredService<MusicSourceService>();
        await service.InitializeScriptSourcesAsync();
        Assert.True(await service.SetDefaultSourceAsync("other-platform"));

        var candidates = await ((IQualityCandidateSource)service).GetCandidatesAsync(QualitySong());

        Assert.Contains(candidates, candidate => candidate.InterfaceId == "low");
        Assert.Contains(candidates, candidate => candidate.InterfaceId == "high");
        Assert.DoesNotContain(candidates, candidate => candidate.InterfaceId == "other-platform");
    }

    [Fact]
    public async Task Changing_the_default_source_switches_the_probed_interface()
    {
        using var host = await TestApplicationHost.StartAsync();
        RegisterScripts(host);
        await SaveScriptsAsync(host);
        var service = host.Services.GetRequiredService<MusicSourceService>();
        await service.InitializeScriptSourcesAsync();
        var probe = host.Services.GetRequiredService<IQualityProbeWorkflow>();
        var song = QualitySong();

        var before = await probe.ProbeAsync(song);
        Assert.Equal("low", Assert.Single(before.Options, option => option.Quality == "128k").InterfaceName);
        Assert.Equal("high", Assert.Single(before.Options, option => option.Quality == "320k").InterfaceName);

        Assert.True(await service.SetDefaultSourceAsync("high"));
        var after = await probe.ProbeAsync(song);

        var high320 = Assert.Single(after.Options, option => option.Quality == "320k");
        Assert.Equal(QualityAvailability.Playable, high320.Status);
        Assert.Equal("high", high320.InterfaceName);
        // Declaration-only probing follows the new default source immediately.\n        Assert.Equal(QualityAvailability.Playable, Assert.Single(after.Options, option => option.Quality == "128k").Status);\n        Assert.Equal("high", Assert.Single(after.Options, option => option.Quality == "128k").InterfaceName);
    }

    private static MusicInfo QualitySong() => new()
    {
        Id = "quality-song",
        Songmid = "quality-song",
        Source = "qq",
        Title = "quality song",
        Artist = "fixture"
    };

    private static void RegisterScripts(TestApplicationHost host)
    {
        host.HttpHandler.RegisterText(HttpMethod.Get, new Uri("https://quality-candidates.test/low/url?source=tx&songId=quality-song&quality=128k"), ResolverBody("128k", LowAudio));
        host.HttpHandler.RegisterText(HttpMethod.Get, new Uri("https://quality-candidates.test/high/url?source=tx&songId=quality-song&quality=320k"), ResolverBody("320k", HighAudio));
        host.HttpHandler.Register(HttpMethod.Get, LowAudio, () => AudioResponse("128k"));
        host.HttpHandler.Register(HttpMethod.Get, HighAudio, () => AudioResponse("320k"));
    }

    private static async Task SaveScriptsAsync(TestApplicationHost host)
    {
        await host.Services.GetRequiredService<IDataService>().SaveMusicSourcesAsync([
            TrustedScript("low", "https://quality-candidates.test/low", ["128k"], 1),
            TrustedScript("high", "https://quality-candidates.test/high", ["128k", "320k"], 2)
        ]);
    }

    private static MusicSource TrustedScript(
        string id,
        string apiUrl,
        IReadOnlyList<string> qualities,
        int priority,
        string platform = "tx")
    {
        var qualityList = string.Join(",", qualities.Select(quality => $"'{quality}'"));
        var script = $$"""
            /** @name {{id}} @version 1.0.0 */
            const API_URL = '{{apiUrl}}';
            const MUSIC_QUALITY = { {{platform}}: [{{qualityList}}] };
            function handleGetMusicUrl(source, musicInfo, quality) {
              return `${API_URL}/url?source=${source}&songId=${musicInfo.songmid}&quality=${quality}`;
            }
            """;
        return new MusicSource
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            ScriptUrl = $"local://{id}",
            ScriptContent = script,
            IsEnabled = true,
            Priority = priority,
            Type = SourceType.Custom,
            TrustState = ScriptTrustState.UserApproved,
            ContentSha256 = ScriptMusicSource.ComputeSha256(script)
        };
    }

    private static string ResolverBody(string quality, Uri audio) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            code = 200,
            url = audio.AbsoluteUri,
            fixtureQuality = quality
        });

    private static HttpResponseMessage AudioResponse(string quality)
    {
        var bytes = new byte[70_000];
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
    }
}
