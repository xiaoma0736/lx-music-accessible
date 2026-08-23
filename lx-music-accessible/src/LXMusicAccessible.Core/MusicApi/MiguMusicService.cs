using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Linq;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 咪咕音乐服务 — 实现 IMusicApi 统一接口
/// 使用 pd.musicapp.migu.cn 公开API
/// </summary>
public class MiguMusicService : IMusicApi
{
    private readonly ILogger<MiguMusicService> _logger;
    private readonly MusicHttpClient _httpClient;
    private const string ApiBase = "https://pd.musicapp.migu.cn/MIGUM2.0/v1.0/content";

    public string SourceId => "migu";
    public string SourceName => "咪咕音乐";

    public MiguMusicService(ILogger<MiguMusicService> logger, MusicHttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    /// <summary>搜索音乐</summary>
    public async Task<PagedResult<MusicInfo>> SearchAsync(string keyword, int page = 1, int pageSize = 30, string searchType = "song")
    {
        try
        {
            _logger.LogInformation("咪咕音乐搜索: {Keyword}, 页码: {Page}", keyword, page);

            if (string.Equals(searchType, "album", StringComparison.OrdinalIgnoreCase))
                return await SearchAlbumsAsync(keyword, page, pageSize);

            var url = $"{ApiBase}/search_all.do";
            var parameters = new Dictionary<string, string>
            {
                ["ua"] = "Android_migu", ["version"] = "5.0.1",
                ["text"] = keyword, ["pageNo"] = page.ToString(),
                ["pageSize"] = pageSize.ToString(),
                ["searchSwitch"] = "{\"song\":1,\"album\":0,\"singer\":0,\"tagSong\":0,\"mvSong\":0,\"songlist\":0,\"bestShow\":1}"
            };
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Linux; Android 11) AppleWebKit/537.36",
                ["Referer"] = "https://music.migu.cn/"
            };

            var response = await _httpClient.GetAsync(url, parameters, headers);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            if (result.TryGetProperty("code", out var code) && code.GetString() == "000000" &&
                result.TryGetProperty("songResultData", out var songData))
            {
                var total = songData.TryGetProperty("totalCount", out var tc) ? (tc.ValueKind == JsonValueKind.String ? int.Parse(tc.GetString() ?? "0") : tc.GetInt32()) : 0;
                var musicList = new List<MusicInfo>();
                if (songData.TryGetProperty("result", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var song in results.EnumerateArray())
                        musicList.Add(MapMiguSongFromApi(song));
                }
                return new PagedResult<MusicInfo> { CurrentPage = page, PageSize = pageSize, TotalItems = total, Items = musicList };
            }
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
        catch (Exception ex) { _logger.LogError(ex, "咪咕音乐搜索失败: {Keyword}", keyword); return PagedResult<MusicInfo>.Empty(page, pageSize); }
    }

    // ========== 专辑搜索 / 专辑详情 ==========

    /// <summary>咪咕专辑搜索：search_all.do 专辑开关 → albumResultData.result[]</summary>
    private async Task<PagedResult<MusicInfo>> SearchAlbumsAsync(string keyword, int page, int pageSize)
    {
        try
        {
            var url = $"{ApiBase}/search_all.do";
            var parameters = new Dictionary<string, string>
            {
                ["ua"] = "Android_migu", ["version"] = "5.0.1",
                ["text"] = keyword, ["pageNo"] = page.ToString(),
                ["pageSize"] = pageSize.ToString(),
                ["searchSwitch"] = "{\"song\":0,\"album\":1,\"singer\":0,\"tagSong\":0,\"mvSong\":0,\"songlist\":0,\"bestShow\":0}"
            };
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Linux; Android 11) AppleWebKit/537.36",
                ["Referer"] = "https://music.migu.cn/"
            };
            var response = await _httpClient.GetAsync(url, parameters, headers);
            var result = JsonSerializer.Deserialize<JsonElement>(response);
            var list = new List<MusicInfo>();
            int total = 0;

