using System.Text.Json;
using System.Text.Json.Serialization;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 网易云音乐个人歌单导入 Provider。
/// 凭据仅保存在本机（外层服务 DPAPI 加密持久化），请求直连 music.163.com 官方私有接口，绝不经过任何第三方服务器。
/// 参考：XiaoMengXinX/Music163Api-Go（eapi 客户端接口，2025 年仍可用）、Binaryify/NeteaseCloudMusicApi。
/// </summary>
public class NeteasePlaylistProvider : IPlaylistImportProvider
{
    private readonly MusicHttpClient _http;
    private readonly ILogger<NeteasePlaylistProvider> _logger;
    private const string WeapiBaseUrl = "https://music.163.com/weapi";
    private static readonly Uri NeteaseUrl = new("https://music.163.com/");

    private static readonly Dictionary<string, string> DefaultHeaders = new()
    {
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Safari/537.36 Chrome/91.0.4472.164 NeteaseMusicDesktop/3.0.18.203152",
        ["Referer"] = "https://music.163.com/",
        ["Origin"] = "https://music.163.com"
    };

    private string _uid = string.Empty;
    private string _nickname = string.Empty;

    public string PlatformId => "netease";
    public string PlatformName => "网易云音乐";

    public NeteasePlaylistProvider(MusicHttpClient http, ILogger<NeteasePlaylistProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    // ============ 粘贴 Cookie 登录 ============

    public async Task<AccountInfo?> LoginByCookieAsync(string cookieText)
    {
        if (string.IsNullOrWhiteSpace(cookieText)) return null;
        var cleaned = CleanCookieHeader(cookieText);
        _http.SetCookies(NeteaseUrl, cleaned);

        var status = await EnsureLoginStatusAsync();
        if (status == null)
        {
            _http.ClearCookies();
            return null;
        }
        (_uid, _nickname) = status.Value;
        return BuildAccountInfo();
    }

    // ============ 会话恢复 / 退出 ============

    public Task LoadAccountAsync(AccountInfo account)
    {
        _uid = account?.Uid ?? string.Empty;
        _nickname = account?.DisplayName ?? string.Empty;
        if (!string.IsNullOrEmpty(account?.AuthData))
        {
            try
            {
                using var doc = JsonDocument.Parse(account.AuthData);
                if (doc.RootElement.TryGetProperty("cookies", out var c) && c.ValueKind == JsonValueKind.String)
                    _http.SetCookies(NeteaseUrl, c.GetString() ?? string.Empty);
            }
            catch { /* 损坏的旧数据，忽略 */ }
        }
        return Task.CompletedTask;
    }

    public Task LogoutAsync()
    {
        _http.ClearCookies();
        _uid = string.Empty;
        _nickname = string.Empty;
        return Task.CompletedTask;
    }

    // ============ 我的歌单 ============

    public async Task<List<RemotePlaylist>> GetMyPlaylistsAsync()
    {
        if (string.IsNullOrEmpty(_uid))
        {
            var status = await EnsureLoginStatusAsync();
            if (status == null)
                throw new InvalidOperationException("登录已过期，请重新登录");
            (_uid, _nickname) = status.Value;
        }

        var result = new List<RemotePlaylist>();
        var uid = long.Parse(_uid);
        for (var offset = 0; ; offset += 1000)
        {
            var resp = await _http.PostWeapiAsync<NeteaseUserPlaylistResp>(
                $"{WeapiBaseUrl}/user/playlist",
                new { uid, limit = 1000, offset, includeVideo = true }, DefaultHeaders);
            var list = resp?.Playlist;
            if (list == null || list.Count == 0) break;

            foreach (var pl in list)
            {
                if (string.IsNullOrEmpty(pl.Name)) continue;
                var liked = pl.SpecialType == 5;
                result.Add(new RemotePlaylist
                {
                    Platform = "netease",
                    RemoteId = pl.Id.ToString(),
                    Name = pl.Name,
                    TrackCount = pl.TrackCount,
                    IsLiked = liked,
                    IsCollected = pl.Subscribed == true && !liked,
                    CoverUrl = pl.CoverImgUrl
                });
            }
            if (list.Count < 1000) break;
        }

        // 排序：我喜欢的音乐优先，其次我创建的，最后我收藏的
        return result
            .OrderByDescending(r => r.IsLiked)
            .ThenBy(r => r.IsCollected)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ============ 歌单歌曲（自动分页） ============

    public async Task<List<MusicInfo>> GetPlaylistSongsAsync(RemotePlaylist playlist)
    {
        if (playlist == null) return new List<MusicInfo>();
        return playlist.IsLiked
            ? await GetLikedSongsAsync()
            : await GetPlaylistDetailAllSongsAsync(playlist.RemoteId);
    }

    /// <summary>我喜欢的音乐：song/like/get 分页取 id，再批量解析详情</summary>
    private async Task<List<MusicInfo>> GetLikedSongsAsync()
    {
        var ids = new List<long>();
        const int limit = 1000;
        for (var offset = 0; ; offset += limit)
        {
            var resp = await _http.PostWeapiAsync<NeteaseLikeGetResp>(
                $"{WeapiBaseUrl}/song/like/get",
                new { uid = long.Parse(_uid), limit, offset }, DefaultHeaders);
            var batch = resp?.Ids;
            if (batch == null || batch.Count == 0) break;
            ids.AddRange(batch);
            if (batch.Count < limit) break;
        }
        return await ResolveSongsByIdsAsync(ids);
    }

    /// <summary>普通歌单：linuxapi 取全部 trackIds，再批量解析详情</summary>
    private async Task<List<MusicInfo>> GetPlaylistDetailAllSongsAsync(string remoteId)
    {
        var detail = await _http.PostLinuxApiAsync<NeteaseLinuxApiResp>(new
        {
            method = "POST",
            url = "https://music.163.com/api/v6/playlist/detail",
            @params = new { id = long.Parse(remoteId), n = 100000, s = 8 }
        }, DefaultHeaders);

        var trackIds = detail?.Playlist?.TrackIds?.Select(t => t.Id).ToList()
                       ?? detail?.Playlist?.Tracks?.Select(t => t.Id).ToList()
                       ?? new List<long>();
        if (trackIds.Count == 0) return new List<MusicInfo>();
        return await ResolveSongsByIdsAsync(trackIds);
    }

    /// <summary>weapi /v3/song/detail 按 200 一批解析歌曲详情</summary>
    private async Task<List<MusicInfo>> ResolveSongsByIdsAsync(List<long> ids)
    {
        var result = new List<MusicInfo>();
        const int chunk = 200;
        for (var i = 0; i < ids.Count; i += chunk)
        {
            var part = ids.Skip(i).Take(chunk).ToList();
            var cJson = "[" + string.Join(",", part.Select(id => "{\"id\":" + id + "}")) + "]";
            var idsJson = "[" + string.Join(",", part) + "]";
            var resp = await _http.PostWeapiAsync<NeteasePlaylistTrackResponse>(
                $"{WeapiBaseUrl}/v3/song/detail",
                new { c = cJson, ids = idsJson }, DefaultHeaders);
            if (resp?.Songs != null)
                result.AddRange(resp.Songs.Select(MapSong));
        }
        return result;
    }

    // ============ 辅助 ============

    private async Task<(string uid, string nickname)?> EnsureLoginStatusAsync()
    {
        try
        {
            var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var resp = await _http.PostWeapiAsync<NeteaseLoginStatusResp>(
                $"{WeapiBaseUrl}/nuser/account/get", new { timestamp = ts }, DefaultHeaders);
            if (resp?.Profile != null)
                return (resp.Profile.UserId.ToString(), resp.Profile.Nickname ?? string.Empty);
            return null;
        }
        catch (Exception)
        {
            _logger.LogError("网易云登录状态校验失败：{Diagnostic}",
                LoginDiagnostics.CreateSafeSummary(PlatformId, string.Empty, LoginDiagnosticOutcome.Exception,
                    LoginDiagnosticError.Unexpected, LoginDiagnostics.CreateCorrelationId()));
            return null;
        }
    }

    private AccountInfo BuildAccountInfo()
    {
        var cookies = _http.GetCookies(NeteaseUrl);
        return new AccountInfo
        {
            Platform = "netease",
            Uid = _uid,
            DisplayName = _nickname,
            LoginTime = DateTime.Now,
            ExpireTime = DateTime.Now.AddDays(7),
            AuthData = JsonSerializer.Serialize(new { cookies })
        };
    }

    /// <summary>清理用户粘贴的 Cookie 文本（去换行/空白，规整为 key=value 分号连接）</summary>
    private static string CleanCookieHeader(string text)
    {
        var parts = text
            .Split(';', '\n', '\r')
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p) && p.Contains('='))
            .Select(p =>
            {
                var idx = p.IndexOf('=');
                var k = p[..idx].Trim();
                var v = p[(idx + 1)..].Trim().TrimEnd(';');
                return $"{k}={v}";
            });
        return string.Join("; ", parts);
    }

