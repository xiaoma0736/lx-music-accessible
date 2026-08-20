using System.Text.Json;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 酷我音乐个人歌单导入 Provider。
/// 登录关键 Cookie：userid、websid（2023 年后酷我网页登录不再下发旧版 kw_token）。
/// 酷我官网接口有反爬（请求头 Secret = f(Hm_Iuvt cookie, cookie 名)，reqId 必须是 UUID v1，且需带 Accept 头），
/// 只能在浏览器页面上下文中通过，因此个人歌单列表由网页登录窗口在页面内拉取后传入本 Provider 缓存；
/// 歌单歌曲走公开接口 nplserver.kuwo.cn/pl.svc（无需反爬，已验证可用）。
/// 凭据仅保存在本机（外层服务 DPAPI 加密持久化），请求直连酷我官方接口，不经过任何第三方服务器。
/// </summary>
public class KuwoPlaylistProvider : IPlaylistImportProvider
{
    private readonly MusicHttpClient _http;
    private readonly ILogger<KuwoPlaylistProvider> _logger;
    private static readonly Uri KuwoUrl = new("https://www.kuwo.cn/");
    private const string NplServerBase = "http://nplserver.kuwo.cn/pl.svc";

    private string _uid = string.Empty;
    private string? _browserDisplayName;
    private List<RemotePlaylist>? _playlists;

    public string PlatformId => "kw";
    public string PlatformName => "酷我音乐";