            if (result.TryGetProperty("code", out var code) && code.GetString() == "000000" &&
                result.TryGetProperty("albumResultData", out var albumData))
            {
                if (albumData.TryGetProperty("totalCount", out var tc))
                    total = tc.ValueKind == JsonValueKind.String ? int.Parse(tc.GetString() ?? "0") : tc.GetInt32();
                if (albumData.TryGetProperty("result", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var alb in results.EnumerateArray())
                    {
                        var id = JsonHelpers.GetString(alb, "id");
                        if (string.IsNullOrEmpty(id)) continue;
                        var name = JsonHelpers.GetString(alb, "name") ?? "未知专辑";
                        var artist = JsonHelpers.GetString(alb, "singer") ?? "";
                        var cover = "";
                        if (alb.TryGetProperty("imgItems", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var im in imgs.EnumerateArray())
                            {
                                cover = JsonHelpers.GetString(im, "img") ?? "";
                                if (cover.Length > 0) break;
                            }
                        }
                        list.Add(JsonHelpers.BuildAlbumRow(id!, name, artist, "migu", cover));
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
            _logger.LogError(ex, "咪咕专辑搜索失败: {Keyword}", keyword);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    /// <summary>
    /// 咪咕专辑详情：原生专辑接口不可用，改用“按专辑名搜歌曲并过滤专辑”兜底。
    /// </summary>
    public async Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50)
    {
        try
        {
            if (album == null || string.IsNullOrEmpty(album.Title))
                return PagedResult<MusicInfo>.Empty(page, pageSize);

            var url = $"{ApiBase}/search_all.do";
            var parameters = new Dictionary<string, string>
            {
                ["ua"] = "Android_migu", ["version"] = "5.0.1",
                ["text"] = album.Title, ["pageNo"] = page.ToString(),
                ["pageSize"] = pageSize.ToString(),
                ["searchSwitch"] = "{\"song\":1,\"album\":0,\"singer\":0,\"tagSong\":0,\"mvSong\":0,\"songlist\":0,\"bestShow\":1}"
            };
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Linux; Android 11) AppleWebKit/537.36",
                ["Referer"] = "https://music.migu.cn/"
            };
            var response = await _httpClient.GetAsync(url, parameters, headers);
            var result = JsonSerializer.Deserialize<JsonElement>(response);
            var list = new List<MusicInfo>();

            if (result.TryGetProperty("code", out var code) && code.GetString() == "000000" &&
                result.TryGetProperty("songResultData", out var songData) &&
                songData.TryGetProperty("result", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var song in results.EnumerateArray())
                {
                    var m = MapMiguSongFromApi(song);
                    if (string.IsNullOrEmpty(m.Id)) continue;
                    if (!JsonHelpers.MatchesAlbum(m.Album, album.Title)) continue;
                    list.Add(m);
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
            _logger.LogError(ex, "咪咕专辑详情失败: {Album}", album?.Title ?? "?");
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
            _logger.LogInformation("获取咪咕音乐播放URL: {MusicId}", musicId);
            var url = $"{ApiBase}/song_info";
            var parameters = new Dictionary<string, string> { ["songId"] = musicId, ["rateType"] = "3" };
            var headers = new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0 (Linux; Android 11) AppleWebKit/537.36" };
            var response = await _httpClient.GetAsync(
                url,
                parameters,
                headers,
                cancellationToken);
            var data = JsonSerializer.Deserialize<JsonElement>(response);
            if (data.TryGetProperty("code", out var c) && c.GetString() == "000000" &&
                data.TryGetProperty("data", out var d) && d.TryGetProperty("url", out var u))
                return u.GetString();
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

    /// <summary>获取歌词：优先直取搜索结果自带的 lyricUrl（实测可用），song_lyric/v3 仅作兜底</summary>
    public async Task<Lyric?> GetLyricAsync(MusicInfo music)
    {
        var musicId = music?.Id ?? "";
        try
        {
            // 1) 搜索结果自带 lyricUrl（d.musicapp.migu.cn 直链，实测可用）
            if (!string.IsNullOrEmpty(music?.LyricUrl))
            {
                var lrc = await _httpClient.GetAsync(music.LyricUrl);
                var parsed = ParseLrc(lrc);
                if (parsed != null) return parsed;
            }

            // 2) 旧接口兜底（路由可能不支持，失败继续）
            var url = $"{ApiBase}/song_lyric";
            var parameters = new Dictionary<string, string> { ["songId"] = musicId };
            var headers = new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0 (Linux; Android 11) AppleWebKit/537.36", ["Referer"] = "https://music.migu.cn/" };
            var response = await _httpClient.GetAsync(url, parameters, headers);
            var data = JsonSerializer.Deserialize<JsonElement>(response);
            if (data.TryGetProperty("code", out var c) && c.GetString() == "000000" &&
                data.TryGetProperty("data", out var d) && d.TryGetProperty("lyric", out var l))
                return ParseLrc(l.GetString());
            _logger.LogWarning("咪咕 song_lyric 歌词失败: {MusicId}", musicId);
            return await GetLyricFallbackAsync(musicId);
        }
        catch (Exception ex) { _logger.LogError(ex, "获取咪咕歌词失败: {MusicId}", musicId); return await GetLyricFallbackAsync(musicId); }
    }

    /// <summary>咪咕 v3 歌词接口回退（copyrightId 兜底）</summary>
    private async Task<Lyric?> GetLyricFallbackAsync(string musicId)
    {
        try
        {
            var url = "https://music.migu.cn/v3/api/music/audioPlayer/getLyric";
            var parameters = new Dictionary<string, string> { ["copyrightId"] = musicId };
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
                ["Referer"] = "https://music.migu.cn/"
            };
            var response = await _httpClient.GetAsync(url, parameters, headers);
            var data = JsonSerializer.Deserialize<JsonElement>(response);
            if (data.TryGetProperty("code", out var c) && c.GetString() == "000000" &&
                data.TryGetProperty("data", out var d) && d.TryGetProperty("lyric", out var l))
                return ParseLrc(l.GetString());
            return null;
        }
        catch (Exception ex) { _logger.LogError(ex, "咪咕 v3 歌词回退失败: {MusicId}", musicId); return null; }
    }

    /// <summary>获取歌单分类 — 参考原版：musiclistplaza-taglist（扁平标签）</summary>
    public async Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync()
    {
        _logger.LogInformation("获取{Name}歌单分类", SourceName);

        try
        {
            var url = "https://app.c.nf.migu.cn/pc/v1.0/template/musiclistplaza-taglist/release";
            var headers = new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15" };
            var response = await _httpClient.GetAsync(url, null, headers);
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

            var tagList = new List<PlaylistTag>();
            if (data.TryGetProperty("code", out var code) && code.GetString() == "000000" &&
                data.TryGetProperty("data", out var d) &&
                d.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (!item.TryGetProperty("texts", out var texts) || texts.ValueKind != JsonValueKind.Array || texts.GetArrayLength() < 2)
                        continue;
                    var name = texts[0].GetString() ?? "";
                    var id = texts[1].GetString() ?? "";
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id))
                        tagList.Add(new PlaylistTag { Id = id, Name = name });
                }
            }

            categories.Add(new PlaylistCategory
            {
                Id = "tags",
                Name = "热门标签",
                Tags = tagList.Count > 0 ? tagList : DefaultTagList()
            });

            return categories;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取咪咕歌单分类失败，使用内置分类");
            return GetDefaultCategories();
        }
    }

