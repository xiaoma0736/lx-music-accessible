using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Dapper;
using LXMusicAccessible.Models;
using System.Text;
using System.Text.Json;

namespace LXMusicAccessible.Data;

public sealed record DatabaseMigrationResult(
    bool Succeeded,
    int PreviousVersion,
    int CurrentVersion,
    int OrphansRemoved,
    string? BackupPath,
    string Message);

/// <summary>
/// 数据库初始化器
/// </summary>
public class DatabaseInitializer
{
    private const int CurrentSchemaVersion = 1;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly string _dataDir;
    private const string DbFileName = "lxmusic.db";

    public DatabaseInitializer(ILogger<DatabaseInitializer> logger, string dataDir)
    {
        _logger = logger;
        _dataDir = dataDir;
    }

    public async Task<DatabaseMigrationResult> InitializeAsync()
    {
        var dbFilePath = Path.Combine(_dataDir, DbFileName);
        var existedBeforeMigration = File.Exists(dbFilePath);
        var previousVersion = 0;
        var orphansRemoved = 0;
        string? backupPath = null;

        try
        {
            Directory.CreateDirectory(_dataDir);
            if (existedBeforeMigration)
            {
                previousVersion = await ReadSchemaVersionAsync(dbFilePath);
                if (previousVersion > CurrentSchemaVersion)
                    throw new InvalidOperationException(
                        $"Database schema version {previousVersion} is newer than supported version {CurrentSchemaVersion}.");

                if (previousVersion < CurrentSchemaVersion)
                    backupPath = await CreateBackupAsync(dbFilePath, previousVersion);
            }

            if (previousVersion < CurrentSchemaVersion)
            {
                await using var migrationConnection = await OpenMigrationConnectionAsync(dbFilePath);
                await InitializeSchemaAsync(migrationConnection);
                orphansRemoved = await NormalizePlaylistItemsAsync(migrationConnection);
                await migrationConnection.ExecuteAsync($"PRAGMA user_version = {CurrentSchemaVersion}");
                await EnableAndVerifyForeignKeysAsync(migrationConnection);
                await VerifyIntegrityAsync(migrationConnection);
            }
            else
            {
                await using var connection = await SqliteDatabaseConnection.OpenAsync(dbFilePath);
                await VerifyIntegrityAsync(connection);
            }

            return new DatabaseMigrationResult(
                true,
                previousVersion,
                CurrentSchemaVersion,
                orphansRemoved,
                backupPath,
                previousVersion == CurrentSchemaVersion
                    ? "Database schema is current and integrity checks passed."
                    : "Database migration and integrity checks passed.");
        }
        catch (Exception ex)
        {
            try
            {
                if (backupPath is not null)
                    RestoreBackup(backupPath, dbFilePath);
                else if (!existedBeforeMigration)
                    DeleteDatabaseFiles(dbFilePath);
            }
            catch (Exception restoreException)
            {
                _logger.LogCritical(restoreException, "Database restore failed; backup retained at {BackupPath}", backupPath);
                return new DatabaseMigrationResult(
                    false,
                    previousVersion,
                    previousVersion,
                    0,
                    backupPath,
                    $"Database migration failed ({ex.GetType().Name}) and automatic restore failed ({restoreException.GetType().Name}). The backup was retained.");
            }

            _logger.LogError(ex, "Database migration failed");
            return new DatabaseMigrationResult(
                false,
                previousVersion,
                previousVersion,
                0,
                backupPath,
                backupPath is null
                    ? $"Database initialization failed: {ex.Message}"
                    : $"Database migration failed and the previous database was restored from backup: {ex.Message}");
        }
    }

    private async Task InitializeSchemaAsync(SqliteConnection connection)
    {
        try
        {
            // 创建表
            await CreateTablesAsync(connection);

            // 迁移：旧版本 MusicSources 表补充 IsDefault 列
            var cols = await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('MusicSources')");
            if (!cols.Any(n => string.Equals(n, "IsDefault", StringComparison.OrdinalIgnoreCase)))
            {
                await connection.ExecuteAsync("ALTER TABLE MusicSources ADD COLUMN IsDefault INTEGER NOT NULL DEFAULT 0");
                _logger.LogInformation("已为 MusicSources 表添加 IsDefault 列");
            }
            if (!cols.Any(n => string.Equals(n, "TrustState", StringComparison.OrdinalIgnoreCase)))
            {
                await connection.ExecuteAsync("ALTER TABLE MusicSources ADD COLUMN TrustState INTEGER NOT NULL DEFAULT 0");
                _logger.LogInformation("已为 MusicSources 表添加 TrustState 列");
            }
            if (!cols.Any(n => string.Equals(n, "ContentSha256", StringComparison.OrdinalIgnoreCase)))
            {
                await connection.ExecuteAsync("ALTER TABLE MusicSources ADD COLUMN ContentSha256 TEXT");
                _logger.LogInformation("已为 MusicSources 表添加 ContentSha256 列");
            }

            // 迁移：Playlists 表补充 Source / RemoteId 列（在线收藏歌单的远程元数据）
            var pcols = await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('Playlists')");
            if (!pcols.Any(n => string.Equals(n, "Source", StringComparison.OrdinalIgnoreCase)))
            {
                await connection.ExecuteAsync("ALTER TABLE Playlists ADD COLUMN Source TEXT");
                _logger.LogInformation("已为 Playlists 表添加 Source 列");
            }
            if (!pcols.Any(n => string.Equals(n, "RemoteId", StringComparison.OrdinalIgnoreCase)))
            {
                await connection.ExecuteAsync("ALTER TABLE Playlists ADD COLUMN RemoteId TEXT");
                _logger.LogInformation("已为 Playlists 表添加 RemoteId 列");
            }
            if (!pcols.Any(n => string.Equals(n, "IsSnapshot", StringComparison.OrdinalIgnoreCase)))
            {
                await connection.ExecuteAsync("ALTER TABLE Playlists ADD COLUMN IsSnapshot INTEGER NOT NULL DEFAULT 0");
                _logger.LogInformation("已为 Playlists 表添加 IsSnapshot 列（个人歌单导入快照标记）");
            }

            // 迁移：PlaylistItems 表补充 MusicId / MusicSource 列（按歌单去重）
            var picols = await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('PlaylistItems')");
            if (!picols.Any(n => string.Equals(n, "MusicId", StringComparison.OrdinalIgnoreCase)))
            {
                await connection.ExecuteAsync("ALTER TABLE PlaylistItems ADD COLUMN MusicId TEXT");
                _logger.LogInformation("已为 PlaylistItems 表添加 MusicId 列");
            }
            if (!picols.Any(n => string.Equals(n, "MusicSource", StringComparison.OrdinalIgnoreCase)))
            {
                await connection.ExecuteAsync("ALTER TABLE PlaylistItems ADD COLUMN MusicSource TEXT");
                _logger.LogInformation("已为 PlaylistItems 表添加 MusicSource 列");
            }

            // 迁移：确保默认本地/在线歌单存在，并把旧 FavoriteSongs 表数据幂等迁入在线“默认收藏”
            await EnsureDefaultPlaylistsAsync(connection);
            await MigrateLegacyFavoritesAsync(connection);

            _logger.LogInformation("数据库初始化完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据库初始化失败");
            throw;
        }
    }

