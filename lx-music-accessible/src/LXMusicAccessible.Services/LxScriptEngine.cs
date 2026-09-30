using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jint;
using Microsoft.ClearScript.V8;
using Microsoft.Extensions.Logging;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Services;

/// <summary>
/// LX Music 自定义源脚本执行引擎（ClearScript V8 优先，Jint 兜底）——忠实运行脚本本体。
/// 支持原版 lx-music-desktop 的第三方音源接口类型：musicUrl / lyric / pic，
/// 以及完整 lx 运行时：request（form/formData/timeout/取消）、utils.crypto
/// （真实 AES/RSA/randomBytes）、utils.buffer、utils.zlib、updateAlert 上抛。
/// </summary>
public class LxScriptEngine
{
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(30);
    // Supported obfuscated sources expand lookup tables during initialization; keep this bounded compatibility budget.
    private const int MaxStatements = 1_000_000;
    private const long MaxHeapBytes = MaxHeapMiB * 1024 * 1024;
    private const int MaxHeapMiB = 96;
    private const int MaxNativeBufferBytes = ScriptNetworkClient.MaxResponseBytes;
    private const int MaxRandomBytes = 64 * 1024;
    private const int MaxUrlCharacters = 8 * 1024;
    private const int MaxHeaderCharacters = 64 * 1024;
    private const int MaxOptionsCharacters = 4 * 1024;
    private const int MaxKeyCharacters = 64 * 1024;
    private const int MaxInitedManifestCharacters = 64 * 1024;
    private static readonly HashSet<string> SafeFailureCodes =
    [
        "content_encoding_invalid",
        "content_encoding_unsupported",
        "compression_input_limit_exceeded",
        "compression_output_limit_exceeded",
        "compression_operation_failed",
        "compression_operation_unsupported",
        "crypto_limit_exceeded",
        "crypto_operation_failed",
        "crypto_operation_unsupported",
        "decompression_limit_exceeded",
        "dns_resolution_failed",
        "http_method_blocked",
        "http_status_rejected",
        "network_request_failed",
        "network_target_blocked",
        "redirect_limit_exceeded",
        "redirect_location_invalid",
        "request_cancelled",
        "request_limit_exceeded",
        "request_timeout",
        "response_limit_exceeded",
        "script_encoding_invalid",
        "url_invalid",
        "action_not_declared",
        "script_result_invalid"
    ];
    private readonly ILogger<LxScriptEngine> _logger;
    private readonly ScriptNetworkClient _networkClient;

    /// <summary>最近一次脚本执行错误（诊断用）</summary>
    public string? LastError => LastFailure?.Code;
    public ScriptExecutionFailure? LastFailure { get; private set; }

    /// <summary>脚本调用 lx.send(EVENT_NAMES.updateAlert, {log, updateUrl}) 时触发（供界面提示）</summary>
    public event EventHandler<LxScriptUpdateAlertEventArgs>? UpdateAlert;

    public LxScriptEngine(ILogger<LxScriptEngine> logger)
        : this(logger, new ScriptNetworkClient())
    {
    }

    public LxScriptEngine(
        ILogger<LxScriptEngine> logger,
        ScriptNetworkClient networkClient)
    {
        _logger = logger;
        _networkClient = networkClient;
    }

    // ================================================================
    //  公开动作入口：musicUrl / lyric / pic
    // ================================================================

    /// <summary>通过 V8（不可用时回退 Jint）执行脚本解析播放 URL（后台线程运行，失败返回 null）</summary>
    public async Task<string?> GetPlayUrlAsync(
        LxScriptInfo script,
        MusicInfo music,
        string source,
        string quality,
        CancellationToken cancellationToken = default)
        => await ExecuteActionAsync(script, "musicUrl", music, source, quality,
            v => v.ValueKind == JsonValueKind.String ? v.GetString() : null,
            cancellationToken);

    /// <summary>通过 V8（不可用时回退 Jint）执行脚本解析封面 URL（失败返回 null）</summary>
    public async Task<string?> GetPicAsync(
        LxScriptInfo script,
        MusicInfo music,
        string source,
        CancellationToken cancellationToken = default)
        => await ExecuteActionAsync(script, "pic", music, source, null,
            v => v.ValueKind == JsonValueKind.String ? v.GetString() : null,
            cancellationToken);

    /// <summary>通过 V8（不可用时回退 Jint）执行脚本解析歌词（失败返回 null）</summary>
    public async Task<LxLyricRaw?> GetLyricAsync(
        LxScriptInfo script,
        MusicInfo music,
        string source,
        CancellationToken cancellationToken = default)
        => await ExecuteActionAsync(
            script,
            "lyric",
            music,
            source,
            null,
            ParseLyricResult,
            cancellationToken);

