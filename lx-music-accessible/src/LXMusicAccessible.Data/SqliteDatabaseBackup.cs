using Microsoft.Data.Sqlite;

namespace LXMusicAccessible.Data;

/// <summary>
/// Creates a consistent SQLite destination through the backup API after checkpointing WAL.
/// A failed backup removes only the destination it created; the source is never mutated destructively.
/// </summary>
public static class SqliteDatabaseBackup
{
    public static void Copy(string sourceDatabase, string destinationDatabase)
    {
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(sourceDatabase);
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(destinationDatabase);
        if (File.Exists(destinationDatabase))
            throw new IOException("SQLite destination already exists: " + destinationDatabase);

        var destinationCreated = false;
        try
        {
            var source = new SqliteConnectionStringBuilder
            {
                DataSource = sourceDatabase,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            var destination = new SqliteConnectionStringBuilder
            {
                DataSource = destinationDatabase,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            using var sourceConnection = new SqliteConnection(source);
            using var destinationConnection = new SqliteConnection(destination);
            sourceConnection.Open();
            sourceConnection.ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE)");
            destinationConnection.Open();
            destinationCreated = true;
            sourceConnection.BackupDatabase(destinationConnection);
        }
        catch when (destinationCreated && File.Exists(destinationDatabase))
        {
            try { File.Delete(destinationDatabase); } catch (Exception cleanupException) { System.Diagnostics.Debug.WriteLine("SQLite backup destination cleanup failed: " + cleanupException.GetType().Name); }
            throw;
        }
    }

    private static void ExecuteNonQuery(this SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }
}