    private static async Task<int> NormalizePlaylistItemsAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync("DROP INDEX IF EXISTS IX_PlaylistItems_UniqueSong");

        await connection.ExecuteAsync(@"
            UPDATE PlaylistItems
            SET MusicId = COALESCE(
                    NULLIF(MusicId, ''),
                    CASE WHEN json_valid(MusicData) THEN NULLIF(json_extract(MusicData, '$.Id'), '') END,
                    Id),
                MusicSource = COALESCE(
                    NULLIF(MusicSource, ''),
                    CASE WHEN json_valid(MusicData) THEN NULLIF(json_extract(MusicData, '$.Source'), '') END,
                    'Local')");

        await connection.ExecuteAsync(@"
            DELETE FROM PlaylistItems
            WHERE rowid NOT IN (
                SELECT MIN(rowid)
                FROM PlaylistItems
                GROUP BY PlaylistId, MusicId, MusicSource)");

        var orphansRemoved = await connection.ExecuteAsync(@"
            DELETE FROM PlaylistItems
            WHERE NOT EXISTS (
                SELECT 1 FROM Playlists WHERE Playlists.Id = PlaylistItems.PlaylistId)");

        await connection.ExecuteAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PlaylistItems_UniqueSong
            ON PlaylistItems(PlaylistId, MusicId, MusicSource)");
        return orphansRemoved;
    }

    private static async Task<int> ReadSchemaVersionAsync(string dbFilePath)
    {
        await using var connection = await SqliteDatabaseConnection.OpenAsync(dbFilePath);
        return await connection.QuerySingleAsync<int>("PRAGMA user_version");
    }

    private static async Task<string> CreateBackupAsync(string dbFilePath, int previousVersion)
    {
        var backupPath = $"{dbFilePath}.pre-v{previousVersion}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak";
        await using var source = await SqliteDatabaseConnection.OpenAsync(dbFilePath);
        await using var destination = await SqliteDatabaseConnection.OpenAsync(backupPath);
        source.BackupDatabase(destination);
        return backupPath;
    }

    private static async Task<SqliteConnection> OpenMigrationConnectionAsync(string dbFilePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbFilePath,
            ForeignKeys = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task EnableAndVerifyForeignKeysAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync("PRAGMA foreign_keys = ON");
        if (await connection.QuerySingleAsync<int>("PRAGMA foreign_keys") != 1)
            throw new InvalidOperationException("SQLite foreign key enforcement could not be enabled.");
    }

    private static async Task VerifyIntegrityAsync(SqliteConnection connection)
    {
        var violations = (await connection.QueryAsync<string>("PRAGMA foreign_key_check")).ToList();
        if (violations.Count != 0)
            throw new InvalidOperationException(
                $"Database foreign key validation found {violations.Count} violation(s).");
    }

    private static void RestoreBackup(string backupPath, string dbFilePath)
    {
        SqliteConnection.ClearAllPools();
        DeleteSidecarFiles(dbFilePath);
        File.Copy(backupPath, dbFilePath, overwrite: true);
    }

    private static void DeleteDatabaseFiles(string dbFilePath)
    {
        SqliteConnection.ClearAllPools();
        DeleteSidecarFiles(dbFilePath);
        if (File.Exists(dbFilePath)) File.Delete(dbFilePath);
    }

    private static void DeleteSidecarFiles(string dbFilePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var path = dbFilePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private async Task CreateTablesAsync(SqliteConnection connection)
    {
        // 播放列表表
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS Playlists (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Description TEXT,
                Tags TEXT,
                Type INTEGER,
                IsLocal INTEGER,
                CreatedDate TEXT,
                ModifiedDate TEXT
            )");

        // 播放列表项目表
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS PlaylistItems (
                Id TEXT PRIMARY KEY,
                PlaylistId TEXT NOT NULL,
                MusicData TEXT NOT NULL,
                OrderNum INTEGER,
                AddedDate TEXT,
                FOREIGN KEY (PlaylistId) REFERENCES Playlists(Id) ON DELETE CASCADE
            )");

        // 设置表
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            )");

        // 音源表
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS MusicSources (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Version TEXT,
                ScriptUrl TEXT,
                ScriptContent TEXT,
                IsEnabled INTEGER,
                IsDefault INTEGER NOT NULL DEFAULT 0,
                Priority INTEGER,
                AddedDate TEXT,
                LastUpdateDate TEXT,
                Type INTEGER,
                TrustState INTEGER NOT NULL DEFAULT 0,
                ContentSha256 TEXT
            )");

        // 播放历史表
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS PlayHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MusicData TEXT NOT NULL,
                PlayedDate TEXT
            )");

        // 本地收藏表（网络歌曲收藏，按 歌曲Id+音源 去重）
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS FavoriteSongs (
                MusicId TEXT NOT NULL,
                MusicSource TEXT NOT NULL,
                MusicData TEXT NOT NULL,
                OrderNum INTEGER,
                AddedDate TEXT,
                PRIMARY KEY (MusicId, MusicSource)
            )");
            

        // 平台账号登录态表（凭据 DPAPI 加密存储，仅本机）
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS Accounts (
                Platform TEXT PRIMARY KEY,
                DisplayName TEXT,
                Uid TEXT,
                EncryptedAuth TEXT,
                LoginTime TEXT,
                ExpireTime TEXT
            )");
        // 创建默认设置
        await CreateDefaultSettingsAsync(connection);
    }
    
    private async Task CreateDefaultSettingsAsync(SqliteConnection connection)
    {
        var defaultSettings = new UserSettings
        {
            DefaultVolume = 0.8,
            WindowWidth = 1200,
            WindowHeight = 800,
            AutoPlay = true,
            LoopMode = false,
            ShuffleMode = false
        };
        
        var settingsJson = JsonSerializer.Serialize(defaultSettings);
        await connection.ExecuteAsync(
            "INSERT OR IGNORE INTO Settings (Key, Value) VALUES (@Key, @Value)",
            new { Key = "UserSettings", Value = settingsJson }
        );
    }

    /// <summary>确保本地“我的本地歌曲”与在线“默认收藏”两个系统默认歌单存在</summary>
    private static async Task EnsureDefaultPlaylistsAsync(SqliteConnection connection)
    {
        await EnsureDefaultPlaylistCore(connection, isLocal: true, "我的本地歌曲");
        await EnsureDefaultPlaylistCore(connection, isLocal: false, "默认收藏");
    }

    private static async Task EnsureDefaultPlaylistCore(SqliteConnection connection, bool isLocal, string name)
    {
        var exists = await connection.QueryFirstOrDefaultAsync<long?>(
            "SELECT COUNT(*) FROM Playlists WHERE IsLocal = @IsLocal AND Type = @Type",
            new { IsLocal = isLocal ? 1 : 0, Type = (int)PlaylistType.System });
        if (exists is > 0) return;
        await connection.ExecuteAsync(@"
            INSERT INTO Playlists (Id, Name, Description, Tags, Type, IsLocal, CreatedDate, ModifiedDate, Source, RemoteId)
            VALUES (@Id, @Name, @Description, @Tags, @Type, @IsLocal, @CreatedDate, @ModifiedDate, @Source, @RemoteId)",
            new
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Description = isLocal ? "本地音乐默认歌单" : "在线收藏默认歌单",
                Tags = string.Empty,
                Type = (int)PlaylistType.System,
                IsLocal = isLocal ? 1 : 0,
                CreatedDate = DateTime.Now.ToString("o"),
                ModifiedDate = DateTime.Now.ToString("o"),
                Source = (string?)null,
                RemoteId = (string?)null
            });
        _ = name; // keep param used for logging clarity
    }

