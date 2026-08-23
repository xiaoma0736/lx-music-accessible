using System.Net.Http;
using System.IO;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ScriptSourceLifecycleTests
{
    private const string ScriptUrl = "https://fixture.invalid/source.js";
    private const string ValidScript = """
        /**
         * @name Test source
         * @version 1.0.0
         * @description Test-only source
         */
        lx.on(lx.EVENT_NAMES.request, function () {
          return Promise.resolve('https://fixture.invalid/audio.mp3');
        });
        """;

    [Fact]
    public void Script_digest_is_stable_across_windows_and_unix_checkouts()
    {
        Assert.Equal(
            "dbea9325179efe46ea2add94f7b6b745ca983fabb208dc6d34aa064623d7ee23",
            ScriptMusicSource.ComputeSha256("first\r\nsecond\r\n"));
    }

    [Fact]
    public async Task Cold_start_loads_verified_bundled_source_without_network()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();

        await sources.InitializeScriptSourcesAsync();

        Assert.Empty(app.HttpHandler.Requests);
        var bundled = Assert.Single(sources.GetAllSources(), source =>
            source.TrustState == ScriptTrustState.Bundled);
        Assert.True(bundled.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(bundled.ContentSha256));
        Assert.NotNull(sources.GetScriptSource().GetScript(bundled.Id));
    }

    [Fact]
    public async Task Network_import_is_staged_until_the_user_explicitly_enables_it()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var sources = app.Services.GetRequiredService<IMusicSourceService>();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), ValidScript);

        var staged = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));

        Assert.False(staged.IsEnabled);
        Assert.Equal(ScriptTrustState.Untrusted, staged.TrustState);
        Assert.False(string.IsNullOrWhiteSpace(staged.ContentSha256));
        Assert.Null(((MusicSourceService)sources).GetScriptSource().GetScript(staged.Id));

        var result = await sources.EnableSourceAsync(staged);

        Assert.True(result.Succeeded);
        Assert.Equal(ScriptActivationStatus.Activated, result.Status);
        Assert.True(staged.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, staged.TrustState);
        Assert.NotNull(((MusicSourceService)sources).GetScriptSource().GetScript(staged.Id));

        var sameUrlAgain = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));
        Assert.NotEqual(staged.Id, sameUrlAgain.Id);
        Assert.False(sameUrlAgain.IsEnabled);
        Assert.Equal(ScriptTrustState.Untrusted, sameUrlAgain.TrustState);
        Assert.Null(((MusicSourceService)sources).GetScriptSource().GetScript(sameUrlAgain.Id));
    }

    [Fact]
    public async Task Changed_staged_content_is_rejected_without_disrupting_a_trusted_source()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), ValidScript);
        await sources.InitializeScriptSourcesAsync();
        var trusted = Assert.Single(sources.GetAllSources(), source =>
            source.TrustState == ScriptTrustState.Bundled);
        var staged = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));
        staged.ScriptContent += "\n// changed after staging";

        var result = await sources.EnableSourceAsync(staged);

        Assert.False(result.Succeeded);
        Assert.Equal(ScriptActivationStatus.DigestMismatch, result.Status);
        Assert.False(staged.IsEnabled);
        Assert.Equal(ScriptTrustState.Untrusted, staged.TrustState);
        Assert.Null(sources.GetScriptSource().GetScript(staged.Id));
        Assert.True(trusted.IsEnabled);
        Assert.NotNull(sources.GetScriptSource().GetScript(trusted.Id));
    }

    [Fact]
    public async Task Invalid_staged_script_does_not_replace_the_current_trusted_source()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), "/* no request handler */");
        await sources.InitializeScriptSourcesAsync();
        var trusted = Assert.Single(sources.GetAllSources(), source =>
            source.TrustState == ScriptTrustState.Bundled);
        var staged = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));

        var result = await sources.EnableSourceAsync(staged);

        Assert.False(result.Succeeded);
        Assert.Equal(ScriptActivationStatus.ValidationFailed, result.Status);
        Assert.False(staged.IsEnabled);
        Assert.Null(sources.GetScriptSource().GetScript(staged.Id));
        Assert.NotNull(sources.GetScriptSource().GetScript(trusted.Id));
    }

    [Fact]
    public async Task Cancelled_network_import_does_not_create_a_staged_source()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var sources = app.Services.GetRequiredService<IMusicSourceService>();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), ValidScript);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sources.ImportFromUrlAsync(ScriptUrl, cancellation.Token));

        Assert.Empty(sources.GetAllSources());
    }

    [Fact]
    public async Task Legacy_remote_script_is_preserved_but_disabled_and_never_downloaded()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync(new List<MusicSource>
        {
            new()
            {
                Id = "legacy-remote",
                Name = "Legacy remote",
                ScriptUrl = ScriptUrl,
                ScriptContent = ValidScript,
                Type = SourceType.Network,
                IsEnabled = true,
                TrustState = ScriptTrustState.Untrusted
            }
        });
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();

        await sources.InitializeScriptSourcesAsync();

        Assert.Empty(app.HttpHandler.Requests);
        var legacy = Assert.Single(sources.GetAllSources(), source => source.Id == "legacy-remote");
        Assert.False(legacy.IsEnabled);
        Assert.Equal(ScriptTrustState.Untrusted, legacy.TrustState);
        Assert.Null(sources.GetScriptSource().GetScript(legacy.Id));
    }

    [Fact]
    public async Task Persistence_failure_rolls_back_activation_and_keeps_the_trusted_source()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), ValidScript);
        await sources.InitializeScriptSourcesAsync();
        var trusted = Assert.Single(sources.GetAllSources(), source =>
            source.TrustState == ScriptTrustState.Bundled);
        var staged = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));
        var databasePath = Path.Combine(app.DataDirectory, "lxmusic.db");
        var backupPath = databasePath + ".activation-test-backup";
        SqliteConnection.ClearAllPools();
        File.Move(databasePath, backupPath);
        Directory.CreateDirectory(databasePath);

        ScriptActivationResult result;
        try
        {
            result = await sources.EnableSourceAsync(staged);
        }
        finally
        {
            Directory.Delete(databasePath);
            File.Move(backupPath, databasePath);
            SqliteConnection.ClearAllPools();
        }

        Assert.False(result.Succeeded);
        Assert.Equal(ScriptActivationStatus.PersistenceFailed, result.Status);
        Assert.False(staged.IsEnabled);
        Assert.Equal(ScriptTrustState.Untrusted, staged.TrustState);
        Assert.Null(sources.GetScriptSource().GetScript(staged.Id));
        Assert.NotNull(sources.GetScriptSource().GetScript(trusted.Id));
    }
}
