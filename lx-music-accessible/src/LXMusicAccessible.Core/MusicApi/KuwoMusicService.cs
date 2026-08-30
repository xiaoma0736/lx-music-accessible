using Microsoft.Extensions.Logging;
using System.Text.Json;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 酷我音乐服务 — 实现 IMusicApi 统一接口
/// </summary>
public class KuwoMusicService : IMusicApi
{
    private readonly ILogger<KuwoMusicService> _logger;
    private readonly MusicHttpClient _httpClient;
    private const string BaseUrl = "http://www.kuwo.cn";
    private const string SearchUrl = "http://search.kuwo.cn";
    private const string WbdUrl = "https://wbd.kuwo.cn";

    public string SourceId => "kw";
    public string SourceName => "酷我音乐";

    public KuwoMusicService(ILogger<KuwoMusicService> logger, MusicHttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    /// <summary>
    /// 搜索音乐（使用 search.kuwo.cn 接口）
    /// </summary>
    public async Task<PagedResult<MusicInfo>> SearchAsync(string keyword, int page = 1, int pageSize = 30, string searchType = "song", CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("酷我音乐搜索: {Keyword}, 页码: {Page}", keyword, page);

            if (string.Equals(searchType, "album", StringComparison.OrdinalIgnoreCase))
                return await SearchAlbumsAsync(keyword, page, pageSize, cancellationToken);

            var url = $"{SearchUrl}/r.s";
            var parameters = new Dictionary<string, string>
            {
                ["client"] = "kt",
                ["all"] = keyword,
                ["pn"] = (page - 1).ToString(),
                ["rn"] = pageSize.ToString(),
                ["uid"] = "794762570",
                ["ver"] = "kwplayer_ar_9.2.2.1",
                ["vipver"] = "1",
                ["show_copyright_off"] = "1",
                ["newver"] = "1",
                ["ft"] = "music",
                ["cluster"] = "0",
                ["strategy"] = "2012",
                ["encoding"] = "utf8",
                ["rformat"] = "json",
                ["vermerge"] = "1",
                ["mobi"] = "1",
                ["issubtitle"] = "1"
            };

            var response = await _httpClient.GetAsync(url, parameters, null, cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            int total = 0;
            var musicList = new List<MusicInfo>();

            if (result.TryGetProperty("abslist", out var abslist) && abslist.ValueKind == JsonValueKind.Array)
            {
                if (result.TryGetProperty("TOTAL", out var totalEl))
                    int.TryParse(totalEl.GetString(), out total);

                foreach (var song in abslist.EnumerateArray())
                {
                    var songId = song.TryGetProperty("MUSICRID", out var rid)
                        ? rid.GetString()?.Replace("MUSIC_", "") ?? ""
                        : "";

                    musicList.Add(new MusicInfo
                    {
                        Id = songId,
                        Title = song.TryGetProperty("SONGNAME", out var sn) ? sn.GetString() ?? "" : "",
                        Artist = FormatSinger(song.TryGetProperty("ARTIST", out var ar) ? ar.GetString() ?? "" : ""),
                        Album = song.TryGetProperty("ALBUM", out var al) ? al.GetString() ?? "" : "",
                        Duration = ParseDuration(song.TryGetProperty("DURATION", out var dur) ? dur.GetString() : null),
                        Source = "kw",
                        CoverUrl = song.TryGetProperty("ALBUMID", out var alId) && !string.IsNullOrEmpty(alId.GetString())
                            ? $"https://img2.kuwo.cn/star/albumcover/{alId.GetString()}.jpg"
                            : "",
                        Quality = 128
                    });
                }
            }

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
            _logger.LogError(ex, "酷我音乐搜索失败: {Keyword}", keyword);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    // ========== 专辑搜索 / 专辑详情 ==========

    /// <summary>酷我专辑搜索：search.kuwo.cn/r.s?ft=album&itemset=web_2013 → albumlist[]（需 https + itemset，去掉 vermerge）</summary>
    private async Task<PagedResult<MusicInfo>> SearchAlbumsAsync(string keyword, int page, int pageSize, CancellationToken cancellationToken)
    {
        try
        {
            var url = "https://search.kuwo.cn/r.s";
            var parameters = new Dictionary<string, string>
            {
                ["client"] = "kt",
                ["all"] = keyword,
                ["pn"] = (page - 1).ToString(),
                ["rn"] = pageSize.ToString(),
                ["uid"] = "794762570",
                ["ver"] = "kwplayer_ar_9.2.2.1",
                ["vipver"] = "1",
                ["ft"] = "album",
                ["encoding"] = "utf8",
                ["rformat"] = "json",
                ["itemset"] = "web_2013",
                ["mobi"] = "1"
            };
            var response = await _httpClient.GetAsync(url, parameters, null, cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(response);
            var list = new List<MusicInfo>();
            int total = 0;

            if (result.TryGetProperty("total", out var totalEl))
            {
                if (totalEl.ValueKind == JsonValueKind.Number) total = totalEl.GetInt32();
                else if (totalEl.ValueKind == JsonValueKind.String) int.TryParse(totalEl.GetString(), out total);
            }
            if (total <= 0 && result.TryGetProperty("TOTAL", out var totalEl2))
            {
                if (totalEl2.ValueKind == JsonValueKind.Number) total = totalEl2.GetInt32();
                else if (totalEl2.ValueKind == JsonValueKind.String) int.TryParse(totalEl2.GetString(), out total);
            }

            if (result.TryGetProperty("albumlist", out var albumlist) && albumlist.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in albumlist.EnumerateArray())
                {
                    var id = JsonHelpers.GetString(item, "albumid") ?? JsonHelpers.GetString(item, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var name = JsonHelpers.GetString(item, "name") ?? JsonHelpers.GetString(item, "albumname") ?? "未知专辑";
                    var artist = JsonHelpers.GetString(item, "artist") ?? "";
                    var pic = JsonHelpers.GetString(item, "pic") ?? JsonHelpers.GetString(item, "img") ?? JsonHelpers.GetString(item, "hts_img") ?? "";
                    if (!string.IsNullOrEmpty(pic) && !pic.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        pic = "http://img1.sycdn.kuwo.cn/star/albumcover/" + pic.TrimStart('/');
                    list.Add(JsonHelpers.BuildAlbumRow(id!, name, artist, "kw", pic));
                }
            }
            return new PagedResult<MusicInfo>
            {
                CurrentPage = page, PageSize = pageSize,
                TotalItems = total > 0 ? total : list.Count,
                HasMore = total > page * pageSize,
                Items = list
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "酷我专辑搜索失败: {Keyword}", keyword);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    /// <summary>
    /// 酷我专辑详情：原生专辑歌曲接口已风控，改用“按专辑名搜歌曲并过滤专辑”兜底。
    /// </summary>
    public async Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            if (album == null || string.IsNullOrEmpty(album.Title))
                return PagedResult<MusicInfo>.Empty(page, pageSize);

            var keyword = string.IsNullOrWhiteSpace(album.Artist) ? album.Title : $"{album.Title} {album.Artist}";
            var url = $"{SearchUrl}/r.s";
            var parameters = new Dictionary<string, string>
            {
                ["client"] = "kt",
                ["all"] = keyword,
                ["pn"] = (page - 1).ToString(),
                ["rn"] = pageSize.ToString(),
                ["uid"] = "794762570",
                ["ver"] = "kwplayer_ar_9.2.2.1",
                ["vipver"] = "1",
                ["show_copyright_off"] = "1",
                ["newver"] = "1",
                ["ft"] = "music",
                ["cluster"] = "0",
                ["strategy"] = "2012",
                ["encoding"] = "utf8",
                ["rformat"] = "json",
                ["vermerge"] = "1",
                ["mobi"] = "1",
                ["issubtitle"] = "1"
            };
            var response = await _httpClient.GetAsync(url, parameters, null, cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(response);
            var list = new List<MusicInfo>();

            if (result.TryGetProperty("abslist", out var abslist) && abslist.ValueKind == JsonValueKind.Array)
            {
                foreach (var song in abslist.EnumerateArray())
                {
                    var albumName = song.TryGetProperty("ALBUM", out var al) ? al.GetString() ?? "" : "";
                    if (!JsonHelpers.MatchesAlbum(albumName, album.Title)) continue;
                    var songId = song.TryGetProperty("MUSICRID", out var rid)
                        ? rid.GetString()?.Replace("MUSIC_", "") ?? "" : "";
                    if (string.IsNullOrEmpty(songId)) continue;
                    list.Add(new MusicInfo
                    {
                        Id = songId,
                        Title = song.TryGetProperty("SONGNAME", out var sn) ? sn.GetString() ?? "" : "",
                        Artist = FormatSinger(song.TryGetProperty("ARTIST", out var ar) ? ar.GetString() ?? "" : ""),
                        Album = albumName,
                        Duration = ParseDuration(song.TryGetProperty("DURATION", out var dur) ? dur.GetString() : null),
                        Source = "kw"
                    });
                }
            }
            return new PagedResult<MusicInfo>
            {
                CurrentPage = page, PageSize = pageSize,
                TotalItems = list.Count, Items = list
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "酷我专辑详情失败: {Album}", album?.Title ?? "?");
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    /// <summary>获取支持的音质列表</summary>
    public List<string> GetSupportedQualities() => new() { "128k" };

    /// <summary>获取播放 URL</summary>
    public async Task<string?> GetPlayUrlAsync(
        string musicId,
        string quality = "128k",
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("获取酷我音乐播放URL: {MusicId}", musicId);

            var url = $"{BaseUrl}/api/v1/www/music/playUrl";
            var parameters = new Dictionary<string, string>
            {
                ["mid"] = musicId,
                ["type"] = "music",
                ["httpsStatus"] = "1"
            };

            var response = await _httpClient.GetAsync(
                url,
                parameters,
                cancellationToken: cancellationToken);
            var result = JsonSerializer.Deserialize<KuwoPlayUrlResponse>(response);

            if (result?.Code == 200 && result.Data?.Url != null)
                return result.Data.Url;

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

    /// <summary>
    /// 获取歌词
    /// </summary>
    public async Task<Lyric?> GetLyricAsync(MusicInfo music)
    {
        var musicId = music?.Id ?? "";
        try
        {
            var url = $"https://m.kuwo.cn/newh5/singles/songinfoandlrc";
            var parameters = new Dictionary<string, string>
            {
                ["musicId"] = musicId,
                ["httpsStatus"] = "1"
            };
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15",
                ["Referer"] = "https://www.kuwo.cn/"
            };

            var response = await _httpClient.GetAsync(url, parameters, headers);
            var data = JsonSerializer.Deserialize<JsonElement>(response);

            if (data.TryGetProperty("data", out var lrData) &&
                lrData.TryGetProperty("lrclist", out var lrcList))
            {
                var lines = new List<LyricLine>();
                foreach (var item in lrcList.EnumerateArray())
                {
                    var timeStr = item.TryGetProperty("time", out var t) ? t.GetString() ?? "0" : "0";
                    var textStr = item.TryGetProperty("lineLyric", out var txt) ? txt.GetString() ?? "" : "";

                    if (double.TryParse(timeStr, out var seconds))
                    {
                        lines.Add(new LyricLine
                        {
                            Time = TimeSpan.FromSeconds(seconds),
                            Text = textStr
                        });
                    }
                }

                if (lines.Count > 0)
                    return new Lyric { Lines = lines };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取酷我歌词失败: {MusicId}", musicId);
            return null;
        }
    }

    /// <summary>
    /// 获取歌单分类 — 参考原版 lx-music-desktop：wapi getTagList
    /// </summary>
    public async Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("获取{Name}歌单分类", SourceName);

        try
        {
            var url = "http://wapi.kuwo.cn/api/pc/classify/playlist/getTagList";
            var parameters = new Dictionary<string, string>
            {
                ["cmd"] = "rcm_keyword_playlist", ["user"] = "0",
                ["prod"] = "kwplayer_pc_9.0.5.0", ["vipver"] = "9.0.5.0",
                ["source"] = "kwplayer_pc_9.0.5.0",
                ["loginUid"] = "0", ["loginSid"] = "0", ["appUid"] = "76039576"
            };

            var response = await _httpClient.GetAsync(url, parameters, null, cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            var categories = new List<PlaylistCategory>
            {
                new PlaylistCategory
                {
                    Id = "all",
                    Name = "全部歌单",
                    Tags = new List<PlaylistTag> { new() { Id = "", Name = "全部" } }
                }
            };

            if (result.TryGetProperty("code", out var code) && code.GetInt32() == 200 &&
                result.TryGetProperty("data", out var groups) &&
                groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    var groupName = group.TryGetProperty("name", out var gn) ? gn.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(groupName)) continue;

                    var tagList = new List<PlaylistTag>();
                    if (group.TryGetProperty("data", out var tagData) && tagData.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tag in tagData.EnumerateArray())
                        {
                            var name = tag.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                            var id = tag.TryGetProperty("id", out var tid)
                                ? (tid.ValueKind == JsonValueKind.Number ? tid.GetInt64().ToString() : tid.GetString() ?? "")
                                : "";
                            if (!string.IsNullOrEmpty(name))
                                tagList.Add(new PlaylistTag { Id = id, Name = name });
                        }
                    }

                    if (tagList.Count > 0)
                        categories.Add(new PlaylistCategory { Id = groupName, Name = groupName, Tags = tagList });
                }
            }

            if (categories.Count <= 1)
                return GetDefaultCategories();

            return categories;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取酷我歌单分类失败，使用内置分类");
            return GetDefaultCategories();
        }
    }

    private static List<PlaylistCategory> GetDefaultCategories()
    {
        return new List<PlaylistCategory>
        {
            new() { Id = "all", Name = "全部歌单", Tags = new() { new() { Id = "", Name = "全部" } } },
            new() { Id = "1", Name = "主题", Tags = new() { new() { Id = "2189", Name = "短视频" }, new() { Id = "1265", Name = "经典" }, new() { Id = "2200", Name = "情歌" } } },
            new() { Id = "2", Name = "专区", Tags = new() { new() { Id = "211", Name = "经典老歌专区" }, new() { Id = "87", Name = "网红专区" }, new() { Id = "1", Name = "DJ专区" } } },
            new() { Id = "3", Name = "心情", Tags = new() { new() { Id = "146", Name = "伤感" } } }
        };
    }

    /// <summary>
    /// 按分类获取歌单列表 — 参考原版：getRcmPlayList(全部) / getTagPlayList(指定标签)
    /// </summary>
    public async Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string categoryId, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("获取酷我歌单: {Category}, 页码: {Page}", categoryId, page);

            var isAll = string.IsNullOrEmpty(categoryId) || categoryId == "all";
            var url = isAll
                ? "http://wapi.kuwo.cn/api/pc/classify/playlist/getRcmPlayList"
                : "http://wapi.kuwo.cn/api/pc/classify/playlist/getTagPlayList";

            var parameters = new Dictionary<string, string>
            {
                ["loginUid"] = "0", ["loginSid"] = "0", ["appUid"] = "76039576",
                ["pn"] = page.ToString(), ["rn"] = pageSize.ToString()
            };
            if (isAll)
                parameters["order"] = "hot";
            else
                parameters["id"] = categoryId;

            var response = await _httpClient.GetAsync(url, parameters, null, cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            var playlists = new List<Playlist>();
            int total = 0;

            if (result.TryGetProperty("code", out var code) && code.GetInt32() == 200 &&
                result.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("total", out var tEl))
                {
                    if (tEl.ValueKind == JsonValueKind.Number) total = tEl.GetInt32();
                    else int.TryParse(tEl.GetString() ?? "0", out total);
                }

                if (data.TryGetProperty("data", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idEl)
                            ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString() ?? "")
                            : "";
                        if (string.IsNullOrEmpty(id)) continue;

                        playlists.Add(new Playlist
                        {
                            Id = "kw_" + id,
                            Name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                            Description = item.TryGetProperty("desc", out var desc) ? desc.GetString() ?? "" : "",
                            Tags = new List<string>(),
                            Type = PlaylistType.Recommend,
                            CreatedDate = DateTime.Now,
                            ModifiedDate = DateTime.Now
                        });
                    }
                }
            }

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
            _logger.LogError(ex, "获取酷我歌单列表失败: {Category}", categoryId);
            return PagedResult<Playlist>.Empty(page, pageSize);
        }
    }

    /// <summary>
    /// 获取歌单详情 — 参考原版：nplserver pl.svc getlistinfo
    /// </summary>
    public async Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("获取酷我歌单详情: {PlaylistId}", playlistId);

            var url = "http://nplserver.kuwo.cn/pl.svc";
            var parameters = new Dictionary<string, string>
            {
                ["op"] = "getlistinfo", ["pid"] = playlistId,
                ["pn"] = (page - 1).ToString(), ["rn"] = pageSize.ToString(),
                ["encode"] = "utf8", ["keyset"] = "pl2012", ["identity"] = "kuwo",
                ["pcmp4"] = "1", ["vipver"] = "MUSIC_9.0.5.0_W1", ["newver"] = "1"
            };

            var response = await _httpClient.GetAsync(url, parameters, null, cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            var musicList = new List<MusicInfo>();
            int total = 0;

            if (result.TryGetProperty("result", out var res) && res.GetString() == "ok")
            {
                if (result.TryGetProperty("total", out var tEl))
                {
                    if (tEl.ValueKind == JsonValueKind.Number) total = tEl.GetInt32();
                    else int.TryParse(tEl.GetString() ?? "0", out total);
                }

                if (result.TryGetProperty("musiclist", out var musiclist) && musiclist.ValueKind == JsonValueKind.Array)
                {
                    foreach (var song in musiclist.EnumerateArray())
                    {
                        var songId = song.TryGetProperty("id", out var sid)
                            ? (sid.ValueKind == JsonValueKind.Number ? sid.GetInt64().ToString() : sid.GetString() ?? "")
                            : "";
                        var duration = song.TryGetProperty("duration", out var dur) ? dur.GetString() : null;

                        musicList.Add(new MusicInfo
                        {
                            Id = songId,
                            Title = song.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                            Artist = FormatSinger(song.TryGetProperty("artist", out var ar) ? ar.GetString() ?? "" : ""),
                            Album = song.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "",
                            Duration = ParseDuration(duration),
                            Source = "kw",
                            Quality = 128
                        });
                    }
                }
            }

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
            _logger.LogError(ex, "获取酷我歌单详情失败: {PlaylistId}", playlistId);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    /// <summary>
    /// 获取排行榜列表
    /// </summary>
    public async Task<List<Ranking>> GetRankingsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("获取酷我音乐排行榜");

        try
        {
            // 尝试从API获取排行榜列表
            var url = $"{BaseUrl}/api/www/bang/bang/bangMenu";
            var parameters = new Dictionary<string, string>
            {
                ["httpsStatus"] = "1"
            };

            var response = await _httpClient.GetAsync(url, parameters, null, cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            var rankings = new List<Ranking>();

            if (result.TryGetProperty("code", out var code) && code.GetInt32() == 200 &&
                result.TryGetProperty("data", out var data) && data.TryGetProperty("child", out var child))
            {
                foreach (var item in child.EnumerateArray())
                {
                    if (item.TryGetProperty("sourceid", out var sid))
                    {
                        rankings.Add(new Ranking
                        {
                            Id = $"kw_{sid.GetString()}",
                            Name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                            Description = "",
                            Source = "kw",
                            Type = RankingType.Hot,
                            UpdateDate = DateTime.Now
                        });
                    }
                }
            }

            if (rankings.Count > 0)
                return rankings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "从API获取酷我排行榜失败，使用内置列表");
        }

        // 内置排行榜列表
        return new List<Ranking>
        {
            new Ranking { Id = "kw_16", Name = "酷我热歌榜", Description = "酷我音乐热歌榜", Source = "kw", Type = RankingType.Hot, UpdateDate = DateTime.Now },
            new Ranking { Id = "kw_17", Name = "酷我新歌榜", Description = "酷我音乐新歌榜", Source = "kw", Type = RankingType.New, UpdateDate = DateTime.Now },
            new Ranking { Id = "kw_93", Name = "酷我飙升榜", Description = "酷我音乐飙升榜", Source = "kw", Type = RankingType.Rising, UpdateDate = DateTime.Now },
            new Ranking { Id = "kw_158", Name = "抖音热歌榜", Description = "抖音热歌排行榜", Source = "kw", Type = RankingType.Hot, UpdateDate = DateTime.Now },
            new Ranking { Id = "kw_26", Name = "经典怀旧榜", Description = "经典怀旧歌曲", Source = "kw", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "kw_104", Name = "华语榜", Description = "华语歌曲排行榜", Source = "kw", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "kw_22", Name = "欧美榜", Description = "欧美歌曲排行榜", Source = "kw", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "kw_183", Name = "日语榜", Description = "日语歌曲排行榜", Source = "kw", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "kw_184", Name = "韩语榜", Description = "韩语歌曲排行榜", Source = "kw", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "kw_187", Name = "流行趋势榜", Description = "流行趋势排行榜", Source = "kw", Type = RankingType.Other, UpdateDate = DateTime.Now }
        };
    }

    /// <summary>
    /// 获取排行榜详情 — kbangserver ksong.s（原版接口）
    /// </summary>
    public async Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("获取酷我排行榜详情: {RankingId}", rankingId);

            var url = "http://kbangserver.kuwo.cn/ksong.s";
            var parameters = new Dictionary<string, string>
            {
                ["from"] = "pc", ["fmt"] = "json",
                ["pn"] = (page - 1).ToString(), ["rn"] = pageSize.ToString(),
                ["type"] = "bang", ["data"] = "content",
                ["id"] = rankingId, ["show_copyright_off"] = "0",
                ["pcmp4"] = "1", ["isbang"] = "1"
            };

            var response = await _httpClient.GetAsync(url, parameters, null, cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            var musicList = new List<MusicInfo>();
            int total = 0;

            if (result.TryGetProperty("musiclist", out var musiclist) && musiclist.ValueKind == JsonValueKind.Array)
            {
                if (result.TryGetProperty("num", out var numEl))
                    int.TryParse(numEl.GetString() ?? "0", out total);

                foreach (var song in musiclist.EnumerateArray())
                {
                    var songId = song.TryGetProperty("id", out var sid)
                        ? (sid.ValueKind == JsonValueKind.Number ? sid.GetInt64().ToString() : sid.GetString() ?? "")
                        : "";
                    var duration = song.TryGetProperty("song_duration", out var sd) ? sd.GetString() : null;
                    if (string.IsNullOrEmpty(duration) || duration == "0")
                        duration = song.TryGetProperty("duration", out var d2) ? d2.GetString() : null;

                    var albumId = song.TryGetProperty("albumid", out var aid) ? aid.GetString() ?? "" : "";

                    musicList.Add(new MusicInfo
                    {
                        Id = songId,
                        Title = song.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                        Artist = FormatSinger(song.TryGetProperty("artist", out var ar) ? ar.GetString() ?? "" : ""),
                        Album = song.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "",
                        Duration = ParseDuration(duration),
                        Source = "kw",
                        CoverUrl = !string.IsNullOrEmpty(albumId)
                            ? $"https://img2.kuwo.cn/star/albumcover/{albumId}.jpg"
                            : "",
                        Quality = 128
                    });
                }
            }

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
            _logger.LogError(ex, "获取酷我排行榜详情失败: {RankingId}", rankingId);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    // ========== 辅助方法 ==========

    private MusicInfo MapKuwoSongToMusicInfo(JsonElement song)
    {
        var songId = song.TryGetProperty("musicrid", out var rid) && rid.ValueKind == JsonValueKind.String
            ? rid.GetString()?.Replace("MUSIC_", "") ?? ""
            : song.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";

        return new MusicInfo
        {
            Id = songId,
            Title = song.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
            Artist = FormatSinger(song.TryGetProperty("artist", out var ar) ? ar.GetString() ?? "" : ""),
            Album = song.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "",
            Duration = ParseDuration(song.TryGetProperty("duration", out var dur) ? dur.GetString() : null),
            Source = "kw",
            CoverUrl = song.TryGetProperty("pic", out var pic) ? pic.GetString() ?? "" : "",
            Quality = 128
        };
    }

    private static string FormatSinger(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "未知歌手";
        // 酷我的分隔符可能是 & 或 /
        return raw.Replace("&", "、").Replace("/", "、");
    }

    private static string ParseDuration(string? durationStr)
    {
        if (string.IsNullOrEmpty(durationStr)) return "00:00";
        if (int.TryParse(durationStr, out var seconds))
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.Hours > 0 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
        }
        return "00:00";
    }

}