    /// <summary>把旧版 FavoriteSongs 表数据幂等迁入在线“默认收藏”歌单（仅当该歌单为空且旧表有数据时执行一次）</summary>
    private static async Task MigrateLegacyFavoritesAsync(SqliteConnection connection)
    {
        var defaultPlaylistId = await connection.QueryFirstOrDefaultAsync<string>(
            "SELECT Id FROM Playlists WHERE IsLocal = 0 AND Type = @Type ORDER BY CreatedDate ASC LIMIT 1",
            new { Type = (int)PlaylistType.System });
        if (string.IsNullOrEmpty(defaultPlaylistId)) return;

        var count = await connection.QueryFirstOrDefaultAsync<long>(
            "SELECT COUNT(*) FROM PlaylistItems WHERE PlaylistId = @PlaylistId", new { PlaylistId = defaultPlaylistId });
        if (count > 0) return;

        var legacyRows = await connection.QueryAsync<string>("SELECT MusicData FROM FavoriteSongs ORDER BY AddedDate DESC");
        int order = 0;
        foreach (var json in legacyRows)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                var music = JsonSerializer.Deserialize<MusicInfo>(json);
                if (music == null || string.IsNullOrEmpty(music.Id) || string.IsNullOrEmpty(music.Source)) continue;
                await connection.ExecuteAsync(@"
                    INSERT OR IGNORE INTO PlaylistItems (Id, PlaylistId, MusicData, MusicId, MusicSource, OrderNum, AddedDate)
                    VALUES (@Id, @PlaylistId, @MusicData, @MusicId, @MusicSource, @OrderNum, @AddedDate)",
                    new
                    {
                        Id = Guid.NewGuid().ToString(),
                        PlaylistId = defaultPlaylistId,
                        MusicData = json,
                        MusicId = music.Id,
                        MusicSource = music.Source,
                        OrderNum = order++,
                        AddedDate = DateTime.Now.ToString("o")
                    });
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Legacy favorite migration skipped invalid JSON: {ex.GetType().Name}");
            }
        }
        if (order > 0)
            System.Diagnostics.Debug.WriteLine($"已迁移 {order} 条旧收藏到在线默认收藏");
    }
}

