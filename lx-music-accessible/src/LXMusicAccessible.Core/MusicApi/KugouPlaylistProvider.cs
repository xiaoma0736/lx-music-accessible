using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 酷狗音乐个人歌单导入 Provider。
/// 登录采用粘贴 Cookie（www.kugou.com 登录后 F12 → document.cookie），需包含 KugooID 与 t（兼容 userid 与 token）；
/// “我的歌单”走酷狗 Android 客户端接口 cloudlist.service.kugou.com/v7/get_all_list（MD5 签名，参考 SakuraCake/ku_gou_music_api）；
/// 歌单歌曲走 cloudlist.service.kugou.com/v4/get_list_all_file（Android 签名，需客户端 token，用 listid）。
/// 凭据仅保存在本机（外层服务 DPAPI 加密持久化），请求直连酷狗官方接口，不经过任何第三方服务器。
/// </summary>
public class KugouPlaylistProvider : IPlaylistImportProvider
{
    private readonly MusicHttpClient _http;
    private readonly ILogger<KugouPlaylistProvider> _logger;
    private static readonly Uri KugouUrl = new("https://www.kugou.com/");

    // 酷狗 Android 标准版常量（来自 ku_gou_music_api）
    private const int AppId = 1005;
    private const int ClientVer = 20489;
    private const string SignatureSalt = "OIlwieks28dk2k092lksi2UIkp";
    private const string UserAgent = "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi";
    private const string KgRf = "B9EDA08A64250DEFFBCADDEE00F8F25F";

    private readonly string _mid;
    private string _dfid = "-";
    private string _uid = string.Empty;
    private string _token = string.Empty;
    private string _nickname = string.Empty;

    public string PlatformId => "kg";
    public string PlatformName => "酷狗音乐";

    public KugouPlaylistProvider(MusicHttpClient http, ILogger<KugouPlaylistProvider> logger)
    {
        _http = http;
        _logger = logger;
        _mid = GenerateMid();
    }

    // ============ 粘贴 Cookie 登录 ============

    public async Task<AccountInfo?> LoginByCookieAsync(string cookieText)
    {
        if (string.IsNullOrWhiteSpace(cookieText)) return null;
        var cleaned = CleanCookieHeader(cookieText);
        var uid = ExtractUid(cleaned);
        var token = ExtractToken(cleaned);
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(token))
            return null;

        _http.SetCookies(KugouUrl, cleaned, ".kugou.com");
        _uid = uid;
        _token = token;
        _dfid = string.IsNullOrEmpty(ExtractValue(cleaned, "dfid")) ? "-" : ExtractValue(cleaned, "dfid");

