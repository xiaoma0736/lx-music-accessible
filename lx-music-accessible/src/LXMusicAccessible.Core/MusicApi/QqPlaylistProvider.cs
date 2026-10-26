using System.Text.Json;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// QQ音乐个人歌单导入 Provider。
/// 登录支持：粘贴 Cookie（y.qq.com 登录后 F12 → document.cookie，需包含 uin 与 qm_keyst/qqmusic_key）。
/// “我的歌单”走 musicu.fcg（music.musicasset.PlaylistBaseRead.GetPlaylistByUin 创建 / PlaylistFavRead.CgiGetPlaylistFavInfo 收藏），
/// 歌单歌曲走 musicu.fcg（music.srfDissInfo.DissInfo.CgiGetDiss，公开可用，已验证）。
/// 凭据仅保存在本机（外层服务 DPAPI 加密持久化），请求直连腾讯官方接口，不经过任何第三方服务器。
/// </summary>
public class QqPlaylistProvider : IPlaylistImportProvider
{
    private readonly MusicHttpClient _http;
    private readonly ILogger<QqPlaylistProvider> _logger;
    private static readonly Uri QqUrl = new("https://y.qq.com/");
    private const string MusicUUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";

    private static readonly Dictionary<string, string> DefaultHeaders = new()
    {
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        ["Referer"] = "https://y.qq.com/",
        ["Origin"] = "https://y.qq.com"
    };

    private string _uin = string.Empty;
    private string _nickname = string.Empty;

    public string PlatformId => "qq";
    public string PlatformName => "QQ音乐";