/// <summary>
/// 数据服务
/// </summary>
internal static class SqliteDatabaseConnection
{
    public static async Task<SqliteConnection> OpenAsync(
        string dbFilePath,
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbFilePath,
            ForeignKeys = true
        }.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken);
            if (await connection.QuerySingleAsync<int>(new CommandDefinition(
                    "PRAGMA foreign_keys",
                    cancellationToken: cancellationToken)) != 1)
                throw new InvalidOperationException("SQLite foreign key enforcement is disabled.");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

public class DataService : IDataService
{
    private const int PlaylistItemBatchSize = 100;
    private const string UpsertPlaylistSql = @"
        INSERT INTO Playlists
        (Id, Name, Description, Tags, Type, IsLocal, CreatedDate, ModifiedDate, Source, RemoteId, IsSnapshot)
        VALUES (@Id, @Name, @Description, @Tags, @Type, @IsLocal, @CreatedDate, @ModifiedDate, @Source, @RemoteId, @IsSnapshot)
        ON CONFLICT(Id) DO UPDATE SET
            Name = excluded.Name,
            Description = excluded.Description,
            Tags = excluded.Tags,
            Type = excluded.Type,
            IsLocal = excluded.IsLocal,
            CreatedDate = excluded.CreatedDate,
            ModifiedDate = excluded.ModifiedDate,
            Source = excluded.Source,
            RemoteId = excluded.RemoteId,
            IsSnapshot = excluded.IsSnapshot";
    private readonly ILogger<DataService> _logger;
    private readonly string _dataDir;
    private const string DbFileName = "lxmusic.db";

    public DataService(ILogger<DataService> logger, string dataDir)
    {
        _logger = logger;
        _dataDir = dataDir;
    }