        // 已具备 userid+token 即视为登录成功；歌单接口偶发不可用时不再阻断登录（可在“获取我的歌单”时重试）
        try
        {
            await GetMyPlaylistsAsync();
        }
        catch (Exception)
        {
            _logger.LogWarning("酷狗 Cookie 已具备登录凭据，但歌单校验接口异常，继续完成登录：{Diagnostic}",
                LoginDiagnostics.CreateSafeSummary(PlatformId, cleaned, LoginDiagnosticOutcome.Exception,
                    LoginDiagnosticError.Unexpected, LoginDiagnostics.CreateCorrelationId()));
        }
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
                {
                    var cleaned = CleanCookieHeader(c.GetString() ?? string.Empty);
                    _http.SetCookies(KugouUrl, cleaned, ".kugou.com");
                    _token = ExtractToken(cleaned);
                    _dfid = string.IsNullOrEmpty(ExtractValue(cleaned, "dfid")) ? "-" : ExtractValue(cleaned, "dfid");
                }
                if (doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                    _token = t.GetString() ?? _token;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("酷狗登录状态恢复失败: {ErrorType}", ex.GetType().Name);
            }
        }
        return Task.CompletedTask;
    }

    public Task LogoutAsync()
    {
        _http.ClearCookies();
        _uid = string.Empty;
        _token = string.Empty;
        _nickname = string.Empty;
        return Task.CompletedTask;
    }

    // ============ 我的歌单 ============

    public async Task<List<RemotePlaylist>> GetMyPlaylistsAsync()
    {
        if (string.IsNullOrEmpty(_uid) || string.IsNullOrEmpty(_token))
            throw new InvalidOperationException("登录已过期，请重新登录");

        var result = new List<RemotePlaylist>();
        // type=0 我创建的歌单；type=1 我收藏的歌单
        result.AddRange(await FetchUserListsAsync(0, isCollected: false));
        result.AddRange(await FetchUserListsAsync(1, isCollected: true));
        return result
            .GroupBy(r => r.RemoteId)
            .Select(g => g.First())
            .OrderBy(r => r.IsCollected)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<RemotePlaylist>> FetchUserListsAsync(int type, bool isCollected)
    {
        var result = new List<RemotePlaylist>();
        const int pageSize = 100;
        var seen = new HashSet<long>();

        for (var page = 1; ; page++)
        {
            var body = new Dictionary<string, object>
            {
                ["userid"] = long.Parse(_uid),
                ["token"] = _token,
                ["total_ver"] = 979,
                ["type"] = type,
                ["page"] = page,
                ["pagesize"] = pageSize
            };
            var resp = await PostAndroidAsync<JsonElement>("/v7/get_all_list", body, "cloudlist.service.kugou.com");
            if (resp.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("获取歌单失败：网络异常");
            if (resp.TryGetProperty("error_code", out var ec) && ec.ValueKind == JsonValueKind.Number && ec.GetInt32() != 0)
            {
                _logger.LogWarning(
                    "酷狗云歌单接口返回 error_code={Code}，type={Type}，page={Page}，tokenLen={TokenLen}",
                    ec.GetInt32(), type, page, _token?.Length ?? 0);
                throw new InvalidOperationException("酷狗登录已失效，请重新粘贴Cookie登录");
            }

            var lists = FindPlaylistArray(resp);
            foreach (var item in lists)
            {
                var id = GetPlaylistId(item);
                var name = GetPlaylistName(item);
                if (id <= 0 || string.IsNullOrEmpty(name) || !seen.Add(id)) continue;
                result.Add(new RemotePlaylist
                {
                    Platform = "kg",
                    RemoteId = id.ToString(),
                    Name = name,
                    TrackCount = GetPlaylistCount(item),
                    IsLiked = false,
                    IsCollected = isCollected,
                    CoverUrl = GetPlaylistCover(item)
                });
            }

            // total is accepted when present, but a short page remains authoritative for endpoints that
            // omit it; this avoids stopping after a full page when total is stale.
            var total = resp.TryGetProperty("data", out var data)
                && data.TryGetProperty("total", out var totalElement)
                && totalElement.ValueKind == JsonValueKind.Number
                ? totalElement.GetInt32()
                : -1;
            if (lists.Count < pageSize || (total >= 0 && result.Count >= total)) break;
            if (page >= 100)
                throw new InvalidOperationException("获取歌单失败：分页数量异常。");
        }

        return result;
    }
    // ============ 歌单歌曲（公开接口，已验证） ============

    /// <summary>歌单歌曲：cloudlist.service.kugou.com/v4/get_list_all_file（Android 签名，需客户端 token，用 listid）</summary>
    public async Task<List<MusicInfo>> GetPlaylistSongsAsync(RemotePlaylist playlist)
    {
        if (playlist == null) throw new ArgumentNullException(nameof(playlist));

        var result = new List<MusicInfo>();
        const int pageSize = 50;
        for (var page = 1; ; page++)
        {
            var body = new Dictionary<string, object>
            {
                ["listid"] = long.Parse(playlist.RemoteId),
                ["userid"] = long.Parse(_uid),
                ["area_code"] = 1,
                ["show_relate_goods"] = 0,
                ["pagesize"] = pageSize,
                ["allplatform"] = 1,
                ["show_cover"] = 1,
                ["type"] = 0,
                ["token"] = _token,
                ["page"] = page
            };

            var resp = await PostAndroidAsync<JsonElement>("/v4/get_list_all_file", body, "cloudlist.service.kugou.com");
            if (resp.ValueKind == JsonValueKind.Undefined) break;
            if (resp.TryGetProperty("error_code", out var ec) && ec.ValueKind == JsonValueKind.Number && ec.GetInt32() != 0) break;
            if (!resp.TryGetProperty("data", out var data)) break;
            if (!data.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array || info.GetArrayLength() == 0)
                break;

            foreach (var s in info.EnumerateArray())
                result.Add(MapSong(s));

            var got = info.GetArrayLength();
            int total = data.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
            if (got < pageSize) break;
            if (total >= 0 && result.Count >= total) break;
        }
        return result;
    }

    private static MusicInfo MapSong(JsonElement s) => new()
    {
        Id = GetString(s, "hash"),
        Hash = GetString(s, "hash"),
        Title = GetSongTitle(s),
        Artist = GetSongArtist(s),
        Album = GetSongAlbum(s),
        Duration = FormatDurationMs(GetLong(s, "timelen")),
        Source = "kg",
        CoverUrl = GetSongCover(s),
        Quality = 128
    };

    /// <summary>酷狗歌曲 name 形如“歌手 - 歌名.mp3”，拆出歌名（去掉扩展名）</summary>
    private static string GetSongTitle(JsonElement s)
    {
        var fn = GetString(s, "name");
        if (fn.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            fn = fn[..^4];
        var dash = fn.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && dash + 3 < fn.Length)
            return fn[(dash + 3)..];
        return string.IsNullOrEmpty(fn) ? "未知歌曲" : fn;
    }

    /// <summary>酷狗歌曲歌手在 singerinfo 数组中（[{name,...}]）</summary>
    private static string GetSongArtist(JsonElement s)
    {
        if (s.TryGetProperty("singerinfo", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var names = arr.EnumerateArray()
                .Select(x => GetString(x, "name"))
                .Where(n => !string.IsNullOrEmpty(n));
            var joined = string.Join("、", names);
            if (!string.IsNullOrEmpty(joined)) return joined;
        }
        return "未知歌手";
    }

    private static string GetSongAlbum(JsonElement s)
    {
        if (s.TryGetProperty("albuminfo", out var al) && al.ValueKind == JsonValueKind.Object)
            return GetString(al, "name");
        return "未知专辑";
    }

    private static string GetSongCover(JsonElement s)
    {
        var cover = GetString(s, "cover");
        return cover.Replace("{size}", "300");
    }

    // ============ Android 签名请求 ============

    private async Task<T?> PostAndroidAsync<T>(string path, Dictionary<string, object> body, string router)
    {
        var clienttime = DateTimeOffset.Now.ToUnixTimeSeconds();
        var jsonBody = JsonSerializer.Serialize(body);

        var @params = new Dictionary<string, object>
        {
            ["dfid"] = _dfid,
            ["mid"] = _mid,
            ["uuid"] = "-",
            ["appid"] = AppId,
            ["clientver"] = ClientVer,
            ["clienttime"] = clienttime
        };
        if (!string.IsNullOrEmpty(_token)) @params["token"] = _token;
        if (!string.IsNullOrEmpty(_uid)) @params["userid"] = long.Parse(_uid);
        @params["signature"] = SignAndroid(@params, jsonBody);

        var query = string.Join("&", @params.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value?.ToString() ?? "")}"));
        var url = $"https://gateway.kugou.com{path}?{query}";

        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = UserAgent,
            ["Accept"] = "application/json, text/plain, */*",
            ["dfid"] = _dfid,
            ["clienttime"] = clienttime.ToString(),
            ["mid"] = _mid,
            ["kg-rc"] = "1",
            ["kg-thash"] = "5d816a0",
            ["kg-rec"] = "1",
            ["kg-rf"] = KgRf,
            ["x-router"] = router
        };

        return await _http.PostRawJsonAsync<T>(url, jsonBody, headers);
    }

    private static string SignAndroid(Dictionary<string, object> @params, string data)
    {
        var sorted = @params.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value is string s ? $"{kv.Key}={s}" : $"{kv.Key}={kv.Value}");
        var paramsStr = string.Concat(sorted);
        var input = SignatureSalt + paramsStr + data + SignatureSalt;
        return FrameworkCompatibility.ToHexString(FrameworkCompatibility.Md5HashData(Encoding.UTF8.GetBytes(input))).ToLower();
    }

    private static string GenerateMid()
    {
        var guid = Guid.NewGuid().ToString();
        var md5Hex = FrameworkCompatibility.ToHexString(FrameworkCompatibility.Md5HashData(Encoding.UTF8.GetBytes(guid))).ToLower();
        var big = BigInteger.Parse("0" + md5Hex, NumberStyles.HexNumber);
        return big.ToString();
    }

    // ============ 响应解析 ============

    private static List<JsonElement> FindPlaylistArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Undefined) return new List<JsonElement>();
        if (root.TryGetProperty("data", out var data))
        {
            foreach (var key in new[] { "list", "info", "playlists" })
            {
                if (data.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var items = arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList();
                    if (items.Count > 0) return items;
                }
            }
            if (data.ValueKind == JsonValueKind.Array)
                return data.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList();
        }
        return new List<JsonElement>();
    }

    private static long GetPlaylistId(JsonElement item)
    {
        foreach (var key in new[] { "listid", "list_id", "specialid", "id", "special_id" })
            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetInt64();
        return 0;
    }

    private static string GetPlaylistName(JsonElement item)
    {
        foreach (var key in new[] { "name", "list_name", "specialname", "title" })
            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static int GetPlaylistCount(JsonElement item)
    {
        foreach (var key in new[] { "count", "m_count", "songcount", "song_num", "songs_total", "total" })
            if (item.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n)) return n;
            }
        return 0;
    }

    private static string GetPlaylistCover(JsonElement item)
    {
        foreach (var key in new[] { "img", "cover", "pic", "imgurl" })
            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return (v.GetString() ?? string.Empty).Replace("{size}", "300");
        return string.Empty;
    }

    private static string GetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static long GetLong(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetInt64();
        return 0;
    }

    private static string FormatDurationMs(long milliseconds)
    {
        var ts = TimeSpan.FromMilliseconds(milliseconds);
        return ts.Hours > 0 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    private AccountInfo BuildAccountInfo()
    {
        var cookies = _http.GetCookies(KugouUrl);
        return new AccountInfo
        {
            Platform = "kg",
            Uid = _uid,
            DisplayName = string.IsNullOrEmpty(_nickname) ? $"酷狗用户{_uid}" : _nickname,
            LoginTime = DateTime.Now,
            ExpireTime = DateTime.Now.AddDays(30),
            AuthData = JsonSerializer.Serialize(new { cookies, token = _token })
        };
    }

    /// <summary>提取登录用户ID：优先 KugooID（网页版登录），兜底 userid（客户端版）</summary>
    private static string ExtractUid(string cookieHeader)
    {
        var v = ExtractValue(cookieHeader, "KugooID");
        return string.IsNullOrEmpty(v) ? ExtractValue(cookieHeader, "userid") : v;
    }

    /// <summary>提取登录令牌：优先 t（网页版登录），兜底 token（客户端版）</summary>
    private static string ExtractToken(string cookieHeader)
    {
        var v = ExtractValue(cookieHeader, "t");
        return string.IsNullOrEmpty(v) ? ExtractValue(cookieHeader, "token") : v;
    }

    private static string ExtractValue(string cookieHeader, string key)
    {
        foreach (var part in cookieHeader.SplitTrimmed(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }
        return string.Empty;
    }

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
}