    public KuwoPlaylistProvider(MusicHttpClient http, ILogger<KuwoPlaylistProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>接收网页登录窗口在页面内拉取到的个人歌单与昵称（酷我反爬只能在页面内通过）</summary>
    public void SetBrowserLoginResult(string? displayName, List<RemotePlaylist>? playlists)
    {
        _browserDisplayName = displayName;
        _playlists = playlists;
    }

    // ============ 粘贴 Cookie 登录 ============

    public Task<AccountInfo?> LoginByCookieAsync(string cookieText)
    {
        if (string.IsNullOrWhiteSpace(cookieText))
            return Task.FromResult<AccountInfo?>(null);

        var cleaned = CleanCookieHeader(cookieText);
        var userId = ExtractValue(cleaned, "userid");
        var websid = ExtractValue(cleaned, "websid");
        // 登录关键 Cookie：userid + websid（旧版 kw_token 已废弃，不再作为登录判定依据）
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(websid))
            return Task.FromResult<AccountInfo?>(null);

        _http.SetCookies(KuwoUrl, cleaned, ".kuwo.cn");
        _uid = userId;

        // 酷我 API 有反爬（Secret 头 + UUIDv1 reqId），脱离浏览器页面无法通过，故此处不做联网校验；
        // 个人歌单列表依赖网页登录时在页面内拉取（见 SetBrowserLoginResult / GetMyPlaylistsAsync）。
        try
        {
            return Task.FromResult<AccountInfo?>(BuildAccountInfo());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "酷我 Cookie 登录失败");
            _http.ClearCookies();
            return Task.FromResult<AccountInfo?>(null);
        }
    }

    // ============ 会话恢复 / 退出 ============

    public Task LoadAccountAsync(AccountInfo account)
    {
        _uid = account?.Uid ?? string.Empty;
        _browserDisplayName = string.IsNullOrEmpty(account?.DisplayName) ? null : account!.DisplayName;
        _playlists = null;
        if (!string.IsNullOrEmpty(account?.AuthData))
        {
            try
            {
                using var doc = JsonDocument.Parse(account.AuthData);
                if (doc.RootElement.TryGetProperty("cookies", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var cleaned = CleanCookieHeader(c.GetString() ?? string.Empty);
                    _http.SetCookies(KuwoUrl, cleaned, ".kuwo.cn");
                }
                if (doc.RootElement.TryGetProperty("playlists", out var pl) && pl.ValueKind == JsonValueKind.String)
                {
                    var json = pl.GetString();
                    if (!string.IsNullOrEmpty(json))
                        _playlists = JsonSerializer.Deserialize<List<RemotePlaylist>>(json);
                }
            }
            catch { /* 损坏的旧数据，忽略 */ }
        }
        return Task.CompletedTask;
    }

    public Task LogoutAsync()
    {
        _http.ClearCookies();
        _uid = string.Empty;
        _browserDisplayName = null;
        _playlists = null;
        return Task.CompletedTask;
    }

    // ============ 我的歌单 ============

    public async Task<List<RemotePlaylist>> GetMyPlaylistsAsync()
    {
        // 优先用网页登录时在页面内拉取到的歌单（可能包含收藏歌单）
        if (_playlists != null)
            return _playlists; // 可能为空列表（已登录但无歌单），也可能有数据

        // 兜底：服务端直连 nplserver（无需浏览器反爬）按 uid 拉取用户歌单，比页面内拉取更可靠
        if (!string.IsNullOrEmpty(_uid))
        {
            var server = await FetchUserPlaylistsFromServerAsync();
            if (server != null)
            {
                _playlists = server;
                return server;
            }
        }

        throw new InvalidOperationException(
            "未获取到酷我音乐的个人歌单。酷我网页版个人歌单接口存在反爬限制，可能无法自动读取；" +
            "请确认已在网页登录窗口中完成登录，必要时可退出后重新使用“网页登录”再试。");
    }

    /// <summary>服务端直连 nplserver.kuwo.cn/pl.svc 按 uid 获取用户歌单（公开接口，无需反爬 Secret）</summary>
    private async Task<List<RemotePlaylist>?> FetchUserPlaylistsFromServerAsync()
    {
        try
        {
            var result = new List<RemotePlaylist>();
            const int pageSize = 100;
            for (var page = 0; ; page++)
            {
                var raw = await _http.GetAsync(
                    NplServerBase,
                    new Dictionary<string, string>
                    {
                        ["op"] = "getuserlist",
                        ["uid"] = _uid,
                        ["pn"] = page.ToString(),
                        ["rn"] = pageSize.ToString(),
                        ["encode"] = "utf8",
                        ["keyset"] = "pl2012"
                    },
                    new Dictionary<string, string>
                    {
                        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                        ["Referer"] = "http://www.kuwo.cn/"
                    });
                var resp = JsonDocument.Parse(raw).RootElement;
                if (!resp.TryGetProperty("result", out var res) || res.GetString() != "ok") break;
                if (!resp.TryGetProperty("plist", out var plist) || plist.ValueKind != JsonValueKind.Array || plist.GetArrayLength() == 0)
                    break;

                foreach (var p in plist.EnumerateArray())
                {
                    var id = GetString(p, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var type = GetString(p, "type");
                    // 跳过无意义的系统特殊列表（手机默认/排序/标签/电台等）
                    if (string.Equals(type, "MOBI_DEFAULT", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "ORDER", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "TAG", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "RADIO", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var name = GetString(p, "title");
                    if (string.IsNullOrEmpty(name) && !string.Equals(type, "PC_DEFAULT", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var total = 0;
                    if (p.TryGetProperty("musicnum", out var mn))
                        total = mn.ValueKind == JsonValueKind.Number ? mn.GetInt32() : (int.TryParse(mn.GetString(), out var n) ? n : 0);
                    result.Add(new RemotePlaylist
                    {
                        Platform = "kw",
                        RemoteId = id,
                        Name = string.IsNullOrEmpty(name) ? "默认列表" : name,
                        TrackCount = total,
                        IsLiked = string.Equals(type, "PC_DEFAULT", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(type, "MYFAVORITE", StringComparison.OrdinalIgnoreCase),
                        CoverUrl = GetString(p, "pic")
                    });
                }

                var got = plist.GetArrayLength();
                int total2 = resp.TryGetProperty("total", out var t)
                    ? (t.ValueKind == JsonValueKind.Number ? t.GetInt32() : (int.TryParse(t.GetString(), out var n2) ? n2 : -1))
                    : -1;
                if (got < pageSize) break;
                if (total2 >= 0 && result.Count >= total2) break;
            }
            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "酷我 nplserver 获取用户歌单失败: uid={Uid}", _uid);
            return null;
        }
    }

    // ============ 歌单歌曲（公开接口，已验证） ============

    public async Task<List<MusicInfo>> GetPlaylistSongsAsync(RemotePlaylist playlist)
    {
        if (playlist == null) throw new ArgumentNullException(nameof(playlist));

        var result = new List<MusicInfo>();
        const int pageSize = 100;
        for (var page = 0; ; page++)
        {
            var raw = await _http.GetAsync(
                NplServerBase,
                new Dictionary<string, string>
                {
                    ["op"] = "getlistinfo",
                    ["pid"] = playlist.RemoteId,
                    ["pn"] = page.ToString(),
                    ["rn"] = pageSize.ToString(),
                    ["encode"] = "utf8",
                    ["keyset"] = "pl2012",
                    ["identity"] = "kuwo",
                    ["pcmp4"] = "1",
                    ["vipver"] = "MUSIC_9.0.5.0_W1",
                    ["newver"] = "1"
                },
                new Dictionary<string, string>
                {
                    ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                    ["Referer"] = "http://www.kuwo.cn/"
                });
            var resp = JsonDocument.Parse(raw).RootElement;

            if (resp.ValueKind == JsonValueKind.Undefined) break;
            if (!resp.TryGetProperty("result", out var res) || res.GetString() != "ok") break;
            if (!resp.TryGetProperty("musiclist", out var musiclist) || musiclist.ValueKind != JsonValueKind.Array || musiclist.GetArrayLength() == 0)
                break;

            foreach (var s in musiclist.EnumerateArray())
                result.Add(MapSong(s));

            var got = musiclist.GetArrayLength();
            int total = resp.TryGetProperty("total", out var t)
                ? (t.ValueKind == JsonValueKind.Number ? t.GetInt32() : (int.TryParse(t.GetString(), out var n) ? n : -1))
                : -1;
            if (got < pageSize) break;
            if (total >= 0 && result.Count >= total) break;
        }
        return result;
    }

    private static MusicInfo MapSong(JsonElement s) => new()
    {
        Id = GetString(s, "id"),
        Title = GetString(s, "name"),
        Artist = GetString(s, "artist"),
        Album = GetString(s, "album"),
        Duration = FormatDuration(GetString(s, "duration")),
        Source = "kw",
        Quality = 128
    };

    // ============ 辅助 ============

    private AccountInfo BuildAccountInfo()
    {
        var cookies = _http.GetCookies(KuwoUrl);
        string? playlistsJson = _playlists == null ? null : JsonSerializer.Serialize(_playlists);
        return new AccountInfo
        {
            Platform = "kw",
            Uid = string.IsNullOrEmpty(_uid) ? ExtractValue(cookies, "userid") : _uid,
            DisplayName = string.IsNullOrEmpty(_browserDisplayName) ? "酷我音乐用户" : _browserDisplayName!,
            LoginTime = DateTime.Now,
            ExpireTime = DateTime.Now.AddDays(30),
            AuthData = JsonSerializer.Serialize(new { cookies, playlists = playlistsJson })
        };
    }

    private static string GetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v))
        {
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? string.Empty,
                JsonValueKind.Number => v.GetRawText(),
                _ => string.Empty
            };
        }
        return string.Empty;
    }

    private static string FormatDuration(string seconds)
    {
        if (int.TryParse(seconds, out var sec))
        {
            var ts = TimeSpan.FromSeconds(sec);
            return ts.Hours > 0 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
        }
        return string.Empty;
    }

    private static string ExtractValue(string cookieHeader, string key)
    {
        foreach (var part in cookieHeader.Split(';', StringSplitOptions.TrimEntries))
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
