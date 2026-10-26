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

    public async Task<PagedResult<MusicInfo>> SearchAsync(string keyword, int page = 1, int pageSize = 30, string searchType = "song", CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("搜索音乐: {Keyword}, 页码: {Page}", keyword, page);

            if (string.Equals(searchType, "album", StringComparison.OrdinalIgnoreCase))
                return await SearchAlbumsAsync(keyword, page, pageSize, cancellationToken);

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
                DefaultHeaders,
                cancellationToken);

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
    private async Task<PagedResult<MusicInfo>> SearchAlbumsAsync(string keyword, int page, int pageSize, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _httpClient.PostEapiAsync<JsonElement>(
                "/api/cloudsearch/pc",
                new { s = keyword, type = 10, limit = pageSize, offset = pageSize * (page - 1) },
                DefaultHeaders,
                cancellationToken);

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
    public async Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            if (album == null || string.IsNullOrEmpty(album.Id))
                return PagedResult<MusicInfo>.Empty(page, pageSize);

            var result = await _httpClient.PostEapiAsync<JsonElement>(
                $"/api/v1/album/{album.Id}",
                new { id = album.Id },
                DefaultHeaders,
                cancellationToken);

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

    public async Task<List<Ranking>> GetRankingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = $"{WeapiBaseUrl}/toplist";
            var result = await _httpClient.PostWeapiAsync<NeteaseTopList>(
                endpoint,
                new { },
                DefaultHeaders,
                cancellationToken);

            ThrowIfNeteaseCodeFailed(result?.Code, result?.Message, "获取排行榜列表", endpoint);
            if (result?.List == null)
                throw new InvalidOperationException(
                    $"获取排行榜列表失败：网易云未返回排行榜数据 (code={result?.Code ?? 0}){NeteaseMessageSuffix(result?.Message)}（接口：{endpoint}）。");

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取排行榜列表失败");
            throw;
        }
    }

    public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
        => GetDetailPageAsync(rankingId, page, pageSize, "获取排行榜详情", cancellationToken);

    // ========== 歌单分类 ==========

    public async Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var catalogueEndpoint = $"{WeapiBaseUrl}/playlist/catalogue";
        var hotTagsEndpoint = $"{WeapiBaseUrl}/playlist/hottags";
        var officialTask = _httpClient.PostWeapiAsync<JsonElement?>(catalogueEndpoint, new { }, DefaultHeaders, cancellationToken);
        var hotTask = _httpClient.PostWeapiAsync<JsonElement?>(hotTagsEndpoint, new { }, DefaultHeaders, cancellationToken);
        await Task.WhenAll(officialTask, hotTask);
        var official = await officialTask;
        var hot = await hotTask;
        if (official is null || official.Value.ValueKind != JsonValueKind.Object || !official.Value.TryGetProperty("sub", out var subs) || subs.ValueKind != JsonValueKind.Array ||
            !official.Value.TryGetProperty("categories", out var categoryNames) || categoryNames.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"获取歌单分类失败：网易云未返回分类数据 (endpoint={catalogueEndpoint})。");

        var grouped = subs.EnumerateArray()
            .Where(item => item.TryGetProperty("category", out var _) && JsonHelpers.GetString(item, "name")?.Length > 0)
            .GroupBy(item => JsonHelpers.GetString(item, "category") ?? "", StringComparer.Ordinal)
            .Select(group => new PlaylistCategory
            {
                Id = group.Key,
                Name = categoryNames.TryGetProperty(group.Key, out var name) ? name.GetString() ?? "其他" : "其他",
                Tags = group.Select(item => new PlaylistTag { Id = JsonHelpers.GetString(item, "name") ?? "", Name = JsonHelpers.GetString(item, "name") ?? "" }).ToList()
            })
            .Where(category => category.Tags.Count > 0)
            .ToList();

        var hotTags = new List<PlaylistTag>();
        if (hot is not null && hot.Value.ValueKind == JsonValueKind.Object && hot.Value.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tags.EnumerateArray())
            {
                if (item.TryGetProperty("playlistTag", out var tag) && tag.TryGetProperty("name", out var name))
                {
                    var value = name.GetString() ?? "";
                    if (value.Length > 0) hotTags.Add(new PlaylistTag { Id = value, Name = value });
                }
            }
        }
        if (hotTags.Count > 0)
            grouped.Insert(0, new PlaylistCategory { Id = "recommend", Name = "推荐", Tags = hotTags });
        return grouped;
    }

    // ========== 歌单列表 ==========

    public async Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string categoryId, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default)
    {
        var endpoint = $"{WeapiBaseUrl}/playlist/list";
        var result = await _httpClient.PostWeapiAsync<NeteasePlaylistResult>(
            endpoint,
            new { cat = categoryId, order = "hot", limit = pageSize, offset = pageSize * (page - 1), total = true },
            DefaultHeaders,
            cancellationToken);

        ThrowIfNeteaseCodeFailed(result?.Code, result?.Message, "获取歌单列表", endpoint);
        if (result?.Playlists == null)
            throw new InvalidOperationException(
                $"获取歌单列表失败：网易云未返回歌单数据 (cat={categoryId}, code={result?.Code ?? 0}){NeteaseMessageSuffix(result?.Message)}（接口：{endpoint}）。");

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

    // ========== 歌单详情 ==========

    public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
        => GetDetailPageAsync(playlistId, page, pageSize, "获取歌单详情", cancellationToken);

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

        foreach (var line in lrc.SplitTrimmed('\n'))
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
        foreach (var line in tLrc.SplitTrimmed('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^\[(\d{2}):(\d{2})(?:\.(\d+))?\](.*)");
            if (m.Success)
            {
                var ms = m.Groups[3].Success
                    ? int.Parse(m.Groups[3].Value.PadRight(3, '0')[..3])
                    : 0;
                var idx = int.Parse(m.Groups[1].Value) * 60000
                    + int.Parse(m.Groups[2].Value) * 1000
                    + ms;
                map[idx] = m.Groups[4].Value.Trim();
            }
        }
        return map;
    }

    // ========== 详情分页（歌单/排行榜共用） ==========

    private const int SongDetailChunkSize = 200;
    private const string PlaylistDetailEndpoint = "https://music.163.com/api/v6/playlist/detail";

    /// <summary>
    /// linuxapi v6 详情只内嵌少量 tracks，完整的曲目顺序在 trackIds 中（大歌单尤其明显），
    /// 因此必须按页码切片 trackIds，再用 weapi /v3/song/detail 解析本页歌曲。
    /// </summary>
    private async Task<PagedResult<MusicInfo>> GetDetailPageAsync(
        string id,
        int page,
        int pageSize,
        string action,
        CancellationToken cancellationToken)
    {
        var requestId = long.TryParse(id, out var numericId) ? (object)numericId : id;
        var response = await _httpClient.PostLinuxApiAsync<NeteaseLinuxApiResp>(new
        {
            method = "POST",
            url = PlaylistDetailEndpoint,
            @params = new { id = requestId, n = pageSize, s = 8 }
        }, DefaultHeaders, cancellationToken);

        ThrowIfNeteaseCodeFailed(response?.Code, response?.Message, action, PlaylistDetailEndpoint);
        if (response?.Playlist == null)
            throw new InvalidOperationException(
                $"{action}失败：网易云未返回歌单数据 (id={id}, code={response?.Code ?? 0}){NeteaseMessageSuffix(response?.Message)}。");

        var playlist = response.Playlist;
        var ids = playlist.TrackIds?.Select(track => track.Id).ToList()
                  ?? playlist.Tracks?.Select(track => track.Id).ToList()
                  ?? new List<long>();
        var total = playlist.TrackCount > 0 ? playlist.TrackCount : ids.Count;
        var pageIds = ids.Skip(Math.Max(0, (page - 1) * pageSize)).Take(pageSize).ToList();

        var songsById = new Dictionary<long, NeteaseSong>();
        var qualityById = new Dictionary<long, NeteasePrivilege>();
        if (playlist.Tracks != null)
        {
            foreach (var song in playlist.Tracks)
            {
                songsById[song.Id] = song;
                if (song.Privilege != null) qualityById[song.Id] = song.Privilege;
            }
        }
        if (response.Privileges != null)
        {
            foreach (var privilege in response.Privileges) qualityById[privilege.Id] = privilege;
        }

        var items = new List<MusicInfo>();
        if (pageIds.All(songsById.ContainsKey))
        {
            // 本页曲目已内嵌在详情响应里，无需额外请求
            foreach (var songId in pageIds) items.Add(MapSong(songsById[songId], qualityById));
            return BuildDetailPage(page, pageSize, total, items);
        }

        var missingIds = pageIds.Where(songId => !songsById.ContainsKey(songId)).ToList();
        var detailEndpoint = $"{WeapiBaseUrl}/v3/song/detail";
        foreach (var chunk in Chunk(missingIds, SongDetailChunkSize))
        {
            var cJson = "[" + string.Join(",", chunk.Select(songId => "{\"id\":" + songId + "}")) + "]";
            var idsJson = "[" + string.Join(",", chunk) + "]";
            var detail = await _httpClient.PostWeapiAsync<NeteasePlaylistTrackResponse>(
                detailEndpoint,
                new { c = cJson, ids = idsJson },
                DefaultHeaders,
                cancellationToken);

            ThrowIfNeteaseCodeFailed(detail?.Code, detail?.Message, action, detailEndpoint);
            if (detail?.Privileges != null)
            {
                foreach (var privilege in detail.Privileges) qualityById[privilege.Id] = privilege;
            }
            if (detail?.Songs != null)
            {
                foreach (var song in detail.Songs) songsById[song.Id] = song;
            }
        }

        // 按本页曲目顺序输出；详情缺失的曲目直接跳过，绝不为“数量不足”抛错
        foreach (var songId in pageIds)
        {
            if (songsById.TryGetValue(songId, out var song)) items.Add(MapSong(song, qualityById));
        }

        return BuildDetailPage(page, pageSize, total, items);
    }

    private static PagedResult<MusicInfo> BuildDetailPage(int page, int pageSize, int total, List<MusicInfo> items)
        => new()
        {
            CurrentPage = page,
            PageSize = pageSize,
            TotalItems = total,
            Items = items
        };

    private static MusicInfo MapSong(NeteaseSong song, IReadOnlyDictionary<long, NeteasePrivilege> qualityById)
    {
        qualityById.TryGetValue(song.Id, out var privilege);
        return new MusicInfo
        {
            Id = song.Id.ToString(),
            Title = song.Name ?? "未知歌曲",
            Artist = song.GetArtistNames(),
            Album = song.Album?.Name ?? "未知专辑",
            Duration = song.GetDurationString(),
            Source = "netease",
            CoverUrl = song.Album?.PicUrl ?? "",
            Quality = GetQualityLevel(privilege)
        };
    }

    private static IEnumerable<List<long>> Chunk(List<long> source, int size)
    {
        for (var index = 0; index < source.Count; index += size)
            yield return source.GetRange(index, Math.Min(size, source.Count - index));
    }

    private static string NeteaseMessageSuffix(string? message)
        => string.IsNullOrWhiteSpace(message) ? string.Empty : $"，message={message}";

    /// <summary>校验网易云业务状态码：0（部分接口省略）与 200 视为成功，其余抛出可读错误。</summary>
    private static void ThrowIfNeteaseCodeFailed(int? code, string? message, string action, string endpoint)
    {
        if (code is null or 0 or SuccessCode) return;
        throw new InvalidOperationException(
            $"{action}失败：网易云返回 code={code}{NeteaseMessageSuffix(message)}（接口：{endpoint}）。");
    }
}
