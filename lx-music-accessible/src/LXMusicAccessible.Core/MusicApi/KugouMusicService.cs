using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 酷狗音乐服务 — 实现 IMusicApi 统一接口
/// </summary>
public class KugouMusicService : IMusicApi
{
    private readonly ILogger<KugouMusicService> _logger;
    private readonly MusicHttpClient _httpClient;
    private const string BaseUrl = "http://mobilecdn.kugou.com";
    private const string ApiBaseV3 = "http://mobilecdn.kugou.com/api/v3";
    private const string ApiBaseV5 = "http://msearchcdn.kugou.com/api/v3";

    public string SourceId => "kg";
    public string SourceName => "酷狗音乐";

    public KugouMusicService(ILogger<KugouMusicService> logger, MusicHttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    /// <summary>
    /// 搜索音乐
    /// </summary>
    public async Task<PagedResult<MusicInfo>> SearchAsync(string keyword, int page = 1, int pageSize = 30, string searchType = "song")
    {
        try
        {
            _logger.LogInformation("酷狗音乐搜索: {Keyword}, 页码: {Page}", keyword, page);

            if (string.Equals(searchType, "album", StringComparison.OrdinalIgnoreCase))
                return await SearchAlbumsAsync(keyword, page, pageSize);

            var url = $"{ApiBaseV3}/search/song";
            var parameters = new Dictionary<string, string>
            {
                ["keyword"] = keyword,
                ["page"] = page.ToString(),
                ["pagesize"] = pageSize.ToString(),
                ["format"] = "json",
                ["showtype"] = "1"
            };

            var response = await _httpClient.GetAsync(url, parameters);
            var result = JsonSerializer.Deserialize<KugouSearchResult>(response);

            if (result?.ErrorCode == 0 && result.Data?.Info != null)
            {
                var musicList = result.Data.Info.Select(song => new MusicInfo
                {
                    Id = song.Hash ?? "",
                        Hash = song.Hash ?? "",
                    Title = song.SongName ?? "未知歌曲",
                    Artist = song.SingerName ?? "未知歌手",
                    Album = song.AlbumName ?? "",
                    Duration = song.GetDurationString(),
                    Source = "kg",
                    CoverUrl = song.AlbumId != null
                        ? $"https://imgcache.qq.com/music/photo/album_300/{int.Parse(song.AlbumId) % 100}/{int.Parse(song.AlbumId)}.jpg"
                        : "",
                    Quality = 128
                }).ToList();

                return new PagedResult<MusicInfo>
                {
                    CurrentPage = page,
                    PageSize = pageSize,
                    TotalItems = result.Data.Total,
                    Items = musicList
                };
            }

            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "酷狗音乐搜索失败: {Keyword}", keyword);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    // ========== 专辑搜索 / 专辑详情 ==========

    /// <summary>酷狗专辑搜索：/api/v3/search/album → data.info[]</summary>
    private async Task<PagedResult<MusicInfo>> SearchAlbumsAsync(string keyword, int page, int pageSize)
    {
        try
        {
            var url = "http://msearchcdn.kugou.com/api/v3/search/album";
            var parameters = new Dictionary<string, string>
            {
                ["keyword"] = keyword,
                ["page"] = page.ToString(),
                ["pagesize"] = pageSize.ToString(),
                ["format"] = "json"
            };
            var response = await _httpClient.GetAsync(url, parameters);
            var result = JsonSerializer.Deserialize<JsonElement>(response);
            var list = new List<MusicInfo>();
            int total = 0;

            if (result.TryGetProperty("data", out var data) &&
                data.TryGetProperty("total", out var t)) total = t.GetInt32();

            if (data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in info.EnumerateArray())
                {
                    var id = JsonHelpers.GetString(item, "albumid");
                    if (string.IsNullOrEmpty(id)) continue;
                    var name = JsonHelpers.GetString(item, "albumname") ?? "未知专辑";
                    var artist = JsonHelpers.GetString(item, "singername") ?? "";
                    list.Add(JsonHelpers.BuildAlbumRow(id!, name, artist, "kg",
                        (JsonHelpers.GetString(item, "imgurl") ?? "").Replace("{size}", "300")));
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
            _logger.LogError(ex, "酷狗专辑搜索失败: {Keyword}", keyword);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    /// <summary>酷狗专辑详情：/api/v3/album/song → data.info[]（hash 歌曲）</summary>
    public async Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50)
    {
        try
        {
            if (album == null || string.IsNullOrEmpty(album.Id))
                return PagedResult<MusicInfo>.Empty(page, pageSize);

            var url = $"{ApiBaseV3}/album/song";
            var parameters = new Dictionary<string, string>
            {
                ["albumid"] = album.Id,
                ["page"] = page.ToString(),
                ["pagesize"] = pageSize.ToString(),
                ["format"] = "json"
            };
            var response = await _httpClient.GetAsync(url, parameters);
            var result = JsonSerializer.Deserialize<JsonElement>(response);
            var list = new List<MusicInfo>();
            int total = 0;

            if (result.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("total", out var t)) total = t.GetInt32();
                if (data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in info.EnumerateArray())
                    {
                        var hash = JsonHelpers.GetString(item, "hash");
                        if (string.IsNullOrEmpty(hash)) continue;
                        var filename = JsonHelpers.GetString(item, "filename") ?? "";
                        var title = JsonHelpers.GetString(item, "songname") ?? "";
                        var artist = JsonHelpers.GetString(item, "singer_name") ?? "";
                        // 兜底：从 "歌手 - 歌名" 文件名拆出
                        if (string.IsNullOrEmpty(title) && filename.Contains(" - ", StringComparison.Ordinal))
                        {
                            var dash = filename.IndexOf(" - ", StringComparison.Ordinal);
                            artist = filename[..dash].Trim();
                            title = filename[(dash + 3)..].Trim();
                        }
                        var albumName = JsonHelpers.GetString(item, "album_name") ?? album.Title;
                        var duration = "";
                        if (JsonHelpers.GetString(item, "duration") is string d && int.TryParse(d, out var sec))
                            duration = sec >= 3600 ? TimeSpan.FromSeconds(sec).ToString(@"hh\:mm\:ss") : TimeSpan.FromSeconds(sec).ToString(@"mm\:ss");
                        list.Add(new MusicInfo
                        {
                            Id = hash!, Hash = hash!,
                            Title = string.IsNullOrEmpty(title) ? "未知歌曲" : title,
                            Artist = artist, Album = albumName, Duration = duration,
                            Source = "kg"
                        });
                    }
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
            _logger.LogError(ex, "酷狗专辑详情失败: {Album}", album?.Title ?? "?");
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    /// <summary>
    /// 获取播放 URL
    /// </summary>
        /// <summary>??????????</summary>
    public List<string> GetSupportedQualities() => new() { "128k" };

public async Task<string?> GetPlayUrlAsync(string musicId, string quality = "128k")
    {
        try
        {
            _logger.LogInformation("获取酷狗音乐播放URL: {MusicId}", musicId);

            // mobilecdn 已失效，改用 m.kugou.com；响应为扁平结构（顶层 url/backup_url）
            var url = "https://m.kugou.com/app/i/getSongInfo.php";
            var parameters = new Dictionary<string, string>
            {
                ["cmd"] = "playInfo",
                ["hash"] = musicId,
                ["format"] = "json"
            };
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
                ["Referer"] = "https://www.kugou.com/"
            };

            var response = await _httpClient.GetAsync(url, parameters, headers);
            try
            {
                var result = JsonSerializer.Deserialize<KugouPlayUrlResponse>(response);
                if (result?.ErrorCode == 0 && result.Data is { Count: > 0 })
                    return result.Data[0].Url;
            }
            catch { }

            using (var doc = JsonDocument.Parse(response))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(u.GetString()))
                    return u.GetString();
                if (root.TryGetProperty("backup_url", out var bu) && bu.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(bu.GetString()))
                    return bu.GetString();
                if (root.TryGetProperty("backupUrl", out var bu2) && bu2.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(bu2.GetString()))
                    return bu2.GetString();
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取酷狗音乐播放URL失败: {MusicId}", musicId);
            return null;
        }
    }

    /// <summary>
    /// 获取歌词
    /// </summary>
    public async Task<Lyric?> GetLyricAsync(MusicInfo music)
    {
        var hash = string.IsNullOrEmpty(music?.Hash) ? music?.Id ?? "" : music.Hash;
        if (string.IsNullOrEmpty(hash)) return null;
        try
        {
            // mobiles.kugou.com/api/v3/lyric 已返回 Access Deny，改用 m.kugou.com krc.php（实测可用，直接返回 LRC 文本）
            var timelength = GetDurationMs(music?.Duration) ?? 300000;
            var url = "https://m.kugou.com/app/i/krc.php";
            var parameters = new Dictionary<string, string>
            {
                ["cmd"] = "100",
                ["hash"] = hash,
                ["timelength"] = timelength.ToString()
            };
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
                ["Referer"] = "https://www.kugou.com/"
            };

            var response = await _httpClient.GetAsync(url, parameters, headers);
            return ParseLrcContent(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取酷狗歌词失败: {MusicId}", hash);
            return null;
        }
    }

    /// <summary>把 mm:ss / hh:mm:ss 时长解析为毫秒；失败返回 null</summary>
    private static int? GetDurationMs(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return null;
        if (TimeSpan.TryParseExact(duration, new[] { @"mm\:ss", @"hh\:mm\:ss" }, System.Globalization.CultureInfo.InvariantCulture, out var ts))
            return (int)ts.TotalMilliseconds;
        return null;
    }

    /// <summary>
    /// 获取歌单分类 — 参考原版 lx-music-desktop：www2 getSpecial?is_smarty=1 → data.tagids
    /// </summary>
    public async Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync()
    {
        _logger.LogInformation("获取{Name}歌单分类", SourceName);

        try
        {
            var url = "http://www2.kugou.kugou.com/yueku/v9/special/getSpecial";
            var response = await _httpClient.GetAsync(url, new Dictionary<string, string>
            {
                ["is_smarty"] = "1", ["cdn"] = "cdn", ["t"] = "5"
            });
            var data = JsonSerializer.Deserialize<JsonElement>(response);

            var categories = new List<PlaylistCategory>
            {
                new PlaylistCategory
                {
                    Id = "all",
                    Name = "全部歌单",
                    Tags = new List<PlaylistTag> { new() { Id = "", Name = "全部" } }
                }
            };

            if (data.TryGetProperty("status", out var st) && st.GetInt32() == 1 &&
                data.TryGetProperty("data", out var d) &&
                d.TryGetProperty("tagids", out var tagids) &&
                tagids.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in tagids.EnumerateObject())
                {
                    var categoryName = prop.Name;
                    var tagObj = prop.Value;
                    if (!tagObj.TryGetProperty("data", out var tagData) || tagData.ValueKind != JsonValueKind.Array)
                        continue;

                    var tagList = new List<PlaylistTag>();
                    foreach (var tag in tagData.EnumerateArray())
                    {
                        var name = tag.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                        var id = tag.TryGetProperty("id", out var tid)
                            ? (tid.ValueKind == JsonValueKind.Number ? tid.GetInt64().ToString() : tid.GetString() ?? "")
                            : "";
                        if (!string.IsNullOrEmpty(name))
                            tagList.Add(new PlaylistTag { Id = id, Name = name });
                    }

                    if (tagList.Count > 0)
                        categories.Add(new PlaylistCategory { Id = categoryName, Name = categoryName, Tags = tagList });
                }
            }

            if (categories.Count <= 1)
                return GetDefaultCategories();

            return categories;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取酷狗歌单分类失败，使用内置分类");
            return GetDefaultCategories();
        }
    }

    private static List<PlaylistCategory> GetDefaultCategories()
    {
        return new List<PlaylistCategory>
        {
            new() { Id = "all", Name = "全部歌单", Tags = new() { new() { Id = "", Name = "全部" } } },
            new() { Id = "1", Name = "语种", Tags = new() { new() { Id = "4086", Name = "华语" }, new() { Id = "4087", Name = "欧美" }, new() { Id = "4088", Name = "日语" }, new() { Id = "4089", Name = "韩语" }, new() { Id = "4090", Name = "粤语" } } },
            new() { Id = "2", Name = "风格", Tags = new() { new() { Id = "1001", Name = "流行" }, new() { Id = "1015", Name = "摇滚" }, new() { Id = "1002", Name = "民谣" }, new() { Id = "1009", Name = "电子" } } },
            new() { Id = "3", Name = "场景", Tags = new() { new() { Id = "6001", Name = "清晨" }, new() { Id = "6002", Name = "夜晚" }, new() { Id = "6003", Name = "运动" } } }
        };
    }

    /// <summary>
    /// 按分类获取歌单列表 — 参考原版：getSpecial?is_ajax=1 → special_db
    /// </summary>
    public async Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string categoryId, int page = 1, int pageSize = 30)
    {
        try
        {
            _logger.LogInformation("获取酷狗歌单: {Category}, 页码: {Page}", categoryId, page);

            var url = "http://www2.kugou.kugou.com/yueku/v9/special/getSpecial";
            var parameters = new Dictionary<string, string>
            {
                ["is_ajax"] = "1", ["cdn"] = "cdn", ["t"] = "5",
                ["c"] = categoryId ?? "", ["p"] = page.ToString()
            };

            var response = await _httpClient.GetAsync(url, parameters);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            var playlists = new List<Playlist>();
            int total = 0;

            if (result.TryGetProperty("status", out var st) && st.GetInt32() == 1 &&
                result.TryGetProperty("special_db", out var specialDb) &&
                specialDb.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in specialDb.EnumerateArray())
                {
                    var specialId = item.TryGetProperty("specialid", out var sid)
                        ? (sid.ValueKind == JsonValueKind.Number ? sid.GetInt64().ToString() : sid.GetString() ?? "")
                        : "";
                    playlists.Add(new Playlist
                    {
                        Id = "kg_" + specialId,
                        Name = item.TryGetProperty("specialname", out var snm) ? snm.GetString() ?? "" : "",
                        Description = item.TryGetProperty("intro", out var desc) ? desc.GetString() ?? "" : "",
                        Tags = new List<string>(),
                        Type = PlaylistType.Recommend,
                        CreatedDate = DateTime.Now,
                        ModifiedDate = DateTime.Now
                    });
                }
            }

            // 酷狗接口不返回总数，交由 UI 估算分页
            return new PagedResult<Playlist>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = total,
                Items = playlists
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取酷狗歌单列表失败: {Category}", categoryId);
            return PagedResult<Playlist>.Empty(page, pageSize);
        }
    }

    /// <summary>
    /// 获取歌单详情 — /api/v3/special/song，歌曲字段为 filename(歌手 - 歌名)/hash/duration
    /// </summary>
    public async Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50)
    {
        try
        {
            _logger.LogInformation("获取酷狗歌单详情: {PlaylistId}", playlistId);

            var url = $"{ApiBaseV3}/special/song";
            var parameters = new Dictionary<string, string>
            {
                ["specialid"] = playlistId,
                ["page"] = page.ToString(),
                ["pagesize"] = pageSize.ToString(),
                ["format"] = "json"
            };

            var response = await _httpClient.GetAsync(url, parameters);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            var musicList = new List<MusicInfo>();
            var albumIdByMusicId = new Dictionary<string, string>();
            int total = 0;

            if (result.TryGetProperty("status", out var st) && st.GetInt32() == 1 &&
                result.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("total", out var tEl))
                {
                    if (tEl.ValueKind == JsonValueKind.Number) total = tEl.GetInt32();
                    else int.TryParse(tEl.GetString() ?? "0", out total);
                }

                if (data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
                {
                    foreach (var song in info.EnumerateArray())
                    {
                        var hash = song.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "";
                        var filename = song.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "" : "";
                        var duration = song.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
                        var albumId = song.TryGetProperty("album_id", out var aid) ? aid.GetString() ?? "" : "";

                        // 接口不返回歌手/专辑字段，歌手从 filename（"歌手 - 歌名"）解析，专辑名按 album_id 补全
                        var (artist, title) = SplitFilename(filename);
                        if (string.IsNullOrEmpty(artist)) artist = "未知歌手";

                        musicList.Add(new MusicInfo
                        {
                            Id = hash,
                            Hash = hash,
                            Title = string.IsNullOrEmpty(title) ? "未知歌曲" : title,
                            Artist = artist,
                            Album = "",
                            Duration = ParseKgDuration(duration),
                            Source = "kg",
                            CoverUrl = !string.IsNullOrEmpty(albumId) && int.TryParse(albumId, out var ai)
                                ? $"https://imgcache.qq.com/music/photo/album_300/{ai % 100}/{ai}.jpg"
                                : "",
                            Quality = 128
                        });
                        if (!string.IsNullOrEmpty(albumId)) albumIdByMusicId[hash] = albumId;
                    }
                }
            }

            // 并发补全专辑名（album/info，去重 + 内存缓存 + 单条容错）
            var albumNames = await FetchAlbumNamesAsync(albumIdByMusicId.Values);
            ApplyAlbumNames(musicList, albumIdByMusicId, albumNames);

            return new PagedResult<MusicInfo>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = total,
                Items = musicList
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取酷狗歌单详情失败: {PlaylistId}", playlistId);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    /// <summary>从酷狗 filename（"歌手 - 歌名"）解析歌手与歌名，兼容不同分隔符</summary>
    private static (string artist, string title) SplitFilename(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return ("", "");
        foreach (var sep in new[] { " - ", " -", "- " })
        {
            var idx = filename.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                var artist = filename[..idx].Trim().Trim('、', ',', ' ', '\t');
                var title = filename[(idx + sep.Length)..].Trim();
                if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(title))
                    return (artist, title);
            }
        }
        return ("", filename.Trim());
    }

