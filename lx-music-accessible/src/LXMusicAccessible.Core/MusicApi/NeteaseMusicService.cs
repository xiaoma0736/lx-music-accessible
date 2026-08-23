using System.Text.Json;
using System.Text.Json.Serialization;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 网易云音乐API服务 — 使用 weapi/eapi/linuxapi 加密
/// 参考原版 lx-music-desktop 实现
/// </summary>
public class NeteaseMusicService : IMusicApi
{
    private readonly MusicHttpClient _httpClient;
    private readonly ILogger<NeteaseMusicService> _logger;
    private const string BaseUrl = "https://music.163.com";
    private const string WeapiBaseUrl = "https://music.163.com/weapi";
    private const int SuccessCode = 200;

    public string SourceId => "netease";
    public string SourceName => "网易云音乐";

    private static readonly Dictionary<string, string> DefaultHeaders = new()
    {
        ["User-Agent"] = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/60.0.3112.90 Safari/537.36",
        ["Referer"] = "https://music.163.com/",
        ["Origin"] = "https://music.163.com"
    };

    public NeteaseMusicService(MusicHttpClient httpClient, ILogger<NeteaseMusicService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // ========== 搜索 ==========

    public async Task<PagedResult<MusicInfo>> SearchAsync(string keyword, int page = 1, int pageSize = 30, string searchType = "song")
    {
        try
        {
            _logger.LogInformation("搜索音乐: {Keyword}, 页码: {Page}", keyword, page);

            if (string.Equals(searchType, "album", StringComparison.OrdinalIgnoreCase))
                return await SearchAlbumsAsync(keyword, page, pageSize);

            var result = await _httpClient.PostEapiAsync<NeteaseEapiSearchResult>(
                "/api/search/song/list/page",
                new
                {
                    keyword,
                    needCorrect = "1",
                    channel = "typing",
                    offset = pageSize * (page - 1),
                    scene = "normal",
                    total = page == 1,
                    limit = pageSize
                },
                DefaultHeaders);

            if (result?.Data?.Resources == null)
                return PagedResult<MusicInfo>.Empty(page, pageSize);

            var musicList = result.Data.Resources
                .Where(r => r.BaseInfo?.SimpleSongData != null)
                .Select(r => r.BaseInfo!.SimpleSongData!)
                .Select(song => new MusicInfo
                {
                    Id = song.Id.ToString(),
                    Title = song.Name ?? "未知歌曲",
                    Artist = song.GetArtistNames(),
                    Album = song.Album?.Name ?? "未知专辑",
                    Duration = song.GetDurationString(),
                    Source = "netease",
                    CoverUrl = song.Album?.PicUrl ?? "",
                    Quality = GetQualityLevel(song.Privilege)
                }).ToList();

            return new PagedResult<MusicInfo>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = result.Data.TotalCount,
                Items = musicList
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索音乐失败: {Keyword}", keyword);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    // ========== 专辑搜索 / 专辑详情 ==========

    /// <summary>专辑搜索：eapi /api/cloudsearch/pc type=10，返回 result.albums[]</summary>
    private async Task<PagedResult<MusicInfo>> SearchAlbumsAsync(string keyword, int page, int pageSize)
    {
        try
        {
            var result = await _httpClient.PostEapiAsync<JsonElement>(
                "/api/cloudsearch/pc",
                new { s = keyword, type = 10, limit = pageSize, offset = pageSize * (page - 1) },
                DefaultHeaders);

            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("result", out var res) || res.ValueKind != JsonValueKind.Object)
                return PagedResult<MusicInfo>.Empty(page, pageSize);

            int total = 0;
            if (res.TryGetProperty("albumCount", out var ac)) total = ac.GetInt32();

            var list = new List<MusicInfo>();
            if (res.TryGetProperty("albums", out var albums) && albums.ValueKind == JsonValueKind.Array)
            {
                foreach (var alb in albums.EnumerateArray())
                {
                    if (alb.ValueKind != JsonValueKind.Object) continue;
                    var id = JsonHelpers.GetString(alb, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var name = JsonHelpers.GetString(alb, "name") ?? "未知专辑";
                    list.Add(JsonHelpers.BuildAlbumRow(id!, name, JsonHelpers.GetAlbumArtist(alb), "netease",
                        JsonHelpers.GetString(alb, "picUrl") ?? ""));
                }
            }
            return new PagedResult<MusicInfo>
            {
                CurrentPage = page, PageSize = pageSize,
                TotalItems = total > 0 ? total : list.Count,
                Items = list
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "网易云专辑搜索失败: {Keyword}", keyword);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    /// <summary>专辑详情：eapi /api/v1/album/{id}，返回 songs[]</summary>
    public async Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50)
    {
        try
        {
            if (album == null || string.IsNullOrEmpty(album.Id))
                return PagedResult<MusicInfo>.Empty(page, pageSize);

            var result = await _httpClient.PostEapiAsync<JsonElement>(
                $"/api/v1/album/{album.Id}",
                new { id = album.Id },
                DefaultHeaders);

            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                return PagedResult<MusicInfo>.Empty(page, pageSize);

            var list = new List<MusicInfo>();
            foreach (var item in songs.EnumerateArray())
            {
                var songId = JsonHelpers.GetString(item, "id");
                if (string.IsNullOrEmpty(songId)) continue;
                var title = JsonHelpers.GetString(item, "name") ?? "未知歌曲";
                var artist = "";
                if (item.TryGetProperty("ar", out var ar) && ar.ValueKind == JsonValueKind.Array)
                    artist = string.Join("、", ar.EnumerateArray().Select(a => JsonHelpers.GetString(a, "name") ?? "").Where(n => n.Length > 0));
                var albumName = "";
                string? cover = null;
                if (item.TryGetProperty("al", out var al) && al.ValueKind == JsonValueKind.Object)
                {
                    albumName = JsonHelpers.GetString(al, "name") ?? "";
                    cover = JsonHelpers.GetString(al, "picUrl");
                }
                var duration = "";
                if (JsonHelpers.GetString(item, "dt") is string dt && long.TryParse(dt, out var dms))
                    duration = TimeSpan.FromMilliseconds(dms).ToString(dms >= 3600000 ? @"hh\:mm\:ss" : @"mm\:ss");

                list.Add(new MusicInfo
                {
                    Id = songId!, Title = title, Artist = artist,
                    Album = albumName, Duration = duration,
                    Source = "netease", CoverUrl = cover ?? ""
                });
            }
            return new PagedResult<MusicInfo>
            {
                CurrentPage = page, PageSize = pageSize,
                TotalItems = list.Count, Items = list
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "网易云专辑详情失败: {Album}", album?.Title ?? "?");
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    // ========== 播放URL ==========

    /// <summary>获取支持的音质列表</summary>
    public List<string> GetSupportedQualities() => new() { "128k", "192k", "320k", "flac", "flac24bit" };

    /// <summary>获取播放 URL</summary>
    public async Task<string?> GetPlayUrlAsync(
        string musicId,
        string quality = "128k",
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("获取播放URL: {MusicId}", musicId);

            var br = quality switch
            {
                "flac24bit" => "999000",
                "flac" => "999000",
                "320k" => "320000",
                "192k" => "192000",
                _ => "128000"
            };

            var result = await _httpClient.PostWeapiAsync<NeteasePlayUrlResponse>(
                $"{WeapiBaseUrl}/song/enhance/player/url",
                new { ids = $"[{musicId}]", br },
                DefaultHeaders,
                cancellationToken);

            if (result?.Data != null && result.Data.Count > 0 && result.Data[0].Url != null)
                return result.Data[0].Url;

            _logger.LogWarning("播放URL为空: {MusicId}, code={Code}", musicId, result?.Code);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _logger.LogError(
                "Play URL lookup failed ({SourceId}, {MusicId}): {ErrorType}",
                SourceId,
                musicId,
                exception.GetType().Name);
            return null;
        }
    }

    // ========== 歌词 ==========

    public async Task<Lyric?> GetLyricAsync(MusicInfo music)
    {
        var musicId = music?.Id ?? "";
        try
        {
            var result = await _httpClient.PostWeapiAsync<NeteaseLyricResponse>(
                $"{WeapiBaseUrl}/song/lyric",
                new { id = musicId, lv = -1, kv = -1, tv = -1 },
                DefaultHeaders);

            if (result?.LrcData?.Lyric != null)
            {
                var parsed = ParseLrcContent(result.LrcData.Lyric, result.TranslatedData?.Lyric);
                if (parsed != null && parsed.Lines.Count > 0)
                    return parsed;
                // weapi 返回了内容但无有效时间戳（如纯元数据/纯音乐），继续回退公开接口
                _logger.LogWarning("网易云 weapi 歌词解析为空，回退公开接口: {MusicId}", musicId);
                return await GetLyricPublicFallbackAsync(musicId);
            }

            _logger.LogWarning("网易云 weapi 歌词为空，回退公开接口: {MusicId}", musicId);
            return await GetLyricPublicFallbackAsync(musicId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取歌词失败: {MusicId}", musicId);
            return await GetLyricPublicFallbackAsync(musicId);
        }
    }

    /// <summary>网易云公开接口歌词回退（weapi 不可用时使用）</summary>
    private async Task<Lyric?> GetLyricPublicFallbackAsync(string musicId)
    {
        try
        {
            var url = $"{BaseUrl}/api/song/lyric";
            var parameters = new Dictionary<string, string>
            {
                ["id"] = musicId, ["lv"] = "-1", ["kv"] = "-1", ["tv"] = "-1"
            };
            var response = await _httpClient.GetAsync(url, parameters, DefaultHeaders);
            var data = JsonSerializer.Deserialize<JsonElement>(response);
            if (data.TryGetProperty("lrc", out var lrc) && lrc.TryGetProperty("lyric", out var lyric) &&
                lyric.ValueKind == JsonValueKind.String)
            {
                string? tLyric = null;
                if (data.TryGetProperty("tlyric", out var tl) && tl.TryGetProperty("lyric", out var tlText) &&
                    tlText.ValueKind == JsonValueKind.String)
                    tLyric = tlText.GetString();
                return ParseLrcContent(lyric.GetString(), tLyric);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "网易云公开接口歌词回退失败: {MusicId}", musicId);
            return null;
        }
    }

    // ========== 排行榜 ==========

    public async Task<List<Ranking>> GetRankingsAsync()
    {
        try
        {
            var result = await _httpClient.PostWeapiAsync<NeteaseTopList>(
                $"{WeapiBaseUrl}/toplist",
                new { },
                DefaultHeaders);

            if (result?.List == null)
                return GetDefaultRankings();

            return result.List.Select(board => new Ranking
            {
                Id = $"netease_{board.Id}",
                Name = board.Name ?? "未知排行榜",
                Description = board.UpdateFrequency ?? "",
                Source = "netease",
                Type = MapRankingType(board.Name),
                UpdateDate = DateTimeOffset.FromUnixTimeMilliseconds(board.UpdateTime).DateTime
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取排行榜列表失败，使用内置列表");
            return GetDefaultRankings();
        }
    }

    public async Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50)
    {
        try
        {
            var result = await _httpClient.PostWeapiAsync<NeteasePlaylistDetailResp>(
                $"{WeapiBaseUrl}/v3/playlist/detail",
                new { id = rankingId, n = pageSize, p = page, s = 8 },
                DefaultHeaders);

            if (result?.Playlist?.Tracks == null)
                return PagedResult<MusicInfo>.Empty(page, pageSize);

            var musicList = result.Playlist.Tracks.Select(song => new MusicInfo
            {
                Id = song.Id.ToString(),
                Title = song.Name ?? "未知歌曲",
                Artist = song.GetArtistNames(),
                Album = song.Album?.Name ?? "未知专辑",
                Duration = song.GetDurationString(),
                Source = "netease",
                CoverUrl = song.Album?.PicUrl ?? "",
                Quality = GetQualityLevel(song.Privilege)
            }).ToList();

            return new PagedResult<MusicInfo>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = result.Playlist.TrackCount,
                Items = musicList
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取排行榜详情失败: {RankingId}", rankingId);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    // ========== 歌单分类 ==========

    public async Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync()
    {
        try
        {
            var result = await _httpClient.PostWeapiAsync<NeteaseCategoryResult>(
                $"{WeapiBaseUrl}/playlist/catalogue",
                new { },
                DefaultHeaders);

            if (result?.Sub == null || result.Categories == null)
                return GetDefaultCategories();

            var grouped = result.Sub
                .GroupBy(s => s.Category)
                .Select(g => new PlaylistCategory
                {
                    Id = g.Key.ToString(),
                    Name = result.Categories.TryGetValue(g.Key.ToString(), out var name) ? name : "其他",
                    Tags = g.Select(c => new PlaylistTag { Id = c.Name ?? "", Name = c.Name ?? "" }).ToList()
                })
                .Where(c => c.Tags.Count > 0)
                .ToList();

            grouped.Insert(0, new PlaylistCategory
            {
                Id = "all",
                Name = "全部歌单",
                Tags = new List<PlaylistTag> { new() { Id = "全部", Name = "全部" } }
            });

            return grouped;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取歌单分类失败，使用内置分类");
            return GetDefaultCategories();
        }
    }

    // ========== 歌单列表 ==========

    public async Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string categoryId, int page = 1, int pageSize = 30)
    {
        try
        {
            var result = await _httpClient.PostWeapiAsync<NeteasePlaylistResult>(
                $"{WeapiBaseUrl}/playlist/list",
                new { cat = categoryId, order = "hot", limit = pageSize, offset = pageSize * (page - 1), total = true },
                DefaultHeaders);

            if (result?.Playlists == null)
                return PagedResult<Playlist>.Empty(page, pageSize);

            var playlists = result.Playlists.Select(pl => new Playlist
            {
                Id = $"netease_{pl.Id}",
                Name = pl.Name ?? "未知歌单",
                Description = pl.Description ?? "",
                Tags = pl.Tags ?? new List<string>(),
                Type = PlaylistType.Recommend,
                CreatedDate = DateTime.Now,
                ModifiedDate = DateTime.Now
            }).ToList();

            return new PagedResult<Playlist>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = result.Total,
                Items = playlists
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取歌单列表失败: {Category}", categoryId);
            return PagedResult<Playlist>.Empty(page, pageSize);
        }
    }

    // ========== 歌单详情 ==========

    public async Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50)
    {
        try
        {
            var result = await _httpClient.PostLinuxApiAsync<NeteaseLinuxApiResp>(
                new
                {
                    method = "POST",
                    url = "https://music.163.com/api/v3/playlist/detail",
                    @params = new { id = playlistId, n = pageSize, s = 8 }
                },
                DefaultHeaders);

            if (result?.Playlist?.Tracks == null)
                return PagedResult<MusicInfo>.Empty(page, pageSize);

            var tracks = result.Playlist.Tracks;
            var start = (page - 1) * pageSize;
            var pageItems = tracks.Skip(start).Take(pageSize);

            var musicList = pageItems.Select(song => new MusicInfo
            {
                Id = song.Id.ToString(),
                Title = song.Name ?? "未知歌曲",
                Artist = song.GetArtistNames(),
                Album = song.Album?.Name ?? "未知专辑",
                Duration = song.GetDurationString(),
                Source = "netease",
                CoverUrl = song.Album?.PicUrl ?? "",
                Quality = GetQualityLevel(song.Privilege)
            }).ToList();

            return new PagedResult<MusicInfo>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = result.Playlist.TrackCount,
                Items = musicList
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取歌单详情失败: {PlaylistId}", playlistId);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    // ========== 辅助方法 ==========

    private static int GetQualityLevel(NeteasePrivilege? p)
    {
        if (p == null) return 128;
        if (p.MaxBrLevel == "hires") return 999;
        return p.Maxbr switch { 999000 => 999, 320000 => 320, 192000 => 192, _ => 128 };
    }

    private static RankingType MapRankingType(string? name)
    {
        if (name == null) return RankingType.Other;
        if (name.Contains("热歌")) return RankingType.Hot;
        if (name.Contains("新歌")) return RankingType.New;
        if (name.Contains("原创")) return RankingType.Original;
        if (name.Contains("飙升")) return RankingType.Rising;
        return RankingType.Other;
    }

    private static Lyric? ParseLrcContent(string? lrc, string? tLrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return null;
        var lines = new List<LyricLine>();
        var tMap = ParseTranslateLrc(tLrc);

        foreach (var line in lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^\[(\d{2}):(\d{2})(?:\.(\d+))?\](.*)");
            if (!m.Success) continue;
            var min = int.Parse(m.Groups[1].Value);
            var sec = int.Parse(m.Groups[2].Value);
            var ms = m.Groups[3].Success ? int.Parse(m.Groups[3].Value.PadRight(3, '0')[..3]) : 0;
            var idx = min * 60000 + sec * 1000 + ms;
            lines.Add(new LyricLine
            {
                Time = new TimeSpan(0, 0, min, sec, ms),
                Text = m.Groups[4].Value.Trim(),
                Translation = tMap.TryGetValue(idx, out var t) ? t : null
            });
        }
        return lines.Count > 0 ? new Lyric { Lines = lines } : null;
    }

    private static Dictionary<int, string> ParseTranslateLrc(string? tLrc)
    {
        var map = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(tLrc)) return map;
        foreach (var line in tLrc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^\[(\d{2}):(\d{2})(?:\.(\d+))?\](.*)");
            if (m.Success)
            {
                var idx = int.Parse(m.Groups[1].Value) * 60000 + int.Parse(m.Groups[2].Value) * 1000;
                map[idx] = m.Groups[4].Value.Trim();
            }
        }
        return map;
    }

    // ========== 备用数据 ==========

    private static List<PlaylistCategory> GetDefaultCategories()
    {
        return new List<PlaylistCategory>
        {
            new() { Id = "all", Name = "全部歌单", Tags = new() { new() { Id = "全部", Name = "全部" } } },
            new() { Id = "1", Name = "语种", Tags = new() { new() { Id = "华语", Name = "华语" }, new() { Id = "欧美", Name = "欧美" }, new() { Id = "日语", Name = "日语" }, new() { Id = "韩语", Name = "韩语" }, new() { Id = "粤语", Name = "粤语" } } },
            new() { Id = "2", Name = "风格", Tags = new() { new() { Id = "流行", Name = "流行" }, new() { Id = "摇滚", Name = "摇滚" }, new() { Id = "民谣", Name = "民谣" }, new() { Id = "电子", Name = "电子" }, new() { Id = "说唱", Name = "说唱" } } },
            new() { Id = "3", Name = "场景", Tags = new() { new() { Id = "清晨", Name = "清晨" }, new() { Id = "夜晚", Name = "夜晚" }, new() { Id = "学习", Name = "学习" }, new() { Id = "运动", Name = "运动" } } },
            new() { Id = "4", Name = "情感", Tags = new() { new() { Id = "怀旧", Name = "怀旧" }, new() { Id = "浪漫", Name = "浪漫" }, new() { Id = "伤感", Name = "伤感" }, new() { Id = "快乐", Name = "快乐" } } },
            new() { Id = "5", Name = "主题", Tags = new() { new() { Id = "影视原声", Name = "影视原声" }, new() { Id = "游戏", Name = "游戏" }, new() { Id = "网络歌曲", Name = "网络歌曲" }, new() { Id = "经典", Name = "经典" } } }
        };
    }

    private static List<Ranking> GetDefaultRankings()
    {
        return new List<Ranking>
        {
            new() { Id = "19723756", Name = "飙升榜", Source = "netease", Type = RankingType.Rising },
            new() { Id = "3778678", Name = "热歌榜", Source = "netease", Type = RankingType.Hot },
            new() { Id = "3779629", Name = "新歌榜", Source = "netease", Type = RankingType.New },
            new() { Id = "2884035", Name = "原创榜", Source = "netease", Type = RankingType.Original },
            new() { Id = "2250011882", Name = "抖音排行榜", Source = "netease", Type = RankingType.Hot },
            new() { Id = "745956260", Name = "电音榜", Source = "netease", Type = RankingType.Other },
            new() { Id = "1899724", Name = "说唱榜", Source = "netease", Type = RankingType.Other },
            new() { Id = "5453912201", Name = "爵士榜", Source = "netease", Type = RankingType.Other }
        };
    }
}
