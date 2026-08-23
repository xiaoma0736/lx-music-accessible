using System.Text.RegularExpressions;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 外部音源脚本加载器 — 解析 LX Music 源脚本（huibq/flower/聆澜等）
/// 脚本格式：define API_URL + API_KEY + handleGetMusicUrl(action)
/// </summary>
public class ScriptMusicSource : IDisposable
{
    private readonly ILogger<ScriptMusicSource> _logger;
    private readonly ScriptNetworkClient _networkClient;
    private readonly bool _ownsNetworkClient;
    private readonly Dictionary<string, LxScriptInfo> _scripts = new();

    public string? LastFailureCode { get; private set; }

    public ScriptMusicSource(ILogger<ScriptMusicSource> logger)
        : this(logger, new ScriptNetworkClient(), ownsNetworkClient: true)
    {
    }

    public ScriptMusicSource(ILogger<ScriptMusicSource> logger, HttpMessageHandler handler)
        : this(logger, new ScriptNetworkClient(handler), ownsNetworkClient: true)
    {
    }

    public ScriptMusicSource(
        ILogger<ScriptMusicSource> logger,
        ScriptNetworkClient networkClient)
        : this(logger, networkClient, ownsNetworkClient: false)
    {
    }

    private ScriptMusicSource(
        ILogger<ScriptMusicSource> logger,
        ScriptNetworkClient networkClient,
        bool ownsNetworkClient)
    {
        _logger = logger;
        _networkClient = networkClient;
        _ownsNetworkClient = ownsNetworkClient;
    }

    // ================================================================
    //  内置预设源
    // ================================================================

    // ================================================================
    //  加载脚本
    // ================================================================

    /// <summary>下载并解析到暂存对象，不注册也不执行。</summary>
    public async Task<LxScriptInfo?> StageFromUrlAsync(
        string url,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        LastFailureCode = null;
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new ScriptNetworkException("url_invalid");
            _logger.LogInformation("暂存网络音源脚本: {Host}", uri.IdnHost);
            var scriptContent = await _networkClient.GetScriptAsync(uri, cancellationToken);
            return ParseLxScript(scriptContent, sourceId, url, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ScriptNetworkException ex)
        {
            LastFailureCode = ex.Code;
            _logger.LogWarning("网络脚本暂存被拒绝: {Code}", ex.Code);
            return null;
        }
        catch (Exception ex)
        {
            LastFailureCode = "script_stage_failed";
            _logger.LogError(ex, "暂存网络脚本失败");
            return null;
        }
    }