    /// <summary>解析 rank/song 响应的 authors 字段（对象或数组）</summary>
    private static string ParseAuthors(JsonElement song)
    {
        if (!song.TryGetProperty("authors", out var authors)) return "";
        try
        {
            if (authors.ValueKind == JsonValueKind.Object)
            {
                if (authors.TryGetProperty("author_name", out var an) && an.ValueKind == JsonValueKind.String)
                    return an.GetString() ?? "";
            }
            else if (authors.ValueKind == JsonValueKind.Array)
            {
                var names = new List<string>();
                foreach (var a in authors.EnumerateArray())
                {
                    if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("author_name", out var an2) &&
                        an2.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(an2.GetString()))
                        names.Add(an2.GetString()!);
                }
                if (names.Count > 0) return string.Join("、", names);
            }
        }
        catch { }
        return "";
    }

    private static readonly ConcurrentDictionary<string, string> AlbumNameCache = new();

    /// <summary>并发获取专辑名（album/info，去重 + 内存缓存 + 单条容错）</summary>
    private async Task<Dictionary<string, string>> FetchAlbumNamesAsync(IEnumerable<string> albumIds)
    {
        var result = new ConcurrentDictionary<string, string>();
        var ids = albumIds.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Where(x => int.TryParse(x, out _))
            .Distinct()
            .ToList();
        if (ids.Count == 0) return new Dictionary<string, string>();

        using var semaphore = new SemaphoreSlim(8);
        var tasks = ids.Select(async id =>
        {
            if (AlbumNameCache.TryGetValue(id, out var cached))
            {
                result[id] = cached;
                return;
            }
            await semaphore.WaitAsync();
            try
            {
                var response = await _httpClient.GetAsync($"{ApiBaseV3}/album/info", new Dictionary<string, string>
                {
                    ["albumid"] = id, ["format"] = "json"
                });
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("status", out var st) && st.GetInt32() == 1 &&
                    doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("albumname", out var an) && an.ValueKind == JsonValueKind.String)
                {
                    var name = an.GetString() ?? "";
                    if (!string.IsNullOrEmpty(name))
                    {
                        AlbumNameCache[id] = name;
                        result[id] = name;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取酷狗专辑名失败: {AlbumId}", id);
            }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);
        return new Dictionary<string, string>(result);
    }

    /// <summary>把抓取到的专辑名回填到歌曲列表</summary>
    private static void ApplyAlbumNames(List<MusicInfo> musicList, Dictionary<string, string> albumIdByMusicId, Dictionary<string, string> albumNames)
    {
        foreach (var m in musicList)
        {
            if (albumIdByMusicId.TryGetValue(m.Id, out var aid) && albumNames.TryGetValue(aid, out var name) && !string.IsNullOrEmpty(name))
                m.Album = name;
        }
    }

    private static string ParseKgDuration(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    /// <summary>
    /// 获取排行榜列表
    /// </summary>
    public async Task<List<Ranking>> GetRankingsAsync()
    {
        _logger.LogInformation("获取酷狗排行榜");

        try
        {
            var url = $"{ApiBaseV3}/rank/list";
            var parameters = new Dictionary<string, string> { ["format"] = "json", ["pid"] = "1", ["withsong"] = "0" };

            var response = await _httpClient.GetAsync(url, parameters);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            var rankings = new List<Ranking>();

            if (result.TryGetProperty("data", out var data) &&
                data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in info.EnumerateArray())
                {
                    rankings.Add(new Ranking
                    {
                        Id = "kg_" + (item.TryGetProperty("rankid", out var rid)
                            ? rid.ValueKind == JsonValueKind.Number ? rid.GetInt64().ToString() : rid.GetString() ?? ""
                            : ""),
                        Name = item.TryGetProperty("rankname", out var rn) ? rn.GetString() ?? "" : "",
                        Description = "",
                        Source = "kg",
                        Type = RankingType.Hot,
                        UpdateDate = DateTime.Now
                    });
                }
            }

            if (rankings.Count > 0)
                return rankings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "从API获取酷狗排行榜失败，使用内置列表");
        }

        return new List<Ranking>
        {
            new Ranking { Id = "kg_8888", Name = "酷狗TOP500", Description = "酷狗音乐热歌榜", Source = "kg", Type = RankingType.Hot, UpdateDate = DateTime.Now },
            new Ranking { Id = "kg_6666", Name = "酷狗飙升榜", Description = "酷狗音乐飙升榜", Source = "kg", Type = RankingType.Rising, UpdateDate = DateTime.Now },
            new Ranking { Id = "kg_23784", Name = "酷狗新歌榜", Description = "酷狗音乐新歌榜", Source = "kg", Type = RankingType.New, UpdateDate = DateTime.Now },
            new Ranking { Id = "kg_21101", Name = "酷狗华语榜", Description = "华语歌曲排行榜", Source = "kg", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "kg_13289", Name = "酷狗欧美榜", Description = "欧美歌曲排行榜", Source = "kg", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "kg_6012", Name = "酷狗韩语榜", Description = "韩语歌曲排行榜", Source = "kg", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "kg_1855", Name = "酷狗日语榜", Description = "日语歌曲排行榜", Source = "kg", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "kg_352", Name = "酷狗粤语榜", Description = "粤语歌曲排行榜", Source = "kg", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "kg_22206", Name = "DJ热歌榜", Description = "DJ热歌排行榜", Source = "kg", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "kg_21370", Name = "抖音热歌榜", Description = "抖音热歌排行榜", Source = "kg", Type = RankingType.Other, UpdateDate = DateTime.Now }
        };
    }

    /// <summary>
    /// 获取排行榜详情
    /// </summary>
    public async Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50)
    {
        try
        {
            _logger.LogInformation("获取酷狗排行榜详情: {RankingId}", rankingId);

            var url = $"{ApiBaseV3}/rank/song";
            var parameters = new Dictionary<string, string>
            {
                ["rankid"] = rankingId,
                ["page"] = page.ToString(),
                ["pagesize"] = pageSize.ToString(),
                ["format"] = "json"
            };

            var response = await _httpClient.GetAsync(url, parameters);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            var musicList = new List<MusicInfo>();
            var albumIdByMusicId = new Dictionary<string, string>();
            int total = 0;

            if (result.TryGetProperty("errcode", out var ec) && ec.GetInt32() == 0 &&
                result.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("total", out var tEl))
                {
                    if (tEl.ValueKind == JsonValueKind.Number) total = tEl.GetInt32();
                    else int.TryParse(tEl.GetString() ?? "0", out total);
                }

                if (data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
                {
                    foreach (var song in info.EnumerateArray())
                    {
                        var hash = song.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "";
                        var filename = song.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "" : "";
                        var songname = song.TryGetProperty("songname", out var sn) ? sn.GetString() ?? "" : "";
                        var duration = song.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
                        var albumId = song.TryGetProperty("album_id", out var aid) ? aid.GetString() ?? "" : "";

                        // 歌手：authors（对象或数组）优先，否则从 filename 解析；歌名：songname 优先
                        var (artist, titleFromFilename) = SplitFilename(filename);
                        var artistFromAuthors = ParseAuthors(song);
                        if (!string.IsNullOrEmpty(artistFromAuthors)) artist = artistFromAuthors;
                        if (string.IsNullOrEmpty(artist)) artist = "未知歌手";

                        var title = !string.IsNullOrEmpty(songname) ? songname : titleFromFilename;
                        if (string.IsNullOrEmpty(title)) title = "未知歌曲";

                        musicList.Add(new MusicInfo
                        {
                            Id = hash,
                            Hash = hash,
                            Title = title,
                            Artist = artist,
                            Album = "",
                            Duration = ParseKgDuration(duration),
                            Source = "kg",
                            CoverUrl = !string.IsNullOrEmpty(albumId) && int.TryParse(albumId, out var ai)
                                ? $"https://imgcache.qq.com/music/photo/album_300/{ai % 100}/{ai}.jpg"
                                : "",
                            Quality = 128
                        });
                        if (!string.IsNullOrEmpty(albumId)) albumIdByMusicId[hash] = albumId;
                    }
                }
            }

            // 并发补全专辑名
            var albumNames = await FetchAlbumNamesAsync(albumIdByMusicId.Values);
            ApplyAlbumNames(musicList, albumIdByMusicId, albumNames);

            return new PagedResult<MusicInfo>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = total,
                Items = musicList
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取酷狗排行榜详情失败: {RankingId}", rankingId);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    // ========== 辅助方法 ==========

    private static Lyric? ParseLrcContent(string? lrcContent)
    {
        if (string.IsNullOrWhiteSpace(lrcContent)) return null;

        var lines = new List<LyricLine>();
        foreach (var line in lrcContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^\[(\d{2}):(\d{2})(?:\.(\d+))?\](.*)");
            if (match.Success)
            {
                var min = int.Parse(match.Groups[1].Value);
                var sec = int.Parse(match.Groups[2].Value);
                var ms = match.Groups[3].Success ? int.Parse(match.Groups[3].Value.PadRight(3, '0').Substring(0, 3)) : 0;
                var text = match.Groups[4].Value.Trim();

                lines.Add(new LyricLine
                {
                    Time = new TimeSpan(0, 0, min, sec, ms),
                    Text = text
                });
            }
        }

        return lines.Count > 0 ? new Lyric { Lines = lines } : null;
    }

    public async Task<List<HotSearchWord>> GetHotSearchWordsAsync()
    {
        return await Task.FromResult(new List<HotSearchWord>
        {
            new() { Word = "热门歌曲", Score = 100, Source = SourceId },
        });
    }
}
