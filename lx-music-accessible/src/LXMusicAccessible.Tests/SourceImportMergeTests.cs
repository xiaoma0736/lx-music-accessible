using System.IO;
using System.Net.Http;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class SourceImportMergeTests
{
    private const string SourceUrl = "https://fixture.invalid/merge-source.js";

    private static string Script(string version, string contentUrl, string author = "Same Author") => $@"
        /**
         * @name Merge source
         * @version {version}
         * @author {author}
         */
        lx.on(lx.EVENT_NAMES.request, function () {{
          return Promise.resolve('{contentUrl}');
        }});
        ";

    [Fact]
    public async Task Same_identity_version_and_content_merge_into_existing_source()
    {
        using var app = await TestApplicationHost.StartAsync();
        var sources = app.Services.GetRequiredService<MusicSourceService>();
        var firstPath = Path.Combine(app.DataDirectory, "first.js");
        var secondPath = Path.Combine(app.DataDirectory, "second.js");
        File.WriteAllText(firstPath, Script("1.0.0", "https://fixture.invalid/first.mp3"));
        File.WriteAllText(secondPath, Script("1.0.0", "https://fixture.invalid/first.mp3"));

        var first = await sources.ImportFromFileDetailedAsync(firstPath);
        var second = await sources.ImportFromFileDetailedAsync(secondPath);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(MusicSourceImportStatus.Merged, second!.Status);
        Assert.Equal(first!.Source.Id, second.Source.Id);
        Assert.Single(sources.GetAllSources(), source => source.Name == "Merge source");
    }

    [Fact]
    public async Task Higher_version_replaces_the_existing_source_in_place()
    {
        using var app = await TestApplicationHost.StartAsync();
        var sources = app.Services.GetRequiredService<MusicSourceService>();
        var data = app.Services.GetRequiredService<IDataService>();
        var oldPath = Path.Combine(app.DataDirectory, "old.js");
        var newPath = Path.Combine(app.DataDirectory, "new.js");
        File.WriteAllText(oldPath, Script("1.0.0", "https://fixture.invalid/old.mp3"));
        File.WriteAllText(newPath, Script("2.0.0", "https://fixture.invalid/new.mp3"));

        var oldSource = await sources.ImportFromFileDetailedAsync(oldPath);
        Assert.NotNull(oldSource);
        var preserved = oldSource!.Source;
        preserved.IsEnabled = false;
        preserved.IsDefault = false;
        var expectedPriority = preserved.Priority;
        var expectedAddedDate = preserved.AddedDate;
        var replaced = await sources.ImportFromFileDetailedAsync(newPath);

        Assert.NotNull(replaced);
        Assert.Equal(MusicSourceImportStatus.Replaced, replaced!.Status);
        Assert.Equal(preserved.Id, replaced.Source.Id);
        Assert.Equal("2.0.0", replaced.Source.Version);
        Assert.Contains("new.mp3", replaced.Source.ScriptContent);
        Assert.False(replaced.Source.IsEnabled);
        Assert.False(replaced.Source.IsDefault);
        Assert.Equal(expectedPriority, replaced.Source.Priority);
        Assert.Equal(expectedAddedDate, replaced.Source.AddedDate);
        Assert.NotNull(sources.GetScriptSource().GetScript(replaced.Source.Id));
        var persisted = Assert.Single(await data.GetMusicSourcesAsync(), source => source.Id == replaced.Source.Id);
        Assert.Equal("2.0.0", persisted.Version);
    }

    [Fact]
    public async Task Lower_version_import_does_not_replace_the_existing_source()
    {
        using var app = await TestApplicationHost.StartAsync();
        var sources = app.Services.GetRequiredService<MusicSourceService>();
        var newPath = Path.Combine(app.DataDirectory, "new.js");
        var oldPath = Path.Combine(app.DataDirectory, "old.js");
        File.WriteAllText(newPath, Script("2.0.0", "https://fixture.invalid/new.mp3"));
        File.WriteAllText(oldPath, Script("1.0.0", "https://fixture.invalid/old.mp3"));

        var current = await sources.ImportFromFileDetailedAsync(newPath);
        var rejected = await sources.ImportFromFileDetailedAsync(oldPath);

        Assert.NotNull(current);
        Assert.NotNull(rejected);
        Assert.Equal(MusicSourceImportStatus.VersionConflict, rejected!.Status);
        Assert.Equal(current!.Source.Id, rejected.Source.Id);
        Assert.Equal("2.0.0", rejected.Source.Version);
        Assert.Contains("new.mp3", rejected.Source.ScriptContent!);
        Assert.Single(sources.GetAllSources(), source => source.Name == "Merge source");
    }

    [Fact]
    public async Task Same_version_different_content_keeps_the_validated_source()
    {
        using var app = await TestApplicationHost.StartAsync();
        var sources = app.Services.GetRequiredService<MusicSourceService>();
        var firstPath = Path.Combine(app.DataDirectory, "first.js");
        var conflictPath = Path.Combine(app.DataDirectory, "conflict.js");
        File.WriteAllText(firstPath, Script("1.0.0", "https://fixture.invalid/first.mp3"));
        File.WriteAllText(conflictPath, Script("1.0.0", "https://fixture.invalid/conflict.mp3"));

        var current = await sources.ImportFromFileDetailedAsync(firstPath);
        var conflict = await sources.ImportFromFileDetailedAsync(conflictPath);

        Assert.NotNull(current);
        Assert.NotNull(conflict);
        Assert.Equal(MusicSourceImportStatus.VersionConflict, conflict!.Status);
        Assert.Equal(current!.Source.Id, conflict.Source.Id);
        Assert.Contains("first.mp3", conflict.Source.ScriptContent!);
    }

    [Fact]
    public async Task Invalid_higher_version_cannot_corrupt_the_active_source()
    {
        using var app = await TestApplicationHost.StartAsync();
        var sources = app.Services.GetRequiredService<MusicSourceService>();
        var validPath = Path.Combine(app.DataDirectory, "valid.js");
        var invalidPath = Path.Combine(app.DataDirectory, "invalid.js");
        File.WriteAllText(validPath, Script("1.0.0", "https://fixture.invalid/valid.mp3"));
        File.WriteAllText(invalidPath, """
            /**
             * @name Merge source
             * @version 9.0.0
             * @author Same Author
             */
            var invalidSource = true;
            """);

        var current = await sources.ImportFromFileDetailedAsync(validPath);
        var rejected = await sources.ImportFromFileDetailedAsync(invalidPath);

        Assert.NotNull(current);
        Assert.Null(rejected);
        Assert.Equal("1.0.0", current!.Source.Version);
        Assert.Contains("valid.mp3", current.Source.ScriptContent!);
        Assert.True(current.Source.IsEnabled);
        Assert.NotNull(sources.GetScriptSource().GetScript(current.Source.Id));
    }

    [Fact]
    public async Task Sources_without_an_author_match_by_name()
    {
        using var app = await TestApplicationHost.StartAsync();
        var sources = app.Services.GetRequiredService<MusicSourceService>();
        var oldPath = Path.Combine(app.DataDirectory, "old.js");
        var newPath = Path.Combine(app.DataDirectory, "new.js");
        File.WriteAllText(oldPath, Script("1.0.0", "https://fixture.invalid/old.mp3", author: ""));
        File.WriteAllText(newPath, Script("2.0.0", "https://fixture.invalid/new.mp3", author: ""));

        var oldSource = await sources.ImportFromFileDetailedAsync(oldPath);
        var replaced = await sources.ImportFromFileDetailedAsync(newPath);

        Assert.NotNull(oldSource);
        Assert.NotNull(replaced);
        Assert.Equal(MusicSourceImportStatus.Replaced, replaced!.Status);
        Assert.Equal(oldSource!.Source.Id, replaced.Source.Id);
    }

    [Fact]
    public async Task Source_author_round_trips_through_persistence()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        const string author = "Persistent Author";

        await data.SaveMusicSourcesAsync([new MusicSource
        {
            Id = "author-source",
            Name = "Author source",
            Author = author,
            Version = "1.0.0",
            ScriptContent = "validated",
            ContentSha256 = ScriptMusicSource.ComputeSha256("validated"),
            TrustState = ScriptTrustState.UserApproved,
            IsEnabled = true,
            IsDefault = true
        }]);

        var restored = Assert.Single(await data.GetMusicSourcesAsync(), source => source.Id == "author-source");
        Assert.Equal(author, restored.Author);
    }

    [Fact]
    public async Task Importing_into_duplicate_identity_consolidates_legacy_sources()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        const string script = """
            /**
             * @name Merge source
             * @version 1.0.0
             * @author Same Author
             */
            lx.on(lx.EVENT_NAMES.request, function () {
              return Promise.resolve('https://fixture.invalid/old.mp3');
            });
            """;
        MusicSource Duplicate(string id, bool isDefault, int priority) => new()
        {
            Id = id,
            Name = "Merge source",
            Author = "Same Author",
            Version = "1.0.0",
            ScriptUrl = SourceUrl,
            ScriptContent = script,
            ContentSha256 = ScriptMusicSource.ComputeSha256(script),
            TrustState = ScriptTrustState.UserApproved,
            IsEnabled = true,
            IsDefault = isDefault,
            Priority = priority
        };
        await data.SaveMusicSourcesAsync([Duplicate("duplicate-a", true, 1), Duplicate("duplicate-b", false, 2)]);
        var sources = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);
        await sources.InitializeScriptSourcesAsync();

        var newPath = Path.Combine(app.DataDirectory, "new.js");
        File.WriteAllText(newPath, Script("2.0.0", "https://fixture.invalid/new.mp3"));
        var replaced = await sources.ImportFromFileDetailedAsync(newPath);

        Assert.NotNull(replaced);
        Assert.Equal(MusicSourceImportStatus.Replaced, replaced!.Status);
        Assert.Equal("duplicate-a", replaced.Source.Id);
        Assert.Single(sources.GetAllSources(), source => source.Name == "Merge source");
        var persisted = Assert.Single(
            await data.GetMusicSourcesAsync(),
            source => source.Name == "Merge source");
        Assert.Equal("duplicate-a", persisted.Id);
    }

    [Fact]
    public async Task Manual_refresh_updates_author_metadata()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        var scriptUrl = "https://fixture.invalid/author-refresh.js";
        var oldScript = Script("1.0.0", "https://fixture.invalid/old.mp3", author: "Original Author");
        await data.SaveMusicSourcesAsync([new MusicSource
        {
            Id = "author-refresh",
            Name = "Merge source",
            Author = "Original Author",
            Version = "1.0.0",
            ScriptUrl = scriptUrl,
            ScriptContent = oldScript,
            ContentSha256 = ScriptMusicSource.ComputeSha256(oldScript),
            TrustState = ScriptTrustState.UserApproved,
            IsEnabled = true,
            IsDefault = true,
            Type = SourceType.Network
        }]);
        var sources = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);
        await sources.InitializeScriptSourcesAsync();
        var source = Assert.Single(sources.GetAllSources(), item => item.Id == "author-refresh");
        app.HttpHandler.RegisterText(
            HttpMethod.Get,
            new Uri(scriptUrl),
            Script("2.0.0", "https://fixture.invalid/new.mp3", author: "Updated Author"));

        var updated = await sources.RefreshSourceAsync(source);

        Assert.True(updated);
        Assert.Equal("Updated Author", source.Author);
        Assert.Equal("2.0.0", source.Version);
    }

    [Fact]
    public async Task Network_import_uses_the_same_identity_merge_path()
    {
        using var app = await TestApplicationHost.StartAsync();
        var sources = app.Services.GetRequiredService<MusicSourceService>();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(SourceUrl), Script("1.0.0", "https://fixture.invalid/network.mp3"));

        var first = await sources.ImportFromUrlDetailedAsync(SourceUrl);
        var merged = await sources.ImportFromUrlDetailedAsync(SourceUrl);

        Assert.NotNull(first);
        Assert.NotNull(merged);
        Assert.Equal(MusicSourceImportStatus.Merged, merged!.Status);
        Assert.Equal(first!.Source.Id, merged.Source.Id);
        Assert.Single(sources.GetAllSources(), source => source.Name == "Merge source");
    }
}