    private async Task<SqliteConnection> GetConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        return await SqliteDatabaseConnection.OpenAsync(
            Path.Combine(_dataDir, DbFileName),
            cancellationToken);
    }

    /// <summary>把数据库中的 Tags 文本解析为标签列表（空/无效返回空列表；兼容逗号分隔旧格式）</summary>
    private static List<string> ParseTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return new List<string>();
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(tags);
            return list ?? new List<string>();
        }
        catch
        {
            return tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
    }

    /// <summary>把标签列表序列化为数据库文本（JSON 数组）</summary>
    private static string SerializeTags(List<string> tags) => JsonSerializer.Serialize(tags ?? []);

    public async Task<PlaylistSnapshotReplacement> ReplacePlaylistSnapshotAsync(
        Playlist playlist,
        IEnumerable<MusicInfo> songs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(songs);
        if (string.IsNullOrWhiteSpace(playlist.Id))
            throw new ArgumentException("Playlist Id is required.", nameof(playlist));

        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await GetConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                UpsertPlaylistSql,
                CreatePlaylistParameters(playlist),
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM PlaylistItems WHERE PlaylistId = @PlaylistId",
                new { PlaylistId = playlist.Id },
                transaction,
                cancellationToken: cancellationToken));

            var seen = new HashSet<(string MusicId, string MusicSource)>();
            var batch = new List<PlaylistItemWrite>(PlaylistItemBatchSize);
            var imported = 0;
            var skipped = 0;
            var addedDate = DateTime.Now.ToString("o");

            foreach (var music in songs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (music is null)
                {
                    skipped++;
                    continue;
                }

                var musicId = string.IsNullOrEmpty(music.Id)
                    ? music.PlayUrl ?? Guid.NewGuid().ToString()
                    : music.Id;
                var musicSource = string.IsNullOrEmpty(music.Source) ? "Local" : music.Source;
                if (!seen.Add((musicId, musicSource)))
                {
                    skipped++;
                    continue;
                }

                batch.Add(new PlaylistItemWrite(
                    Guid.NewGuid().ToString(),
                    playlist.Id,
                    JsonSerializer.Serialize(music),
                    musicId,
                    musicSource,
                    imported++,
                    addedDate));

                if (batch.Count == PlaylistItemBatchSize)
                {
                    await WritePlaylistItemBatchAsync(connection, transaction, batch, cancellationToken);
                    batch.Clear();
                }
            }

            if (batch.Count != 0)
                await WritePlaylistItemBatchAsync(connection, transaction, batch, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
            return new PlaylistSnapshotReplacement(imported, skipped);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                _logger.LogError(rollbackException, "Failed to roll back playlist snapshot {PlaylistId}", playlist.Id);
            }
            throw;
        }
    }

    private static async Task WritePlaylistItemBatchAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        IReadOnlyList<PlaylistItemWrite> batch,
        CancellationToken cancellationToken)
    {
        var sql = new StringBuilder(@"
            INSERT INTO PlaylistItems
            (Id, PlaylistId, MusicData, MusicId, MusicSource, OrderNum, AddedDate)
            VALUES ");
        var parameters = new DynamicParameters();
        for (var index = 0; index < batch.Count; index++)
        {
            if (index != 0) sql.Append(',');
            sql.Append($"(@Id{index}, @PlaylistId{index}, @MusicData{index}, @MusicId{index}, @MusicSource{index}, @OrderNum{index}, @AddedDate{index})");
            var item = batch[index];
            parameters.Add($"Id{index}", item.Id);
            parameters.Add($"PlaylistId{index}", item.PlaylistId);
            parameters.Add($"MusicData{index}", item.MusicData);
            parameters.Add($"MusicId{index}", item.MusicId);
            parameters.Add($"MusicSource{index}", item.MusicSource);
            parameters.Add($"OrderNum{index}", item.OrderNum);
            parameters.Add($"AddedDate{index}", item.AddedDate);
        }
        sql.Append(@"
            ON CONFLICT(PlaylistId, MusicId, MusicSource) DO UPDATE SET
                MusicData = excluded.MusicData,
                OrderNum = excluded.OrderNum,
                AddedDate = excluded.AddedDate");

        await connection.ExecuteAsync(new CommandDefinition(
            sql.ToString(),
            parameters,
            transaction,
            cancellationToken: cancellationToken));
    }

    private static object CreatePlaylistParameters(Playlist playlist) => new
    {
        playlist.Id,
        playlist.Name,
        playlist.Description,
        Tags = SerializeTags(playlist.Tags),
        Type = playlist.Type,
        IsLocal = playlist.IsLocal ? 1 : 0,
        CreatedDate = playlist.CreatedDate.ToString("o"),
        ModifiedDate = playlist.ModifiedDate.ToString("o"),
        Source = string.IsNullOrEmpty(playlist.Source) ? null : playlist.Source,
        RemoteId = string.IsNullOrEmpty(playlist.RemoteId) ? null : playlist.RemoteId,
        IsSnapshot = playlist.IsSnapshot ? 1 : 0
    };

    private sealed record PlaylistItemWrite(
        string Id,
        string PlaylistId,
        string MusicData,
        string MusicId,
        string MusicSource,
        long OrderNum,
        string AddedDate);

    private sealed record PlaylistItemIndex(
        string MusicId,
        string MusicSource,
        long OrderNum);

    public async Task SavePlaylistAsync(Playlist playlist)
    {
        _logger.LogInformation("保存播放列表: {Name}", playlist.Name);
        
        using var connection = await GetConnectionAsync();
        
        await connection.ExecuteAsync(UpsertPlaylistSql, CreatePlaylistParameters(playlist));
    }

    public async Task<List<Playlist>> GetPlaylistsAsync()
    {
        _logger.LogInformation("获取播放列表");
        
        using var connection = await GetConnectionAsync();
        
        var playlists = await connection.QueryAsync<dynamic>(@"
            SELECT Id, Name, Description, Tags, Type, IsLocal, CreatedDate, ModifiedDate, Source, RemoteId, IsSnapshot
            FROM Playlists
            ORDER BY CreatedDate DESC");
            
        return playlists.Select(p => new Playlist
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            Tags = ParseTags(p.Tags as string),
            Type = (PlaylistType)p.Type,
            IsLocal = p.IsLocal == 1,
            CreatedDate = DateTime.Parse(p.CreatedDate),
            ModifiedDate = DateTime.Parse(p.ModifiedDate),
            Source = p.Source as string ?? string.Empty,
            RemoteId = p.RemoteId as string ?? string.Empty,
            IsSnapshot = Convert.ToInt64(p.IsSnapshot) == 1
        }).ToList();
    }

    public async Task DeletePlaylistAsync(string playlistId)
    {
        _logger.LogInformation("删除播放列表: {Id}", playlistId);
        
        using var connection = await GetConnectionAsync();
        
        await connection.ExecuteAsync("DELETE FROM Playlists WHERE Id = @Id", new { Id = playlistId });
        // 播放列表项目会通过外键自动删除
    }

    public async Task SaveSettingsAsync(
        UserSettings settings,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("保存设置");
        
        using var connection = await GetConnectionAsync(cancellationToken);
        
        var settingsJson = JsonSerializer.Serialize(settings);
        await connection.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO Settings (Key, Value) VALUES (@Key, @Value)
              ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
            new { Key = "UserSettings", Value = settingsJson },
            cancellationToken: cancellationToken));
    }

    public async Task<UserSettings?> GetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("获取设置");
        
        using var connection = await GetConnectionAsync(cancellationToken);
        
        var result = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT Value FROM Settings WHERE Key = @Key",
            new { Key = "UserSettings" },
            cancellationToken: cancellationToken));
            
        if (string.IsNullOrEmpty(result))
        {
            return new UserSettings(); // 返回默认设置
        }
        
        try
        {
            return JsonSerializer.Deserialize<UserSettings>(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析设置失败");
            return new UserSettings();
        }
    }

    public async Task SaveMusicSourcesAsync(
        List<MusicSource> sources,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("保存音源");
        
        using var connection = await GetConnectionAsync(cancellationToken);
        using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var parameters = new Dictionary<string, object>();
            for (var index = 0; index < sources.Count; index++)
                parameters[$"@Id{index}"] = sources[index].Id;
            var idList = string.Join(", ", Enumerable.Range(0, sources.Count).Select(index => $"@Id{index}"));
            await connection.ExecuteAsync(new CommandDefinition(
                sources.Count == 0
                    ? "DELETE FROM MusicSources"
                    : $"DELETE FROM MusicSources WHERE Id NOT IN ({idList})",
                parameters,
                transaction,
                cancellationToken: cancellationToken));

            foreach (var source in sources)
            {
                await connection.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO MusicSources
                (Id, Name, Version, ScriptUrl, ScriptContent, IsEnabled, Priority, AddedDate, LastUpdateDate, Type, IsDefault, TrustState, ContentSha256)
                VALUES (@Id, @Name, @Version, @ScriptUrl, @ScriptContent, @IsEnabled, @Priority, @AddedDate, @LastUpdateDate, @Type, @IsDefault, @TrustState, @ContentSha256)
                ON CONFLICT(Id) DO UPDATE SET
                    Name = excluded.Name,
                    Version = excluded.Version,
                    ScriptUrl = excluded.ScriptUrl,
                    ScriptContent = excluded.ScriptContent,
                    IsEnabled = excluded.IsEnabled,
                    Priority = excluded.Priority,
                    AddedDate = excluded.AddedDate,
                    LastUpdateDate = excluded.LastUpdateDate,
                    Type = excluded.Type,
                    IsDefault = excluded.IsDefault,
                    TrustState = excluded.TrustState,
                    ContentSha256 = excluded.ContentSha256",
                    new
                    {
                        Id = source.Id,
                        Name = source.Name,
                        Version = source.Version,
                        ScriptUrl = source.ScriptUrl,
                        ScriptContent = source.ScriptContent,
                        IsEnabled = source.IsEnabled ? 1 : 0,
                        IsDefault = source.IsDefault ? 1 : 0,
                        Priority = source.Priority,
                        AddedDate = source.AddedDate.ToString("o"),
                        LastUpdateDate = source.LastUpdateDate.ToString("o"),
                        Type = source.Type,
                        TrustState = source.TrustState,
                        source.ContentSha256
                    }, transaction, cancellationToken: cancellationToken));
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<List<MusicSource>> GetMusicSourcesAsync()
    {
        _logger.LogInformation("获取音源");
        
        using var connection = await GetConnectionAsync();
        
        var sources = await connection.QueryAsync<dynamic>(@"
            SELECT Id, Name, Version, ScriptUrl, ScriptContent, IsEnabled, IsDefault, Priority, AddedDate, LastUpdateDate, Type, TrustState, ContentSha256
            FROM MusicSources
            ORDER BY Priority ASC");
            
        var result = new List<MusicSource>();
        foreach (var s in sources)
        {
                        DateTime addedDate = DateTime.Now;
            DateTime lastUpdateDate = DateTime.Now;
            
            
            
            if (!string.IsNullOrEmpty(s.AddedDate)) { if (DateTime.TryParse(s.AddedDate, out DateTime tempAddedDate)) { addedDate = tempAddedDate; } }if (!string.IsNullOrEmpty(s.LastUpdateDate)) { if (DateTime.TryParse(s.LastUpdateDate, out DateTime tempLastUpdateDate)) { lastUpdateDate = tempLastUpdateDate; } }result.Add(new MusicSource
            {
                Id = s.Id,
                Name = s.Name,
                Version = s.Version,
                ScriptUrl = s.ScriptUrl,
                ScriptContent = s.ScriptContent,
                IsEnabled = Convert.ToInt64(s.IsEnabled) == 1,
                IsDefault = Convert.ToInt64(s.IsDefault) == 1,
                Priority = Convert.ToInt32(s.Priority),
                AddedDate = addedDate,
                LastUpdateDate = lastUpdateDate,
                Type = (SourceType)Convert.ToInt32(s.Type),
                TrustState = (ScriptTrustState)Convert.ToInt32(s.TrustState),
                ContentSha256 = s.ContentSha256
            });
        }
        
        return result;
    }
 
    public async Task SavePlaylistItemAsync(string playlistId, MusicInfo music, int orderNum)
    {
        _logger.LogInformation("保存播放列表项目: {PlaylistId} - {Title}", playlistId, music.Title);
        
        using var connection = await GetConnectionAsync();
        
        var musicJson = JsonSerializer.Serialize(music);
        var itemId = Guid.NewGuid().ToString();
        
        await connection.ExecuteAsync(@"
            INSERT INTO PlaylistItems
            (Id, PlaylistId, MusicData, MusicId, MusicSource, OrderNum, AddedDate)
            VALUES (@Id, @PlaylistId, @MusicData, @MusicId, @MusicSource, @OrderNum, @AddedDate)
            ON CONFLICT(PlaylistId, MusicId, MusicSource) DO UPDATE SET
                MusicData = excluded.MusicData,
                OrderNum = excluded.OrderNum,
                AddedDate = excluded.AddedDate",
            new
            {
                Id = itemId,
                PlaylistId = playlistId,
                MusicData = musicJson,
                MusicId = string.IsNullOrEmpty(music.Id) ? music.PlayUrl ?? itemId : music.Id,
                MusicSource = string.IsNullOrEmpty(music.Source) ? "Local" : music.Source,
                OrderNum = orderNum,
                AddedDate = DateTime.Now
            });
        }
        
        public async Task<List<MusicInfo>> GetPlaylistItemsAsync(string playlistId)
        {
            _logger.LogInformation("获取播放列表项目: {PlaylistId}", playlistId);
            
            using var connection = await GetConnectionAsync();
        
        var items = await connection.QueryAsync<string>(@"
            SELECT MusicData 
            FROM PlaylistItems 
            WHERE PlaylistId = @PlaylistId 
            ORDER BY OrderNum ASC",
            new { PlaylistId = playlistId });
            
        var result = new List<MusicInfo>();
        foreach (var musicJson in items)
        {
            try
            {
                var music = JsonSerializer.Deserialize<MusicInfo>(musicJson);
                if (music != null)
                {
                    result.Add(music);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析播放列表项目失败");
            }
        }
        
        return result;
    }
    
    public async Task ClearPlaylistItemsAsync(string playlistId)
    {
        _logger.LogInformation("清空播放列表项目: {PlaylistId}", playlistId);
        
        using var connection = await GetConnectionAsync();
        
        await connection.ExecuteAsync("DELETE FROM PlaylistItems WHERE PlaylistId = @PlaylistId", new { PlaylistId = playlistId });
    }

    // ============ 收藏歌单（本地/在线，V1.3） ============

    public async Task<List<Playlist>> GetFavoritePlaylistsAsync(bool local)
    {
        _logger.LogInformation("获取收藏歌单: {IsLocal}", local ? "本地" : "在线");
        using var connection = await GetConnectionAsync();
        var rows = await connection.QueryAsync<dynamic>(@"
            SELECT Id, Name, Description, Tags, Type, IsLocal, CreatedDate, ModifiedDate, Source, RemoteId, IsSnapshot
            FROM Playlists
            WHERE IsLocal = @IsLocal
            ORDER BY Type ASC, CreatedDate ASC",
            new { IsLocal = local ? 1 : 0 });

        return rows.Select(p => new Playlist
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            Tags = ParseTags(p.Tags as string),
            Type = (PlaylistType)p.Type,
            IsLocal = p.IsLocal == 1,
            CreatedDate = DateTime.TryParse(p.CreatedDate as string, out var cd) ? cd : DateTime.Now,
            ModifiedDate = DateTime.TryParse(p.ModifiedDate as string, out var md) ? md : DateTime.Now,
            Source = p.Source as string ?? string.Empty,
            RemoteId = p.RemoteId as string ?? string.Empty,
            IsSnapshot = Convert.ToInt64(p.IsSnapshot) == 1
        }).ToList();
    }

    public async Task<Playlist?> GetOrCreateDefaultPlaylistAsync(bool local)
    {
        using var connection = await GetConnectionAsync();
        var name = local ? "我的本地歌曲" : "默认收藏";
        var existing = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT Id, Name, Description, Tags, Type, IsLocal, CreatedDate, ModifiedDate, Source, RemoteId, IsSnapshot
            FROM Playlists
            WHERE IsLocal = @IsLocal AND Type = @Type
            ORDER BY CreatedDate ASC LIMIT 1",
            new { IsLocal = local ? 1 : 0, Type = (int)PlaylistType.System });
        if (existing != null)
        {
            return new Playlist
            {
                Id = existing.Id,
                Name = existing.Name,
                Description = existing.Description,
                Tags = ParseTags(existing.Tags as string),
                Type = (PlaylistType)existing.Type,
                IsLocal = existing.IsLocal == 1,
                CreatedDate = DateTime.TryParse(existing.CreatedDate as string, out var cd) ? cd : DateTime.Now,
                ModifiedDate = DateTime.TryParse(existing.ModifiedDate as string, out var md) ? md : DateTime.Now,
                Source = existing.Source as string ?? string.Empty,
                RemoteId = existing.RemoteId as string ?? string.Empty,
                IsSnapshot = Convert.ToInt64(existing.IsSnapshot) == 1
            };
        }
        var created = new Playlist
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Description = local ? "本地音乐默认歌单" : "在线收藏默认歌单",
            Type = PlaylistType.System,
            IsLocal = local
        };
        await SavePlaylistAsync(created);
        return created;
    }

    public async Task<PlaylistSongAddition> AddSongsToPlaylistAsync(
        string playlistId,
        IEnumerable<MusicInfo> songs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(songs);
        if (string.IsNullOrWhiteSpace(playlistId)) return default;

        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await GetConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = (await connection.QueryAsync<PlaylistItemIndex>(new CommandDefinition(@"
                SELECT MusicId, MusicSource, OrderNum
                FROM PlaylistItems
                WHERE PlaylistId = @PlaylistId",
                new { PlaylistId = playlistId },
                transaction,
                cancellationToken: cancellationToken))).AsList();
            var seen = new HashSet<(string MusicId, string MusicSource)>();
            var nextOrder = 0L;
            foreach (var item in existing)
            {
                seen.Add((item.MusicId, item.MusicSource));
                nextOrder = Math.Max(nextOrder, item.OrderNum + 1);
            }

            var batch = new List<PlaylistItemWrite>(PlaylistItemBatchSize);
            var added = 0;
            var skipped = 0;
            var addedDate = DateTime.Now.ToString("o");
            foreach (var music in songs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (music is null)
                {
                    skipped++;
                    continue;
                }

                var musicId = string.IsNullOrEmpty(music.Id)
                    ? music.PlayUrl ?? Guid.NewGuid().ToString()
                    : music.Id;
                var musicSource = string.IsNullOrEmpty(music.Source) ? "Local" : music.Source;
                if (!seen.Add((musicId, musicSource)))
                {
                    skipped++;
                    continue;
                }

                batch.Add(new PlaylistItemWrite(
                    Guid.NewGuid().ToString(),
                    playlistId,
                    JsonSerializer.Serialize(music),
                    musicId,
                    musicSource,
                    nextOrder++,
                    addedDate));
                added++;

                if (batch.Count == PlaylistItemBatchSize)
                {
                    await WritePlaylistItemBatchAsync(connection, transaction, batch, cancellationToken);
                    batch.Clear();
                }
            }

            if (batch.Count != 0)
                await WritePlaylistItemBatchAsync(connection, transaction, batch, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation(
                "Added {AddedCount} songs to playlist {PlaylistId}; skipped {SkippedCount} duplicates",
                added,
                playlistId,
                skipped);
            return new PlaylistSongAddition(added, skipped);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                _logger.LogError(rollbackException, "Failed to roll back playlist addition {PlaylistId}", playlistId);
            }
            throw;
        }
    }

    public async Task RemoveSongFromPlaylistAsync(string playlistId, MusicInfo music)
    {
        if (music == null || string.IsNullOrEmpty(playlistId)) return;
        _logger.LogInformation("从歌单移除: {Title}", music.Title);
        using var connection = await GetConnectionAsync();
        var musicId = string.IsNullOrEmpty(music.Id) ? music.PlayUrl ?? string.Empty : music.Id;
        var musicSource = string.IsNullOrEmpty(music.Source) ? "Local" : music.Source;
        await connection.ExecuteAsync(@"
            DELETE FROM PlaylistItems
            WHERE PlaylistId = @PlaylistId AND MusicId = @MusicId AND MusicSource = @MusicSource",
            new { PlaylistId = playlistId, MusicId = musicId, MusicSource = musicSource });
    }

    public async Task RenamePlaylistAsync(string playlistId, string newName)
    {
        if (string.IsNullOrEmpty(playlistId) || string.IsNullOrWhiteSpace(newName)) return;
        _logger.LogInformation("重命名歌单: {PlaylistId} -> {NewName}", playlistId, newName);
        using var connection = await GetConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE Playlists SET Name = @Name, ModifiedDate = @ModifiedDate WHERE Id = @Id",

            new { Name = newName.Trim(), ModifiedDate = DateTime.Now.ToString("o"), Id = playlistId });
    }

    // ============ 平台账号登录态（DPAPI 加密存储） ============

    public async Task SaveAccountAsync(AccountInfo account)
    {
        if (account == null || string.IsNullOrEmpty(account.Platform)) return;
        _logger.LogInformation("保存账号登录态: {Platform}", account.Platform);
        using var connection = await GetConnectionAsync();
        // 明文字段加密后再落盘；AuthData 为空时加密空串，避免数据库残留明文
        var encrypted = SecureStore.Protect(account.AuthData ?? string.Empty);
        await connection.ExecuteAsync(@"
            INSERT INTO Accounts (Platform, DisplayName, Uid, EncryptedAuth, LoginTime, ExpireTime)
            VALUES (@Platform, @DisplayName, @Uid, @EncryptedAuth, @LoginTime, @ExpireTime)
            ON CONFLICT(Platform) DO UPDATE SET
                DisplayName = excluded.DisplayName,
                Uid = excluded.Uid,
                EncryptedAuth = excluded.EncryptedAuth,
                LoginTime = excluded.LoginTime,
                ExpireTime = excluded.ExpireTime",
            new
            {
                Platform = account.Platform,
                DisplayName = account.DisplayName,
                Uid = account.Uid,
                EncryptedAuth = encrypted,
                LoginTime = account.LoginTime.ToString("o"),
                ExpireTime = account.ExpireTime.ToString("o")
            });
    }

    public async Task<AccountInfo?> GetAccountAsync(string platform)
    {
        if (string.IsNullOrEmpty(platform)) return null;
        using var connection = await GetConnectionAsync();
        var row = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT Platform, DisplayName, Uid, EncryptedAuth, LoginTime, ExpireTime
            FROM Accounts WHERE Platform = @Platform",
            new { Platform = platform });
        if (row == null) return null;
        return new AccountInfo
        {
            Platform = row.Platform,
            DisplayName = row.DisplayName as string ?? string.Empty,
            Uid = row.Uid as string ?? string.Empty,
            EncryptedAuth = row.EncryptedAuth as string,
            AuthData = SecureStore.Unprotect(row.EncryptedAuth as string) ?? string.Empty,
            LoginTime = DateTime.TryParse(row.LoginTime as string, out var lt) ? lt : DateTime.Now,
            ExpireTime = DateTime.TryParse(row.ExpireTime as string, out var et) ? et : DateTime.Now
        };
    }

    public async Task DeleteAccountAsync(string platform)
    {
        if (string.IsNullOrEmpty(platform)) return;
        _logger.LogInformation("删除账号登录态: {Platform}", platform);
        using var connection = await GetConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM Accounts WHERE Platform = @Platform", new { Platform = platform });
    }

    // ============ Settings 表通用读写 ============

    /// <summary>读取 Settings 表任意键的原始值；无该键返回 null</summary>
    public async Task<string?> GetSettingValueAsync(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        using var connection = await GetConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT Value FROM Settings WHERE Key = @Key", new { Key = key });
    }

    /// <summary>写入 Settings 表任意键的原始值；value 为 null 时删除该键</summary>
    public async Task SetSettingValueAsync(string key, string? value)
    {
        if (string.IsNullOrEmpty(key)) return;
        using var connection = await GetConnectionAsync();
        if (value == null)
        {
            await connection.ExecuteAsync("DELETE FROM Settings WHERE Key = @Key", new { Key = key });
        }
        else
        {
            await connection.ExecuteAsync(
                @"INSERT INTO Settings (Key, Value) VALUES (@Key, @Value)
                  ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
                new { Key = key, Value = value });
        }
    }
}



