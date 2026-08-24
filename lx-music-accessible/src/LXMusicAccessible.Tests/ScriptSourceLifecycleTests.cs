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

    private const string ObfuscatedCompatibilityScript = """
        /**
         * @name Resource-heavy compatibility fixture
         * @version 1.0.0
         */
        // Models the string-table expansion and decoder loop used by supported obfuscated sources.
        var lookup = [];
        for (var i = 0; i < 35000; i++) {
          lookup.push('compatibility-entry-' + i + '-xxxxxxxxxxxxxxxx');
        }
        var checksum = 0;
        for (var j = 0; j < 400000; j++) {
          checksum ^= j;
        }
        lx.on(lx.EVENT_NAMES.request, function () {
          return Promise.resolve('https://fixture.invalid/' + checksum + '.mp3');
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
        var official = sources.GetEnabledSources().Where(source => source.IsSystemHidden).ToList();
        Assert.Equal(5, official.Count);
        Assert.All(official, source =>
            Assert.Equal(ScriptTrustState.UserApproved, source.TrustState));
    }

    [Fact]
    public async Task Network_import_is_automatically_trusted_enabled_and_persisted()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var sources = app.Services.GetRequiredService<IMusicSourceService>();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), ValidScript);

        var imported = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));

        Assert.True(imported.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, imported.TrustState);
        Assert.False(string.IsNullOrWhiteSpace(imported.ContentSha256));
        Assert.NotNull(((MusicSourceService)sources).GetScriptSource().GetScript(imported.Id));
        var persisted = Assert.Single(
            await app.Services.GetRequiredService<IDataService>().GetMusicSourcesAsync(),
            source => source.Id == imported.Id);
        Assert.True(persisted.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, persisted.TrustState);

        var reloaded = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);
        await reloaded.InitializeScriptSourcesAsync();
        var restored = Assert.Single(reloaded.GetAllSources(), source => source.Id == imported.Id);
        Assert.True(restored.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, restored.TrustState);

        var sameUrlAgain = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));
        Assert.NotEqual(imported.Id, sameUrlAgain.Id);
        Assert.True(sameUrlAgain.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, sameUrlAgain.TrustState);
        Assert.NotNull(((MusicSourceService)sources).GetScriptSource().GetScript(sameUrlAgain.Id));
    }

    [Fact]
    public async Task Changed_imported_content_is_rejected_without_disrupting_the_active_source()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), ValidScript);
        await sources.InitializeScriptSourcesAsync();
        var trusted = Assert.Single(sources.GetAllSources(), source =>
            source.TrustState == ScriptTrustState.Bundled);
        var imported = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));
        imported.ScriptContent += "\n// changed after import";

        var result = await sources.EnableSourceAsync(imported);

        Assert.False(result.Succeeded);
        Assert.Equal(ScriptActivationStatus.DigestMismatch, result.Status);
        Assert.True(imported.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, imported.TrustState);
        Assert.Equal(ValidScript, sources.GetScriptSource().GetScript(imported.Id)?.RawScript);
        Assert.True(trusted.IsEnabled);
        Assert.NotNull(sources.GetScriptSource().GetScript(trusted.Id));
    }

    [Fact]
    public async Task Invalid_import_is_rejected_without_replacing_the_current_trusted_source()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), "/* no request handler */");
        await sources.InitializeScriptSourcesAsync();
        var trusted = Assert.Single(sources.GetAllSources(), source =>
            source.TrustState == ScriptTrustState.Bundled);
        var imported = await sources.ImportFromUrlAsync(ScriptUrl);

        Assert.Null(imported);
        Assert.Single(sources.GetAllSources());
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
    public async Task Legacy_script_without_trust_metadata_is_migrated_enabled_without_network()
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
                Type = SourceType.BuiltIn,
                IsEnabled = false,
                TrustState = ScriptTrustState.Untrusted
            }
        });
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();

        await sources.InitializeScriptSourcesAsync();

        Assert.Empty(app.HttpHandler.Requests);
        var legacy = Assert.Single(sources.GetAllSources(), source => source.Id == "legacy-remote");
        Assert.True(legacy.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, legacy.TrustState);
        Assert.Equal("已启用", legacy.StatusText);
        Assert.Equal(ScriptMusicSource.ComputeSha256(ValidScript), legacy.ContentSha256);
        Assert.NotNull(sources.GetScriptSource().GetScript(legacy.Id));

        var persisted = Assert.Single(await data.GetMusicSourcesAsync(), source => source.Id == legacy.Id);
        Assert.True(persisted.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, persisted.TrustState);
        Assert.Equal(legacy.ContentSha256, persisted.ContentSha256);
    }

    [Fact]
    public async Task Explicit_disable_is_preserved_and_enable_command_is_persisted()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), ValidScript);
        var imported = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));
        var disableChanges = 0;
        sources.SourcesChanged += (_, _) => disableChanges++;
        sources.DisableSource(imported);
        Assert.Equal(1, disableChanges);
        await sources.PersistSourcesAsync();

        var reloaded = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);
        await reloaded.InitializeScriptSourcesAsync();
        var disabled = Assert.Single(reloaded.GetAllSources(), source => source.Id == imported.Id);
        Assert.False(disabled.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, disabled.TrustState);
        var changes = 0;
        reloaded.SourcesChanged += (_, _) => changes++;

        var result = await reloaded.EnableSourceAsync(disabled);

        Assert.True(result.Succeeded);
        Assert.True(disabled.IsEnabled);
        Assert.Equal("已启用", disabled.StatusText);
        Assert.Equal(1, changes);
        var persisted = Assert.Single(
            await app.Services.GetRequiredService<IDataService>().GetMusicSourcesAsync(),
            source => source.Id == imported.Id);
        Assert.True(persisted.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, persisted.TrustState);
    }

    [Fact]
    public async Task Local_import_is_automatically_trusted_and_enabled()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        var scriptPath = Path.Combine(app.DataDirectory, "local-source.js");
        await File.WriteAllTextAsync(scriptPath, ValidScript);

        var imported = Assert.IsType<MusicSource>(await sources.ImportFromFileAsync(scriptPath));

        Assert.True(imported.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, imported.TrustState);
        Assert.NotNull(sources.GetScriptSource().GetScript(imported.Id));
    }

    [Fact]
    public async Task Resource_heavy_obfuscated_source_registers_activates_and_reloads()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var scriptSource = app.Services.GetRequiredService<ScriptMusicSource>();
        var scriptEngine = app.Services.GetRequiredService<LxScriptEngine>();
        var staged = Assert.IsType<LxScriptInfo>(scriptSource.StageFromContent(
            ObfuscatedCompatibilityScript,
            "obfuscated-compatibility",
            "https://fixture.invalid/obfuscated.js",
            false));
        Assert.True(await scriptEngine.TestLoadAsync(staged), scriptEngine.LastError);
        var data = app.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync(new List<MusicSource>
        {
            new()
            {
                Id = "obfuscated-compatibility",
                Name = "Resource-heavy compatibility fixture",
                Version = "1.0.0",
                ScriptUrl = "https://fixture.invalid/obfuscated.js",
                ScriptContent = ObfuscatedCompatibilityScript,
                ContentSha256 = ScriptMusicSource.ComputeSha256(ObfuscatedCompatibilityScript),
                Type = SourceType.Network,
                IsEnabled = false,
                TrustState = ScriptTrustState.Untrusted
            }
        });
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();

        await sources.InitializeScriptSourcesAsync();

        var restored = Assert.Single(sources.GetAllSources(), source => source.Id == "obfuscated-compatibility");
        Assert.True(restored.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, restored.TrustState);
        Assert.NotNull(sources.GetScriptSource().GetScript(restored.Id));

        var reloaded = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);
        await reloaded.InitializeScriptSourcesAsync();
        var persisted = Assert.Single(
            reloaded.GetAllSources(),
            source => source.Id == "obfuscated-compatibility");
        Assert.True(persisted.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, persisted.TrustState);
        Assert.NotNull(reloaded.GetScriptSource().GetScript(persisted.Id));
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
        var imported = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));
        sources.DisableSource(imported);
        await sources.PersistSourcesAsync();
        var databasePath = Path.Combine(app.DataDirectory, "lxmusic.db");
        var backupPath = databasePath + ".activation-test-backup";
        SqliteConnection.ClearAllPools();
        File.Move(databasePath, backupPath);
        Directory.CreateDirectory(databasePath);

        ScriptActivationResult result;
        try
        {
            result = await sources.EnableSourceAsync(imported);
        }
        finally
        {
            Directory.Delete(databasePath);
            File.Move(backupPath, databasePath);
            SqliteConnection.ClearAllPools();
        }

        Assert.False(result.Succeeded);
        Assert.Equal(ScriptActivationStatus.PersistenceFailed, result.Status);
        Assert.False(imported.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, imported.TrustState);
        Assert.NotNull(sources.GetScriptSource().GetScript(imported.Id));
        Assert.NotNull(sources.GetScriptSource().GetScript(trusted.Id));
    }
}
