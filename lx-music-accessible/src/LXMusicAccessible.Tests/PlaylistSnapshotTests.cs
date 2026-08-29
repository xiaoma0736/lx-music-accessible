using System.Buffers.Binary;
using System.IO;
using Dapper;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class PlaylistSnapshotTests
{
    [Fact]
    public async Task Shutdown_cancellation_waits_for_current_fetch_keeps_completed_and_skips_pending_snapshots()
    {
        using var fixture = await TemporaryDatabase.CreateAsync();
        var provider = new DelayedBatchProvider();
        var service = new PlaylistImportService(
            [provider],
            fixture.Data,
            NullLogger<PlaylistImportService>.Instance);
        await using var backgroundWork = new BackgroundWorkHost(
            NullLogger<BackgroundWorkHost>.Instance);
        var playlists = new[]
        {
            Remote("completed"),
            Remote("current"),
            Remote("pending")
        };

        backgroundWork.StartLatest(
            "playlist-import",
            async context =>
            {
                await service.ImportPlaylistsAsync(
                    provider.PlatformId,
                    playlists,
                    cancellationToken: context.CancellationToken);
            });
        await provider.CurrentFetchStarted.Task;
        var stop = backgroundWork.StopAsync();
        Assert.False(stop.IsCompleted);
        provider.ReleaseCurrentFetch.SetResult();

        await stop;
        var snapshots = (await fixture.Data.GetPlaylistsAsync())
            .Where(playlist => playlist.IsSnapshot && playlist.Source == provider.PlatformId)
            .ToList();
        Assert.Equal("completed", Assert.Single(snapshots).Name);
        Assert.Equal(["completed", "current"], provider.FetchedRemoteIds);
    }

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

    [Fact]
    public async Task Favorite_addition_preserves_existing_order_and_reports_existing_input_and_single_duplicates()
    {
        using var fixture = await TemporaryDatabase.CreateAsync();
        var playlist = CreatePlaylist("favorites", "Favorites");
        await SeedAsync(fixture.Data, playlist, CreateSong("old-1"), CreateSong("old-2"));

        var first = await fixture.Data.AddSongsToPlaylistAsync(
            playlist.Id,
            [
                CreateSong("old-1", "must not replace existing"),
                CreateSong("new-1", "first version"),
                CreateSong("new-1", "duplicate version"),
                CreateSong("new-2")
            ]);
        var single = await fixture.Data.AddSongsToPlaylistAsync(
            playlist.Id,
            [CreateSong("new-3")]);
        var repeatedSingle = await fixture.Data.AddSongsToPlaylistAsync(
            playlist.Id,
            [CreateSong("new-3", "duplicate single")]);

        Assert.Equal(new PlaylistSongAddition(2, 2), first);
        Assert.Equal(new PlaylistSongAddition(1, 0), single);
        Assert.Equal(new PlaylistSongAddition(0, 1), repeatedSingle);
        var actual = await fixture.Data.GetPlaylistItemsAsync(playlist.Id);
        Assert.Equal(["old-1", "old-2", "new-1", "new-2", "new-3"],
            actual.Select(song => song.Id));
        Assert.Equal("old-1", actual[0].Title);
        Assert.Equal("first version", actual[2].Title);
        await AssertNoOrphansAsync(fixture.DatabasePath);
    }

    [Fact]
    public async Task Favorite_addition_failure_after_a_completed_batch_rolls_back_every_insert()
    {
        using var fixture = await TemporaryDatabase.CreateAsync();
        var playlist = CreatePlaylist("favorites", "Favorites");
        await SeedAsync(fixture.Data, playlist, CreateSong("old-1"), CreateSong("old-2"));
        await fixture.ExecuteAsync(@"
            CREATE TRIGGER fail_favorite_insert
            BEFORE INSERT ON PlaylistItems
            WHEN NEW.MusicId = 'fail'
            BEGIN
                SELECT RAISE(ABORT, 'injected favorite failure');
            END;");

        var additions = Enumerable.Range(0, 150)
            .Select(index => CreateSong(index == 125 ? "fail" : $"new-{index:D3}"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.Data.AddSongsToPlaylistAsync(playlist.Id, additions));

        Assert.Equal(["old-1", "old-2"],
            (await fixture.Data.GetPlaylistItemsAsync(playlist.Id)).Select(song => song.Id));
        await AssertNoOrphansAsync(fixture.DatabasePath);
    }

    [Fact]
    public async Task Favorite_addition_cancellation_after_a_completed_batch_rolls_back_every_insert()
    {
        using var fixture = await TemporaryDatabase.CreateAsync();
        var playlist = CreatePlaylist("favorites", "Favorites");
        await SeedAsync(fixture.Data, playlist, CreateSong("old-1"), CreateSong("old-2"));
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Data.AddSongsToPlaylistAsync(
                playlist.Id,
                CancelFavoriteAfterFirstBatch(cancellation),
                cancellation.Token));

        Assert.Equal(["old-1", "old-2"],
            (await fixture.Data.GetPlaylistItemsAsync(playlist.Id)).Select(song => song.Id));
        await AssertNoOrphansAsync(fixture.DatabasePath);
    }

    [Fact]
    public async Task Thousand_favorites_keep_input_order_and_use_one_observable_sqlite_commit()
    {
        using var fixture = await TemporaryDatabase.CreateAsync();
        var playlist = CreatePlaylist("large-favorites", "Large favorites");
        await SeedAsync(fixture.Data, playlist, CreateSong("existing"));
        await using var keeper = await OpenAsync(fixture.DatabasePath);
        Assert.Equal("wal", await keeper.QuerySingleAsync<string>("PRAGMA journal_mode=WAL"));
        await keeper.ExecuteAsync("PRAGMA wal_autocheckpoint=0");
        await keeper.QueryAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        var songs = Enumerable.Range(0, 1_000)
            .Select(index => CreateSong($"song-{index:D4}"))
            .ToList();

        var result = await fixture.Data.AddSongsToPlaylistAsync(playlist.Id, songs);

        Assert.Equal(new PlaylistSongAddition(1_000, 0), result);
        Assert.Equal(new[] { "existing" }.Concat(songs.Select(song => song.Id)),
            (await fixture.Data.GetPlaylistItemsAsync(playlist.Id)).Select(song => song.Id));
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

    private static IEnumerable<MusicInfo> CancelFavoriteAfterFirstBatch(
        CancellationTokenSource cancellation)
    {
        for (var index = 0; index < 250; index++)
        {
            if (index == 101) cancellation.Cancel();
            yield return CreateSong($"favorite-{index:D3}");
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

    private static RemotePlaylist Remote(string id) => new()
    {
        Platform = "test",
        RemoteId = id,
        Name = id,
        TrackCount = 1
    };

    private sealed class DelayedBatchProvider : IPlaylistImportProvider
    {
        public string PlatformId => "test";
        public string PlatformName => "测试平台";
        public List<string> FetchedRemoteIds { get; } = [];
        public TaskCompletionSource CurrentFetchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCurrentFetch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AccountInfo?> LoginByCookieAsync(string cookieText) =>
            throw new NotSupportedException();
        public Task LoadAccountAsync(AccountInfo account) => Task.CompletedTask;
        public Task LogoutAsync() => Task.CompletedTask;
        public Task<List<RemotePlaylist>> GetMyPlaylistsAsync() =>
            throw new NotSupportedException();

        public async Task<List<MusicInfo>> GetPlaylistSongsAsync(RemotePlaylist playlist)
        {
            FetchedRemoteIds.Add(playlist.RemoteId);
            if (playlist.RemoteId == "current")
            {
                CurrentFetchStarted.SetResult();
                await ReleaseCurrentFetch.Task;
            }
            return [CreateSong(playlist.RemoteId)];
        }
    }

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