    private static MusicInfo MapSong(NeteaseSong song) => new()
    {
        Id = song.Id.ToString(),
        Title = song.Name ?? "未知歌曲",
        Artist = song.GetArtistNames(),
        Album = song.Album?.Name ?? "未知专辑",
        Duration = song.GetDurationString(),
        Source = "netease",
        CoverUrl = song.Album?.PicUrl ?? "",
        Quality = MapQuality(song.Privilege)
    };

    private static int MapQuality(NeteasePrivilege? p)
    {
        if (p == null) return 128;
        return p.Maxbr switch { 999000 => 999, 320000 => 320, 192000 => 192, _ => 128 };
    }
}

// ============ 网易云个人歌单相关响应模型 ============

public class NeteaseLoginStatusResp
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("profile")] public NeteaseProfile? Profile { get; set; }
}

public class NeteaseProfile
{
    [JsonPropertyName("userId")] public long UserId { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
}

public class NeteaseUserPlaylistResp
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("playlist")] public List<NeteaseUserPlaylist>? Playlist { get; set; }
}

public class NeteaseUserPlaylist
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("subscribed")] public bool? Subscribed { get; set; }
    [JsonPropertyName("trackCount")] public int TrackCount { get; set; }
    [JsonPropertyName("coverImgUrl")] public string? CoverImgUrl { get; set; }
    [JsonPropertyName("specialType")] public int SpecialType { get; set; }
}

public class NeteaseLikeGetResp
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("ids")] public List<long>? Ids { get; set; }
}
