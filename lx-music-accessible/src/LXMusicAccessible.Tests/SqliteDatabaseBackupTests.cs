using System.Data;
using LXMusicAccessible.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class SqliteDatabaseBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LXMusicAccessible.SqliteBackup-" + Guid.NewGuid().ToString("N"));

    public SqliteDatabaseBackupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Backup_checkpoints_wal_and_contains_committed_data()
    {
        var source = Path.Combine(_root, "source.db");
        var destination = Path.Combine(_root, "destination.db");
        CreateDatabase(source);
        File.WriteAllText(source + "-wal", "wal-content");
        File.WriteAllText(source + "-shm", "shm-content");

        SqliteDatabaseBackup.Copy(source, destination);

        Assert.True(File.Exists(destination));
        using var connection = new SqliteConnection($"Data Source={destination};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Settings WHERE [Key] = 'backup'";
        Assert.Equal(1L, Assert.IsType<long>(command.ExecuteScalar()));
        Assert.False(File.Exists(destination + "-wal"));
        Assert.False(File.Exists(destination + "-shm"));
    }

    [Fact]
    public void Backup_refuses_existing_destination_and_preserves_it()
    {
        var source = Path.Combine(_root, "source.db");
        var destination = Path.Combine(_root, "existing.db");
        CreateDatabase(source);
        File.WriteAllText(destination, "existing destination");

        Assert.Throws<IOException>(() => SqliteDatabaseBackup.Copy(source, destination));
        Assert.Equal("existing destination", File.ReadAllText(destination));
    }

    [Fact]
    public void Failed_backup_removes_the_destination_it_created()
    {
        var missingSource = Path.Combine(_root, "missing.db");
        var destination = Path.Combine(_root, "failed.db");

        Assert.Throws<SqliteException>(() => SqliteDatabaseBackup.Copy(missingSource, destination));

        Assert.False(File.Exists(destination));
    }

    private static void CreateDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = "CREATE TABLE Settings ([Key] TEXT PRIMARY KEY, Value TEXT NOT NULL); INSERT INTO Settings ([Key], Value) VALUES ('backup', 'ready');";
        schema.ExecuteNonQuery();
        using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        wal.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