    /// <summary>加载校验：脚本可执行且已注册音乐请求处理器</summary>
    public async Task<bool> TestLoadAsync(LxScriptInfo script, CancellationToken cancellationToken = default)
    {
        LastFailure = null;
        if (!IsScriptAllowed(script)) return false;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        execution.CancelAfter(ExecutionTimeout);
        try
        {
            return await Task.Run(() =>
            {
                execution.Token.ThrowIfCancellationRequested();
                try
                {
                    ExecuteV8Core(script, action: null, music: null!, source: "", quality: null, execution.Token);
                    return true;
                }
                catch (ScriptEngineUnavailableException ex)
                {
                    _logger.LogDebug(ex, "V8 unavailable; loading with Jint");
                    return ExecuteJintLoad(script, execution.Token);
                }
            }, execution.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            SetFailure(execution.IsCancellationRequested
                ? "script_timeout"
                : ClassifyExecutionFailure(ex));
            _logger.LogDebug("脚本 [{Name}] 加载校验失败: {Code}", script.Name, LastError);
            return false;
        }
    }

    /// <summary>执行一次动作分派：V8 优先，仅 V8 运行时不可用时回退 Jint。</summary>
    private async Task<T?> ExecuteActionAsync<T>(
        LxScriptInfo script, string action, MusicInfo music, string source, string? quality,
        Func<JsonElement, T?> parse,
        CancellationToken cancellationToken)
    {
        LastFailure = null;
        if (!IsScriptAllowed(script)) return default;
        if (!IsActionDeclared(script, action, source))
        {
            SetFailure("action_not_declared");
            return default;
        }
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        execution.CancelAfter(ExecutionTimeout);
        try
        {
            return await Task.Run(() =>
            {
                string? resultJson;
                try
                {
                    resultJson = ExecuteV8Core(script, action, music, source, quality, execution.Token);
                }
                catch (ScriptEngineUnavailableException ex)
                {
                    _logger.LogDebug(ex, "V8 unavailable; action {Action} with Jint", action);
                    resultJson = ExecuteJintAction(script, action, music, source, quality, execution.Token);
                }

                return ParseActionResult(action, resultJson, parse);
            }, execution.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (execution.IsCancellationRequested)
            {
                SetFailure("script_timeout");
            }
            else if (LastFailure is null)
            {
                SetFailure(ClassifyExecutionFailure(ex));
            }
            _logger.LogDebug("脚本 [{Name}] 执行失败 ({Action}): {Code}",
                script.Name, action, LastError);
            return default;
        }
    }

    private T? ParseActionResult<T>(string action, string? resultJson, Func<JsonElement, T?> parse)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return default;
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
        {
            var valueJson = value.GetString();
            if (string.IsNullOrWhiteSpace(valueJson) || valueJson == "null") return default;
            using var valueDoc = JsonDocument.Parse(valueJson);
            var parsed = parse(valueDoc.RootElement);
            if (!IsValidActionResult(action, valueDoc.RootElement, parsed))
            {
                SetFailure("script_result_invalid");
                throw new LxScriptActionException("script_result_invalid");
            }
            return parsed;
        }

        if (root.TryGetProperty("err", out var err) && err.ValueKind == JsonValueKind.String)
        {
            SetFailure(SanitizeFailureCode(err.GetString()));
            throw new LxScriptActionException($"{action}: {err.GetString()}");
        }
        return default;
    }

    /// <summary>每次动作使用独立 V8 引擎，避免脚本间全局状态污染。</summary>
    private string? ExecuteV8Core(
        LxScriptInfo script,
        string? action,
        MusicInfo music,
        string source,
        string? quality,
        CancellationToken cancellationToken)
    {
        var rawScript = script.RawScript;
        if (string.IsNullOrEmpty(rawScript))
            throw new LxScriptActionException("脚本内容为空");

        using var engine = CreateV8Engine(cancellationToken);
        using var watchdog = cancellationToken.Register(engine.Interrupt);
        var lxSource = action == null ? string.Empty : ToLxSource(source);
        ConfigureV8Inputs(engine, script, action, music, lxSource, quality);
        engine.Execute(ShimJs);
        engine.Execute(rawScript);

        if (action == null)
        {
            if (!(bool)engine.Evaluate("globalThis.lx.__probe()"))
                throw new LxScriptActionException("脚本未注册 request 处理器");

            ApplyInitedSources(
                script,
                engine.Evaluate("String(globalThis.__lxInitedJson || '')") as string);
            return null;
        }

        // lx.request is asynchronous. Wait for the promise before reading __r.
        var invocation = (Task<object?>)engine.Evaluate(InvokeSnippet);
        invocation.GetAwaiter().GetResult();
        return (string)engine.Evaluate("JSON.stringify(globalThis.__r)");
    }

    private V8ScriptEngine CreateV8Engine(CancellationToken cancellationToken)
    {
        try
        {
            var engine = new V8ScriptEngine(
                new V8RuntimeConstraints
                {
                    MaxOldSpaceSize = MaxHeapMiB,
                    MaxNewSpaceSize = 16,
                },
                V8ScriptEngineFlags.EnableDateTimeConversion
                | V8ScriptEngineFlags.EnableTaskPromiseConversion
                | V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
            engine.EnableRuntimeInterruptPropagation = true;
            engine.MaxRuntimeHeapSize = (nuint)(MaxHeapBytes - (16L * 1024 * 1024));
            engine.RuntimeHeapSizeViolationPolicy = V8RuntimeViolationPolicy.Interrupt;
            engine.Script.__lxNativeRequest = new Func<string, string, string, string, string, string>(
                (url, method, headers, body, options) => NativeRequest(url, method, headers, body, options, cancellationToken));
            engine.Script.__lxNativeCrypto = new Func<string, string, string>(NativeCrypto);
            engine.Script.__lxNativeZlib = new Func<string, string, string>(NativeZlib);
            engine.Script.__lxNativeUpdateAlert = new Func<string, string>(NativeUpdateAlert);
            return engine;
        }
        catch (Exception ex)
        {
            throw new ScriptEngineUnavailableException("ClearScript V8 runtime is unavailable.", ex);
        }
    }

    private static void ConfigureV8Inputs(
        V8ScriptEngine engine, LxScriptInfo script, string? action, MusicInfo music, string lxSource, string? quality)
    {
        engine.Script.__scriptInfo = BuildScriptInfoJson(script);
        engine.Script.__mi = action == null ? "{}" : BuildMusicInfoJson(music, lxSource);
        engine.Script.__src = lxSource;
        engine.Script.__act = action ?? "";
        engine.Script.__q = quality ?? "";
    }

    private bool ExecuteJintLoad(LxScriptInfo script, CancellationToken cancellationToken)
    {
        var rawScript = script.RawScript;
        if (string.IsNullOrEmpty(rawScript))
            throw new LxScriptActionException("脚本内容为空");

        using var engine = CreateEngine(cancellationToken);
        engine.Execute(ShimJs);
        engine.Execute(rawScript);

        if (!engine.Evaluate("globalThis.lx.__probe()").AsBoolean())
            throw new LxScriptActionException("脚本未注册 request 处理器");

        ApplyInitedSources(
            script,
            engine.Evaluate("String(globalThis.__lxInitedJson || '')").AsString());
        return true;
    }
    private string ExecuteJintAction(LxScriptInfo script, string action, MusicInfo music, string source, string? quality, CancellationToken cancellationToken)
    {
        var lxSource = ToLxSource(source);
        using var engine = CreateEngine(cancellationToken);
        engine.SetValue("__scriptInfo", BuildScriptInfoJson(script));
        engine.SetValue("__mi", BuildMusicInfoJson(music, lxSource));
        engine.SetValue("__src", lxSource);
        engine.SetValue("__act", action);
        engine.SetValue("__q", quality ?? "");
        var rawScript = script.RawScript;
        if (string.IsNullOrEmpty(rawScript)) throw new LxScriptActionException("脚本内容为空");
        engine.Execute(ShimJs);
        engine.Execute(rawScript);
        engine.Execute(InvokeSnippet);
        return engine.Evaluate("JSON.stringify(globalThis.__r)").AsString();
    }


    private sealed class ScriptEngineUnavailableException : Exception
    {
        public ScriptEngineUnavailableException(string message, Exception inner) : base(message, inner) { }
    }

    private sealed class LxScriptActionException : Exception
    {
        public LxScriptActionException(string message) : base(message) { }
    }

    private static void ApplyInitedSources(LxScriptInfo script, string? initedJson)
    {
        if (string.IsNullOrWhiteSpace(initedJson)) return;
        if (initedJson.Length > MaxInitedManifestCharacters) return;
        try
        {
            using var document = JsonDocument.Parse(initedJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("sources", out var sources)
                || sources.ValueKind != JsonValueKind.Object)
                return;

            foreach (var declaredSource in sources.EnumerateObject())
            {
                var platform = MapSourceId(declaredSource.Name);
                if (platform is null
                    || declaredSource.Value.ValueKind != JsonValueKind.Object
                    || !declaredSource.Value.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !string.Equals(type.GetString(), "music", StringComparison.Ordinal)
                    || !declaredSource.Value.TryGetProperty("actions", out var actions)
                    || actions.ValueKind != JsonValueKind.Array
                    || !declaredSource.Value.TryGetProperty("qualitys", out var qualities)
                    || qualities.ValueKind != JsonValueKind.Array)
                    continue;

                var validActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var actionValue in actions.EnumerateArray())
                {
                    if (actionValue.ValueKind != JsonValueKind.String) continue;
                    var action = actionValue.GetString()?.Trim();
                    if (action is "musicUrl" or "lyric" or "pic") validActions.Add(action!);
                }
                if (validActions.Count == 0) continue;

                var seenQualities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var parsedQualities = new List<string>();
                foreach (var qualityValue in qualities.EnumerateArray())
                {
                    if (qualityValue.ValueKind != JsonValueKind.String) continue;
                    var quality = qualityValue.GetString()?.Trim();
                    if (string.IsNullOrEmpty(quality) || !seenQualities.Add(quality)) continue;
                    parsedQualities.Add(quality);
                }
                if (parsedQualities.Count == 0) continue;

                if (!script.SupportedSources.Contains(platform, StringComparer.OrdinalIgnoreCase))
                    script.SupportedSources.Add(platform);
                if (!script.ActionMap.TryGetValue(platform, out var existingActions))
                    script.ActionMap[platform] = existingActions = new(StringComparer.OrdinalIgnoreCase);
                existingActions.UnionWith(validActions);
                if (!script.QualityMap.TryGetValue(platform, out var existingQualities))
                    script.QualityMap[platform] = existingQualities = [];
                foreach (var quality in parsedQualities)
                {
                    if (!existingQualities.Contains(quality, StringComparer.OrdinalIgnoreCase))
                        existingQualities.Add(quality);
                }
            }
        }
        catch (JsonException)
        {
            // A malformed runtime declaration must not make the script eligible for playback.
        }
    }

    private static string? MapSourceId(string source) => source.ToLowerInvariant() switch
    {
        "tx" => "qq",
        "wy" => "netease",
        "mg" => "migu",
        "kg" => "kg",
        "kw" => "kw",
        "git" or "github" => null,
        _ => source.ToLowerInvariant()
    };

    private static bool IsActionDeclared(LxScriptInfo script, string action, string source)
    {
        if (!script.ActionMap.TryGetValue(source.ToLowerInvariant(), out var declared) || declared.Count == 0)
            return true;
        return declared.Contains(action, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsValidActionResult<T>(string action, JsonElement value, T? parsed)
    {
        if (action is "musicUrl" or "pic")
        {
            return value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 and <= 2048 } candidate
                && Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        if (action != "lyric" || parsed is not LxLyricRaw lyric) return false;
        return !string.IsNullOrWhiteSpace(lyric.Lyric)
            && lyric.Lyric.Length <= 51200
            && (lyric.Tlyric?.Length ?? 0) <= 5120
            && (lyric.Rlyric?.Length ?? 0) <= 5120
            && (lyric.Lxlyric?.Length ?? 0) <= 8192;
    }

    private static LxLyricRaw? ParseLyricResult(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) return null;
        var lyric = GetStr(v, "lyric");
        if (string.IsNullOrWhiteSpace(lyric)) return null;
        return new LxLyricRaw
        {
            Lyric = lyric,
            Tlyric = GetStr(v, "tlyric"),
            Rlyric = GetStr(v, "rlyric"),
            Lxlyric = GetStr(v, "lxlyric")
        };
    }

    private static string? GetStr(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>应用源 id → LX 源 id</summary>
    private static string ToLxSource(string source) => source.ToLower() switch
    {
        "qq" => "tx",
        "netease" => "wy",
        "migu" => "mg",
        _ => source
    };

    private static string BuildScriptInfoJson(LxScriptInfo script)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = script.Name,
            ["description"] = script.Description ?? "",
            ["version"] = script.Version ?? "1.0.0",
            ["author"] = script.Author ?? "",
            ["homepage"] = script.Homepage ?? "",
            ["rawScript"] = script.RawScript
        });

    private static string BuildMusicInfoJson(MusicInfo music, string lxSource)
    {
        var mi = new Dictionary<string, object?>
        {
            ["id"] = music.Id,
            ["name"] = music.Title,
            ["singer"] = music.Artist,
            ["album"] = music.Album,
            ["source"] = lxSource
        };
        var hash = music.Hash;
        var songmid = music.Songmid;
        switch (lxSource)
        {
            case "wy":
            case "kw":
            case "mg":
            case "tx":
                if (string.IsNullOrEmpty(songmid) && !string.IsNullOrEmpty(music.Id)) songmid = music.Id;
                break;
            case "kg":
                if (string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(music.Id)) hash = music.Id;
                break;
        }
        if (!string.IsNullOrEmpty(hash)) mi["hash"] = hash;
        if (!string.IsNullOrEmpty(songmid)) mi["songmid"] = songmid;
        if (TimeSpan.TryParseExact(music.Duration, @"mm\:ss", System.Globalization.CultureInfo.InvariantCulture, out var dur))
            mi["interval"] = (int)dur.TotalSeconds;
        return JsonSerializer.Serialize(mi);
    }

    private Engine CreateEngine(CancellationToken cancellationToken)
    {
        var engine = new Engine(o =>
        {
            o.TimeoutInterval(ExecutionTimeout);
            o.MaxStatements(MaxStatements);
            o.LimitMemory(MaxHeapBytes);
            o.CancellationToken(cancellationToken);
        });
        engine.SetValue("__lxNativeRequest", new Func<string, string, string, string, string, string>(
            (url, method, headers, body, options) =>
                NativeRequest(url, method, headers, body, options, cancellationToken)));
        engine.SetValue("__lxNativeCrypto", new Func<string, string, string>(NativeCrypto));
        engine.SetValue("__lxNativeZlib", new Func<string, string, string>(NativeZlib));
        engine.SetValue("__lxNativeUpdateAlert", new Func<string, string>(NativeUpdateAlert));
        return engine;
    }
    // ================================================================
    //  原生桥（同步，脚本线程内阻塞调用）
    // ================================================================

    /// <summary>同步 HTTP：返回 JSON {status, headers, body, bodyB64, err}；支持按请求超时</summary>
    private string NativeRequest(
        string url,
        string method,
        string headersJson,
        string body,
        string optsJson,
        CancellationToken executionToken)
    {
        try
        {
            if (url.Length > MaxUrlCharacters
                || headersJson.Length > MaxHeaderCharacters
                || optsJson.Length > MaxOptionsCharacters
                || Encoding.UTF8.GetByteCount(body) > ScriptNetworkClient.MaxRequestBodyBytes)
            {
                return RequestError("request_limit_exceeded");
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(executionToken);
            int timeoutMs = 0;
            try
            {
                using var odoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(optsJson) ? "{}" : optsJson);
                if (odoc.RootElement.TryGetProperty("timeoutMs", out var t) && t.ValueKind == JsonValueKind.Number)
                    timeoutMs = t.GetInt32();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    "Script HTTP options were ignored: {ErrorType}",
                    exception.GetType().Name);
            }
            if (timeoutMs > 0) cts.CancelAfter(Math.Min(timeoutMs, (int)ExecutionTimeout.TotalMilliseconds));

            var requestMethod = string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant();
            if (requestMethod is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD"))
                return RequestError("http_method_blocked");
            using var req = new HttpRequestMessage(new HttpMethod(requestMethod), url);
            string? contentType = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(headersJson))
                {
                    var hdrs = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                    if (hdrs != null)
                        foreach (var h in hdrs)
                        {
                            if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                            {
                                contentType = h.Value;
                                continue;
                            }
                            if (!req.Headers.TryAddWithoutValidation(h.Key, h.Value))
                            {
                                try
                                {
                                    req.Content ??= new ByteArrayContent(Array.Empty<byte>());
                                    req.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                                }
                                catch (Exception exception)
                                {
                                    _logger.LogDebug(
                                        "Script content header was ignored: {ErrorType}",
                                        exception.GetType().Name);
                                }
                            }
                        }
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    "Script HTTP headers were ignored: {ErrorType}",
                    exception.GetType().Name);
            }
            req.Headers.TryAddWithoutValidation("User-Agent", "lx-music-desktop/2.0.0");

            if (req.Method != HttpMethod.Get && req.Method != HttpMethod.Head && (!string.IsNullOrEmpty(body) || contentType is not null))
            {
                req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body ?? string.Empty));
                if (!string.IsNullOrWhiteSpace(contentType))
                    req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            var resp = _networkClient.SendAsync(
                req,
                ScriptNetworkClient.MaxResponseBytes,
                cts.Token).GetAwaiter().GetResult();
            var raw = resp.Body;
            var text = Encoding.UTF8.GetString(raw);
            return JsonSerializer.Serialize(new
            {
                status = resp.StatusCode,
                headers = resp.Headers,
                body = text,
                bodyB64 = Convert.ToBase64String(raw),
                err = ""
            });
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            return RequestError("request_cancelled");
        }
        catch (OperationCanceledException)
        {
            return RequestError("request_timeout");
        }
        catch (ScriptNetworkException ex)
        {
            return RequestError(ex.Code);
        }
        catch
        {
            return RequestError("network_request_failed");
        }
    }

    /// <summary>同步加密桥：op = aesEncrypt/aesDecrypt/rsaEncrypt/randomBytes，返回 {ok, b64} 或 {ok:false, err}</summary>
    private string NativeCrypto(string op, string argsJson)
    {
        try
        {
            if (argsJson.Length > MaxNativeBufferBytes * 2)
                return Err("crypto_limit_exceeded");
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var r = doc.RootElement;
            switch (op)
            {
                case "aesEncrypt":
                case "aesDecrypt":
                {
                    var data = GetB64(r, "data", MaxNativeBufferBytes);
                    var key = GetB64(r, "key", 64);
                    var iv = GetB64(r, "iv", 32);
                    var mode = GetStr(r, "mode") ?? "aes-128-cbc";
                    return Ok(Convert.ToBase64String(AesTransform(op == "aesEncrypt", data, mode, key, iv)));
                }
                case "rsaEncrypt":
                {
                    var data = GetB64(r, "data", MaxNativeBufferBytes);
                    var keyPem = GetStr(r, "key") ?? "";
                    if (keyPem.Length > MaxKeyCharacters) return Err("crypto_limit_exceeded");
                    return Ok(Convert.ToBase64String(RsaEncryptNoPadding(data, keyPem)));
                }
                case "randomBytes":
                {
                    int size = r.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 16;
                    if (size < 0 || size > MaxRandomBytes) return Err("crypto_limit_exceeded");
                    return Ok(Convert.ToBase64String(FrameworkCompatibility.GetRandomBytes(size)));
                }
                case "digest":
                {
                    var data = GetB64(r, "data", MaxNativeBufferBytes);
                    var algorithm = (GetStr(r, "algorithm") ?? "").ToLowerInvariant();
                    byte[] hash = algorithm switch
                    {
                        "md5" => System.Security.Cryptography.MD5.Create().ComputeHash(data),
                        "sha1" => System.Security.Cryptography.SHA1.Create().ComputeHash(data),
                        "sha256" => System.Security.Cryptography.SHA256.Create().ComputeHash(data),
                        "sha512" => System.Security.Cryptography.SHA512.Create().ComputeHash(data),
                        _ => throw new InvalidOperationException("unsupported digest algorithm")
                    };
                    return Ok(Convert.ToBase64String(hash));
                }

                default:
                    return Err("crypto_operation_unsupported");
            }
        }
        catch (ScriptResourceLimitException ex)
        {
            return Err(ex.Code);
        }
        catch (InvalidOperationException)
        {
            return Err("crypto_operation_unsupported");
        }
        catch
        {
            return Err("crypto_operation_failed");
        }
    }

    /// <summary>同步 zlib 桥：op = inflate/deflate/gzip，返回 {ok, b64} 或 {ok:false, err}</summary>
    private string NativeZlib(string op, string b64)
    {
        try
        {
            if (b64.Length > ((MaxNativeBufferBytes + 2) / 3) * 4)
                return Err("compression_input_limit_exceeded");
            var data = string.IsNullOrWhiteSpace(b64) ? Array.Empty<byte>() : Convert.FromBase64String(b64);
            if (data.Length > MaxNativeBufferBytes) return Err("compression_input_limit_exceeded");
            byte[] result;
            switch (op)
            {
                case "inflate":
                    result = InflateZLib(data);
                    break;
                case "deflate":
                    result = DeflateZLib(data);
                    if (result.Length > MaxNativeBufferBytes)
                        return Err("compression_output_limit_exceeded");
                    break;
                case "gzip":
                    using (var gzIn = new MemoryStream(data))
                    using (var gz = new System.IO.Compression.GZipStream(gzIn, System.IO.Compression.CompressionMode.Decompress))
                    using (var gzOut = new MemoryStream())
                    {
                        CopyToBounded(gz, gzOut, MaxNativeBufferBytes);
                        result = gzOut.ToArray();
                    }
                    break;
                default:
                    return Err("compression_operation_unsupported");
            }
            return Ok(Convert.ToBase64String(result));
        }
        catch (ScriptResourceLimitException ex)
        {
            return Err(ex.Code);
        }
        catch
        {
            return Err("compression_operation_failed");
        }
    }

    private static void CopyToBounded(Stream input, Stream output, int maxBytes)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) return;
            if (output.Length + read > maxBytes)
                throw new ScriptResourceLimitException("compression_output_limit_exceeded");
            output.Write(buffer, 0, read);
        }
    }

    private bool IsScriptAllowed(LxScriptInfo? script)
    {
        if (script is null || string.IsNullOrEmpty(script.RawScript))
        {
            SetFailure("script_missing");
            return false;
        }
        if (Encoding.UTF8.GetByteCount(script.RawScript) <= ScriptNetworkClient.MaxScriptBytes)
            return true;
        SetFailure("script_limit_exceeded");
        return false;
    }

    private void SetFailure(string code)
    {
        LastFailure = new ScriptExecutionFailure(code, code switch
        {
            "script_timeout" => "脚本执行超过时间限制",
            "script_statement_limit_exceeded" => "脚本执行超过语句限制",
            "script_memory_limit_exceeded" => "脚本执行超过内存限制",
            "script_limit_exceeded" => "脚本内容超过大小限制",
            "request_cancelled" => "脚本请求已取消",
            _ => "脚本操作被安全边界拒绝"
        });
    }

    private static string ClassifyExecutionFailure(Exception exception)
    {
        if (exception is OperationCanceledException or TimeoutException
            or Microsoft.ClearScript.ScriptInterruptedException) return "script_timeout";
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var name = current.GetType().Name;
            if (name.Contains("MemoryLimit", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("heap out of memory", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("exceeded its memory limit", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("Runtime heap size violation", StringComparison.OrdinalIgnoreCase))
                return "script_memory_limit_exceeded";
            if (name.Contains("Statements", StringComparison.OrdinalIgnoreCase))
                return "script_statement_limit_exceeded";
            if (name.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
                return "script_timeout";
        }
        return "script_execution_failed";
    }

    private static string SanitizeFailureCode(string? value)
    {
        if (value is null) return "script_execution_failed";
        foreach (var code in SafeFailureCodes)
            if (value.Contains(code, StringComparison.Ordinal)) return code;
        return "script_execution_failed";
    }

    private static string RequestError(string code)
        => JsonSerializer.Serialize(new
        {
            status = 0,
            headers = new Dictionary<string, string>(),
            body = "",
            bodyB64 = "",
            err = code,
            error = new { code }
        });

    /// <summary>updateAlert 上抛桥</summary>
    private string NativeUpdateAlert(string argsJson)
    {
        try
        {
            if (argsJson.Length > MaxHeaderCharacters) return "{}";
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("log", out var l)
                || l.ValueKind != JsonValueKind.String
                || l.GetString() is not { Length: > 0 } rawLog)
                return "{}";

            var log = rawLog.Length > 1024 ? rawLog[..1024] + "..." : rawLog;
            string? updateUrl = null;
            if (root.TryGetProperty("updateUrl", out var u)
                && u.ValueKind == JsonValueKind.String
                && u.GetString() is { Length: <= 1024 } candidate
                && Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                updateUrl = candidate;
            }

            try
            {
                UpdateAlert?.Invoke(
                    this,
                    new LxScriptUpdateAlertEventArgs { Log = log, UpdateUrl = updateUrl });
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Script update event failed: {ErrorType}",
                    exception.GetType().Name);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                "Script update event payload was ignored: {ErrorType}",
                exception.GetType().Name);
        }
        return "{}";
    }

    private static byte[] GetB64(JsonElement r, string name, int maxBytes)
    {
        if (r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(p.GetString()))
        {
            var value = p.GetString()!;
            if (value.Length > ((maxBytes + 2) / 3) * 4)
                throw new ScriptResourceLimitException("crypto_limit_exceeded");
            var buffer = new byte[maxBytes];
            var bytes = DecodeBase64Bounded(value, maxBytes);
            if (bytes.Length > 0)
                return bytes;
        }
        return Array.Empty<byte>();
    }

    private static byte[] InflateZLib(byte[] data)
    {
        if (data.Length < 3 || (data[0] & 0x0f) != 8) throw new InvalidDataException("invalid zlib header");
        using var input = new MemoryStream(data, 2, data.Length - 2);
        using var deflate = new DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output, 81920);
        return output.ToArray();
    }

    private static byte[] DeflateZLib(byte[] data)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x9c);
        using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data, 0, data.Length);
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        var checksum = (b << 16) | a;
        output.Write(BitConverter.GetBytes(checksum), 0, 4);
        return output.ToArray();
    }

    private static byte[] DecodeBase64Bounded(string value, int maxBytes)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length <= maxBytes ? bytes : Array.Empty<byte>();
        }
        catch (FormatException)
        {
            return Array.Empty<byte>();
        }
    }

    private static byte[] Slice(byte[] value, int start, int? end = null)
    {
        var stop = end ?? value.Length;
        var result = new byte[Math.Max(0, stop - start)];
        Buffer.BlockCopy(value, start, result, 0, result.Length);
        return result;
    }

    private static string Ok(string b64) => JsonSerializer.Serialize(new { ok = true, b64 });
    private static string Err(string msg) => JsonSerializer.Serialize(new { ok = false, err = msg });

    /// <summary>AES 变换：支持 aes-128/192/256-cbc/ecb/cfb/ctr；密钥/IV 自动截断或补零</summary>
    private static byte[] AesTransform(bool encrypt, byte[] data, string mode, byte[] key, byte[] iv)
    {
        var m = System.Text.RegularExpressions.Regex.Match(mode, @"^aes[-_](\d{3})[-_](cbc|ecb|cfb|ctr)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) throw new InvalidOperationException("unsupported aes mode: " + mode);
        var keyBits = int.Parse(m.Groups[1].Value);
        var block = m.Groups[2].Value.ToLowerInvariant();
        var keyLen = keyBits / 8;

        var keyBytes = key.Length == 0 ? new byte[keyLen] : key;
        if (keyBytes.Length != keyLen)
            keyBytes = keyBytes.Length > keyLen ? Slice(keyBytes, 0, keyLen) : keyBytes.Concat(new byte[keyLen - keyBytes.Length]).ToArray();

        var ivBytes = iv.Length == 0 ? new byte[16] : iv;
        if (ivBytes.Length != 16)
            ivBytes = ivBytes.Length > 16 ? Slice(ivBytes, 0, 16) : ivBytes.Concat(new byte[16 - ivBytes.Length]).ToArray();

        using var aes = Aes.Create();
        aes.KeySize = keyBits;
        aes.Key = keyBytes;

        switch (block)
        {
            case "ecb":
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;
                return encrypt ? Transform(aes.CreateEncryptor(), data) : Transform(aes.CreateDecryptor(), data);
            case "ctr":
                return CtrTransform(data, aes, ivBytes);
            case "cfb":
                aes.Mode = CipherMode.CFB;
                aes.FeedbackSize = 128;
                aes.Padding = PaddingMode.PKCS7;
                aes.IV = ivBytes;
                return encrypt ? Transform(aes.CreateEncryptor(), data) : Transform(aes.CreateDecryptor(), data);
            default: // cbc
                using (var t = encrypt ? aes.CreateEncryptor(aes.Key, ivBytes) : aes.CreateDecryptor(aes.Key, ivBytes))
                    return Transform(t, data);
        }
    }

    /// <summary>CTR：计数器 = IV（大端递增），密钥流 = ECB 无填充加密计数器</summary>
    private static byte[] CtrTransform(byte[] data, Aes aes, byte[] iv)
    {
        var counter = (byte[])iv.Clone();
        var outBuf = new byte[data.Length];
        for (int off = 0; off < data.Length; off += 16)
        {
            aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; var ks = Transform(aes.CreateEncryptor(), counter);
            int n = Math.Min(16, data.Length - off);
            for (int i = 0; i < n; i++)
                outBuf[off + i] = (byte)(data[off + i] ^ ks[i]);
            for (int i = 15; i >= 0; i--)
            {
                if (++counter[i] != 0) break;
            }
        }
        return outBuf;
    }

    private static byte[] Transform(ICryptoTransform t, byte[] data)
    {
        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, t, CryptoStreamMode.Write))
        {
            cs.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }

    /// <summary>RSA 无填充加密（对齐原版：左补零到 128 字节后 raw RSA），兼容任意公钥尺寸</summary>
    private byte[] RsaEncryptNoPadding(byte[] data, string keyPem)
    {
        var parameters = ParseRsaPublicKey(keyPem);
        if (parameters is null)
        {
            using var xmlRsa = RSA.Create();
            try { xmlRsa.FromXmlString(keyPem); parameters = xmlRsa.ExportParameters(false); }
            catch (Exception ex) when (ex is CryptographicException or System.Xml.XmlException)
            {
                _logger.LogDebug("RSA XML key parsing failed: {ErrorType}", ex.GetType().Name);
            }
        }
        if (parameters is not { } p || p.Modulus is null || p.Exponent is null)
            throw new InvalidOperationException("invalid rsa key");

        const int padLen = 128;
        byte[] msg;
        if (data.Length < padLen)
        {
            msg = new byte[padLen];
            Buffer.BlockCopy(data, 0, msg, padLen - data.Length, data.Length);
        }
        else
        {
            msg = data;
        }
        var mInt = ToPositiveBigInteger(msg);
        var nInt = ToPositiveBigInteger(p.Modulus);
        var eInt = ToPositiveBigInteger(p.Exponent);
        if (mInt >= nInt) throw new InvalidOperationException("message too large for rsa key");
        var enc = System.Numerics.BigInteger.ModPow(mInt, eInt, nInt);
        var littleEndian = enc.ToByteArray();
        if (littleEndian.Length > 0 && littleEndian[littleEndian.Length - 1] == 0)
            littleEndian = Slice(littleEndian, 0, littleEndian.Length - 1);
        var be = littleEndian.Reverse().ToArray();
        var outBuf = new byte[p.Modulus.Length];
        Buffer.BlockCopy(be, 0, outBuf, outBuf.Length - be.Length, Math.Min(be.Length, outBuf.Length));
        return outBuf;
    }

    private RSAParameters? ParseRsaPublicKey(string keyPem)
    {
        try
        {
            var body = string.Concat(keyPem
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => !line.StartsWith("-----", StringComparison.Ordinal)));
            var bytes = Convert.FromBase64String(body);
            if (TryParseSubjectPublicKeyInfo(bytes, out var parameters))
                return parameters;

            using var rsa = RSA.Create();
            try { rsa.FromXmlString(keyPem); return rsa.ExportParameters(false); }
            catch (Exception ex) when (ex is CryptographicException or System.Xml.XmlException)
            {
                _logger.LogDebug("RSA XML key parsing failed: {ErrorType}", ex.GetType().Name);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        {
            _logger.LogDebug("RSA PEM key parsing failed: {ErrorType}", ex.GetType().Name);
        }
        return null;
    }

    private static System.Numerics.BigInteger ToPositiveBigInteger(byte[] bigEndian)
    {
        var littleEndian = bigEndian.Reverse().ToArray();
        Array.Resize(ref littleEndian, littleEndian.Length + 1);
        return new System.Numerics.BigInteger(littleEndian);
    }

    private static bool TryParseSubjectPublicKeyInfo(byte[] der, out RSAParameters parameters)
    {
        parameters = default;
        var offset = 0;
        if (!TryReadDer(der, ref offset, 0x30, out var outer)) return false;
        var position = 0;
        if (!TryReadDer(outer, ref position, 0x30, out _)) return false;
        if (!TryReadDer(outer, ref position, 0x03, out var bitString)
            || bitString.Length < 2
            || bitString[0] != 0) return false;

        var rsa = CopySegment(bitString, 1, bitString.Length);
        var rsaOffset = 0;
        if (!TryReadDer(rsa, ref rsaOffset, 0x30, out var publicKey)) return false;
        var keyOffset = 0;
        if (!TryReadDer(publicKey, ref keyOffset, 0x02, out var modulus)
            || !TryReadDer(publicKey, ref keyOffset, 0x02, out var exponent)) return false;
        parameters.Modulus = TrimLeadingZero(modulus);
        parameters.Exponent = TrimLeadingZero(exponent);
        return parameters.Modulus.Length > 0 && parameters.Exponent.Length > 0;
    }

    private static bool TryReadDer(byte[] data, ref int offset, byte expectedTag, out byte[] content)
    {
        content = Array.Empty<byte>();
        if (offset + 2 > data.Length || data[offset] != expectedTag) return false;
        var length = (int)data[offset + 1];
        var headerLength = 2;
        if ((length & 0x80) != 0)
        {
            var lengthBytes = length & 0x7f;
            if (lengthBytes == 0 || lengthBytes > 4 || offset + 2 + lengthBytes > data.Length) return false;
            length = 0;
            for (var index = 0; index < lengthBytes; index++)
                length = (length << 8) | data[offset + 2 + index];
            headerLength += lengthBytes;
        }
        if (length < 0 || offset + headerLength + length > data.Length) return false;
        content = CopySegment(data, offset + headerLength, offset + headerLength + length);
        offset += headerLength + length;
        return true;
    }

    private static byte[] TrimLeadingZero(byte[] value)
    {
        var index = 0;
        while (index < value.Length - 1 && value[index] == 0) index++;
        return CopySegment(value, index, value.Length);
    }

    private static byte[] CopySegment(byte[] value, int start, int stop)
    {
        if (stop <= start) return Array.Empty<byte>();
        var result = new byte[stop - start];
        Buffer.BlockCopy(value, start, result, 0, result.Length);
        return result;
    }

    private const string InvokeSnippet = """
(async () => {
  try {
    var mi = JSON.parse(globalThis.__mi);
    var res = await globalThis.lx.__invoke(globalThis.__act, globalThis.__src, mi, globalThis.__q || null);
    globalThis.__r = { ok: true, value: JSON.stringify(res === undefined ? null : res) };
  } catch (e) {
    globalThis.__r = { ok: false, err: String((e && e.message) || e) };
  }
})();
""";

    // ================================================================
    //  lx 运行时 shim（纯 JS，闭包持有 handlers，避免解构后 this 丢失）
    // ================================================================
    private const string ShimJs = """
(function () {
  var nativeReq = globalThis.__lxNativeRequest;
  var nativeCrypto = globalThis.__lxNativeCrypto;
  var nativeZlib = globalThis.__lxNativeZlib;
  var nativeUpdateAlert = globalThis.__lxNativeUpdateAlert;
  var handlers = {};
  function noop() {}

  // ===================== md5（对 null/undefined 宽容） =====================
  var MD5 = (function () {
    function safeAdd(x, y) { var lsw = (x & 0xFFFF) + (y & 0xFFFF); return (((x >> 16) + (y >> 16) + (lsw >> 16)) << 16) | (lsw & 0xFFFF); }
    function bitRotateLeft(num, cnt) { return (num << cnt) | (num >>> (32 - cnt)); }
    function md5cmn(q, a, b, x, s, t) { return safeAdd(bitRotateLeft(safeAdd(safeAdd(a, q), safeAdd(x, t)), s), b); }
    function md5ff(a, b, c, d, x, s, t) { return md5cmn((b & c) | (~b & d), a, b, x, s, t); }
    function md5gg(a, b, c, d, x, s, t) { return md5cmn((b & d) | (c & ~d), a, b, x, s, t); }
    function md5hh(a, b, c, d, x, s, t) { return md5cmn(b ^ c ^ d, a, b, x, s, t); }
    function md5ii(a, b, c, d, x, s, t) { return md5cmn(c ^ (b | ~d), a, b, x, s, t); }
    function binl2hex(binarray) { var hexTab = '0123456789abcdef'; var str = ''; for (var i = 0; i < binarray.length * 4; i++) { str += hexTab.charAt((binarray[i >> 2] >> ((i % 4) * 8 + 4)) & 0xF) + hexTab.charAt((binarray[i >> 2] >> ((i % 4) * 8)) & 0xF); } return str; }
    function convertToWordArray(str) { if (str == null) str = ''; var lWordCount; var lMessageLength = str.length; var lNumberOfWordsTemp1 = lMessageLength + 8; var lNumberOfWordsTemp2 = (lNumberOfWordsTemp1 - (lNumberOfWordsTemp1 % 64)) / 64; var lNumberOfWords = (lNumberOfWordsTemp2 + 1) * 16; var lWordArray = Array(lNumberOfWords - 1); var lBytePosition = 0, lByteCount = 0; while (lByteCount < lMessageLength) { lWordCount = (lByteCount - (lByteCount % 4)) / 4; lBytePosition = (lByteCount % 4) * 8; lWordArray[lWordCount] = (lWordArray[lWordCount] | (str.charCodeAt(lByteCount) << lBytePosition)); lByteCount++; } lWordCount = (lByteCount - (lByteCount % 4)) / 4; lBytePosition = (lByteCount % 4) * 8; lWordArray[lWordCount] = lWordArray[lWordCount] | (0x80 << lBytePosition); lWordArray[lNumberOfWords - 2] = lMessageLength << 3; lWordArray[lNumberOfWords - 1] = lMessageLength >>> 29; return lWordArray; }
    function md5(message) {
      if (message == null) message = '';
      var messageArray = convertToWordArray(message);
      var a = 1732584193, b = -271733879, c = -1732584194, d = 271733878;
      for (var k = 0; k < messageArray.length; k += 16) {
        var aa = a, bb = b, cc = c, dd = d;
        a = md5ff(a, b, c, d, messageArray[k], 7, -680876936); d = md5ff(d, a, b, c, messageArray[k + 1], 12, -389564586); c = md5ff(c, d, a, b, messageArray[k + 2], 17, 606105819); b = md5ff(b, c, d, a, messageArray[k + 3], 22, -1044525330);
        a = md5ff(a, b, c, d, messageArray[k + 4], 7, -176418897); d = md5ff(d, a, b, c, messageArray[k + 5], 12, 1200080426); c = md5ff(c, d, a, b, messageArray[k + 6], 17, -1473231341); b = md5ff(b, c, d, a, messageArray[k + 7], 22, -45705983);
        a = md5ff(a, b, c, d, messageArray[k + 8], 7, 1770035416); d = md5ff(d, a, b, c, messageArray[k + 9], 12, -1958414417); c = md5ff(c, d, a, b, messageArray[k + 10], 17, -42063); b = md5ff(b, c, d, a, messageArray[k + 11], 22, -1990404162);
        a = md5ff(a, b, c, d, messageArray[k + 12], 7, 1804603682); d = md5ff(d, a, b, c, messageArray[k + 13], 12, -40341101); c = md5ff(c, d, a, b, messageArray[k + 14], 17, -1502002290); b = md5ff(b, c, d, a, messageArray[k + 15], 22, 1236535329);
        a = md5gg(a, b, c, d, messageArray[k + 1], 5, -165796510); d = md5gg(d, a, b, c, messageArray[k + 6], 9, -1069501632); c = md5gg(c, d, a, b, messageArray[k + 11], 14, 643717713); b = md5gg(b, c, d, a, messageArray[k], 20, -373897302);
        a = md5gg(a, b, c, d, messageArray[k + 5], 5, -701558691); d = md5gg(d, a, b, c, messageArray[k + 10], 9, 38016083); c = md5gg(c, d, a, b, messageArray[k + 15], 14, -660478335); b = md5gg(b, c, d, a, messageArray[k + 4], 20, -405537848);
        a = md5gg(a, b, c, d, messageArray[k + 9], 5, 568446438); d = md5gg(d, a, b, c, messageArray[k + 14], 9, -1019803690); c = md5gg(c, d, a, b, messageArray[k + 3], 14, -187363961); b = md5gg(b, c, d, a, messageArray[k + 8], 20, 1163531501);
        a = md5gg(a, b, c, d, messageArray[k + 13], 5, -1444681467); d = md5gg(d, a, b, c, messageArray[k + 2], 9, -51403784); c = md5gg(c, d, a, b, messageArray[k + 7], 14, 1735328473); b = md5gg(b, c, d, a, messageArray[k + 12], 20, -1926607734);
        a = md5hh(a, b, c, d, messageArray[k + 5], 4, -378558); d = md5hh(d, a, b, c, messageArray[k + 8], 11, -2022574463); c = md5hh(c, d, a, b, messageArray[k + 11], 16, 1839030562); b = md5hh(b, c, d, a, messageArray[k + 14], 23, -35309556);
        a = md5hh(a, b, c, d, messageArray[k + 1], 4, -1530992060); d = md5hh(d, a, b, c, messageArray[k + 4], 11, 1272893353); c = md5hh(c, d, a, b, messageArray[k + 7], 16, -155497632); b = md5hh(b, c, d, a, messageArray[k + 10], 23, -1094730640);
        a = md5hh(a, b, c, d, messageArray[k + 13], 4, 681279174); d = md5hh(d, a, b, c, messageArray[k], 11, -358537222); c = md5hh(c, d, a, b, messageArray[k + 3], 16, -722521979); b = md5hh(b, c, d, a, messageArray[k + 6], 23, 76029189);
        a = md5hh(a, b, c, d, messageArray[k + 9], 4, -640364487); d = md5hh(d, a, b, c, messageArray[k + 12], 11, -421815835); c = md5hh(c, d, a, b, messageArray[k + 15], 16, 530742520); b = md5hh(b, c, d, a, messageArray[k + 2], 23, -995338651);
        a = md5ii(a, b, c, d, messageArray[k], 6, -198630844); d = md5ii(d, a, b, c, messageArray[k + 7], 10, 1126891415); c = md5ii(c, d, a, b, messageArray[k + 14], 15, -1416354905); b = md5ii(b, c, d, a, messageArray[k + 5], 21, -57434055);
        a = md5ii(a, b, c, d, messageArray[k + 12], 6, 1700485571); d = md5ii(d, a, b, c, messageArray[k + 3], 10, -1894986606); c = md5ii(c, d, a, b, messageArray[k + 10], 15, -1051523); b = md5ii(b, c, d, a, messageArray[k + 1], 21, -2054922799);
        a = md5ii(a, b, c, d, messageArray[k + 8], 6, 1873313359); d = md5ii(d, a, b, c, messageArray[k + 15], 10, -30611744); c = md5ii(c, d, a, b, messageArray[k + 6], 15, -1560198380); b = md5ii(b, c, d, a, messageArray[k + 13], 21, 1309151649);
        a = md5ii(a, b, c, d, messageArray[k + 4], 6, -145523070); d = md5ii(d, a, b, c, messageArray[k + 11], 10, -1120210379); c = md5ii(c, d, a, b, messageArray[k + 2], 15, 718787259); b = md5ii(b, c, d, a, messageArray[k + 9], 21, -343485551);
        a = safeAdd(a, aa); b = safeAdd(b, bb); c = safeAdd(c, cc); d = safeAdd(d, dd);
      }
      return binl2hex([a, b, c, d]);
    }
    return md5;
  })();

  function utf8Bytes(s) { if (s == null) s = ''; var arr = []; for (var i = 0; i < s.length; i++) { var c = s.charCodeAt(i); if (c < 128) arr.push(c); else if (c < 2048) { arr.push((c >> 6) | 192); arr.push((c & 63) | 128); } else { arr.push((c >> 12) | 224); arr.push(((c >> 6) & 63) | 128); arr.push((c & 63) | 128); } } return arr; }
  function bytesToHex(bytes) { var h = '0123456789abcdef'; var out = ''; for (var i = 0; i < bytes.length; i++) out += h.charAt((bytes[i] >>> 4) & 0xf) + h.charAt(bytes[i] & 0xf); return out; }

  var SHA1 = (function () {
    function rol(n, b) { return (n << b) | (n >>> (32 - b)); }
    function compute(s) {
      var msg = utf8Bytes(s);
      var l = msg.length, bitLen = l * 8;
      var m = msg.slice(); m.push(0x80);
      while ((m.length % 64) !== 56) m.push(0);
      m.push((bitLen >>> 24) & 255, (bitLen >>> 16) & 255, (bitLen >>> 8) & 255, bitLen & 255, 0, 0, 0, 0);
      var h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476, h4 = 0xC3D2E1F0;
      for (var i = 0; i < m.length; i += 64) {
        var w = new Array(80);
        for (var t = 0; t < 16; t++) w[t] = ((m[i + t * 4] << 24) | (m[i + t * 4 + 1] << 16) | (m[i + t * 4 + 2] << 8) | (m[i + t * 4 + 3])) >>> 0;
        for (t = 16; t < 80; t++) w[t] = rol(w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16], 1);
        var a = h0, b = h1, c = h2, d = h3, e = h4;
        for (t = 0; t < 80; t++) {
          var f, k;
          if (t < 20) { f = (b & c) | ((~b) & d); k = 0x5A827999; }
          else if (t < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1; }
          else if (t < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
          else { f = b ^ c ^ d; k = 0xCA62C1D6; }
          var temp = (rol(a, 5) + f + e + k + w[t]) >>> 0;
          e = d; d = c; c = rol(b, 30); b = a; a = temp;
        }
        h0 = (h0 + a) >>> 0; h1 = (h1 + b) >>> 0; h2 = (h2 + c) >>> 0; h3 = (h3 + d) >>> 0; h4 = (h4 + e) >>> 0;
      }
      var out = [];
      [h0, h1, h2, h3, h4].forEach(function (v) { out.push((v >>> 24) & 255, (v >>> 16) & 255, (v >>> 8) & 255, v & 255); });
      return bytesToHex(out);
    }
    return compute;
  })();

  var SHA256 = (function () {
    var K = [0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2];
    function rotr(x, n) { return (x >>> n) | (x << (32 - n)); }
    function compute(s) {
      var msg = utf8Bytes(s);
      var l = msg.length, bitLen = l * 8;
      var m = msg.slice(); m.push(0x80);
      while ((m.length % 64) !== 56) m.push(0);
      m.push((bitLen >>> 24) & 255, (bitLen >>> 16) & 255, (bitLen >>> 8) & 255, bitLen & 255, 0, 0, 0, 0);
      var H = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];
      for (var i = 0; i < m.length; i += 64) {
        var w = new Array(64);
        for (var t = 0; t < 16; t++) w[t] = ((m[i + t * 4] << 24) | (m[i + t * 4 + 1] << 16) | (m[i + t * 4 + 2] << 8) | (m[i + t * 4 + 3])) >>> 0;
        for (t = 16; t < 64; t++) {
          var s0 = rotr(w[t - 15], 7) ^ rotr(w[t - 15], 18) ^ (w[t - 15] >>> 3);
          var s1 = rotr(w[t - 2], 17) ^ rotr(w[t - 2], 19) ^ (w[t - 2] >>> 10);
          w[t] = (w[t - 16] + s0 + w[t - 7] + s1) >>> 0;
        }
        var a = H[0], b = H[1], c = H[2], d = H[3], e = H[4], f = H[5], g = H[6], h = H[7];
        for (t = 0; t < 64; t++) {
          var S1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25);
          var ch = (e & f) ^ (~e & g);
          var temp1 = (h + S1 + ch + K[t] + w[t]) >>> 0;
          var S0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22);
          var maj = (a & b) ^ (a & c) ^ (b & c);
          var temp2 = (S0 + maj) >>> 0;
          h = g; g = f; f = e; e = (d + temp1) >>> 0; d = c; c = b; b = a; a = (temp1 + temp2) >>> 0;
        }
        H[0] = (H[0] + a) >>> 0; H[1] = (H[1] + b) >>> 0; H[2] = (H[2] + c) >>> 0; H[3] = (H[3] + d) >>> 0;
        H[4] = (H[4] + e) >>> 0; H[5] = (H[5] + f) >>> 0; H[6] = (H[6] + g) >>> 0; H[7] = (H[7] + h) >>> 0;
      }
      var out = [];
      for (var j = 0; j < 8; j++) out.push((H[j] >>> 24) & 255, (H[j] >>> 16) & 255, (H[j] >>> 8) & 255, H[j] & 255);
      return bytesToHex(out);
    }
    return compute;
  })();

  // ===================== base64 =====================
  var B64TAB = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  function toBase64(str) {
    if (str == null) str = '';
    var out = '', i, l, c1, c2, c3, e1, e2, e3, e4;
    for (i = 0, l = str.length; i < l; i += 3) {
      c1 = str.charCodeAt(i) & 0xFF;
      c2 = i + 1 < l ? str.charCodeAt(i + 1) & 0xFF : NaN;
      c3 = i + 2 < l ? str.charCodeAt(i + 2) & 0xFF : NaN;
      e1 = c1 >> 2;
      e2 = ((c1 & 3) << 4) | (c2 >> 4);
      e3 = isNaN(c2) ? 64 : ((c2 & 15) << 2) | (c3 >> 6);
      e4 = isNaN(c3) ? 64 : (c3 & 63);
      out += B64TAB.charAt(e1) + B64TAB.charAt(e2) + (e3 === 64 ? '=' : B64TAB.charAt(e3)) + (e4 === 64 ? '=' : B64TAB.charAt(e4));
    }
    return out;
  }
  function atobImpl(s) {
    if (s == null) return '';
    s = String(s).replace(/[^A-Za-z0-9+/=]/g, '');
    var out = '';
    for (var i = 0; i < s.length; i += 4) {
      var a = B64TAB.indexOf(s.charAt(i));
      var b = B64TAB.indexOf(s.charAt(i + 1));
      var c = B64TAB.indexOf(s.charAt(i + 2));
      var d = B64TAB.indexOf(s.charAt(i + 3));
      out += String.fromCharCode((a << 2) | (b >> 4));
      if (c !== -1) out += String.fromCharCode(((b & 15) << 4) | (c >> 2));
      if (d !== -1) out += String.fromCharCode(((c & 3) << 6) | d);
    }
    return out;
  }
  function bytesToBase64(bytes) {
    if (!bytes) return '';
    var out = '';
    for (var i = 0; i < bytes.length; i += 3) {
      var b0 = bytes[i] & 255;
      var b1 = i + 1 < bytes.length ? bytes[i + 1] & 255 : NaN;
      var b2 = i + 2 < bytes.length ? bytes[i + 2] & 255 : NaN;
      var e1 = b0 >> 2;
      var e2 = ((b0 & 3) << 4) | (b1 >> 4);
      var e3 = isNaN(b1) ? 64 : ((b1 & 15) << 2) | (b2 >> 6);
      var e4 = isNaN(b2) ? 64 : (b2 & 63);
      out += B64TAB.charAt(e1) + B64TAB.charAt(e2) + (e3 === 64 ? '=' : B64TAB.charAt(e3)) + (e4 === 64 ? '=' : B64TAB.charAt(e4));
    }
    return out;
  }
  function b64ToBytes(b64) {
    var bin = atobImpl(b64);
    var out = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i) & 255;
    return out;
  }
  function utf8ToBase64(str) { return bytesToBase64(new Uint8Array(utf8Bytes(str))); }
  function anyToB64(x) {
    if (x == null) return '';
    if (typeof x === 'string') return utf8ToBase64(x);
    if (x instanceof Uint8Array) return bytesToBase64(x);
    return bytesToBase64(new Uint8Array(x));
  }

  // ===================== 字节工具 =====================
  function toBytes(a, enc) {
    if (a == null) return new Uint8Array(0);
    if (a instanceof Uint8Array) return a;
    var e = String(enc || '').toLowerCase();
    if (typeof a === 'string') {
      if (e === 'base64') return b64ToBytes(a);
      if (e === 'hex') { var arr = new Uint8Array(Math.floor(a.length / 2)); for (var i = 0; i < arr.length; i++) arr[i] = parseInt(a.substr(i * 2, 2), 16); return arr; }
      if (e === 'binary' || e === 'latin1') { var o = new Uint8Array(a.length); for (var j = 0; j < a.length; j++) o[j] = a.charCodeAt(j) & 255; return o; }
      return new Uint8Array(utf8Bytes(a));
    }
    if (Array.isArray(a)) return new Uint8Array(a);
    if (a && typeof a.length === 'number') return new Uint8Array(Array.prototype.slice.call(a));
    return new Uint8Array(0);
  }
  function bytesToString(bytes, format) {
    if (bytes == null) return '';
    if (typeof bytes === 'string') return bytes;
    var f = String(format || 'utf8').toLowerCase();
    if (f === 'base64') return bytesToBase64(bytes);
    if (f === 'hex') { var o = ''; for (var i = 0; i < bytes.length; i++) { var h = (bytes[i] & 255).toString(16); o += (h.length === 1 ? '0' : '') + h; } return o; }
    if (f === 'binary' || f === 'latin1') { var s = ''; for (var j = 0; j < bytes.length; j++) s += String.fromCharCode(bytes[j] & 255); return s; }
    var str = '', idx = 0;
    while (idx < bytes.length) {
      var c = bytes[idx++] & 255;
      if (c < 0x80) { str += String.fromCharCode(c); }
      else if (c < 0xE0) { str += String.fromCharCode(((c & 0x1F) << 6) | (bytes[idx++] & 0x3F)); }
      else if (c < 0xF0) { str += String.fromCharCode(((c & 0x0F) << 12) | ((bytes[idx++] & 0x3F) << 6) | (bytes[idx++] & 0x3F)); }
      else { var cp = ((c & 0x07) << 18) | ((bytes[idx++] & 0x3F) << 12) | ((bytes[idx++] & 0x3F) << 6) | (bytes[idx++] & 0x3F); str += String.fromCodePoint(cp); }
    }
    return str;
  }
  function concatBytes(arr) {
    var total = 0, i;
    for (i = 0; i < arr.length; i++) total += arr[i] ? arr[i].length : 0;
    var out = new Uint8Array(total);
    var off = 0;
    for (i = 0; i < arr.length; i++) { if (arr[i]) { out.set(arr[i], off); off += arr[i].length; } }
    return out;
  }

  // ===================== Buffer（基于 Uint8Array） =====================
  var BufferCtor = null;
  try {
    BufferCtor = class extends Uint8Array { toString(encoding) { return bytesToString(this, encoding); } };
  } catch (e) { BufferCtor = Uint8Array; }
  globalThis.Buffer = {
    from: function (a, enc) { return new BufferCtor(toBytes(a, enc)); },
    alloc: function (n) { return new BufferCtor(n || 0); },
    concat: function (arr) { return new BufferCtor(concatBytes(arr || [])); },
    isBuffer: function (b) { return b instanceof Uint8Array || (b != null && typeof b.length === 'number' && b.buffer instanceof ArrayBuffer); }
  };

  // ===================== 浏览器/环境全局 =====================
  globalThis.window = globalThis;
  globalThis.global = globalThis;
  globalThis.self = globalThis;
  globalThis.top = globalThis;
  globalThis.parent = globalThis;
  globalThis.navigator = { userAgent: 'lx-music-desktop/2.0.0', appVersion: '5.0', platform: 'Win32' };
  globalThis.location = { href: 'https://localhost/', origin: 'https://localhost', protocol: 'https:', hostname: 'localhost', pathname: '/', search: '', hash: '' };
  globalThis.process = { env: {}, version: 'v18.0.0', versions: { node: '18.0.0' }, platform: 'win32', argv: [], nextTick: function (f) { f(); } };
  globalThis.crypto = { getRandomValues: function (arr) { for (var i = 0; i < arr.length; i++) arr[i] = Math.floor(Math.random() * 256); return arr; } };
  globalThis.atob = atobImpl;
  globalThis.btoa = toBase64;
  globalThis.console = { log: noop, info: noop, warn: noop, error: noop, debug: noop, trace: noop, group: noop, groupCollapsed: noop, groupEnd: noop, table: noop, dir: noop, dirxml: noop, count: noop, countReset: noop, time: noop, timeEnd: noop, timeLog: noop, assert: noop, clear: noop, profile: noop, profileEnd: noop };
  globalThis.setTimeout = function () { return 0; };
  globalThis.setInterval = function () { return 0; };
  globalThis.clearTimeout = noop;
  globalThis.clearInterval = noop;

  // ===================== 原生桥（JS 包装） =====================
  function nativeAes(op, buffer, mode, key, iv) {
    var raw = nativeCrypto(op, JSON.stringify({ data: anyToB64(buffer), key: anyToB64(key), iv: anyToB64(iv), mode: String(mode || '') }));
    var r = JSON.parse(raw);
    if (!r.ok) throw new Error(r.err || 'crypto error');
    return b64ToBytes(r.b64);
  }
  function nativeRsa(buffer, key) {
    var k = key;
    if (k && k instanceof Uint8Array) k = bytesToString(k, 'utf8');
    var raw = nativeCrypto('rsaEncrypt', JSON.stringify({ data: anyToB64(buffer), key: String(k || '') }));
    var r = JSON.parse(raw);
    if (!r.ok) throw new Error(r.err || 'crypto error');
    return b64ToBytes(r.b64);
  }
  function nativeRandomBytes(size) {
    var raw = nativeCrypto('randomBytes', JSON.stringify({ size: (typeof size === 'number' && size >= 0) ? Math.floor(size) : 16 }));
    var r = JSON.parse(raw);
    if (!r.ok) throw new Error(r.err || 'crypto error');
    return b64ToBytes(r.b64);
  }
  function nativeDigest(algorithm, data) {
    var raw = nativeCrypto('digest', JSON.stringify({ algorithm: String(algorithm || ''), data: anyToB64(data) }));
    var r = JSON.parse(raw);
    if (!r.ok) throw new Error(r.err || 'digest error');
    return r.b64 ? b64ToBytes(r.b64) : [];
  }
  function nativeDigestString(algorithm) {
    return function (value) {
      var bytes = nativeDigest(algorithm, value == null ? '' : value);
      return bytesToHex(bytes);
    };
  }
  function nativeZlibOp(op, data) {
    return new Promise(function (resolve, reject) {
      var raw;
      try {
        raw = nativeZlib(op, anyToB64(data));
        var r = JSON.parse(raw);
        if (r.ok) resolve(b64ToBytes(r.b64)); else reject(new Error(r.err || 'zlib error'));
      } catch (e) { reject(e); }
    });
  }

  // ===================== lx 运行时 =====================
  var EVENT_NAMES = { request: 'request', inited: 'inited', updateAlert: 'updateAlert', destroy: 'destroy' };

  function parseResp(raw) {
    var parsed = null;
    try { parsed = JSON.parse(raw); } catch (e) { parsed = { status: 0, body: raw, err: String(raw) }; }
    if (parsed.err) { var e0 = new Error(parsed.err); e0.isLxNativeError = true; throw e0; }
    var bodyVal = null;
    if (typeof parsed.body === 'string') { try { bodyVal = JSON.parse(parsed.body); } catch (e) { bodyVal = parsed.body; } }
    else { bodyVal = parsed.body; }
    var rawBytes = null;
    if (parsed.bodyB64) { try { rawBytes = b64ToBytes(parsed.bodyB64); } catch (e) { throw new Error('invalid response body encoding'); } }
    var resp = {
      status: parsed.status, statusCode: parsed.status,
      ok: parsed.status >= 200 && parsed.status < 300,
      headers: parsed.headers || {},
      bytes: rawBytes ? rawBytes.length : 0,
      raw: rawBytes ? new BufferCtor(rawBytes) : null,
      body: bodyVal,
      json: function () { return Promise.resolve(bodyVal); },
      text: function () { return Promise.resolve(typeof parsed.body === 'string' ? parsed.body : JSON.stringify(bodyVal)); }
    };
    return resp;
  }

  var utils = {
    createRandomID: function (len) { var s = ''; var chars = 'abcdefghijklmnopqrstuvwxyz0123456789'; var n = len || 16; for (var i = 0; i < n; i++) s += chars.charAt(Math.floor(Math.random() * chars.length)); return s; },
    bufToString: function (buf, format) { return bytesToString(buf, format); },
    log: function () {},
    md5: nativeDigestString('md5'),
    sha1: nativeDigestString('sha1'),
    sha256: nativeDigestString('sha256'),
    sha512: nativeDigestString('sha512'),
    base64Encode: function (s) { return toBase64(s == null ? '' : String(s)); },
    base64Decode: function (s) { try { return atobImpl(s); } catch (e) { return s; } },
    crypto: {
      md5: nativeDigestString('md5'), sha1: nativeDigestString('sha1'), sha256: nativeDigestString('sha256'), sha512: nativeDigestString('sha512'),
      aesEncrypt: function (buffer, mode, key, iv) { return nativeAes('aesEncrypt', buffer, mode, key, iv); },
      aesDecrypt: function (buffer, mode, key, iv) { return nativeAes('aesDecrypt', buffer, mode, key, iv); },
      rsaEncrypt: function (buffer, key) { return nativeRsa(buffer, key); },
      randomBytes: function (size) { return nativeRandomBytes(size); }
    },
    aesEncrypt: function (buffer, mode, key, iv) { return nativeAes('aesEncrypt', buffer, mode, key, iv); },
    aesDecrypt: function (buffer, mode, key, iv) { return nativeAes('aesDecrypt', buffer, mode, key, iv); },
    rsaEncrypt: function (buffer, key) { return nativeRsa(buffer, key); },
    buffer: {
      from: function (a, enc) { return toBytes(a, enc); },
      bufToString: function (buf, format) { return bytesToString(buf, format); }
    },
    zlib: {
      inflate: function (buf) { return nativeZlibOp('inflate', buf); },
      deflate: function (data) { return nativeZlibOp('deflate', data); }
    }
  };

  globalThis.lx = {
    EVENT_NAMES: EVENT_NAMES,
    on: function (name, fn) {
      if (name !== EVENT_NAMES.request && name !== EVENT_NAMES.destroy) return Promise.reject(new Error('The event is not supported: ' + name));
      handlers[name] = fn;
      return Promise.resolve();
    },
    send: function (eventName, data) {
      return new Promise(function (resolve, reject) {
        if (eventName === EVENT_NAMES.inited) {
          if (!globalThis.__lxInitedCaptured) {
            globalThis.__lxInitedCaptured = true;
            try {
              var serialized = JSON.stringify(data || null);
              globalThis.__lxInitedJson = typeof serialized === 'string' && serialized.length <= 65536
                ? serialized
                : null;
            } catch (e) {
              globalThis.__lxInitedJson = null;
            }
          }
          resolve();
          return;
        }
        if (eventName === EVENT_NAMES.updateAlert) {
          if (globalThis.__lxUpdateAlertCaptured) {
            reject(new Error('The update alert can only be called once.'));
            return;
          }
          globalThis.__lxUpdateAlertCaptured = true;
          try { if (nativeUpdateAlert) nativeUpdateAlert(JSON.stringify(data || {})); }
          catch (e) { reject(e); return; }
          resolve();
          return;
        }
        reject(new Error('The event is not supported: ' + eventName));
      });
    },
    request: function (url, options, callback) {
      var opt = options || {};
      var cancelled = false;
      var reqBody = opt.body;
      var contentType = (opt.headers && (opt.headers['Content-Type'] || opt.headers['content-type'])) || '';
      var hdrs = {};
      if (opt.headers) { for (var hk in opt.headers) { if (Object.prototype.hasOwnProperty.call(opt.headers, hk)) hdrs[hk] = String(opt.headers[hk]); } }
      if (opt.form != null) {
        var parts = [];
        for (var k in opt.form) { if (Object.prototype.hasOwnProperty.call(opt.form, k)) parts.push(encodeURIComponent(k) + '=' + encodeURIComponent(String(opt.form[k]))); }
        reqBody = parts.join('&');
        if (!contentType) contentType = 'application/x-www-form-urlencoded';
      } else if (opt.formData != null) {
        var boundary = '----lxboundary' + Math.random().toString(36).substring(2);
        var mp = '';
        for (var k2 in opt.formData) { if (Object.prototype.hasOwnProperty.call(opt.formData, k2)) { mp += '--' + boundary + '\r\nContent-Disposition: form-data; name="' + k2 + '"\r\n\r\n' + String(opt.formData[k2]) + '\r\n'; } }
        mp += '--' + boundary + '--';
        reqBody = mp;
        contentType = 'multipart/form-data; boundary=' + boundary;
      } else if (reqBody != null && typeof reqBody === 'object') {
        reqBody = JSON.stringify(reqBody);
        if (!contentType) contentType = 'application/json';
      } else {
        if (reqBody == null) reqBody = '';
        if (!contentType) contentType = 'application/x-www-form-urlencoded';
      }
      hdrs['Content-Type'] = contentType;
      var raw;
      try {
        raw = nativeReq(String(url), String(opt.method || 'GET'), JSON.stringify(hdrs), String(reqBody),
          JSON.stringify({ timeoutMs: (typeof opt.timeout === 'number' && opt.timeout > 0) ? Math.min(opt.timeout, 60000) : 0 }));
      } catch (e) { if (callback) callback(e, null); return function () {}; }
      if (cancelled) return function () {};
      var resp;
      try { resp = parseResp(raw); }
      catch (e) { if (callback) callback(e, null); return function () {}; }
      if (callback) callback(null, resp, resp.body);
      return function () { cancelled = true; };
    },
    utils: utils,
    env: 'desktop',
    version: '2.0.0',
    currentScriptInfo: (function () { try { return JSON.parse(globalThis.__scriptInfo); } catch (e) { return { name: 'lx-music-accessible', version: '1.0.0' }; } })()
  };

  globalThis.__lxInitedJson = null;
  globalThis.__lxInitedCaptured = false;
  globalThis.__lxUpdateAlertCaptured = false;
  globalThis.lx.__invoke = function (action, source, musicInfo, type) {
    var h = handlers['request'];
    if (typeof h === 'function') return h({ action: action, source: source, info: { musicInfo: musicInfo, type: type } });
    if (typeof globalThis.handleGetMusicUrl === 'function' && action === 'musicUrl') return globalThis.handleGetMusicUrl(source, musicInfo, type);
    throw new Error('no handler for action: ' + action);
  };
  globalThis.lx.__probe = function () {
    return typeof handlers['request'] === 'function' || typeof globalThis.handleGetMusicUrl === 'function';
  };

  // ===================== global fetch =====================
  globalThis.fetch = function (url, options) {
    return new Promise(function (resolve, reject) {
      var opt = options || {};
      var body = opt.body;
      var ct = (opt.headers && (opt.headers['Content-Type'] || opt.headers['content-type'])) || '';
      if (body != null && typeof body === 'object') { body = JSON.stringify(body); if (!ct) ct = 'application/json'; }
      var hdrs = {};
      if (opt.headers) { for (var hk in opt.headers) { if (Object.prototype.hasOwnProperty.call(opt.headers, hk)) hdrs[hk] = String(opt.headers[hk]); } }
      if (ct) hdrs['Content-Type'] = ct;
      var raw;
      try {
        raw = nativeReq(String(url), String(opt.method || 'GET'), JSON.stringify(hdrs), String(body || ''),
          JSON.stringify({ timeoutMs: (typeof opt.timeout === 'number' && opt.timeout > 0) ? Math.min(opt.timeout, 60000) : 0 }));
      } catch (e) { reject(e); return; }
      try { resolve(parseResp(raw)); }
      catch (e) { reject(e); }
    });
  };

  // ===================== CommonJS 全局 =====================
  globalThis.module = { exports: {} };
  globalThis.exports = globalThis.module.exports;
  globalThis.__filename = 'script.js';
  globalThis.__dirname = '/';
  globalThis.require = function (name) {
    if (name === 'lx-music-source' || name === 'lx-music-api') return globalThis.lx;
    if (name === 'crypto') return globalThis.lx.utils.crypto;
    if (name === 'buffer') return { Buffer: globalThis.Buffer };
    if (name === 'zlib') return globalThis.lx.utils.zlib;
    if (name === 'path') return {};
    if (name === 'url') return {};
    return globalThis.lx;
  };

  // ===================== AMD define + httpFetch =====================
  globalThis.define = function (name, deps, factory) {
    if (typeof name === 'function') { factory = name; deps = []; name = null; }
    else if (typeof deps === 'function') { factory = deps; deps = []; }
    deps = deps || [];
    var module = { exports: {} };
    function req(n) { return globalThis.require(n); }
    function httpFetch(url, options) {
      return new Promise(function (resolve, reject) {
        var opt = options || {};
        var body = opt.body;
        var ct = (opt.headers && (opt.headers['Content-Type'] || opt.headers['content-type'])) || '';
        if (body != null && typeof body === 'object') { body = JSON.stringify(body); if (!ct) ct = 'application/json'; }
        var hdrs = {};
        if (opt.headers) { for (var hk in opt.headers) { if (Object.prototype.hasOwnProperty.call(opt.headers, hk)) hdrs[hk] = String(opt.headers[hk]); } }
        if (ct) hdrs['Content-Type'] = ct;
        var raw;
        try {
          raw = nativeReq(String(url), String(opt.method || 'GET'), JSON.stringify(hdrs), String(body || ''),
            JSON.stringify({ timeoutMs: (typeof opt.timeout === 'number' && opt.timeout > 0) ? Math.min(opt.timeout, 60000) : 0 }));
        } catch (e) { reject(e); return; }
        try { resolve(parseResp(raw)); }
        catch (e) { reject(e); }
      });
    }
    var args = deps.map(function (dd) {
      if (dd === 'exports') return module.exports;
      if (dd === 'module') return module;
      if (dd === 'require') return req;
      if (dd === 'httpFetch') return httpFetch;
      return undefined;
    });
    var ret = factory.apply(globalThis, args);
    if (ret !== undefined) module.exports = ret;
    globalThis.__lastModuleExports = module.exports;
    return module.exports;
  };
})();
""";
}

public sealed record ScriptExecutionFailure(string Code, string Message);

internal sealed class ScriptResourceLimitException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>脚本 updateAlert 事件参数（供界面提示音源更新）</summary>
public class LxScriptUpdateAlertEventArgs : EventArgs
{
    public string Log { get; set; } = "";
    public string? UpdateUrl { get; set; }
}

/// <summary>脚本 lyric 动作返回的原始歌词文本（含翻译/罗马音/逐字）</summary>
public class LxLyricRaw
{
    public string Lyric { get; set; } = "";
    public string? Tlyric { get; set; }
    public string? Rlyric { get; set; }
    public string? Lxlyric { get; set; }
}
