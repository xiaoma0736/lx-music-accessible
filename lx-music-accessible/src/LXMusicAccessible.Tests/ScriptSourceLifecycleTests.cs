using System.Net.Http;
using System.Net;
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
    private const string SubscriptionUrl = "https://raw.githubusercontent.com/Macrohard0001/lx-ikun-music-sources/main/V260817/%E6%8E%A8%E8%8D%90/HYWmusic_beta_%E5%85%AC%E7%9B%8A%E6%B5%8B%E8%AF%95.js";
    private const string SecondSubscriptionUrl = "https://api.music.lerd.dpdns.org/script.js";
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

    private const string UpdatedScript = """
        /**
         * @name Updated test source
         * @version 2.0.0
         */
        lx.on(lx.EVENT_NAMES.request, function () {
          return Promise.resolve('https://fixture.invalid/updated.mp3');
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
    public async Task Saving_sources_removes_sources_missing_from_the_current_list()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        var first = TrustedSource("first");
        var second = TrustedSource("second");

        await data.SaveMusicSourcesAsync([first, second]);
        await data.SaveMusicSourcesAsync([second]);

        var persisted = await data.GetMusicSourcesAsync();
        Assert.Single(persisted, source => source.Id == "second");
        Assert.DoesNotContain(persisted, source => source.Id == "first");
    }

    [Fact]
    public async Task Deleted_subscription_is_not_readded_on_restart()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync([TrustedSource("hyw", SubscriptionUrl)]);
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(SubscriptionUrl), UpdatedScript);

        var sources = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);
        await sources.InitializeScriptSourcesAsync();
        sources.RemoveSource(sources.GetAllSources().Single(source => source.Id == "hyw"));
        Assert.DoesNotContain(await data.GetMusicSourcesAsync(), source => source.Id == "hyw");

        var restarted = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);
        await restarted.InitializeScriptSourcesAsync();

        Assert.DoesNotContain(restarted.GetAllSources(), source => source.Id == "hyw");
        Assert.DoesNotContain(await data.GetMusicSourcesAsync(), source => source.Id == "hyw");
    }

    [Fact]
    public async Task Refresh_source_downloads_validates_and_persists_the_remote_script()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        var url = new Uri("https://fixture.invalid/custom.js");
        await data.SaveMusicSourcesAsync([TrustedSource("custom", url.ToString())]);
        app.HttpHandler.RegisterText(HttpMethod.Get, url, UpdatedScript);
        var sources = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);
        await sources.InitializeScriptSourcesAsync();
        var source = sources.GetAllSources().Single(item => item.Id == "custom");

        var updated = await sources.RefreshSourceAsync(source);

        Assert.True(updated);
        Assert.Equal("Updated test source", source.Name);
        Assert.Equal("2.0.0", source.Version);
        Assert.Equal(UpdatedScript, source.ScriptContent);
        Assert.Equal(ScriptMusicSource.ComputeSha256(UpdatedScript), source.ContentSha256);
        Assert.Equal(UpdatedScript, sources.GetScriptSource().GetScript("custom")?.RawScript);
        Assert.Equal(UpdatedScript, (await data.GetMusicSourcesAsync()).Single(item => item.Id == "custom").ScriptContent);
    }

    [Fact]
    public async Task Failed_refresh_source_keeps_the_active_version()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        var url = new Uri("https://fixture.invalid/custom.js");
        await data.SaveMusicSourcesAsync([TrustedSource("custom", url.ToString())]);
        app.HttpHandler.RegisterText(HttpMethod.Get, url, "/* no request handler */");
        var sources = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);
        await sources.InitializeScriptSourcesAsync();
        var source = sources.GetAllSources().Single(item => item.Id == "custom");

        var updated = await sources.RefreshSourceAsync(source);

        Assert.False(updated);
        Assert.Equal(ValidScript, source.ScriptContent);
        Assert.Equal(ValidScript, sources.GetScriptSource().GetScript("custom")?.RawScript);
        Assert.Equal(ValidScript, (await data.GetMusicSourcesAsync()).Single(item => item.Id == "custom").ScriptContent);
    }

    [Fact]
    public async Task Cold_start_keeps_verified_bundled_source_when_subscriptions_are_unavailable()
    {
        using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();

        await sources.InitializeScriptSourcesAsync();

        Assert.Empty(app.HttpHandler.Requests);
        Assert.All(app.HttpHandler.Requests, request =>
            Assert.Equal(HttpMethod.Get.Method, request.Method));
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
    public async Task Cold_start_does_not_import_unconfigured_fixed_subscriptions()
    {
        using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(SubscriptionUrl), ValidScript);

        await sources.InitializeScriptSourcesAsync();

        Assert.Empty(app.HttpHandler.Requests);
        Assert.DoesNotContain(sources.GetAllSources(), source => source.Id == "hyw");
    }

    [Fact]
    public async Task Subscription_update_preserves_an_explicitly_disabled_source()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync([
            new MusicSource
            {
                Id = "hyw",
                Name = "Old test source",
                Version = "1.0.0",
                ScriptUrl = SubscriptionUrl,
                ScriptContent = ValidScript,
                ContentSha256 = ScriptMusicSource.ComputeSha256(ValidScript),
                TrustState = ScriptTrustState.UserApproved,
                Type = SourceType.Network,
                IsEnabled = false,
                Priority = 10
            }
        ]);
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(SubscriptionUrl), UpdatedScript);
        var sources = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);

        await sources.InitializeScriptSourcesAsync();

        var updated = Assert.Single(sources.GetAllSources(), source => source.Id == "hyw");
        Assert.Equal("2.0.0", updated.Version);
        Assert.Equal(UpdatedScript, updated.ScriptContent);
        Assert.Equal(ScriptMusicSource.ComputeSha256(UpdatedScript), updated.ContentSha256);
        Assert.Equal(ScriptTrustState.UserApproved, updated.TrustState);
        Assert.False(updated.IsEnabled);
        var persisted = Assert.Single(await data.GetMusicSourcesAsync(), source => source.Id == "hyw");
        Assert.False(persisted.IsEnabled);
        Assert.Equal(UpdatedScript, persisted.ScriptContent);
    }

    [Fact]
    public async Task Invalid_subscription_update_keeps_the_active_version()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync([
            new MusicSource
            {
                Id = "hyw",
                Name = "Existing source",
                Version = "1.0.0",
                ScriptUrl = SubscriptionUrl,
                ScriptContent = ValidScript,
                ContentSha256 = ScriptMusicSource.ComputeSha256(ValidScript),
                TrustState = ScriptTrustState.UserApproved,
                Type = SourceType.Network,
                IsEnabled = true,
                Priority = 10
            }
        ]);
        app.HttpHandler.RegisterText(
            HttpMethod.Get,
            new Uri(SubscriptionUrl),
            "/* no request handler */");
        var sources = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);

        await sources.InitializeScriptSourcesAsync();

        var retained = Assert.Single(sources.GetAllSources(), source => source.Id == "hyw");
        Assert.True(retained.IsEnabled);
        Assert.Equal(ValidScript, retained.ScriptContent);
        Assert.Equal(ValidScript, sources.GetScriptSource().GetScript("hyw")?.RawScript);
        var persisted = Assert.Single(await data.GetMusicSourcesAsync(), source => source.Id == "hyw");
        Assert.Equal(ValidScript, persisted.ScriptContent);
    }

    [Fact]
    public async Task Oversized_subscription_update_keeps_the_active_version()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync([
            new MusicSource
            {
                Id = "hyw",
                Name = "Existing source",
                Version = "1.0.0",
                ScriptUrl = SubscriptionUrl,
                ScriptContent = ValidScript,
                ContentSha256 = ScriptMusicSource.ComputeSha256(ValidScript),
                TrustState = ScriptTrustState.UserApproved,
                Type = SourceType.Network,
                IsEnabled = true,
                Priority = 10
            }
        ]);
        app.HttpHandler.Register(HttpMethod.Get, new Uri(SubscriptionUrl), () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[ScriptNetworkClient.MaxScriptBytes + 1])
            });
        var sources = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);

        await sources.InitializeScriptSourcesAsync();

        var retained = Assert.Single(sources.GetAllSources(), source => source.Id == "hyw");
        Assert.True(retained.IsEnabled);
        Assert.Equal(ValidScript, retained.ScriptContent);
        Assert.Equal(ValidScript, sources.GetScriptSource().GetScript("hyw")?.RawScript);
    }

    [Fact]
    public async Task Unconfigured_fixed_subscriptions_are_not_refreshed_on_cold_start()
    {
        using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        app.HttpHandler.RegisterText(
            HttpMethod.Get,
            new Uri(SubscriptionUrl),
            "/* no request handler */");
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(SecondSubscriptionUrl), ValidScript);

        await sources.InitializeScriptSourcesAsync();

        Assert.Empty(app.HttpHandler.Requests);
        Assert.DoesNotContain(sources.GetAllSources(), source => source.Id == "hyw");
        Assert.DoesNotContain(sources.GetAllSources(), source => source.Id == "jheapi");
    }

    [Fact]
    public async Task Existing_subscription_by_id_is_updated_without_a_duplicate()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync([
            new MusicSource
            {
                Id = "hyw",
                Name = "Manually imported subscription",
                Version = "1.0.0",
                ScriptUrl = SubscriptionUrl,
                ScriptContent = ValidScript,
                ContentSha256 = ScriptMusicSource.ComputeSha256(ValidScript),
                TrustState = ScriptTrustState.UserApproved,
                Type = SourceType.Network,
                IsEnabled = true,
                Priority = 10
            }
        ]);
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(SubscriptionUrl), UpdatedScript);
        var sources = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);

        await sources.InitializeScriptSourcesAsync();

        var matching = sources.GetAllSources()
            .Where(source => source.ScriptUrl == SubscriptionUrl)
            .ToList();
        var updated = Assert.Single(matching);
        Assert.Equal("hyw", updated.Id);
        Assert.Equal(UpdatedScript, updated.ScriptContent);
    }

    [Fact]
    public async Task Network_import_is_automatically_trusted_enabled_and_persisted()
    {
        using var app = await TestApplicationHost.StartAsync();
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
        Assert.Equal(imported.Id, sameUrlAgain.Id);
        Assert.True(sameUrlAgain.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, sameUrlAgain.TrustState);
        Assert.NotNull(((MusicSourceService)sources).GetScriptSource().GetScript(sameUrlAgain.Id));
    }

    [Fact]
    public async Task Changed_imported_content_is_rejected_without_disrupting_the_active_source()
    {
        using var app = await TestApplicationHost.StartAsync();
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
        using var app = await TestApplicationHost.StartAsync();
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
        using var app = await TestApplicationHost.StartAsync();
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
        using var app = await TestApplicationHost.StartAsync();
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

        Assert.DoesNotContain(app.HttpHandler.Requests, request =>
            request.Uri == ScriptUrl);
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
        using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), ValidScript);
        var imported = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));
        Assert.True(await sources.SetDefaultSourceAsync("netease"));
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
        using var app = await TestApplicationHost.StartAsync();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        var scriptPath = Path.Combine(app.DataDirectory, "local-source.js");
        await Task.Run(() => File.WriteAllText(scriptPath, ValidScript));

        var imported = Assert.IsType<MusicSource>(await sources.ImportFromFileAsync(scriptPath));

        Assert.True(imported.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, imported.TrustState);
        Assert.NotNull(sources.GetScriptSource().GetScript(imported.Id));
    }

    [Fact]
    public async Task Resource_heavy_obfuscated_source_registers_activates_and_reloads()
    {
        using var app = await TestApplicationHost.StartAsync();
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
        using var app = await TestApplicationHost.StartAsync();
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

    private static MusicSource TrustedSource(string id, string? scriptUrl = null) => new()
    {
        Id = id,
        Name = $"{id} source",
        Version = "1.0.0",
        ScriptUrl = scriptUrl ?? "",
        ScriptContent = ValidScript,
        ContentSha256 = ScriptMusicSource.ComputeSha256(ValidScript),
        TrustState = ScriptTrustState.UserApproved,
        Type = SourceType.Network,
        IsEnabled = true,
        Priority = 10
    };
}