    public QqPlaylistProvider(MusicHttpClient http, ILogger<QqPlaylistProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    // ============ 粘贴 Cookie 登录 ============

    public async Task<AccountInfo?> LoginByCookieAsync(string cookieText)
    {
        if (string.IsNullOrWhiteSpace(cookieText)) return null;
        var cleaned = CleanCookieHeader(cookieText);
        var uin = ExtractUin(cleaned);
        if (string.IsNullOrEmpty(uin) || uin == "0")
            return null;

        // 必须带有非空的登录密钥（qm_keyst 或 qqmusic_key）
        var hasKey = HasNonEmptyValue(cleaned, "qm_keyst") || HasNonEmptyValue(cleaned, "qqmusic_key");
        if (!hasKey)
        {
            _logger.LogWarning("QQ Cookie 缺少非空的 qm_keyst/qqmusic_key 登录密钥，登录失败");
            return null;
        }

        _http.SetCookies(QqUrl, cleaned, ".qq.com");
        _uin = uin;

        // 已具备会话密钥即视为登录成功；歌单接口偶发不可用时不再阻断登录（可在“获取我的歌单”时重试）
        try
        {
            await GetMyPlaylistsAsync();
        }
        catch (Exception)
        {
            _logger.LogWarning("QQ Cookie 已具备登录密钥，但歌单校验接口异常，继续完成登录：{Diagnostic}",
                LoginDiagnostics.CreateSafeSummary(PlatformId, cleaned, LoginDiagnosticOutcome.Exception,
                    LoginDiagnosticError.Unexpected, LoginDiagnostics.CreateCorrelationId()));
        }
        return BuildAccountInfo();
    }

    // ============ 会话恢复 / 退出 ============

    public Task LoadAccountAsync(AccountInfo account)
    {
        _uin = account?.Uid ?? string.Empty;
        _nickname = account?.DisplayName ?? string.Empty;
        if (!string.IsNullOrEmpty(account?.AuthData))
        {
            try
            {
                using var doc = JsonDocument.Parse(account.AuthData);
                if (doc.RootElement.TryGetProperty("cookies", out var c) && c.ValueKind == JsonValueKind.String)
                    _http.SetCookies(QqUrl, c.GetString() ?? string.Empty, ".qq.com");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("QQ 音乐登录状态恢复失败: {ErrorType}", ex.GetType().Name);
            }
        }
        return Task.CompletedTask;
    }

    public Task LogoutAsync()
    {
        _http.ClearCookies();
        _uin = string.Empty;
        _nickname = string.Empty;
        return Task.CompletedTask;
    }

    // ============ 我的歌单 ============

    public async Task<List<RemotePlaylist>> GetMyPlaylistsAsync()
    {
        if (string.IsNullOrEmpty(_uin))
            throw new InvalidOperationException("登录已过期，请重新登录");

        var result = new List<RemotePlaylist>();
        result.AddRange(await FetchCreatedPlaylistsAsync());
        result.AddRange(await FetchCollectedPlaylistsAsync());

        if (result.Count == 0 && _http.GetCookies(QqUrl).IndexOf("qm_keyst", StringComparison.OrdinalIgnoreCase) < 0
            && _http.GetCookies(QqUrl).IndexOf("qqmusic_key", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Cookie 中缺少登录密钥（qm_keyst 或 qqmusic_key），登录无效");

        return result
            .OrderBy(r => r.IsCollected)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 拉取“创建的歌单”：music.musicasset.PlaylistBaseRead.GetPlaylistByUin（当前可用，替代已废弃的 macroDataUserDiss）。
    /// </summary>
    private async Task<List<RemotePlaylist>> FetchCreatedPlaylistsAsync()
    {
        var result = new List<RemotePlaylist>();
        try
        {
            var payload = new
            {
                comm = new { cv = 1602, ct = 24, uin = long.Parse(_uin) },
                req_0 = new
                {
                    module = "music.musicasset.PlaylistBaseRead",
                    method = "GetPlaylistByUin",
                    param = new { uin = _uin }
                }
            };

            var response = await _http.GetAsync(MusicUUrl, new Dictionary<string, string>
            {
                ["format"] = "json",
                ["platform"] = "wk_v15.json",
                ["data"] = JsonSerializer.Serialize(payload)
            }, DefaultHeaders);

            var root = JsonDocument.Parse(response).RootElement;
            if (!root.TryGetProperty("req_0", out var req0))
            {
                _logger.LogWarning("QQ 创建歌单响应缺少 req_0");
                return result;
            }
            if (req0.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
            {
                _logger.LogWarning("QQ 创建歌单接口返回错误 code={Code}", code.GetInt32());
                throw new InvalidOperationException("QQ 登录已失效，请重新登录");
            }
            if (!req0.TryGetProperty("data", out var data))
            {
                _logger.LogWarning("QQ 创建歌单响应缺少 data");
                return result;
            }

            foreach (var item in FindDissArray(data))
            {
                var id = GetDissId(item);
                var name = GetDissName(item);
                if (id <= 0 || string.IsNullOrEmpty(name)) continue;
                result.Add(new RemotePlaylist
                {
                    Platform = "qq",
                    RemoteId = id.ToString(),
                    Name = name,
                    TrackCount = GetDissCount(item),
                    IsLiked = false,
                    IsCollected = false,
                    CoverUrl = GetDissCover(item)
                });
            }
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception)
        {
            _logger.LogWarning("QQ 拉取创建歌单失败：{Diagnostic}",
                LoginDiagnostics.CreateSafeSummary(PlatformId, string.Empty, LoginDiagnosticOutcome.Exception,
                    LoginDiagnosticError.Unexpected, LoginDiagnostics.CreateCorrelationId()));
        }
        return result;
    }

    /// <summary>
    /// 拉取“收藏的歌单”：music.musicasset.PlaylistFavRead.CgiGetPlaylistFavInfo（需使用 Cookie 中的 euin，分页拉取）。
    /// </summary>
    private async Task<List<RemotePlaylist>> FetchCollectedPlaylistsAsync()
    {
        var result = new List<RemotePlaylist>();
        var euin = ExtractEuin(_http.GetCookies(QqUrl));
        if (string.IsNullOrEmpty(euin))
        {
            _logger.LogWarning("QQ Cookie 缺少 euin，无法拉取收藏歌单");
            return result;
        }

        const int pageSize = 100;
        try
        {
            for (var offset = 0; ; offset += pageSize)
            {
                var payload = new
                {
                    comm = new { cv = 1602, ct = 24, uin = long.Parse(_uin) },
                    req_0 = new
                    {
                        module = "music.musicasset.PlaylistFavRead",
                        method = "CgiGetPlaylistFavInfo",
                        param = new { uin = euin, offset, size = pageSize }
                    }
                };

                var response = await _http.GetAsync(MusicUUrl, new Dictionary<string, string>
                {
                    ["format"] = "json",
                    ["platform"] = "wk_v15.json",
                    ["data"] = JsonSerializer.Serialize(payload)
                }, DefaultHeaders);

                var root = JsonDocument.Parse(response).RootElement;
                if (!root.TryGetProperty("req_0", out var req0))
                {
                    _logger.LogWarning("QQ 收藏歌单响应缺少 req_0");
                    break;
                }
                if (req0.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
                {
                    _logger.LogWarning("QQ 收藏歌单接口返回错误 code={Code}", code.GetInt32());
                    break;
                }
                if (!req0.TryGetProperty("data", out var data))
                {
                    _logger.LogWarning("QQ 收藏歌单响应缺少 data");
                    break;
                }

                var page = FindDissArray(data);
                foreach (var item in page)
                {
                    var id = GetDissId(item);
                    var name = GetDissName(item);
                    if (id <= 0 || string.IsNullOrEmpty(name)) continue;
                    result.Add(new RemotePlaylist
                    {
                        Platform = "qq",
                        RemoteId = id.ToString(),
                        Name = name,
                        TrackCount = GetDissCount(item),
                        IsLiked = false,
                        IsCollected = true,
                        CoverUrl = GetDissCover(item)
                    });
                }

                var hasMore = data.TryGetProperty("hasmore", out var hm) && hm.ValueKind == JsonValueKind.Number && hm.GetInt32() == 1;
                if (!hasMore || page.Count == 0) break;
            }
        }
        catch (Exception)
        {
            _logger.LogWarning("QQ 拉取收藏歌单失败：{Diagnostic}",
                LoginDiagnostics.CreateSafeSummary(PlatformId, string.Empty, LoginDiagnosticOutcome.Exception,
                    LoginDiagnosticError.Unexpected, LoginDiagnostics.CreateCorrelationId()));
        }
        return result;
    }
// ============ 歌单歌曲 ============

    public async Task<List<MusicInfo>> GetPlaylistSongsAsync(RemotePlaylist playlist)
    {
        if (playlist == null) throw new ArgumentNullException(nameof(playlist));

        var result = new List<MusicInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int pageSize = 100;
        const int maxPages = 200;
        for (var page = 0; page < maxPages; page++)
        {
            var begin = page * pageSize;
            var payload = new
            {
                comm = new { cv = 1602, ct = 24, uin = 0L },
                req_0 = new
                {
                    module = "music.srfDissInfo.DissInfo",
                    method = "CgiGetDiss",
                    param = new
                    {
                        disstid = long.Parse(playlist.RemoteId),
                        onlysonglist = 0,
                        song_begin = begin,
                        song_num = pageSize,
                        tag = 1,
                        userinfo = 1
                    }
                }
            };

            var response = await _http.GetAsync(MusicUUrl, new Dictionary<string, string>
            {
                ["format"] = "json",
                ["platform"] = "wk_v15.json",
                ["data"] = JsonSerializer.Serialize(payload)
            }, DefaultHeaders);

            var root = JsonDocument.Parse(response).RootElement;
            if (!root.TryGetProperty("req_0", out var req0) || !req0.TryGetProperty("data", out var data))
                break;
            if (!data.TryGetProperty("songlist", out var songlist) || songlist.ValueKind != JsonValueKind.Array || songlist.GetArrayLength() == 0)
                break;

            var added = 0;
            foreach (var s in songlist.EnumerateArray())
            {
                var song = MapSong(s);
                if (!string.IsNullOrWhiteSpace(song.Id) && seen.Add(song.Id))
                {
                    result.Add(song);
                    added++;
                }
            }

            var got = songlist.GetArrayLength();
            if (added == 0) break;
            int total = -1;
            if (data.TryGetProperty("total_song_num", out var tn) && tn.ValueKind == JsonValueKind.Number)
                total = tn.GetInt32();
            if (got < pageSize) break;
            if (total >= 0 && begin + got >= total) break;
        }
        return result;
    }

    private static MusicInfo MapSong(JsonElement s) => new()
    {
        Id = GetString(s, "mid"),
        Songmid = GetString(s, "mid"),
        Title = GetString(s, "name"),
        Artist = GetSingers(s),
        Album = GetAlbumName(s),
        Duration = FormatDuration(GetLong(s, "interval")),
        Source = "qq",
        Quality = 320
    };

    private static string GetSingers(JsonElement s)
    {
        if (s.TryGetProperty("singer", out var sg) && sg.ValueKind == JsonValueKind.Array)
        {
            var names = sg.EnumerateArray()
                .Select(x => x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                .Where(n => !string.IsNullOrEmpty(n));
            return string.Join("、", names);
        }
        return "未知歌手";
    }

    private static string GetAlbumName(JsonElement s)
    {
        if (s.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object &&
            al.TryGetProperty("name", out var n))
            return n.GetString() ?? "未知专辑";
        return "未知专辑";
    }

    private static string FormatDuration(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    // ============ 通用辅助 ============

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

    private static List<JsonElement> FindDissArray(JsonElement data)
    {
        foreach (var key in new[] { "v_playlist", "v_list", "vecDiss", "disslist", "list" })
        {
            if (data.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var items = arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList();
                if (items.Count > 0) return items;
            }
        }
        if (data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList();
        return new List<JsonElement>();
    }

    private static long GetDissId(JsonElement item)
    {
        foreach (var key in new[] { "tid", "dissid", "id", "dirid" })
            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetInt64();
        return 0;
    }

    private static string GetDissName(JsonElement item)
    {
        foreach (var key in new[] { "title", "dissname", "name", "dirName" })
            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static int GetDissCount(JsonElement item)
    {
        foreach (var key in new[] { "songcnt", "songnum", "songNum", "song_num", "songcount" })
            if (item.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n)) return n;
            }
        return 0;
    }

    private static string GetDissCover(JsonElement item)
    {
        foreach (var key in new[] { "cover_url_medium", "cover_url_big", "cover_mid", "pic_mid", "picUrl", "logo", "bigpicUrl" })
        {
            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()))
            {
                var val = v.GetString()!;
                if (val.StartsWith("http")) return val;
                if (key.EndsWith("_mid"))
                    return $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{val}.jpg";
                return val;
            }
        }
        return string.Empty;
    }

    private AccountInfo BuildAccountInfo()
    {
        var cookies = _http.GetCookies(QqUrl);
        return new AccountInfo
        {
            Platform = "qq",
            Uid = _uin,
            DisplayName = string.IsNullOrEmpty(_nickname) ? $"QQ{_uin}" : _nickname,
            LoginTime = DateTime.Now,
            ExpireTime = DateTime.Now.AddDays(30),
            AuthData = JsonSerializer.Serialize(new { cookies })
        };
    }

    private static string ExtractUin(string cookieHeader)
    {
        string? fallback = null;
        foreach (var part in cookieHeader.SplitTrimmed(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim();
            var val = part[(eq + 1)..].Trim();
            if (!key.Equals("uin", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = val.StartsWith("o", StringComparison.OrdinalIgnoreCase) && val.Length > 1 && val[1..].All(char.IsDigit)
                ? val[1..]
                : val;
            fallback ??= candidate;
            if (candidate.All(char.IsDigit)) return candidate;
        }
        return fallback ?? string.Empty;
    }

    private static string ExtractEuin(string cookieHeader)
    {
        foreach (var part in cookieHeader.SplitTrimmed(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part[..eq].Trim().Equals("euin", StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }
        return string.Empty;
    }

    private static bool HasNonEmptyValue(string cookieHeader, string name)
    {
        foreach (var part in cookieHeader.SplitTrimmed(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim().Length > 0;
        }
        return false;
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
