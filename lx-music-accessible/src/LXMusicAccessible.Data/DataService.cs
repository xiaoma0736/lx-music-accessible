using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Dapper;
using LXMusicAccessible.Models;
using System.Text.Json;

namespace LXMusicAccessible.Data;

/// <summary>
/// 数据库初始化器
/// </summary>
public class DatabaseInitializer
{
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly string _dataDir;
    private const string DbFileName = "lxmusic.db";

    public DatabaseInitializer(ILogger<DatabaseInitializer> logger, string dataDir)
    {
        _logger = logger;
        _dataDir = dataDir;
    }

    public async Task InitializeAsync()
    {
        try
        {
            var appDataPath = _dataDir;

            if (!Directory.Exists(appDataPath))
            {
                Directory.CreateDirectory(appDataPath);
            }

            var dbFilePath = Path.Combine(appDataPath, DbFileName);
            var connectionString = $"Data Source={dbFilePath}";

            _logger.LogInformation("数据库路径: {Path}", dbFilePath);

            using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            // 创建表
            await CreateTablesAsync(connection);

            // 迁移：旧版本 MusicSources 表补充 IsDefault 列
            var cols = await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('MusicSources')");
            if (!cols.Any(n => string.Equals(n, "IsDefault", StringComparison.OrdinalIgnoreCase)))
            {
                await connection.ExecuteAsync("ALTER TABLE MusicSources ADD COLUMN IsDefault INTEGER NOT NULL DEFAULT 0");
                _logger.LogInformation("已为 MusicSources 表添加 IsDefault 列");
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
            await connection.ExecuteAsync(@"
                CREATE UNIQUE INDEX IF NOT EXISTS IX_PlaylistItems_UniqueSong
                ON PlaylistItems(PlaylistId, MusicId, MusicSource)");

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
                Type INTEGER
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
            catch { /* 跳过无法解析的历史收藏 */ }
        }
        if (order > 0)
            System.Diagnostics.Debug.WriteLine($"已迁移 {order} 条旧收藏到在线默认收藏");
    }
}

/// <summary>
/// 数据服务
/// </summary>
public class DataService : IDataService
{
    private readonly ILogger<DataService> _logger;
    private readonly string _dataDir;
    private const string DbFileName = "lxmusic.db";

    public DataService(ILogger<DataService> logger, string dataDir)
    {
        _logger = logger;
        _dataDir = dataDir;
    }

    private string GetConnectionString()
    {
        var dbFilePath = Path.Combine(_dataDir, DbFileName);
        return $"Data Source={dbFilePath}";
    }

    private async Task<SqliteConnection> GetConnectionAsync()
    {
        var connection = new SqliteConnection(GetConnectionString());
        await connection.OpenAsync();
        return connection;
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
    private static string SerializeTags(List<string> tags)
    {
        try { return JsonSerializer.Serialize(tags ?? new List<string>()); }
        catch { return string.Empty; }
    }
    public async Task SavePlaylistAsync(Playlist playlist)
    {
        _logger.LogInformation("保存播放列表: {Name}", playlist.Name);
        
        using var connection = await GetConnectionAsync();
        
        await connection.ExecuteAsync(@"
            INSERT OR REPLACE INTO Playlists 
            (Id, Name, Description, Tags, Type, IsLocal, CreatedDate, ModifiedDate, Source, RemoteId, IsSnapshot)
            VALUES (@Id, @Name, @Description, @Tags, @Type, @IsLocal, @CreatedDate, @ModifiedDate, @Source, @RemoteId, @IsSnapshot)",
            new
            {
                Id = playlist.Id,
                Name = playlist.Name,
                Description = playlist.Description,
                Tags = SerializeTags(playlist.Tags),
                Type = playlist.Type,
                IsLocal = playlist.IsLocal ? 1 : 0,
                CreatedDate = playlist.CreatedDate.ToString("o"),
                ModifiedDate = playlist.ModifiedDate.ToString("o"),
                Source = string.IsNullOrEmpty(playlist.Source) ? null : playlist.Source,
                RemoteId = string.IsNullOrEmpty(playlist.RemoteId) ? null : playlist.RemoteId,
                IsSnapshot = playlist.IsSnapshot ? 1 : 0
            });
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

    public async Task SaveSettingsAsync(UserSettings settings)
    {
        _logger.LogInformation("保存设置");
        
        using var connection = await GetConnectionAsync();
        
        var settingsJson = JsonSerializer.Serialize(settings);
        await connection.ExecuteAsync(
            "INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@Key, @Value)",
            new { Key = "UserSettings", Value = settingsJson }
        );
    }

    public async Task<UserSettings?> GetSettingsAsync()
    {
        _logger.LogInformation("获取设置");
        
        using var connection = await GetConnectionAsync();
        
        var result = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT Value FROM Settings WHERE Key = @Key",
            new { Key = "UserSettings" });
            
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

    public async Task SaveMusicSourcesAsync(List<MusicSource> sources)
    {
        _logger.LogInformation("保存音源");
        
        using var connection = await GetConnectionAsync();
        
        foreach (var source in sources)
        {
            await connection.ExecuteAsync(@"
                INSERT OR REPLACE INTO MusicSources 
                (Id, Name, Version, ScriptUrl, ScriptContent, IsEnabled, Priority, AddedDate, LastUpdateDate, Type, IsDefault)
                VALUES (@Id, @Name, @Version, @ScriptUrl, @ScriptContent, @IsEnabled, @Priority, @AddedDate, @LastUpdateDate, @Type, @IsDefault)",
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
                    Type = source.Type
                });
        }
    }

    public async Task<List<MusicSource>> GetMusicSourcesAsync()
    {
        _logger.LogInformation("获取音源");
        
        using var connection = await GetConnectionAsync();
        
        var sources = await connection.QueryAsync<dynamic>(@"
            SELECT Id, Name, Version, ScriptUrl, ScriptContent, IsEnabled, IsDefault, Priority, AddedDate, LastUpdateDate, Type
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
                Type = (SourceType)Convert.ToInt32(s.Type)
            });
        }
        
        return result;
    }
 
    public async Task DeleteMusicSourcesAsync(List<string> ids)
    {
        if (ids == null || ids.Count == 0) return;
        _logger.LogInformation("删除 {Count} 条音源", ids.Count);
        using var connection = await GetConnectionAsync();
        foreach (var id in ids)
        {
            await connection.ExecuteAsync("DELETE FROM MusicSources WHERE Id = @Id", new { Id = id });
        }
    }

    public async Task SavePlayHistoryAsync(MusicInfo music)
    {
        _logger.LogInformation("保存播放历史: {Title}", music.Title);
        
        using var connection = await GetConnectionAsync();
        
        var musicJson = JsonSerializer.Serialize(music);
        await connection.ExecuteAsync(@"
            INSERT INTO PlayHistory (MusicData, PlayedDate)
            VALUES (@MusicData, @PlayedDate)",
            new
            {
                MusicData = musicJson,
                PlayedDate = DateTime.Now.ToString("o")
            });
            
        // 保持历史记录在合理范围内
        await connection.ExecuteAsync(@"
            DELETE FROM PlayHistory 
            WHERE Id NOT IN (
                SELECT Id FROM PlayHistory 
                ORDER BY PlayedDate DESC 
                LIMIT 100
            )");
    }

    public async Task<List<MusicInfo>> GetPlayHistoryAsync(int count = 50)
    {
        _logger.LogInformation("获取播放历史");
        
        using var connection = await GetConnectionAsync();
        
        var history = await connection.QueryAsync<string>(@"
            SELECT MusicData 
            FROM PlayHistory 
            ORDER BY PlayedDate DESC 
            LIMIT @Count",
            new { Count = count });
            
        var result = new List<MusicInfo>();
        foreach (var musicJson in history)
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
                _logger.LogWarning(ex, "解析播放历史失败");
            }
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
            INSERT OR REPLACE INTO PlaylistItems 
            (Id, PlaylistId, MusicData, OrderNum, AddedDate)
            VALUES (@Id, @PlaylistId, @MusicData, @OrderNum, @AddedDate)",
            new
            {
                Id = itemId,
                PlaylistId = playlistId,
                MusicData = musicJson,
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
    
    public async Task DeletePlaylistItemAsync(string itemId)
    {
        _logger.LogInformation("删除播放列表项目: {ItemId}", itemId);
        
        using var connection = await GetConnectionAsync();
        
        await connection.ExecuteAsync("DELETE FROM PlaylistItems WHERE Id = @Id", new { Id = itemId });
    }
    
    public async Task ClearPlaylistItemsAsync(string playlistId)
    {
        _logger.LogInformation("清空播放列表项目: {PlaylistId}", playlistId);
        
        using var connection = await GetConnectionAsync();
        
        await connection.ExecuteAsync("DELETE FROM PlaylistItems WHERE PlaylistId = @PlaylistId", new { PlaylistId = playlistId });
    }

    // ============ 本地收藏（网络歌曲） ============

    public async Task AddFavoriteSongsAsync(IEnumerable<MusicInfo> songs)
    {
        var list = songs?.Where(s => s != null).ToList() ?? new List<MusicInfo>();
        if (list.Count == 0) return;
        _logger.LogInformation("添加 {Count} 首歌曲到收藏", list.Count);

        using var connection = await GetConnectionAsync();
        foreach (var music in list)
        {
            if (string.IsNullOrEmpty(music.Id) || string.IsNullOrEmpty(music.Source)) continue;
            var musicJson = JsonSerializer.Serialize(music);
            await connection.ExecuteAsync(@"
                INSERT INTO FavoriteSongs (MusicId, MusicSource, MusicData, OrderNum, AddedDate)
                VALUES (@MusicId, @MusicSource, @MusicData, @OrderNum, @AddedDate)
                ON CONFLICT(MusicId, MusicSource) DO UPDATE SET MusicData = @MusicData",
                new
                {
                    MusicId = music.Id,
                    MusicSource = music.Source,
                    MusicData = musicJson,
                    OrderNum = list.IndexOf(music),
                    AddedDate = DateTime.Now.ToString("o")
                });
        }
    }

    public async Task<List<MusicInfo>> GetFavoriteSongsAsync()
    {
        _logger.LogInformation("获取收藏歌曲");
        using var connection = await GetConnectionAsync();

        var items = await connection.QueryAsync<string>(@"
            SELECT MusicData
            FROM FavoriteSongs
            ORDER BY AddedDate DESC, OrderNum ASC");

        var result = new List<MusicInfo>();
        foreach (var musicJson in items)
        {
            try
            {
                var music = JsonSerializer.Deserialize<MusicInfo>(musicJson);
                if (music != null) result.Add(music);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析收藏歌曲失败");
            }
        }
        return result;
    }

    public async Task RemoveFavoriteSongAsync(MusicInfo music)
    {
        if (music == null) return;
        _logger.LogInformation("从收藏移除: {Title}", music.Title);
        using var connection = await GetConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM FavoriteSongs WHERE MusicId = @MusicId AND MusicSource = @MusicSource",
            new { MusicId = music.Id, MusicSource = music.Source });
    }

    public async Task ClearFavoriteSongsAsync()
    {
        _logger.LogInformation("清空收藏");
        using var connection = await GetConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM FavoriteSongs");
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

    public async Task AddSongsToPlaylistAsync(string playlistId, IEnumerable<MusicInfo> songs)
    {
        var list = songs?.Where(s => s != null).ToList() ?? new List<MusicInfo>();
        if (list.Count == 0 || string.IsNullOrEmpty(playlistId)) return;
        _logger.LogInformation("添加 {Count} 首歌曲到歌单 {PlaylistId}", list.Count, playlistId);

        using var connection = await GetConnectionAsync();
        int order = 0;
        foreach (var music in list)
        {
            var musicId = string.IsNullOrEmpty(music.Id) ? music.PlayUrl ?? Guid.NewGuid().ToString() : music.Id;
            var musicSource = string.IsNullOrEmpty(music.Source) ? "Local" : music.Source;
            var musicJson = JsonSerializer.Serialize(music);
            await connection.ExecuteAsync(@"
                INSERT INTO PlaylistItems (Id, PlaylistId, MusicData, MusicId, MusicSource, OrderNum, AddedDate)
                VALUES (@Id, @PlaylistId, @MusicData, @MusicId, @MusicSource, @OrderNum, @AddedDate)
                ON CONFLICT(PlaylistId, MusicId, MusicSource) DO UPDATE SET MusicData = @MusicData",
                new
                {
                    Id = Guid.NewGuid().ToString(),
                    PlaylistId = playlistId,
                    MusicData = musicJson,
                    MusicId = musicId,
                    MusicSource = musicSource,
                    OrderNum = order++,
                    AddedDate = DateTime.Now.ToString("o")
                });
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
            INSERT OR REPLACE INTO Accounts (Platform, DisplayName, Uid, EncryptedAuth, LoginTime, ExpireTime)
            VALUES (@Platform, @DisplayName, @Uid, @EncryptedAuth, @LoginTime, @ExpireTime)",
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
                "INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@Key, @Value)",
                new { Key = key, Value = value });
        }
    }
}



