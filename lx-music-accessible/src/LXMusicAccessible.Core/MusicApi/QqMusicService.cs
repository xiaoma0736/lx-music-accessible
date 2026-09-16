using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Text.Json;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// QQ音乐服务 — 实现 IMusicApi 统一接口
/// </summary>
public class QqMusicService : IMusicApi
{
    private readonly ILogger<QqMusicService> _logger;
    private readonly MusicHttpClient _httpClient;
    private const string BaseUrl = "https://c.y.qq.com";
    private const string UUrl = "https://u.y.qq.com";

    public string SourceId => "qq";
    public string SourceName => "QQ音乐";

    public QqMusicService(ILogger<QqMusicService> logger, MusicHttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    /// <summary>
    /// 搜索音乐
    /// </summary>
    public async Task<PagedResult<MusicInfo>> SearchAsync(string keyword, int page = 1, int pageSize = 30, string searchType = "song", CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("QQ音乐搜索: {Keyword}, 页码: {Page}", keyword, page);

            if (string.Equals(searchType, "album", StringComparison.OrdinalIgnoreCase))
                return await SearchAlbumsAsync(keyword, page, pageSize, cancellationToken);

            var url = $"{BaseUrl}/soso/fcgi-bin/client_search_cp";
            var parameters = new Dictionary<string, string>
            {
                ["w"] = keyword,
                ["p"] = page.ToString(),
                ["n"] = pageSize.ToString(),
                ["format"] = "json",
                ["t"] = "0",
                ["aggr"] = "1",
                ["lossless"] = "1",
                ["cr"] = "1"
            };

            var searchHeaders = new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                ["Referer"] = "https://y.qq.com/",
                ["Cookie"] = "uin=0; qm_keyst=Q_H_L_0; qqmusic_fromtag=0"
            };
            var response = await _httpClient.GetAsync(url, parameters, searchHeaders, cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            var codeOk = false;
            if (result.TryGetProperty("code", out var code))
            {
                codeOk = code.ValueKind switch
                {
                    JsonValueKind.Number => code.GetInt32() == 0,
                    JsonValueKind.String => code.GetString() == "0",
                    _ => false
                };
            }
            if (codeOk &&
                result.TryGetProperty("data", out var data) &&
                data.TryGetProperty("song", out var songData))
            {
                var total = songData.TryGetProperty("totalnum", out var tn) ? tn.GetInt32() : 0;
                var musicList = new List<MusicInfo>();

                if (songData.TryGetProperty("list", out var list))
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        var songId = item.TryGetProperty("songid", out var sid) ? sid.GetInt64().ToString() : "";
                        var songMid = item.TryGetProperty("songmid", out var smid) ? smid.GetString() ?? "" : "";

                        var singerName = "";
                        if (item.TryGetProperty("singer", out var singers))
                        {
                            if (singers.ValueKind == JsonValueKind.Array)
                            {
                                var names = singers.EnumerateArray()
                                    .Select(s => s.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "")
                                    .Where(n => !string.IsNullOrEmpty(n));
                                singerName = string.Join("、", names);
                            }
                            else if (singers.TryGetProperty("name", out var sn))
                            {
                                singerName = sn.GetString() ?? "";
                            }
                        }
                        if (string.IsNullOrEmpty(singerName))
                        {
                            if (item.TryGetProperty("singer", out var sng2) && sng2.ValueKind == JsonValueKind.String)
                                singerName = sng2.GetString() ?? "";
                            if (string.IsNullOrEmpty(singerName))
                                singerName = item.TryGetProperty("singername", out var sn3) ? sn3.GetString() ?? "" : "";
                        }

                        var albumMid = "";
                        if (item.TryGetProperty("album", out var album) &&
                            album.TryGetProperty("mid", out var amid))
                            albumMid = amid.GetString() ?? "";

                        var interval = item.TryGetProperty("interval", out var inv) ? inv.GetInt32() : 0;
                        var albumName = item.TryGetProperty("album", out var al) && al.TryGetProperty("name", out var aln)
                            ? aln.GetString() ?? "" : "";

                        musicList.Add(new MusicInfo
                        {
                            Id = songMid.Length > 0 ? songMid : songId,
                        Songmid = songMid,
                            Title = item.TryGetProperty("songname", out var sname) && sname.ValueKind == JsonValueKind.String ? sname.GetString() ?? ""
                                : item.TryGetProperty("name", out var nm2) ? nm2.GetString() ?? "" : "",
                            Artist = singerName.Length > 0 ? singerName :
                                item.TryGetProperty("singer", out var sng) ? sng.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "" : "",
                            Album = albumName,
                            Duration = interval > 0 ? FormatDuration(interval) : "00:00",
                            Source = "qq",
                            CoverUrl = albumMid.Length > 0
                                ? $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg"
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

            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QQ音乐搜索失败: {Keyword}", keyword);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    // ========== 专辑搜索 / 专辑详情 ==========

    /// <summary>QQ 专辑搜索：musicu.fcg DoSearchForQQMusicMobile search_type=2，comm 需带 QIMEI 设备标识 → body.item_album[]</summary>
    private async Task<PagedResult<MusicInfo>> SearchAlbumsAsync(string keyword, int page, int pageSize, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{UUrl}/cgi-bin/musicu.fcg";
            var guid = Guid.NewGuid().ToString("N");
            var (q16, q36) = GenerateQimei();
            var payload = new Dictionary<string, object?>
            {
                ["comm"] = new Dictionary<string, object?>
                {
                    ["ct"] = 11,
                    ["cv"] = 14090008,
                    ["v"] = 14090008,
                    ["chid"] = "10003505",
                    ["tmeAppID"] = "qqmusic",
                    ["phonetype"] = "EBG-AN10",
                    ["deviceScore"] = "553.47",
                    ["devicelevel"] = "32",
                    ["newdevicelevel"] = "32",
                    ["rom"] = "HuaWei/EMOTION/EmotionUI_14.2.0",
                    ["os_ver"] = "12",
                    ["OpenUDID"] = guid,
                    ["OpenUDID2"] = guid,
                    ["udid"] = guid,
                    ["uid"] = "",
                    ["sid"] = "",
                    ["aid"] = guid.Substring(0, 16),
                    ["QIMEI"] = q16,
                    ["QIMEI36"] = q36
                },
                ["req_0"] = new Dictionary<string, object?>
                {
                    ["module"] = "music.search.SearchCgiService",
                    ["method"] = "DoSearchForQQMusicMobile",
                    ["param"] = new Dictionary<string, object?>
                    {
                        ["searchid"] = GetSearchId(),
                        ["query"] = keyword,
                        ["search_type"] = 2,
                        ["num_per_page"] = pageSize,
                        ["page_num"] = page,
                        ["highlight"] = 1,
                        ["grp"] = 1,
                        ["selectors"] = new Dictionary<string, object?>(),
                        ["vec_selectors"] = Array.Empty<object?>()
                    }
                }
            };

            var response = await _httpClient.PostAsync<JsonElement>(url, payload, null, cancellationToken);
            var list = new List<MusicInfo>();
            int total = 0;

            if (response.TryGetProperty("req_0", out var req0) &&
                req0.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("meta", out var meta) &&
                    meta.TryGetProperty("estimate_sum", out var est) &&
                    est.ValueKind == JsonValueKind.Number)
                    total = est.GetInt32();

                if (data.TryGetProperty("body", out var body) &&
                    body.TryGetProperty("item_album", out var albumList) &&
                    albumList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in albumList.EnumerateArray())
                    {
                        var mid = JsonHelpers.GetString(item, "albummid") ?? JsonHelpers.GetString(item, "albumMid") ?? JsonHelpers.GetString(item, "mid");
                        if (string.IsNullOrEmpty(mid)) continue;
                        var name = StripHtml(JsonHelpers.GetString(item, "name") ?? "未知专辑");
                        var artist = "";
                        if (item.TryGetProperty("singer_list", out var sl) && sl.ValueKind == JsonValueKind.Array)
                            artist = string.Join("/", sl.EnumerateArray().Select(a => JsonHelpers.GetString(a, "name") ?? "").Where(n => n.Length > 0));
                        if (string.IsNullOrEmpty(artist))
                            artist = StripHtml(JsonHelpers.GetString(item, "singer") ?? "");
                        list.Add(JsonHelpers.BuildAlbumRow(mid!, name, artist, "qq",
                            JsonHelpers.GetString(item, "pic") ?? ""));
                    }
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
            _logger.LogError(ex, "QQ专辑搜索失败: {Keyword}", keyword);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    /// <summary>QQ 专辑详情：musicu.fcg GetAlbumSongList → data.songList[].songInfo（老 GetAlbumDetail 已不再返回歌曲列表）</summary>
    public async Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            if (album == null || string.IsNullOrEmpty(album.Id))
                return PagedResult<MusicInfo>.Empty(page, pageSize);

            var url = $"{UUrl}/cgi-bin/musicu.fcg";
            var payload = new
            {
                req_0 = new
                {
                    module = "music.musichallAlbum.AlbumSongList",
                    method = "GetAlbumSongList",
                    param = new { albumMid = album.Id, begin = (page - 1) * pageSize, num = pageSize }
                },
                comm = new { uin = 0, format = "json", ct = 24, cv = 0 }
            };

            var response = await _httpClient.PostAsync<JsonElement>(url, payload, null, cancellationToken);
            var list = new List<MusicInfo>();
            int total = 0;

            if (response.TryGetProperty("req_0", out var req0) &&
                req0.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("totalNum", out var tn) && tn.ValueKind == JsonValueKind.Number)
                    total = tn.GetInt32();

                if (data.TryGetProperty("songList", out var songList) && songList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var wrapper in songList.EnumerateArray())
                    {
                        if (!wrapper.TryGetProperty("songInfo", out var item) || item.ValueKind != JsonValueKind.Object)
                            continue;
                        var songMid = JsonHelpers.GetString(item, "mid") ?? JsonHelpers.GetString(item, "songmid");
                        if (string.IsNullOrEmpty(songMid)) continue;
                        var title = JsonHelpers.GetString(item, "name") ?? "未知歌曲";
                        var artist = "";
                        if (item.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array)
                            artist = string.Join("/", singers.EnumerateArray().Select(a => JsonHelpers.GetString(a, "name") ?? "").Where(n => n.Length > 0));
                        var albumName = "";
                        if (item.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object)
                            albumName = JsonHelpers.GetString(al, "name") ?? "";
                        var interval = 0;
                        if (item.TryGetProperty("interval", out var iv) && iv.ValueKind == JsonValueKind.Number)
                            interval = iv.GetInt32();
                        list.Add(new MusicInfo
                        {
                            Id = songMid!, Songmid = songMid!,
                            Title = title, Artist = artist, Album = albumName,
                            Duration = interval > 0 ? FormatDuration(interval) : "",
                            Source = "qq"
                        });
                    }
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
            _logger.LogError(ex, "QQ专辑详情失败: {Album}", album?.Title ?? "?");
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
            _logger.LogInformation("获取QQ音乐播放URL: {MusicId}", musicId);

            // 使用 u.y.qq.com 接口（随机 10 位 guid + 浏览器头，降低风控命中）
            var guid = new Random().Next(1000000000, 1999999999).ToString();
            var payload = new
            {
                req_0 = new
                {
                    module = "vkey.GetVkeyServer",
                    method = "CgiGetVkey",
                    param = new
                    {
                        guid,
                        songmid = new[] { musicId },
                        songtype = new[] { 0 },
                        uin = "0",
                        loginflag = 1,
                        platform = "20",
                        needNewCode = 0
                    }
                }
            };

            var playHeaders = new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                ["Referer"] = "https://y.qq.com/",
                ["Origin"] = "https://y.qq.com"
            };
            var response = await _httpClient.PostFormAsync<JsonElement>(
                $"{UUrl}/cgi-bin/musicu.fcg",
                new Dictionary<string, string>
                {
                    ["data"] = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                    ["format"] = "json"
                },
                playHeaders,
                cancellationToken);
            if (response.TryGetProperty("req_0", out var req0) &&
                req0.TryGetProperty("data", out var resData) &&
                resData.TryGetProperty("midurlinfo", out var midUrlInfo) &&
                midUrlInfo.ValueKind == JsonValueKind.Array && midUrlInfo.GetArrayLength() > 0)
            {
                var first = midUrlInfo[0];
                var purl = first.TryGetProperty("purl", out var p) ? p.GetString() : null;
                var sip = resData.TryGetProperty("sip", out var sipEl) && sipEl.ValueKind == JsonValueKind.Array
                    ? sipEl[0].GetString() : "http://ws.stream.qqmusic.qq.com/";

                if (!string.IsNullOrEmpty(purl))
                    return $"{sip}{purl}";
            }

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
            var url = $"{BaseUrl}/lyric/fcgi-bin/fcg_query_lyric_new.fcg";
            var parameters = new Dictionary<string, string>
            {
                ["songmid"] = musicId,
                ["format"] = "json",
                ["nobase64"] = "1"
            };
            var headers = new Dictionary<string, string>
            {
                ["Referer"] = "https://y.qq.com/",
                ["Cookie"] = "uin=0; qm_keyst=Q_H_L_0; tsLastRefresh=0; qqmusic_fromtag=0",
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
            };

            var response = await _httpClient.GetAsync(url, parameters, headers);
            var data = JsonSerializer.Deserialize<JsonElement>(response);

            var code = data.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            if (code == 0 &&
                data.TryGetProperty("lyric", out var lyric) &&
                lyric.TryGetProperty("lyric", out var lyricStr))
            {
                var raw = lyricStr.GetString() ?? "";
                string content;
                try
                {
                    content = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(raw));
                }
                catch (FormatException)
                {
                    // nobase64=1 时部分接口直接返回明文
                    content = raw;
                }
                return ParseLrcContent(content, null);
            }

            _logger.LogWarning("获取QQ歌词失败: retcode={Code}, MusicId={MusicId}", code, musicId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取QQ歌词失败: {MusicId}", musicId);
            return null;
        }
    }

    /// <summary>
    /// 获取歌单分类 — 参考原版 lx-music-desktop：playlist.PlaylistAllCategoriesServer/get_all_categories
    /// </summary>
        public async Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("获取{Name}歌单分类", SourceName);
        var data = @"{""tags"":{""method"":""get_all_categories"",""param"":{""qq"":""""},""module"":""playlist.PlaylistAllCategoriesServer""}}";
        var officialTask = _httpClient.GetAsync($"{UUrl}/cgi-bin/musicu.fcg", new Dictionary<string, string>
        {
            ["loginUin"] = "0", ["hostUin"] = "0", ["format"] = "json", ["inCharset"] = "utf-8",
            ["outCharset"] = "utf-8", ["notice"] = "0", ["platform"] = "wk_v15.json", ["needNewCode"] = "0", ["data"] = data
        }, null, cancellationToken);
        var hotTask = _httpClient.GetAsync("https://c.y.qq.com/node/pc/wk_v15/category_playlist.html", null, null, cancellationToken);
        await Task.WhenAll(officialTask, hotTask);
        using var document = JsonDocument.Parse(await officialTask);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) || code.GetInt32() != 0 ||
            !root.TryGetProperty("tags", out var tags) || !tags.TryGetProperty("data", out var tagData) ||
            !tagData.TryGetProperty("v_group", out var groups) || groups.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("QQ playlist catalogue returned no data.");

        var result = new List<PlaylistCategory>();
        var hotTags = ParseQqHotTags(await hotTask);
        if (hotTags.Count > 0)
            result.Add(new PlaylistCategory { Id = "recommend", Name = "推荐", Tags = hotTags });
        foreach (var group in groups.EnumerateArray())
        {
            var groupName = group.TryGetProperty("group_name", out var name) ? name.GetString() ?? "" : "";
            if (groupName.Length == 0 || !group.TryGetProperty("v_item", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            var list = items.EnumerateArray().Select(item => new PlaylistTag
            {
                Id = item.TryGetProperty("id", out var id) ? id.ToString() : "",
                Name = item.TryGetProperty("name", out var tagName) ? tagName.GetString() ?? "" : ""
            }).Where(tag => tag.Id.Length > 0 && tag.Name.Length > 0).ToList();
            if (list.Count > 0) result.Add(new PlaylistCategory { Id = groupName, Name = groupName, Tags = list });
        }
        return result;
    }

    private static List<PlaylistTag> ParseQqHotTags(string html)
    {
        var tags = new List<PlaylistTag>();
        foreach (Match match in Regex.Matches(html, @"data-id=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline))
            tags.Add(new PlaylistTag { Id = match.Groups[1].Value, Name = match.Groups[2].Value });
        return tags;
    }

    /// <summary>精选可用的 QQ 歌单分类（标签 ID 经 get_playlist_by_tag 实测有数据）</summary>
    private static List<PlaylistCategory> GetCuratedCategories()
    {
        return new List<PlaylistCategory>
        {
            new() { Id = "all", Name = "全部歌单", Tags = new List<PlaylistTag> { new() { Id = "all", Name = "全部" } } },
            new() { Id = "theme", Name = "主题", Tags = new List<PlaylistTag>
            {
                new() { Id = "59", Name = "经典老歌" },
                new() { Id = "136", Name = "人气音乐节" },
                new() { Id = "95", Name = "现场音乐" },
                new() { Id = "107", Name = "背景音乐" }
            } },
            new() { Id = "scene", Name = "场景", Tags = new List<PlaylistTag>
            {
                new() { Id = "39", Name = "校园" },
                new() { Id = "27", Name = "睡前" }
            } },
            new() { Id = "mood", Name = "心情", Tags = new List<PlaylistTag>
            {
                new() { Id = "16", Name = "励志" },
                new() { Id = "17", Name = "宣泄" },
                new() { Id = "13", Name = "安静" }
            } },
            new() { Id = "era", Name = "年代", Tags = new List<PlaylistTag>
            {
                new() { Id = "141", Name = "80年代" }
            } },
            new() { Id = "genre", Name = "流派", Tags = new List<PlaylistTag>
            {
                new() { Id = "68", Name = "中国风" }
            } },
            new() { Id = "lang", Name = "语种", Tags = new List<PlaylistTag>
            {
                new() { Id = "146", Name = "粤语" },
                new() { Id = "6", Name = "小语种" }
            } }
        };
    }

    private static List<PlaylistCategory> GetDefaultCategories()
    {
        return new List<PlaylistCategory>
        {
            new() { Id = "all", Name = "全部歌单", Tags = new() { new() { Id = "all", Name = "全部" } } },
            new() { Id = "1", Name = "语种", Tags = new() { new() { Id = "67", Name = "华语" }, new() { Id = "68", Name = "欧美" }, new() { Id = "71", Name = "日语" }, new() { Id = "70", Name = "韩语" }, new() { Id = "69", Name = "粤语" } } },
            new() { Id = "2", Name = "风格", Tags = new() { new() { Id = "16", Name = "流行" }, new() { Id = "21", Name = "摇滚" }, new() { Id = "18", Name = "民谣" }, new() { Id = "27", Name = "电子" }, new() { Id = "22", Name = "说唱" } } },
            new() { Id = "3", Name = "场景", Tags = new() { new() { Id = "95", Name = "清晨" }, new() { Id = "96", Name = "夜晚" }, new() { Id = "88", Name = "学习" }, new() { Id = "99", Name = "运动" } } },
            new() { Id = "4", Name = "情感", Tags = new() { new() { Id = "53", Name = "怀旧" }, new() { Id = "52", Name = "浪漫" }, new() { Id = "51", Name = "伤感" }, new() { Id = "50", Name = "快乐" } } },
            new() { Id = "5", Name = "主题", Tags = new() { new() { Id = "64", Name = "KTV金曲" }, new() { Id = "76", Name = "游戏" }, new() { Id = "74", Name = "网络歌曲" }, new() { Id = "65", Name = "经典" } } }
        };
    }

    /// <summary>
    /// 按分类获取歌单列表 — 参考原版：get_playlist_by_tag(全部) / get_category_content(指定标签)
    /// </summary>
        public async Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string categoryId, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default)
    {
        var tag = categoryId ?? string.Empty;
        var useCategory = long.TryParse(tag, out var categoryIdValue);
        object playlistRequest = useCategory
            ? new
            {
                comm = new { cv = 1602, ct = 20 },
                playlist = new
                {
                    method = "get_category_content",
                    @param = new { titleid = categoryIdValue, caller = "0", category_id = categoryIdValue, size = pageSize, page = page - 1, use_page = 1 },
                    module = "playlist.PlayListCategoryServer"
                }
            }
            : new
            {
                comm = new { cv = 1602, ct = 20 },
                playlist = new
                {
                    method = "get_playlist_by_tag",
                    @param = new { id = 10000000, sin = pageSize * (page - 1), size = pageSize, order = 5, cur_page = page },
                    module = "playlist.PlayListPlazaServer"
                }
            };
        var data = JsonSerializer.Serialize(playlistRequest);
        var response = await _httpClient.GetAsync($"{UUrl}/cgi-bin/musicu.fcg", new Dictionary<string, string>
        {
            ["loginUin"] = "0", ["hostUin"] = "0", ["format"] = "json", ["inCharset"] = "utf-8",
            ["outCharset"] = "utf-8", ["notice"] = "0", ["platform"] = "wk_v15.json", ["needNewCode"] = "0", ["data"] = data
        }, null, cancellationToken);
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) || code.GetInt32() != 0 ||
            !root.TryGetProperty("playlist", out var playlist) || !playlist.TryGetProperty("data", out var playlistData))
            throw new InvalidOperationException("QQ playlist page returned no data.");
        var playlists = new List<Playlist>();
        var total = 0;
        if (useCategory)
        {
            if (!playlistData.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("v_item", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("QQ playlist category content returned no data.");
            total = content.TryGetProperty("total_cnt", out var totalCount) ? totalCount.GetInt32() : 0;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("basic", out var basic)) continue;
                var playlistId = basic.TryGetProperty("tid", out var tid) ? tid.ToString() : "";
                if (playlistId.Length == 0) continue;
                playlists.Add(new Playlist
                {
                    Id = "qq_" + playlistId,
                    Name = basic.TryGetProperty("title", out var name) ? name.GetString() ?? "" : "",
                    Description = basic.TryGetProperty("desc", out var description) ? description.GetString() ?? "" : "",
                    Type = PlaylistType.Recommend
                });
            }
        }
        else
        {
            total = playlistData.TryGetProperty("total", out var totalCount) ? totalCount.GetInt32() : 0;
            if (playlistData.TryGetProperty("v_playlist", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var playlistId = item.TryGetProperty("tid", out var tid) ? tid.ToString() : "";
                    if (playlistId.Length == 0) continue;
                    playlists.Add(new Playlist
                    {
                        Id = "qq_" + playlistId,
                        Name = item.TryGetProperty("title", out var name) ? name.GetString() ?? "" : "",
                        Description = item.TryGetProperty("desc", out var description) ? description.GetString() ?? "" : "",
                        Type = PlaylistType.Recommend
                    });
                }
            }
        }
        return new PagedResult<Playlist> { CurrentPage = page, PageSize = pageSize, TotalItems = total, Items = playlists };
    }

    /// <summary>
    /// 获取歌单详情
    /// </summary>
    public async Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {

            _logger.LogInformation("获取QQ歌单详情: {PlaylistId}", playlistId);

            var url = $"{UUrl}/cgi-bin/musicu.fcg";
            var payload = new
            {
                req_0 = new
                {
                    module = "music.srfDissInfo.DissInfo",
                    method = "CgiGetDiss",
                    param = new
                    {
                        disstid = long.TryParse(playlistId, out var id) ? id : 0,
                        onlysonglist = 0,
                        song_begin = (page - 1) * pageSize,
                        song_num = pageSize,
                        tag = 1,
                        userinfo = 1
                    }
                }
            };

            var response = await _httpClient.PostAsync<JsonElement>(url, payload, null, cancellationToken);
            var musicList = new List<MusicInfo>();
            int total = 0;

            if (response.TryGetProperty("req_0", out var req0) &&
                req0.TryGetProperty("data", out var resData))
            {
                if (resData.TryGetProperty("total_song_num", out var tn))
                    total = tn.GetInt32();

                if (resData.TryGetProperty("songlist", out var songlist))
                {
                    foreach (var item in songlist.EnumerateArray())
                    {
                        // 歌曲 Id 使用 mid（原版读取 songmid 导致 Id 为空无法播放）
                        var songMid = item.TryGetProperty("mid", out var smid) ? smid.GetString() ?? "" : "";

                        var singerName = "";
                        if (item.TryGetProperty("singer", out var singers))
                        {
                            if (singers.ValueKind == JsonValueKind.Array)
                            {
                                var names = singers.EnumerateArray()
                                    .Select(s => s.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "")
                                    .Where(n => !string.IsNullOrEmpty(n));
                                singerName = string.Join("、", names);
                            }
                            else if (singers.TryGetProperty("name", out var sn))
                            {
                                singerName = sn.GetString() ?? "";
                            }
                        }
                        if (string.IsNullOrEmpty(singerName))
                        {
                            if (item.TryGetProperty("singer", out var sng2) && sng2.ValueKind == JsonValueKind.String)
                                singerName = sng2.GetString() ?? "";
                            if (string.IsNullOrEmpty(singerName))
                                singerName = item.TryGetProperty("singername", out var sn3) ? sn3.GetString() ?? "" : "";
                        }

                        var albumMid = "";
                        if (item.TryGetProperty("album", out var album) &&
                            album.TryGetProperty("mid", out var amid))
                            albumMid = amid.GetString() ?? "";

                        musicList.Add(new MusicInfo
                        {
                            Id = songMid,
                            Title = item.TryGetProperty("songname", out var sname) && sname.ValueKind == JsonValueKind.String ? sname.GetString() ?? ""
                                : item.TryGetProperty("name", out var nm2) ? nm2.GetString() ?? "" : "",
                            Artist = singerName,
                            Album = item.TryGetProperty("album", out var al) && al.TryGetProperty("name", out var aln)
                                ? aln.GetString() ?? "" : "",
                            Duration = item.TryGetProperty("interval", out var inv)
                                ? FormatDuration(inv.GetInt32()) : "00:00",
                            Source = "qq",
                            CoverUrl = albumMid.Length > 0
                                ? $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg"
                                : "",
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

    /// <summary>
    /// 获取排行榜列表
    /// </summary>
    public async Task<List<Ranking>> GetRankingsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("获取QQ音乐排行榜");

        try
        {
            var url = $"{UUrl}/cgi-bin/musicu.fcg";
            var payload = new
            {
                req_0 = new
                {
                    module = "musicToplist.ToplistInfoServer",
                    method = "GetAll",
                    param = new { }
                }
            };

            var response = await _httpClient.PostAsync<JsonElement>(url, payload, null, cancellationToken);
            var rankings = new List<Ranking>();

            if (response.TryGetProperty("req_0", out var req0) &&
                req0.TryGetProperty("data", out var resData) &&
                resData.TryGetProperty("group", out var group))
            {
                // QQ 的 GetAll 把榜单按“巅峰榜/地区榜/特色榜/全球榜”分组返回在 group[].toplist 中
                foreach (var grp in group.EnumerateArray())
                {
                    if (!grp.TryGetProperty("toplist", out var toplist) || toplist.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var item in toplist.EnumerateArray())
                    {
                        var topId = item.TryGetProperty("topId", out var tid) ? ReadTopId(tid) : 0;
                        if (topId == 201) continue; // 排除 MV 榜（视频榜，非歌曲榜）
                        var title = item.TryGetProperty("title", out var ttl) ? ttl.GetString() ?? "" : "";
                        if (title.StartsWith("巅峰榜·")) title = title[4..];
                        if (!title.EndsWith("榜")) title += "榜";
                        rankings.Add(new Ranking
                        {
                            Id = "qq_" + topId,
                            Name = title,
                            Description = item.TryGetProperty("intro", out var intro) ? intro.GetString() ?? "" : "",
                            Source = "qq",
                            Type = MapQqRankingType(title),
                            UpdateDate = item.TryGetProperty("updateTime", out var ut)
                                ? ParseQqDate(ut) : DateTime.Now
                        });
                    }
                }
            }

            if (rankings.Count > 0)
                return rankings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "从API获取QQ排行榜失败，使用内置列表");
        }

        return new List<Ranking>
        {
            new Ranking { Id = "qq_4", Name = "流行指数榜", Description = "流行指数榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_26", Name = "热歌榜", Description = "QQ音乐热歌榜", Source = "qq", Type = RankingType.Hot, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_27", Name = "新歌榜", Description = "QQ音乐新歌榜", Source = "qq", Type = RankingType.New, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_62", Name = "飙升榜", Description = "QQ音乐飙升榜", Source = "qq", Type = RankingType.Rising, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_58", Name = "说唱榜", Description = "说唱榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_57", Name = "喜力电音榜", Description = "喜力电音榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_28", Name = "网络歌曲榜", Description = "网络歌曲榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_5", Name = "内地榜", Description = "内地榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_3", Name = "欧美榜", Description = "欧美榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_59", Name = "香港地区榜", Description = "香港地区榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_16", Name = "韩国榜", Description = "韩国榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_60", Name = "抖快榜", Description = "抖快榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_29", Name = "影视金曲榜", Description = "影视金曲榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_17", Name = "日本榜", Description = "日本榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_52", Name = "腾讯音乐人原创榜", Description = "腾讯音乐人原创榜", Source = "qq", Type = RankingType.Original, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_36", Name = "K歌金曲榜", Description = "K歌热门歌曲", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_61", Name = "台湾地区榜", Description = "台湾地区榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_63", Name = "DJ舞曲榜", Description = "DJ舞曲榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_64", Name = "综艺新歌榜", Description = "综艺新歌榜", Source = "qq", Type = RankingType.New, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_65", Name = "国风热歌榜", Description = "国风热歌榜", Source = "qq", Type = RankingType.Hot, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_67", Name = "听歌识曲榜", Description = "听歌识曲榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_72", Name = "动漫音乐榜", Description = "动漫音乐榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_73", Name = "游戏音乐榜", Description = "游戏音乐榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_75", Name = "有声榜", Description = "有声榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now },
            new Ranking { Id = "qq_131", Name = "校园音乐人排行榜", Description = "校园音乐人排行榜", Source = "qq", Type = RankingType.Other, UpdateDate = DateTime.Now }
        };
    }

    /// <summary>
    /// 获取排行榜详情
    /// </summary>
    public async Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("获取QQ排行榜详情: {RankingId}", rankingId);

            var url = $"{UUrl}/cgi-bin/musicu.fcg";
            var payload = new
            {
                req_0 = new
                {
                    module = "musicToplist.ToplistInfoServer",
                    method = "GetDetail",
                    param = new
                    {
                        topId = long.TryParse(rankingId, out var tid) ? tid : 26,
                        // QQ GetDetail 不返回榜单总数，无法按 offset 服务端分页；
                        // 一次拉取整榜（QQ 榜单最多 300 首）后在本地切片，保证能获取完整榜单而不是只有前 20 首。
                        num = 300
                    }
                }
            };

            var response = await _httpClient.PostAsync<JsonElement>(url, payload, null, cancellationToken);
            var allSongs = new List<MusicInfo>();

            if (response.TryGetProperty("req_0", out var req0) &&
                req0.TryGetProperty("data", out var resData) &&
                resData.TryGetProperty("songInfoList", out var songInfoList))
                {
                    foreach (var item in songInfoList.EnumerateArray())
                    {
                        var songMid = item.TryGetProperty("mid", out var smid) ? smid.GetString() ?? "" : "";
                        var singerName = "";
                        if (item.TryGetProperty("singer", out var singers))
                        {
                            if (singers.ValueKind == JsonValueKind.Array)
                            {
                                var names = singers.EnumerateArray()
                                    .Select(s => s.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "")
                                    .Where(n => !string.IsNullOrEmpty(n));
                                singerName = string.Join("、", names);
                            }
                            else if (singers.TryGetProperty("name", out var sn))
                            {
                                singerName = sn.GetString() ?? "";
                            }
                        }
                        if (string.IsNullOrEmpty(singerName))
                        {
                            if (item.TryGetProperty("singer", out var sng2) && sng2.ValueKind == JsonValueKind.String)
                                singerName = sng2.GetString() ?? "";
                            if (string.IsNullOrEmpty(singerName))
                                singerName = item.TryGetProperty("singername", out var sn3) ? sn3.GetString() ?? "" : "";
                        }

                        var albumMid = "";
                        if (item.TryGetProperty("album", out var album) &&
                            album.TryGetProperty("mid", out var amid))
                            albumMid = amid.GetString() ?? "";

                        allSongs.Add(new MusicInfo
                        {
                            Id = songMid,
                            Title = item.TryGetProperty("title", out var ttl) ? ttl.GetString() ?? "" : "",
                            Artist = singerName.Length > 0 ? singerName :
                                item.TryGetProperty("singerName", out var sname) ? sname.GetString() ?? "" : "",
                            Album = item.TryGetProperty("album", out var al) && al.TryGetProperty("name", out var aln)
                                ? aln.GetString() ?? "" : "",
                            Duration = item.TryGetProperty("interval", out var inv)
                                ? FormatDuration(inv.GetInt32()) : "00:00",
                            Source = "qq",
                            CoverUrl = albumMid.Length > 0
                                ? $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg"
                                : "",
                            Quality = 128
                        });
                    }
                }

            // 整榜已一次取出：总数为实际歌曲数，本地按页切片，分页完整可用
            var total = allSongs.Count;
            var items = total == 0
                ? new List<MusicInfo>()
                : allSongs.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return new PagedResult<MusicInfo>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = total,
                Items = items
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取QQ排行榜详情失败: {RankingId}", rankingId);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    /// <summary>生成 QQ 移动端搜索会话 ID（模仿官方客户端 searchID 生成算法，仅为随机大数）</summary>
    private static string GetSearchId()
    {
        var e = FrameworkCompatibility.SharedRandom.Next(1, 21);
        var t = e * 18014398509481984L;
        var n = FrameworkCompatibility.SharedRandom.Next(0, 4194305) * 4294967296L;
        var r = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % (24L * 60 * 60 * 1000);
        return (t + n + r).ToString();
    }

    /// <summary>生成随机 QIMEI 设备标识（搜索接口要求非空，否则不返回结果列表）</summary>
    private static (string Q16, string Q36) GenerateQimei()
    {
        const string hex = "0123456789abcdef";
        const string q36Chars = "0123456789abcdefghijklmnopqrstuvwxyz";
        var q16 = new string(Enumerable.Range(0, 16).Select(_ => hex[FrameworkCompatibility.SharedRandom.Next(hex.Length)]).ToArray());
        var q36 = new string(Enumerable.Range(0, 36).Select(_ => q36Chars[FrameworkCompatibility.SharedRandom.Next(q36Chars.Length)]).ToArray());
        return (q16, q36);
    }

    /// <summary>去除搜索结果里的 HTML 高亮标签（如 &lt;em&gt;周杰伦&lt;/em&gt;）</summary>
    private static string StripHtml(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", "");
    }

    // ========== 辅助方法 ==========

    private static RankingType MapQqRankingType(string title)
    {
        if (title.Contains("热歌")) return RankingType.Hot;
        if (title.Contains("新歌")) return RankingType.New;
        if (title.Contains("飙升")) return RankingType.Rising;
        if (title.Contains("原创")) return RankingType.Original;
        return RankingType.Other;
    }

    /// <summary>读取 toplist 的 topId（QQ 接口可能返回数字或字符串）</summary>
    private static long ReadTopId(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.Number => el.GetInt64(),
            JsonValueKind.String => long.TryParse(el.GetString(), out var v) ? v : 0,
            _ => 0
        };

    /// <summary>解析 QQ 榜单更新日期（updateTime 可能是 "yyyy-MM-dd" 字符串或 Unix 秒数）</summary>
    private static DateTime ParseQqDate(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.FromUnixTimeSeconds(el.GetInt64()).DateTime;
        if (el.ValueKind == JsonValueKind.String && DateTime.TryParse(el.GetString(), out var dt))
            return dt;
        return DateTime.Now;
    }

    private static string FormatDuration(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    private static Lyric? ParseLrcContent(string? lrcContent, string? translateContent)
    {
        if (string.IsNullOrWhiteSpace(lrcContent)) return null;

        var lines = new List<LyricLine>();
        foreach (var line in lrcContent.SplitTrimmed('\n'))
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

}