    private static List<PlaylistCategory> GetDefaultCategories()
    {
        return new List<PlaylistCategory>
        {
            new() { Id = "all", Name = "全部歌单", Tags = new() { new() { Id = "", Name = "全部" } } },
            new() { Id = "tags", Name = "热门标签", Tags = DefaultTagList() }
        };
    }

    private static List<PlaylistTag> DefaultTagList() => new()
    {
        new() { Id = "1001076096", Name = "网络热歌" },
        new() { Id = "1000001672", Name = "流行" },
        new() { Id = "1000001635", Name = "经典老歌" },
        new() { Id = "1000001775", Name = "民谣" },
        new() { Id = "1000001675", Name = "中国风" },
        new() { Id = "1000001827", Name = "KTV金曲" },
        new() { Id = "1000001777", Name = "欧美" }
    };

    /// <summary>按分类获取歌单列表 — 参考原版：square-recommend(全部) / listbytag(指定标签)</summary>
    public async Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string categoryId, int page = 1, int pageSize = 30)
    {
        try
        {
            var headers = new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15" };
            var playlists = new List<Playlist>();

            if (string.IsNullOrEmpty(categoryId) || categoryId == "all")
            {
                var url = $"https://app.c.nf.migu.cn/pc/bmw/page-data/playlist-square-recommend/v1.0?templateVersion=2&pageNo={page}";
                var response = await _httpClient.GetAsync(url, null, headers);
                var data = JsonSerializer.Deserialize<JsonElement>(response);
                CollectMiguPlaylists(data, playlists);
                return new PagedResult<Playlist> { CurrentPage = page, PageSize = pageSize, TotalItems = playlists.Count, Items = playlists };
            }

            var listUrl = $"https://app.c.nf.migu.cn/pc/v1.0/template/musiclistplaza-listbytag/release?pageNumber={page}&templateVersion=2&tagId={categoryId}";
            var listResponse = await _httpClient.GetAsync(listUrl, null, headers);
            var listData = JsonSerializer.Deserialize<JsonElement>(listResponse);

            if (listData.TryGetProperty("data", out var d))
            {
                if (d.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array)
                {
                    CollectMiguPlaylists(contents, playlists);
                }
                else if (d.TryGetProperty("contentItemList", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in items.EnumerateArray())
                    {
                        if (!block.TryGetProperty("itemList", out var il) || il.ValueKind != JsonValueKind.Array) continue;
                        foreach (var item in il.EnumerateArray())
                        {
                            string id = "", name = "";
                            if (item.TryGetProperty("logEvent", out var le) && le.TryGetProperty("contentId", out var cid))
                                id = cid.GetString() ?? "";
                            if (item.TryGetProperty("title", out var t)) name = t.GetString() ?? "";
                            if (!string.IsNullOrEmpty(id))
                                playlists.Add(new Playlist
                                {
                                    Id = "migu_" + id,
                                    Name = name,
                                    Description = "",
                                    Tags = new List<string>(),
                                    Type = PlaylistType.Recommend,
                                    CreatedDate = DateTime.Now,
                                    ModifiedDate = DateTime.Now
                                });
                        }
                    }
                }
            }

            // 标签接口不返回总数，交由 UI 估算分页
            return new PagedResult<Playlist> { CurrentPage = page, PageSize = pageSize, TotalItems = 0, Items = playlists };
        }
        catch (Exception ex) { _logger.LogError(ex, "获取咪咕歌单失败"); return PagedResult<Playlist>.Empty(page, pageSize); }
    }

    /// <summary>递归收集模板响应中的歌单节点（resType=="2021"）</summary>
    private static void CollectMiguPlaylists(JsonElement root, List<Playlist> playlists)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray()) CollectMiguPlaylists(el, playlists);
            return;
        }
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("resType", out var rt) && rt.GetString() == "2021" &&
            root.TryGetProperty("resId", out var rid))
        {
            var id = rid.GetString() ?? "";
            if (!string.IsNullOrEmpty(id))
            {
                var name = root.TryGetProperty("txt", out var t) ? t.GetString() ?? "" : "";
                var desc = root.TryGetProperty("txt2", out var t2) ? t2.GetString() ?? "" : "";
                playlists.Add(new Playlist
                {
                    Id = "migu_" + id,
                    Name = name,
                    Description = desc,
                    Tags = new List<string>(),
                    Type = PlaylistType.Recommend,
                    CreatedDate = DateTime.Now,
                    ModifiedDate = DateTime.Now
                });
            }
            return;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name is "contents" or "contentItemList" or "itemList" or "data")
                CollectMiguPlaylists(prop.Value, playlists);
        }
    }

    /// <summary>获取歌单详情 — 参考原版：MIGUM3.0/resource/playlist/song/v2.0</summary>
    public async Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50)
    {
        try
        {
            var url = $"https://app.c.nf.migu.cn/MIGUM3.0/resource/playlist/song/v2.0?pageNo={page}&pageSize={pageSize}&playlistId={playlistId}";
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15",
                ["Referer"] = "https://m.music.migu.cn/"
            };
            var response = await _httpClient.GetAsync(url, null, headers);
            var data = JsonSerializer.Deserialize<JsonElement>(response);
            var musicList = new List<MusicInfo>(); int total = 0;
            if (data.TryGetProperty("code", out var c) && c.GetString() == "000000" && data.TryGetProperty("data", out var d))
            {
                if (d.TryGetProperty("totalCount", out var t))
                {
                    if (t.ValueKind == JsonValueKind.Number) total = t.GetInt32();
                    else int.TryParse(t.GetString() ?? "0", out total);
                }
                if (d.TryGetProperty("songList", out var songs) && songs.ValueKind == JsonValueKind.Array)
                    foreach (var s in songs.EnumerateArray()) musicList.Add(MapMiguPlaylistSong(s));
            }
            return new PagedResult<MusicInfo> { CurrentPage = page, PageSize = pageSize, TotalItems = total, Items = musicList };
        }
        catch (Exception ex) { _logger.LogError(ex, "获取咪咕歌单详情失败"); return PagedResult<MusicInfo>.Empty(page, pageSize); }
    }

    private static MusicInfo MapMiguPlaylistSong(JsonElement song)
    {
        var singerName = "";
        if (song.TryGetProperty("singerList", out var singers) && singers.ValueKind == JsonValueKind.Array)
            singerName = string.Join("、", singers.EnumerateArray()
                .Select(s => s.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "")
                .Where(n => !string.IsNullOrEmpty(n)));

        var albumName = "";
        if (song.TryGetProperty("album", out var al))
        {
            if (al.ValueKind == JsonValueKind.Object && al.TryGetProperty("name", out var aln)) albumName = aln.GetString() ?? "";
            else if (al.ValueKind == JsonValueKind.String) albumName = al.GetString() ?? "";
        }
        if (string.IsNullOrEmpty(albumName) && song.TryGetProperty("albumName", out var an))
            albumName = an.GetString() ?? "";

        var duration = song.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetInt32() : 0;

        var id = song.TryGetProperty("songId", out var sid)
            ? (sid.ValueKind == JsonValueKind.Number ? sid.GetInt64().ToString() : sid.GetString() ?? "")
            : song.TryGetProperty("contentId", out var cid) ? cid.GetString() ?? "" : "";

        return new MusicInfo
        {
            Id = id,
            Title = song.TryGetProperty("songName", out var sn2) ? sn2.GetString() ?? "" : "",
            Artist = singerName.Length > 0 ? singerName : "未知歌手",
            Album = albumName,
            Duration = FmtDur(duration),
            Source = "migu",
            Quality = 128
        };
    }

    /// <summary>获取排行榜列表 — 参考原版：pc/bmw/rank/rank-index</summary>
    public async Task<List<Ranking>> GetRankingsAsync()
    {
        try
        {
            var url = "https://app.c.nf.migu.cn/pc/bmw/rank/rank-index/v1.0";
            var headers = new Dictionary<string, string>
            {
                ["Referer"] = "https://app.c.nf.migu.cn/",
                ["User-Agent"] = "Mozilla/5.0 (Linux; Android 5.1.1; Nexus 6 Build/LYZ28E) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/59.0.3071.115 Mobile Safari/537.36",
                ["channel"] = "0146921"
            };
            var response = await _httpClient.GetAsync(url, null, headers);
            var data = JsonSerializer.Deserialize<JsonElement>(response);
            var rankings = new List<Ranking>();
            if (data.TryGetProperty("code", out var c) && c.GetString() == "000000" && data.TryGetProperty("data", out var d))
                CollectMiguRankings(d, rankings);
            if (rankings.Count > 0) return rankings;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "从API获取咪咕排行榜失败"); }
        return DefaultRankings();
    }

    /// <summary>递归收集排行榜节点（rankId/rankName）</summary>
    private static void CollectMiguRankings(JsonElement root, List<Ranking> rankings)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray()) CollectMiguRankings(el, rankings);
            return;
        }
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("rankId", out var rid) && root.TryGetProperty("rankName", out var rn))
        {
            var id = rid.ValueKind == JsonValueKind.Number ? rid.GetInt64().ToString() : rid.GetString() ?? "";
            if (!string.IsNullOrEmpty(id))
            {
                rankings.Add(new Ranking
                {
                    Id = "migu_" + id,
                    Name = rn.GetString() ?? "",
                    Source = "migu",
                    Type = RankingType.Hot,
                    UpdateDate = DateTime.Now
                });
            }
            return;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name is "contents" or "contentItemList" or "itemList" or "data")
                CollectMiguRankings(prop.Value, rankings);
        }
    }

    /// <summary>获取排行榜详情 — querycontentbyId.do → columnInfo.contents[].objectInfo</summary>
    public async Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50)
    {
        try
        {
            var url = $"https://app.c.nf.migu.cn/MIGUM2.0/v1.0/content/querycontentbyId.do?columnId={rankingId}&needAll=0";
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15",
                ["Referer"] = "https://m.music.migu.cn/"
            };
            var response = await _httpClient.GetAsync(url, null, headers);
            var data = JsonSerializer.Deserialize<JsonElement>(response);
            var allSongs = new List<MusicInfo>();
            if (data.TryGetProperty("code", out var c) && c.GetString() == "000000" &&
                data.TryGetProperty("columnInfo", out var ci) &&
                ci.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contents.EnumerateArray())
                {
                    if (!item.TryGetProperty("objectInfo", out var oi)) continue;
                    allSongs.Add(MapMiguRankingSong(oi));
                }
            }

            var total = allSongs.Count;
            var items = allSongs.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return new PagedResult<MusicInfo> { CurrentPage = page, PageSize = pageSize, TotalItems = total, Items = items };
        }
        catch (Exception ex) { _logger.LogError(ex, "获取咪咕排行榜详情失败"); return PagedResult<MusicInfo>.Empty(page, pageSize); }
    }

    private static MusicInfo MapMiguRankingSong(JsonElement song)
    {
        var singerName = song.TryGetProperty("singer", out var sg) ? sg.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(singerName) && song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
            singerName = string.Join("、", artists.EnumerateArray()
                .Select(a => a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "")
                .Where(n => !string.IsNullOrEmpty(n)));

        var albumName = song.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "";

        var id = song.TryGetProperty("songId", out var sid)
            ? (sid.ValueKind == JsonValueKind.Number ? sid.GetInt64().ToString() : sid.GetString() ?? "")
            : song.TryGetProperty("contentId", out var cid) ? cid.GetString() ?? "" : "";

        var coverUrl = "";
        if (song.TryGetProperty("albumImgs", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0)
            coverUrl = imgs[0].TryGetProperty("img", out var img) ? img.GetString() ?? "" : "";

        return new MusicInfo
        {
            Id = id,
            Title = song.TryGetProperty("songName", out var sn) ? sn.GetString() ?? "" : "",
            Artist = singerName.Length > 0 ? singerName : "未知歌手",
            Album = albumName,
            Duration = NormalizeMiguLength(song.TryGetProperty("length", out var ln) ? ln.GetString() : null),
            Source = "migu",
            CoverUrl = coverUrl,
            Quality = 128
        };
    }

    private static string NormalizeMiguLength(string? length)
    {
        if (string.IsNullOrEmpty(length)) return "00:00";
        var parts = length.Split(':');
        if (parts.Length == 3 && int.TryParse(parts[0], out var h) && h == 0)
            return $"{parts[1]}:{parts[2]}";
        return length;
    }

    // ========== 辅助 ==========

    private static MusicInfo MapMiguSongFromApi(JsonElement song)
    {
        var singerName = "";
        if (song.TryGetProperty("singers", out var singers) && singers.ValueKind == JsonValueKind.Array)
            singerName = string.Join("、", singers.EnumerateArray().Select(s => s.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "").Where(n => !string.IsNullOrEmpty(n)));
        if (string.IsNullOrEmpty(singerName))
            singerName = song.TryGetProperty("singerName", out var sni) ? sni.GetString() ?? "" : song.TryGetProperty("singer", out var sg) ? sg.GetString() ?? "" : "";

        var coverUrl = "";
        if (song.TryGetProperty("imgItems", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0)
            coverUrl = "http://d.musicapp.migu.cn" + (imgs[0].TryGetProperty("img", out var img) ? img.GetString() ?? "" : "");
        if (string.IsNullOrEmpty(coverUrl) || coverUrl == "http://d.musicapp.migu.cn")
            coverUrl = song.TryGetProperty("cover", out var cv) ? cv.GetString() ?? "" : "";

        return new MusicInfo
        {
            Id = song.TryGetProperty("id", out var id) ? id.GetString() ?? "" : song.TryGetProperty("copyrightId", out var cid) ? cid.GetString() ?? "" : "",
            Title = song.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : song.TryGetProperty("songName", out var sn) ? sn.GetString() ?? "" : "",
            Artist = singerName.Length > 0 ? singerName : "未知歌手",
            Album = song.TryGetProperty("albums", out var albs) && albs.ValueKind == JsonValueKind.Array && albs.GetArrayLength() > 0 ? (albs[0].TryGetProperty("name", out var aln) ? aln.GetString() ?? "" : "") : song.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "",
            Duration = song.TryGetProperty("duration", out var dur) ? FmtDur(dur.GetInt32()) : "00:00",
            Source = "migu", CoverUrl = coverUrl, Quality = 128,
            LyricUrl = song.TryGetProperty("lyricUrl", out var lurl) ? lurl.GetString() ?? "" : ""
        };
    }

    private static string FmtDur(int sec) { var ts = TimeSpan.FromSeconds(sec); return ts.Hours > 0 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss"); }

    private static List<PlaylistTag> Tags(params string[] names) => names.Select(n => new PlaylistTag { Id = n, Name = n }).ToList();

    private static List<Ranking> DefaultRankings() => new()
    {
        new() { Id = "migu_hot", Name = "咪咕热歌榜", Source = "migu", Type = RankingType.Hot },
        new() { Id = "migu_new", Name = "咪咕新歌榜", Source = "migu", Type = RankingType.New },
        new() { Id = "migu_rising", Name = "咪咕飙升榜", Source = "migu", Type = RankingType.Rising },
        new() { Id = "migu_original", Name = "咪咕原创榜", Source = "migu", Type = RankingType.Original },
        new() { Id = "migu_show", Name = "咪咕影视综艺", Source = "migu", Type = RankingType.Other },
        new() { Id = "migu_dj", Name = "咪咕DJ热榜", Source = "migu", Type = RankingType.Other }
    };

    private static Lyric? ParseLrc(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var lines = new List<LyricLine>();
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^\[(\d{2}):(\d{2})(?:\.(\d+))?\](.*)");
            if (m.Success)
                lines.Add(new LyricLine { Time = new TimeSpan(0, 0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value.PadRight(3,'0')[..3]) : 0), Text = m.Groups[4].Value.Trim() });
        }
        return lines.Count > 0 ? new Lyric { Lines = lines } : null;
    }

}
