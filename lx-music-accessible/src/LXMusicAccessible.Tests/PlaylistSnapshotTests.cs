using System.Buffers.Binary;
using System.IO;
using Dapper;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class PlaylistSnapshotTests
{
    [Fact]
    public async Task Empty_and_single_song_snapshots_replace_the_previous_snapshot_exactly()
    {
        using var fixture = await TemporaryDatabase.CreateAsync();
        var data = fixture.Data;
        var original = CreatePlaylist("snapshot", "A");
        await SeedAsync(data, original, CreateSong("old-1"), CreateSong("old-2"));

        var empty = CreatePlaylist(original.Id, "Empty");
        var emptyResult = await data.ReplacePlaylistSnapshotAsync(empty, Array.Empty<MusicInfo>());

        Assert.Equal(new PlaylistSnapshotReplacement(0, 0), emptyResult);
        await AssertSnapshotAsync(data, empty, []);

        var single = CreatePlaylist(original.Id, "Single");
        var song = CreateSong("new-1");
        var singleResult = await data.ReplacePlaylistSnapshotAsync(single, [song]);

        Assert.Equal(new PlaylistSnapshotReplacement(1, 0), singleResult);
        await AssertSnapshotAsync(data, single, ["new-1"]);
    }

    [Fact]
    public async Task Duplicate_songs_keep_first_occurrence_order_and_repeat_without_duplicates_or_orphans()
    {
        using var fixture = await TemporaryDatabase.CreateAsync();
        var playlist = CreatePlaylist("snapshot", "Ordered");
        var first = CreateSong("first", title: "first version");
        var duplicate = CreateSong("first", title: "duplicate version");
        var second = CreateSong("second");

        var initial = await fixture.Data.ReplacePlaylistSnapshotAsync(
            playlist,
            [first, duplicate, second]);
        var repeated = await fixture.Data.ReplacePlaylistSnapshotAsync(
            playlist,
            [first, duplicate, second]);

        Assert.Equal(new PlaylistSnapshotReplacement(2, 1), initial);
        Assert.Equal(initial, repeated);
        var songs = await fixture.Data.GetPlaylistItemsAsync(playlist.Id);
        Assert.Equal(["first", "second"], songs.Select(song => song.Id));
        Assert.Equal("first version", songs[0].Title);
        await AssertNoOrphansAsync(fixture.DatabasePath);
    }

    [Fact]
    public async Task Triggered_failure_rolls_back_metadata_delete_and_partial_inserts()
    {
        using var fixture = await TemporaryDatabase.CreateAsync();
        var original = CreatePlaylist("snapshot", "A");
        await SeedAsync(fixture.Data, original, CreateSong("old-1"), CreateSong("old-2"));
        await fixture.ExecuteAsync(@"
            CREATE TRIGGER fail_snapshot_insert
            BEFORE INSERT ON PlaylistItems
            WHEN NEW.MusicId = 'fail'
            BEGIN
                SELECT RAISE(ABORT, 'injected snapshot failure');
            END;");

        var replacement = CreatePlaylist(original.Id, "B");
        var failingSongs = Enumerable.Range(0, 150)
            .Select(index => CreateSong(index == 125 ? "fail" : $"new-{index:D3}"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.Data.ReplacePlaylistSnapshotAsync(replacement, failingSongs));

        await AssertSnapshotAsync(fixture.Data, original, ["old-1", "old-2"]);
        await AssertNoOrphansAsync(fixture.DatabasePath);
    }

    [Fact]
    public async Task Cancellation_during_song_enumeration_rolls_back_to_the_original_snapshot()
    {
        using var fixture = await TemporaryDatabase.CreateAsync();
        var original = CreatePlaylist("snapshot", "A");
        await SeedAsync(fixture.Data, original, CreateSong("old-1"), CreateSong("old-2"));
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Data.ReplacePlaylistSnapshotAsync(
                CreatePlaylist(original.Id, "B"),
                CancelAfterFirstBatch(cancellation),
                cancellation.Token));

        await AssertSnapshotAsync(fixture.Data, original, ["old-1", "old-2"]);
        await AssertNoOrphansAsync(fixture.DatabasePath);
    }

    [Fact]
    public async Task Thousand_song_snapshot_uses_one_observable_sqlite_commit()
    {
        using var fixture = await TemporaryDatabase.CreateAsync();
        await using var keeper = await OpenAsync(fixture.DatabasePath);
        Assert.Equal("wal", await keeper.QuerySingleAsync<string>("PRAGMA journal_mode=WAL"));
        await keeper.ExecuteAsync("PRAGMA wal_autocheckpoint=0");
        await keeper.QueryAsync("PRAGMA wal_checkpoint(TRUNCATE)");

        var songs = Enumerable.Range(0, 1_000)
            .Select(index => CreateSong($"song-{index:D4}"))
            .ToList();
        var result = await fixture.Data.ReplacePlaylistSnapshotAsync(
            CreatePlaylist("large-snapshot", "Large"),
            songs);

        Assert.Equal(new PlaylistSnapshotReplacement(1_000, 0), result);
        Assert.Equal(songs.Select(song => song.Id),
            (await fixture.Data.GetPlaylistItemsAsync("large-snapshot")).Select(song => song.Id));
        Assert.Equal(1, CountWalCommits(fixture.DatabasePath + "-wal"));
        await AssertNoOrphansAsync(fixture.DatabasePath);
    }

    private static IEnumerable<MusicInfo> CancelAfterFirstBatch(CancellationTokenSource cancellation)
    {
        for (var index = 0; index < 250; index++)
        {
            if (index == 101) cancellation.Cancel();
            yield return CreateSong($"new-{index:D3}");
        }
    }

    private static async Task SeedAsync(IDataService data, Playlist playlist, params MusicInfo[] songs)
    {
        await data.SavePlaylistAsync(playlist);
        for (var index = 0; index < songs.Length; index++)
            await data.SavePlaylistItemAsync(playlist.Id, songs[index], index);
    }

    private static async Task AssertSnapshotAsync(
        IDataService data,
        Playlist expected,
        IReadOnlyList<string> expectedSongIds)
    {
        var actual = Assert.Single(await data.GetPlaylistsAsync(), item => item.Id == expected.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.RemoteId, actual.RemoteId);
        Assert.Equal(expected.IsSnapshot, actual.IsSnapshot);
        Assert.Equal(expectedSongIds,
            (await data.GetPlaylistItemsAsync(expected.Id)).Select(song => song.Id).ToList());
    }

    private static async Task AssertNoOrphansAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath);
        Assert.Empty(await connection.QueryAsync<string>("PRAGMA foreign_key_check"));
        Assert.Equal(0, await connection.QuerySingleAsync<int>(@"
            SELECT COUNT(*)
            FROM PlaylistItems item
            WHERE NOT EXISTS (SELECT 1 FROM Playlists playlist WHERE playlist.Id = item.PlaylistId)"));
    }

    private static int CountWalCommits(string walPath)
    {
        using var stream = new FileStream(
            walPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var wal = new byte[stream.Length];
        stream.ReadExactly(wal);
        Assert.True(wal.Length >= 32, "SQLite WAL header is missing.");
        var rawPageSize = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8, 4));
        var pageSize = rawPageSize == 1 ? 65_536 : checked((int)rawPageSize);
        var frameSize = 24 + pageSize;
        Assert.Equal(0, (wal.Length - 32) % frameSize);

        var commits = 0;
        for (var offset = 32; offset + frameSize <= wal.Length; offset += frameSize)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 4, 4)) != 0)
                commits++;
        }
        return commits;
    }

    private static Playlist CreatePlaylist(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Description = $"{name} snapshot",
        Type = PlaylistType.User,
        Source = "test",
        RemoteId = "test_remote",
        IsSnapshot = true,
        CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ModifiedDate = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
    };

    private static MusicInfo CreateSong(string id, string? title = null) => new()
    {
        Id = id,
        Source = "test",
        Title = title ?? id,
        Artist = "artist"
    };

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private const string Prefix = "LXMusicAccessible.PlaylistSnapshotTests-";
        private bool _disposed;

        private TemporaryDatabase(string path)
        {
            Path = path;
            Data = new DataService(NullLogger<DataService>.Instance, path);
        }

        public string Path { get; }
        public string DatabasePath => System.IO.Path.Combine(Path, "lxmusic.db");
        public IDataService Data { get; }

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var database = new TemporaryDatabase(Directory.CreateTempSubdirectory(Prefix).FullName);
            var initialized = await new DatabaseInitializer(
                NullLogger<DatabaseInitializer>.Instance,
                database.Path).InitializeAsync();
            Assert.True(initialized.Succeeded, initialized.Message);
            return database;
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = await OpenAsync(DatabasePath);
            await connection.ExecuteAsync(sql);
        }

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
