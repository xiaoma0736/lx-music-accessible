using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 外部音源脚本加载器 — 解析 LX Music 源脚本（huibq/flower/聆澜等）
/// 脚本格式：define API_URL + API_KEY + handleGetMusicUrl(action)
/// </summary>
public class ScriptMusicSource
{
    private readonly ILogger<ScriptMusicSource> _logger;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, LxScriptInfo> _scripts = new();
    private readonly JsonSerializerOptions _jsonOptions;

    public ScriptMusicSource(ILogger<ScriptMusicSource> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("lx-music-request/1.0");
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    // ================================================================
    //  内置预设源
    // ================================================================

                /// <summary>
    /// 免费音源订阅清单（lx-ikun-music-sources 仓库 v260724/online 的稳定在线源，专为在线自动更新设计）。
    /// 每次启动由 MusicSourceService 下载并“创建或更新”（保留用户启停/默认/优先级设置），
    /// 下载失败时回退到已持久化的旧内容（离线可用）。多为混淆脚本，由 LxScriptEngine（Jint）执行。
    /// 注意：仓库路径更新后订阅可能失效，用户可改用“从网络导入/从本地导入”自行添加。
    /// </summary>
    public static readonly List<ScriptSourcePreset> SubscriptionSources = new()
    {
        new ScriptSourcePreset
        {
            Id = "hyw", Name = "HYW音乐(公益免费)",
            Url = "https://raw.githubusercontent.com/Macrohard0001/lx-ikun-music-sources/main/V260817/%E6%8E%A8%E8%8D%90/HYWmusic_beta_%E5%85%AC%E7%9B%8A%E6%B5%8B%E8%AF%95.js",
            Description = "HYW公益测试源（多平台，实测可用；服务可能波动，播放时自动校验可播性）"
        },
        new ScriptSourcePreset
        {
            Id = "jheapi", Name = "聚合API接口(免费)",
            Url = "https://api.music.lerd.dpdns.org/script.js",
            Description = "聚合API接口(CF)，多平台，实测可用"
        },
        new ScriptSourcePreset
        {
            Id = "quandouyao", Name = "全豆要(免费)",
            Url = "https://raw.githubusercontent.com/Macrohard0001/lx-ikun-music-sources/main/V260817/%E5%85%B6%E4%BB%96/%E5%85%A8%E8%B1%86%E8%A6%81-%E8%81%9A%E5%90%88%E9%9F%B3%E6%BA%90-V4.1%EF%BC%88%E9%85%B7%E7%8B%97%E6%8C%82%E4%BA%86%EF%BC%89.js",
            Description = "全豆要聚合音源，网易/QQ 等实测可用"
        },
        new ScriptSourcePreset
        {
            Id = "nianxin", Name = "念心音源(免费)",
            Url = "https://raw.githubusercontent.com/Macrohard0001/lx-ikun-music-sources/main/V260817/%E5%85%B6%E4%BB%96/%E5%BF%B5%E5%BF%83%E9%9F%B3%E6%BA%90%20v1.0.2.js",
            Description = "念心免费音源，QQ 等实测可用"
        },
    };

    // ================================================================
    //  加载脚本
    // ================================================================

    /// <summary>注册内置脚本源（无需下载 JS，直接提供解析参数，如 huibq 免费源）</summary>
    public void RegisterBuiltin(LxScriptInfo info)
    {
        if (info == null || string.IsNullOrEmpty(info.Id)) return;
        _scripts[info.Id] = info;
        _logger.LogInformation("内置脚本源注册成功: {Name} — API: {ApiUrl}, 支持源: [{Sources}]",
            info.Name, info.ApiUrl, string.Join(",", info.SupportedSources));
    }

    /// <summary>从 URL 加载 LX Music 脚本源</summary>
    public async Task<LxScriptInfo?> LoadFromUrlAsync(string url, string sourceId)
    {
        try
        {
            _logger.LogInformation("下载音源脚本: {Url}", url);
            var scriptContent = await _httpClient.GetStringAsync(url);
            return ParseAndRegister(scriptContent, sourceId, url, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载脚本失败: {Url}", url);
            return null;
        }
    }

    /// <summary>从本地文件加载 LX Music 脚本源</summary>
    public async Task<LxScriptInfo?> LoadFromFileAsync(string filePath, string sourceId)
    {
        try
        {
            _logger.LogInformation("加载本地脚本: {Path}", filePath);
            var scriptContent = await File.ReadAllTextAsync(filePath);
            return ParseAndRegister(scriptContent, sourceId, filePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载本地脚本失败: {Path}", filePath);
            return null;
        }
    }

    /// <summary>从已存在的内容加载脚本源（用于从持久化数据恢复，不重新下载）</summary>
    public LxScriptInfo? LoadFromContentAsync(string content, string sourceId, string source, bool isLocal)
    {
        try
        {
            _logger.LogInformation("从内容加载脚本: {SourceId}", sourceId);
            return ParseAndRegister(content, sourceId, source, isLocal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从内容加载脚本失败: {SourceId}", sourceId);
            return null;
        }
    }

    private LxScriptInfo? ParseAndRegister(string script, string sourceId, string source, bool isLocal)
    {
        var info = ParseLxScript(script, sourceId, source, isLocal);
        if (info != null)
        {
            _scripts[sourceId] = info;
            _logger.LogInformation("音源脚本解析成功: {Name} v{Version} — API: {ApiUrl}, 支持源: [{Sources}]",
                info.Name, info.Version, info.ApiUrl, string.Join(",", info.SupportedSources));
        }
        return info;
    }

    // ================================================================
    //  脚本解析
    // ================================================================

    private LxScriptInfo? ParseLxScript(string script, string sourceId, string source, bool isLocal)
    {
        var info = new LxScriptInfo
        {
            Id = sourceId,
            SourceUrl = source,
            IsLocal = isLocal,
            LoadedAt = DateTime.Now,
            RawScript = script
        };

        // 1. 提取元信息
        var nameMatch = Regex.Match(script, @"@name\s+(.+?)(?:\n|\*\/)", RegexOptions.Singleline);
        info.Name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : sourceId;

        var verMatch = Regex.Match(script, @"@version\s+v?([\d.]+)");
        info.Version = verMatch.Success ? verMatch.Groups[1].Value : "?";

        var descMatch = Regex.Match(script, @"@description\s+(.+?)(?:\n|\*\/)", RegexOptions.Singleline);
        info.Description = descMatch.Success ? descMatch.Groups[1].Value.Trim() : "";

        // 2. 提取 API_URL（宽松：混淆脚本常无 API_URL，交由 LxScriptEngine 执行，正则路径自动跳过）
        var apiUrlMatch = Regex.Match(script, @"(?:const|let|var)\s+API_URL\s*=\s*(?:[`""'])([^`""']+)(?:[`""'])");
        info.ApiUrl = apiUrlMatch.Success ? apiUrlMatch.Groups[1].Value : "";

        // 3. 提取 API_KEY
        var apiKeyMatch = Regex.Match(script, @"const\s+API_KEY\s*=\s*[""']([^""']*)[""']");
        info.ApiKey = apiKeyMatch.Success ? apiKeyMatch.Groups[1].Value : "";

        // 4. 提取 MUSIC_QUALITY → 支持的音乐源列表（兼容字面量对象与 JSON.parse('{...}') 两种写法）
        var qualityBlock = ExtractMusicQualityBlock(script);
        if (qualityBlock != null)
        {
            // 带引号 & 不带引号的键名兼容
            var keys = Regex.Matches(qualityBlock, @"""?(\w+)""?\s*:");
            info.SupportedSources = keys.Select(m => MapSourceId(m.Groups[1].Value)).Where(s => s != null && s != "github" && s != "git").Select(s => s!).Distinct().ToList();
        }

        // 5. 提取 URL 模式 —— 找到 handleGetMusicUrl 中的请求URL模式
        // 模式A: `${API_URL}/url/${source}/${songId}/${quality}`  (huibq)
        // 模式B: `${API_URL}/url?source=${source}&songId=${songId}&quality=${quality}` (聆澜)
        // 模式C: `${API_URL}?source=${source}&id=${songId}&quality=${quality}`
        if (Regex.IsMatch(script, @"\$\{API_URL\}/url/\$\{source\}/\$\{songId\}"))
            info.UrlPattern = LxUrlPattern.PathSegments;
        else if (Regex.IsMatch(script, @"songId=\$\{songId\}") || Regex.IsMatch(script, @"songId=\$\{musicInfo"))
            info.UrlPattern = LxUrlPattern.QueryString;
        else if (Regex.IsMatch(script, @"id=\$\{songId\}"))
            info.UrlPattern = LxUrlPattern.QueryStringId;
        else
            info.UrlPattern = LxUrlPattern.None;  // 无法识别模式，交由 Jint 执行

        // 6. 提取质量映射（限定在 MUSIC_QUALITY 块内，避免误匹配其他对象）
        if (qualityBlock != null)
        {
            var sourceMapRegex = new Regex(@"""?(\w+)""?\s*:\s*\[([^\]]+)\]");
            foreach (Match m in sourceMapRegex.Matches(qualityBlock))
            {
                var src = MapSourceId(m.Groups[1].Value);
                if (src != null)
                {
                    var qualities = m.Groups[2].Value.Split(',')
                        .Select(q => q.Trim().Trim('"', '\''))
                        .Where(q => q.Length > 0)
                        .ToList();
                    info.QualityMap[src] = qualities;
                }
            }
        }
        return info;
    }

    /// <summary>从指定起始位置（左大括号索引）提取平衡的 {...} 块</summary>
    private static string ExtractBalancedBraces(string script, int openBraceIndex)
    {
        if (openBraceIndex < 0 || openBraceIndex >= script.Length || script[openBraceIndex] != '{') return "";
        int depth = 0;
        for (int i = openBraceIndex; i < script.Length; i++)
        {
            if (script[i] == '{') depth++;
            else if (script[i] == '}') { depth--; if (depth == 0) return script.Substring(openBraceIndex, i - openBraceIndex + 1); }
        }
        return "";
    }

    /// <summary>提取 MUSIC_QUALITY 的值块：兼容字面量对象 `MUSIC_QUALITY = { ... }` 与 `MUSIC_QUALITY = JSON.parse('{...}')`；找不到返回 null</summary>
    private static string? ExtractMusicQualityBlock(string script)
    {
        // 形式1：MUSIC_QUALITY = { ... }
        var literalMatch = Regex.Match(script, @"MUSIC_QUALITY\s*=\s*\{", RegexOptions.Singleline);
        if (literalMatch.Success)
            return ExtractBalancedBraces(script, literalMatch.Index + literalMatch.Length - 1);

        // 形式2：MUSIC_QUALITY = JSON.parse('{...}') / JSON.parse("{...}")（IKUN 等付费脚本）
        var jsonMatch = Regex.Match(script, @"MUSIC_QUALITY\s*=\s*JSON\.parse\(\s*['""]\s*\{", RegexOptions.Singleline);
        if (jsonMatch.Success)
            return ExtractBalancedBraces(script, jsonMatch.Index + jsonMatch.Length - 1);

        return null;
    }


    // ================================================================
    //  获取播放 URL
    // ================================================================

    /// <summary>通过脚本源获取播放 URL</summary>
    public async Task<string?> GetPlayUrlAsync(string scriptId, string songId, string source, string quality = "128k")
    {
        if (!_scripts.TryGetValue(scriptId, out var script))
            return null;

        // 无法明确识别 URL 模式的脚本（如 POST 型接口/混淆源），不走正则快路径，交由 Jint 执行真实脚本
        if (script.UrlPattern == LxUrlPattern.None) return null;

        var lxSource = ReverseMapSource(source);  // qq→tx, netease→wy, kg→kg, kw→kw, migu→mg

        try
        {
            var url = BuildScriptUrl(script, lxSource, songId, quality);
            _logger.LogInformation("脚本源 [{Name}] 请求播放URL: {Url}", script.Name, url);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "lx-music-request/1.0");
            if (!string.IsNullOrEmpty(script.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("X-Request-Key", script.ApiKey);
                request.Headers.TryAddWithoutValidation("X-API-Key", script.ApiKey);
            }

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();

            return ExtractUrlFromResponse(body, script.Name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "脚本源 [{Name}] 请求失败", script.Name);
            return null;
        }
    }

    /// <summary>获取所有脚本源</summary>
    public IReadOnlyList<LxScriptInfo> GetAllScripts() => _scripts.Values.ToList().AsReadOnly();

    /// <summary>获取指定脚本源</summary>
    public LxScriptInfo? GetScript(string id) => _scripts.TryGetValue(id, out var s) ? s : null;

    /// <summary>移除</summary>
    public void Remove(string id) { _scripts.Remove(id); }
    public void Clear() { _scripts.Clear(); }

    // ================================================================
    //  辅助
    // ================================================================

    private static string BuildScriptUrl(LxScriptInfo script, string source, string songId, string quality)
    {
        var api = script.ApiUrl.TrimEnd('/');

        return script.UrlPattern switch
        {
            LxUrlPattern.PathSegments =>
                $"{api}/url/{source}/{songId}/{quality}",
            LxUrlPattern.QueryStringId =>
                $"{api}/url?source={source}&id={songId}&quality={quality}",
            _ => // QueryString (default)
                $"{api}/url?source={source}&songId={songId}&quality={quality}"
        };
    }

    private static string? ExtractUrlFromResponse(string body, string sourceName)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);

            // code 可能为数字或字符串；0/200 均视为成功
            if (json.TryGetProperty("code", out var code))
            {
                bool codeOk = code.ValueKind switch
                {
                    JsonValueKind.Number => code.GetInt32() is 0 or 200,
                    JsonValueKind.String => code.GetString() is "0" or "200",
                    _ => false
                };
                if (codeOk && json.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    return u.GetString();
            }

            // 无 code 或 code 非预期时仍尝试 url / data.url / body.url
            if (json.TryGetProperty("url", out var url2) && url2.ValueKind == JsonValueKind.String)
                return url2.GetString();

            if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("url", out var url3) && url3.ValueKind == JsonValueKind.String)
                return url3.GetString();

            if (json.TryGetProperty("body", out var body2) && body2.ValueKind == JsonValueKind.Object &&
                body2.TryGetProperty("url", out var url4) && url4.ValueKind == JsonValueKind.String)
                return url4.GetString();
        }
        catch { }

        // 最后尝试正则提取 URL
        var m = Regex.Match(body, @"https?://[^\s""'<>]+\.(?:mp3|m4a|flac|wav|ogg|aac)");
        return m.Success ? m.Value : null;
    }

    /// <summary>LX 源ID → 我们的源ID: tx→qq, wy→netease, mg→migu, kg→kg, kw→kw</summary>
    private static string? MapSourceId(string lxId) => lxId.ToLower() switch
    {
        "tx" => "qq", "wy" => "netease", "mg" => "migu",
        "kg" => "kg", "kw" => "kw",
        "git" => "github",
        _ => lxId
    };

    /// <summary>我们的源ID → LX 源ID</summary>
    private static string ReverseMapSource(string ourId) => ourId.ToLower() switch
    {
        "qq" => "tx", "netease" => "wy", "migu" => "mg",
        "kg" => "kg", "kw" => "kw",
        _ => ourId
    };
}

// ================================================================
//  数据模型
// ================================================================

public class ScriptSourcePreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
}

public class LxScriptInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string SourceUrl { get; set; } = "";
    public bool IsLocal { get; set; }
    public string ApiUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public List<string> SupportedSources { get; set; } = new();
    public Dictionary<string, List<string>> QualityMap { get; set; } = new();
    public LxUrlPattern UrlPattern { get; set; } = LxUrlPattern.None;
    public string? RawScript { get; set; }
    public DateTime LoadedAt { get; set; }
}

public enum LxUrlPattern
{
    None,            // 未识别出 URL 模式（POST 型/混淆脚本），走 Jint 执行
    PathSegments,    // /url/{source}/{songId}/{quality}
    QueryString,     // /url?source={source}&songId={songId}&quality={quality}
    QueryStringId    // /url?source={source}&id={songId}&quality={quality}
}



