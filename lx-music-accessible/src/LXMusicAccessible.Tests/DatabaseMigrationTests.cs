using System.IO;
using System.Text.Json;
using Dapper;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task New_database_enforces_foreign_keys_and_upsert_preserves_playlist_items()
    {
        using var fixture = new TemporaryDatabaseDirectory();
        var initializer = CreateInitializer(fixture.Path);
        var migration = await initializer.InitializeAsync();

        Assert.True(migration.Succeeded, migration.Message);
        Assert.Equal(1, migration.CurrentVersion);
        Assert.Null(migration.BackupPath);

        var data = CreateDataService(fixture.Path);
        var playlist = CreatePlaylist("playlist-1", "Before");
        var song = CreateSong("song-1");
        await data.SavePlaylistAsync(playlist);
        await data.SavePlaylistItemAsync(playlist.Id, song, 0);

        playlist.Name = "After";
        playlist.ModifiedDate = playlist.ModifiedDate.AddMinutes(1);
        await data.SavePlaylistAsync(playlist);

        Assert.Equal("After", (await data.GetPlaylistsAsync()).Single(p => p.Id == playlist.Id).Name);
        Assert.Equal(song.Id, Assert.Single(await data.GetPlaylistItemsAsync(playlist.Id)).Id);

        var foreignKeyFailure = await Assert.ThrowsAsync<SqliteException>(() =>
            data.SavePlaylistItemAsync("missing-playlist", CreateSong("orphan-attempt"), 0));
        Assert.Equal(19, foreignKeyFailure.SqliteErrorCode);

        await data.DeletePlaylistAsync(playlist.Id);
        Assert.Empty(await data.GetPlaylistItemsAsync(playlist.Id));
        await AssertDatabaseIntegrityAsync(fixture.DatabasePath);
    }

    [Fact]
    public async Task Legacy_database_migrates_once_removes_only_orphans_and_keeps_backup()
    {
        using var fixture = new TemporaryDatabaseDirectory();
        await CreateLegacyDatabaseAsync(fixture.DatabasePath, addFailureTrigger: false);

        var initializer = CreateInitializer(fixture.Path);
        var first = await initializer.InitializeAsync();

        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(0, first.PreviousVersion);
        Assert.Equal(1, first.CurrentVersion);
        Assert.Equal(1, first.OrphansRemoved);
        Assert.NotNull(first.BackupPath);
        Assert.True(File.Exists(first.BackupPath));

        await using (var backup = await OpenAsync(first.BackupPath!, foreignKeys: false))
        {
            Assert.Equal(0, await backup.QuerySingleAsync<int>("PRAGMA user_version"));
            Assert.Equal(2, await backup.QuerySingleAsync<int>("SELECT COUNT(*) FROM PlaylistItems"));
        }

        await using (var connection = await OpenAsync(fixture.DatabasePath, foreignKeys: true))
        {
            Assert.Equal(1, await connection.QuerySingleAsync<int>("PRAGMA foreign_keys"));
            Assert.Equal(1, await connection.QuerySingleAsync<int>("PRAGMA user_version"));
            Assert.Equal(1, await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM PlaylistItems WHERE PlaylistId = 'legacy-playlist'"));
            var legalMusic = await connection.QuerySingleAsync<string>(
                "SELECT MusicData FROM PlaylistItems WHERE PlaylistId = 'legacy-playlist'");
            Assert.Equal("legacy-song", JsonSerializer.Deserialize<MusicInfo>(legalMusic)?.Id);
            Assert.Equal(0, await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM PlaylistItems WHERE PlaylistId = 'missing-playlist'"));
        }
        await AssertDatabaseIntegrityAsync(fixture.DatabasePath);

        var backupsBeforeRepeat = Directory.GetFiles(fixture.Path, "*.bak").Length;
        var second = await initializer.InitializeAsync();
        Assert.True(second.Succeeded, second.Message);
        Assert.Equal(1, second.PreviousVersion);
        Assert.Equal(0, second.OrphansRemoved);
        Assert.Null(second.BackupPath);
        Assert.Equal(backupsBeforeRepeat, Directory.GetFiles(fixture.Path, "*.bak").Length);
    }

    [Fact]
    public async Task Failed_migration_restores_old_database_and_retains_recoverable_backup()
    {
        using var fixture = new TemporaryDatabaseDirectory();
        await CreateLegacyDatabaseAsync(fixture.DatabasePath, addFailureTrigger: true);
        var initializer = CreateInitializer(fixture.Path);

        var failed = await initializer.InitializeAsync();

        Assert.False(failed.Succeeded);
        Assert.Contains("restored from backup", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(failed.BackupPath);
        Assert.True(File.Exists(failed.BackupPath));

        await using (var restored = await OpenAsync(fixture.DatabasePath, foreignKeys: false))
        {
            Assert.Equal(0, await restored.QuerySingleAsync<int>("PRAGMA user_version"));
            Assert.Equal(1, await restored.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM PlaylistItems WHERE PlaylistId = 'missing-playlist'"));
            Assert.Equal(0, await restored.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM pragma_table_info('PlaylistItems') WHERE name = 'MusicId'"));
            await restored.ExecuteAsync("DROP TRIGGER fail_orphan_cleanup");
        }

        var retry = await initializer.InitializeAsync();
        Assert.True(retry.Succeeded, retry.Message);
        Assert.Equal(1, retry.OrphansRemoved);
        await AssertDatabaseIntegrityAsync(fixture.DatabasePath);
    }

    private static DatabaseInitializer CreateInitializer(string directory) =>
        new(NullLogger<DatabaseInitializer>.Instance, directory);

    private static IDataService CreateDataService(string directory) =>
        new DataService(NullLogger<DataService>.Instance, directory);

    private static Playlist CreatePlaylist(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Description = "migration test",
        Type = PlaylistType.User,
        CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ModifiedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static MusicInfo CreateSong(string id) => new()
    {
        Id = id,
        Source = "test",
        Title = id,
        Artist = "artist"
    };

    private static async Task CreateLegacyDatabaseAsync(string databasePath, bool addFailureTrigger)
    {
        await using var connection = await OpenAsync(databasePath, foreignKeys: false);
        await connection.ExecuteAsync(@"
            CREATE TABLE Playlists (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Description TEXT,
                Tags TEXT,
                Type INTEGER,
                IsLocal INTEGER,
                CreatedDate TEXT,
                ModifiedDate TEXT
            );
            CREATE TABLE PlaylistItems (
                Id TEXT PRIMARY KEY,
                PlaylistId TEXT NOT NULL,
                MusicData TEXT NOT NULL,
                OrderNum INTEGER,
                AddedDate TEXT,
                FOREIGN KEY (PlaylistId) REFERENCES Playlists(Id) ON DELETE CASCADE
            );
            INSERT INTO Playlists
                (Id, Name, Description, Tags, Type, IsLocal, CreatedDate, ModifiedDate)
            VALUES
                ('legacy-playlist', 'Legacy', '', '[]', 0, 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            INSERT INTO PlaylistItems (Id, PlaylistId, MusicData, OrderNum, AddedDate)
            VALUES (@LegalId, 'legacy-playlist', @LegalMusic, 0, '2026-01-01T00:00:00Z');
            INSERT INTO PlaylistItems (Id, PlaylistId, MusicData, OrderNum, AddedDate)
            VALUES (@OrphanId, 'missing-playlist', @OrphanMusic, 1, '2026-01-01T00:00:00Z');",
            new
            {
                LegalId = "legacy-item",
                LegalMusic = JsonSerializer.Serialize(CreateSong("legacy-song")),
                OrphanId = "orphan-item",
                OrphanMusic = JsonSerializer.Serialize(CreateSong("orphan-song"))
            });

        if (addFailureTrigger)
        {
            await connection.ExecuteAsync(@"
                CREATE TRIGGER fail_orphan_cleanup
                BEFORE DELETE ON PlaylistItems
                WHEN OLD.PlaylistId = 'missing-playlist'
                BEGIN
                    SELECT RAISE(ABORT, 'injected migration failure');
                END;");
        }
    }

    private static async Task AssertDatabaseIntegrityAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath, foreignKeys: true);
        Assert.Equal(1, await connection.QuerySingleAsync<int>("PRAGMA foreign_keys"));
        Assert.Empty(await connection.QueryAsync<string>("PRAGMA foreign_key_check"));
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath, bool foreignKeys)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = foreignKeys
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private sealed class TemporaryDatabaseDirectory : IDisposable
    {
        private const string Prefix = "LXMusicAccessible.DatabaseMigrationTests-";
        private bool _disposed;

        public TemporaryDatabaseDirectory()
        {
            Path = Directory.CreateTempSubdirectory(Prefix).FullName;
        }

        public string Path { get; }
        public string DatabasePath => System.IO.Path.Combine(Path, "lxmusic.db");

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SqliteConnection.ClearAllPools();

            var tempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            var fullPath = System.IO.Path.GetFullPath(Path);
            var relative = System.IO.Path.GetRelativePath(tempRoot, fullPath);
            if (relative == "."
                || relative.StartsWith("..", StringComparison.Ordinal)
                || System.IO.Path.IsPathRooted(relative)
                || !System.IO.Path.GetFileName(fullPath).StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Refusing to delete non-test directory: {fullPath}");
            }

            if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, recursive: true);
        }
    }
}