    /// <summary>读取并解析到暂存对象，不注册也不执行。</summary>
    public async Task<LxScriptInfo?> StageFromFileAsync(
        string filePath,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        LastFailureCode = null;
        try
        {
            _logger.LogInformation("暂存本地音源脚本: {Path}", filePath);
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            var bytes = await ScriptNetworkClient.ReadBoundedAsync(
                stream,
                ScriptNetworkClient.MaxScriptBytes,
                "script_limit_exceeded",
                cancellationToken);
            var scriptContent = new UTF8Encoding(false, true).GetString(bytes);
            return ParseLxScript(scriptContent, sourceId, filePath, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (ScriptNetworkException ex)
        {
            LastFailureCode = ex.Code;
            _logger.LogWarning("本地脚本暂存被拒绝: {Code}", ex.Code);
            return null;
        }
        catch (Exception ex)
        {
            LastFailureCode = ex is DecoderFallbackException
                ? "script_encoding_invalid"
                : "script_stage_failed";
            _logger.LogError(ex, "暂存本地脚本失败: {Path}", filePath);
            return null;
        }
    }

    /// <summary>解析已取得的内容到暂存对象，不注册也不执行。</summary>
    public LxScriptInfo? StageFromContent(string content, string sourceId, string source, bool isLocal)
    {
        LastFailureCode = null;
        try
        {
            if (Encoding.UTF8.GetByteCount(content) > ScriptNetworkClient.MaxScriptBytes)
                throw new ScriptNetworkException("script_limit_exceeded");
            return ParseLxScript(content, sourceId, source, isLocal);
        }
        catch (ScriptNetworkException ex)
        {
            LastFailureCode = ex.Code;
            _logger.LogWarning("脚本内容暂存被拒绝: {Code}", ex.Code);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "暂存脚本内容失败: {SourceId}", sourceId);
            return null;
        }
    }

    public async Task<LxScriptInfo?> StageBundledAsync(
        string resourceName,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        await using var stream = typeof(ScriptMusicSource).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        var bytes = await ScriptNetworkClient.ReadBoundedAsync(
            stream,
            ScriptNetworkClient.MaxScriptBytes,
            "script_limit_exceeded",
            cancellationToken);
        var content = new UTF8Encoding(false, true).GetString(bytes);
        return StageFromContent(content, sourceId, $"bundle://{resourceName}", true);
    }

    /// <summary>只有内容摘要匹配的暂存对象才能进入运行时注册表。</summary>
    public bool RegisterVerified(LxScriptInfo info, string expectedSha256)
    {
        if (info is null || string.IsNullOrEmpty(info.Id) || string.IsNullOrEmpty(info.RawScript)) return false;
        var actual = ComputeSha256(info.RawScript);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("拒绝注册摘要不匹配的脚本: {SourceId}", info.Id);
            return false;
        }

        _scripts[info.Id] = info;
        _logger.LogInformation("已注册验证脚本: {Name} v{Version} SHA256={Sha256}",
            info.Name, info.Version, actual);
        return true;
    }

    public static string ComputeSha256(string content)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(content.ReplaceLineEndings("\n")))).ToLowerInvariant();

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
    public async Task<string?> GetPlayUrlAsync(
        string scriptId,
        string songId,
        string source,
        string quality = "128k",
        CancellationToken cancellationToken = default)
    {
        if (!_scripts.TryGetValue(scriptId, out var script))
            return null;
        LastFailureCode = null;

        // 无法明确识别 URL 模式的脚本（如 POST 型接口/混淆源），不走正则快路径，交由 Jint 执行真实脚本
        if (script.UrlPattern == LxUrlPattern.None) return null;

        var lxSource = ReverseMapSource(source);  // qq→tx, netease→wy, kg→kg, kw→kw, migu→mg

        try
        {
            var url = BuildScriptUrl(script, lxSource, songId, quality);
            _logger.LogInformation("脚本源 [{Name}] 请求播放 URL", script.Name);

            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["User-Agent"] = "lx-music-request/1.0"
            };
            if (!string.IsNullOrEmpty(script.ApiKey))
            {
                headers["X-Request-Key"] = script.ApiKey;
                headers["X-API-Key"] = script.ApiKey;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var header in headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var response = await _networkClient.SendAsync(
                request,
                ScriptNetworkClient.MaxResponseBytes,
                cancellationToken);
            if (response.StatusCode is < 200 or >= 300) return null;
            var body = Encoding.UTF8.GetString(response.Body);

            return ExtractUrlFromResponse(body, script.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ScriptNetworkException ex)
        {
            LastFailureCode = ex.Code;
            _logger.LogWarning("脚本源 [{Name}] 请求被拒绝: {Code}", script.Name, ex.Code);
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                "Script source request failed ({ScriptId}): {ErrorType}",
                script.Id,
                exception.GetType().Name);
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
    public void Dispose()
    {
        if (_ownsNetworkClient) _networkClient.Dispose();
    }

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

    private string? ExtractUrlFromResponse(string body, string sourceName)
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
        catch (Exception exception)
        {
            _logger.LogDebug(
                "Script source response used text fallback ({SourceName}): {ErrorType}",
                sourceName,
                exception.GetType().Name);
        }

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



