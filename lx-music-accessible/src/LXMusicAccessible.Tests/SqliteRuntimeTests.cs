using System.IO;
using LXMusicAccessible.Data;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace LXMusicAccessible.Tests;

public sealed class SqliteRuntimeTests(ITestOutputHelper output)
{
    private static readonly Version MinimumPatchedVersion = new(3, 50, 2);

    [Fact]
    public async Task Bundled_runtime_is_patched_and_database_smoke_passes()
    {
        await using var app = await TestApplicationHost.StartAsync();
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(app.DataDirectory, "lxmusic.db")}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var versionText = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.True(Version.TryParse(versionText, out var version),
            $"SQLite returned an invalid version: {versionText}");
        Assert.True(version >= MinimumPatchedVersion,
            $"SQLite {version} is older than required security baseline {MinimumPatchedVersion}.");
        output.WriteLine($"Loaded SQLite runtime: {version}");

        var data = app.Services.GetRequiredService<IDataService>();
        await data.SetSettingValueAsync("test.sqlite-runtime", versionText);
        Assert.Equal(versionText, await data.GetSettingValueAsync("test.sqlite-runtime"));
    }
}
