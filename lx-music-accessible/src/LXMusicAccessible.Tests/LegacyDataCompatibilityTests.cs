using System.IO;
using Dapper;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class LegacyDataCompatibilityTests
{
    [Fact]
    public async Task Pre30_database_and_ownership_fixture_preserves_settings_playlists_and_sources()
    {
        using var fixture = new TemporaryLocalAppData();
        var expectedDefault = Path.Combine(fixture.LocalAppData, AppDataPaths.DefaultDirName);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppDataPaths.DefaultDirName),
            AppDataPaths.DefaultDataDir);

        Directory.CreateDirectory(expectedDefault);
        AppDataPaths.PrepareDataDir(fixture.LocalAppData);
        Assert.True(File.Exists(Path.Combine(expectedDefault, AppDataOwnership.MarkerFileName)));
        Assert.True(File.Exists(Path.Combine(
            fixture.LocalAppData,
            ".lxmusic-default-owner.json")));

        await new DatabaseInitializer(
            NullLogger<DatabaseInitializer>.Instance,
            expectedDefault).InitializeAsync();

        var data = new DataService(NullLogger<DataService>.Instance, expectedDefault);
        var playlist = new Playlist
        {
            Id = "pre30-playlist",
            Name = "Pre-3.0 favorites",
            Description = "created by the pre-3.0 app",
            Type = PlaylistType.User,
            CreatedDate = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ModifiedDate = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        };
        var song = new MusicInfo
        {
            Id = "pre30-song",
            Title = "Legacy song",
            Artist = "Legacy artist",
            Source = "legacy-source"
        };
        var source = new MusicSource
        {
            Id = "trusted-source",
            Name = "Trusted pre-3.0 source",
            ScriptContent = "return {};",
            IsEnabled = true,
            Priority = 7,
            TrustState = ScriptTrustState.UserApproved,
            ContentSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        };

        await data.SavePlaylistAsync(playlist);
        await data.SavePlaylistItemAsync(playlist.Id, song, 4);
        await data.SetSettingValueAsync("hotkeys", "{\"toggle\":\"Ctrl+Alt+M\"}");
        await data.SaveMusicSourcesAsync(new List<MusicSource> { source });

        Assert.Equal(1, await QueryAsync<int>(expectedDefault, "PRAGMA user_version").ContinueWith(t => t.Result.Single()));
        Assert.Single(await QueryAsync<string>(expectedDefault, "SELECT Value FROM Settings WHERE Key = 'hotkeys'"));
        Assert.Equal(1, (await QueryAsync<int>(expectedDefault, "SELECT COUNT(*) FROM PlaylistItems")).Single());

        var resolved = AppDataPaths.ResolveDataDir(fixture.LocalAppData);
        Assert.Equal(expectedDefault, resolved);
        var reopened = new DataService(NullLogger<DataService>.Instance, resolved);
        Assert.Equal("{\"toggle\":\"Ctrl+Alt+M\"}", await reopened.GetSettingValueAsync("hotkeys"));
        var savedPlaylist = Assert.Single(await reopened.GetPlaylistsAsync(), item => item.Id == playlist.Id);
        Assert.Equal("Pre-3.0 favorites", savedPlaylist.Name);
        var savedSong = Assert.Single(await reopened.GetPlaylistItemsAsync(savedPlaylist.Id));
        Assert.Equal("pre30-song", savedSong.Id);
        var savedSource = Assert.Single(await reopened.GetMusicSourcesAsync());
        Assert.Equal(ScriptTrustState.UserApproved, savedSource.TrustState);
    }

    [Fact]
    public void Legacy_pointer_schema_remains_safe_and_selects_owned_custom_data()
    {
        using var fixture = new TemporaryLocalAppData();
        var selected = Path.Combine(fixture.Root, "custom-data");
        Directory.CreateDirectory(selected);
        var ownershipId = AppDataOwnership.Create(selected);
        Directory.CreateDirectory(Path.Combine(fixture.LocalAppData, AppDataPaths.DefaultDirName));
        File.WriteAllText(
            Path.Combine(fixture.LocalAppData, AppDataPaths.DefaultDirName, "data_dir.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Path = selected,
                OwnershipId = ownershipId
            }));

        Assert.Equal(selected, AppDataPaths.ResolveDataDir(fixture.LocalAppData));
    }

    private static async Task<IReadOnlyList<T>> QueryAsync<T>(string directory, string sql)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "lxmusic.db")
        }.ToString());
        await connection.OpenAsync();
        using (connection)
        {
            var rows = await connection.QueryAsync<T>(sql);
            return rows.AsList();
        }
    }
}

file sealed class TemporaryLocalAppData : IDisposable
{
    public TemporaryLocalAppData()
    {
        Root = Path.Combine(Path.GetTempPath(), "LXMusicAccessible.Pre30-" + Guid.NewGuid().ToString("N"));
        LocalAppData = Path.Combine(Root, "LocalAppData");
        Directory.CreateDirectory(LocalAppData);
    }

    public string Root { get; }
    public string LocalAppData { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }
}
