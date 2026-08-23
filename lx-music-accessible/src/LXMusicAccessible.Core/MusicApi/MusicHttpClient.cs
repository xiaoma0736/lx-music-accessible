using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// HTTP 客户端服务，用于音乐 API 调用
/// </summary>
public class MusicHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly CookieContainer _cookieContainer;
    private readonly ILogger<MusicHttpClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public MusicHttpClient(ILogger<MusicHttpClient> logger)
        : this(logger, CreateDefaultTransport())
    {
    }

    private MusicHttpClient(
        ILogger<MusicHttpClient> logger,
        (HttpMessageHandler Handler, CookieContainer Cookies) transport)
        : this(logger, transport.Handler, transport.Cookies)
    {
    }

    public MusicHttpClient(
        ILogger<MusicHttpClient> logger,
        HttpMessageHandler handler,
        CookieContainer? cookieContainer = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(handler);
        _cookieContainer = cookieContainer ?? new CookieContainer();
        _httpClient = new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    private static (HttpMessageHandler Handler, CookieContainer Cookies) CreateDefaultTransport()
    {
        var cookies = new CookieContainer();
        return (new HttpClientHandler { UseCookies = true, CookieContainer = cookies }, cookies);
    }

    /// <summary>
    /// 发送 GET 请求
    /// </summary>
    public async Task<string> GetAsync(
        string url,
        Dictionary<string, string>? parameters = null,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullUrl = url;
            if (parameters != null && parameters.Count > 0)
            {
                var queryString = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
                fullUrl += $"?{queryString}";
            }

            _logger.LogInformation("GET请求: {Endpoint}", GetSafeEndpoint(fullUrl));

            using var request = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("GET请求成功: {Endpoint}, 返回数据长度: {Length}", GetSafeEndpoint(fullUrl), content.Length);
            return content;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError("GET请求失败: {Endpoint}, ErrorCode=HttpRequestFailed", GetSafeEndpoint(url));
            throw;
        }
    }

    /// <summary>
    /// 发送 GET 请求并反序列化响应
    /// </summary>
    public async Task<T?> GetAsync<T>(
        string url,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("GET请求: {Endpoint}", GetSafeEndpoint(url));
            
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<T>(content, _jsonOptions);
            
            _logger.LogInformation("GET请求成功: {Endpoint}, 返回数据长度: {Length}", GetSafeEndpoint(url), content.Length);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError("GET请求失败: {Endpoint}, ErrorCode=HttpRequestFailed", GetSafeEndpoint(url));
            throw;
        }
    }

    /// <summary>
    /// 发送 POST 请求
    /// </summary>
    public async Task<T?> PostAsync<T>(
        string url,
        object? data = null,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("POST请求: {Endpoint}", GetSafeEndpoint(url));
            
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            if (data != null)
            {
                var json = JsonSerializer.Serialize(data, _jsonOptions);
                request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<T>(content, _jsonOptions);
            
            _logger.LogInformation("POST请求成功: {Endpoint}, 返回数据长度: {Length}", GetSafeEndpoint(url), content.Length);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError("POST请求失败: {Endpoint}, ErrorCode=HttpRequestFailed", GetSafeEndpoint(url));
            throw;
        }
    }

    /// <summary>
    /// 发送 POST 表单请求
    /// </summary>
    public async Task<T?> PostFormAsync<T>(
        string url,
        Dictionary<string, string> formData,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("POST表单请求: {Endpoint}", GetSafeEndpoint(url));
            
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            request.Content = new FormUrlEncodedContent(formData);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<T>(content, _jsonOptions);
            
            _logger.LogInformation("POST表单请求成功: {Endpoint}, 返回数据长度: {Length}", GetSafeEndpoint(url), content.Length);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError("POST表单请求失败: {Endpoint}, ErrorCode=HttpRequestFailed", GetSafeEndpoint(url));
            throw;
        }
    }

    /// <summary>
    /// 发送 weapi 加密 POST 请求（网易云音乐）
    /// </summary>
    public async Task<T?> PostWeapiAsync<T>(
        string url,
        object data,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("weapi POST: {Endpoint}", GetSafeEndpoint(url));

            var crypto = NeteaseCrypto.Weapi(data);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            AddCommonHeaders(request, headers);

            request.Content = new FormUrlEncodedContent(crypto);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<T>(content, _jsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError("weapi POST 失败: {Endpoint}, ErrorCode=HttpRequestFailed", GetSafeEndpoint(url));
            throw;
        }
    }

    /// <summary>
    /// 发送 eapi 加密 POST 请求（网易云音乐搜索等）
    /// </summary>
    public async Task<T?> PostEapiAsync<T>(
        string path,
        object data,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "http://interface.music.163.com/eapi/batch";
            _logger.LogInformation("eapi POST: {Endpoint}", GetSafeEndpoint(path));

            var crypto = NeteaseCrypto.Eapi(path, data);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            AddCommonHeaders(request, headers);

            request.Content = new FormUrlEncodedContent(crypto);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<T>(content, _jsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError("eapi POST 失败: {Endpoint}, ErrorCode=HttpRequestFailed", GetSafeEndpoint(path));
            throw;
        }
    }

    /// <summary>
    /// 发送 linuxapi 加密 POST 请求（网易云歌单详情等）
    /// </summary>
    public async Task<T?> PostLinuxApiAsync<T>(
        object data,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "https://music.163.com/api/linux/forward";
            _logger.LogInformation("linuxapi POST");

            var crypto = NeteaseCrypto.Linuxapi(data);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            AddCommonHeaders(request, headers);

            request.Content = new FormUrlEncodedContent(crypto);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<T>(content, _jsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError("linuxapi POST 失败: ErrorCode=HttpRequestFailed");
            throw;
        }
    }

    /// <summary>
    /// 发送 JSON POST 请求（QQ音乐等）
    /// </summary>
    public async Task<T?> PostJsonAsync<T>(
        string url,
        object data,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("JSON POST: {Endpoint}", GetSafeEndpoint(url));

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            AddCommonHeaders(request, headers);

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<T>(content, _jsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError("JSON POST 失败: {Endpoint}, ErrorCode=HttpRequestFailed", GetSafeEndpoint(url));
            throw;
        }
    }

    /// <summary>
    /// 发送原始 JSON 字符串 POST 请求（用于需要精确控制请求体以计算签名的接口，如酷狗）
    /// </summary>
    public async Task<T?> PostRawJsonAsync<T>(
        string url,
        string jsonBody,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("RAW JSON POST: {Endpoint}", GetSafeEndpoint(url));

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            AddCommonHeaders(request, headers);

            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<T>(content, _jsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogError("RAW JSON POST 失败: {Endpoint}, ErrorCode=HttpRequestFailed", GetSafeEndpoint(url));
            throw;
        }
    }
    /// <summary>添加通用请求头</summary>
    private void AddCommonHeaders(HttpRequestMessage request, Dictionary<string, string>? additionalHeaders)
    {
        if (additionalHeaders != null)
        {
            foreach (var h in additionalHeaders)
                request.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
    }
        // ============ 会话 Cookie 管理（登录类接口使用） ============

    /// <summary>为指定站点设置 Cookie（逐条写入，单条损坏不影响其余；可指定父域以便跨子域接口共享，如 QQ 传 .qq.com、酷狗传 .kugou.com）</summary>
    public void SetCookies(Uri uri, string cookieHeader, string? cookieDomain = null)
    {
        if (uri == null || string.IsNullOrWhiteSpace(cookieHeader)) return;
        try
        {
            var parts = cookieHeader.Split(';', StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part) || !part.Contains('=')) continue;
                var eq = part.IndexOf('=');
                var name = part[..eq].Trim();
                var value = part[(eq + 1)..].Trim();
                if (string.IsNullOrEmpty(name)) continue;
                try
                {
                    var cookie = new Cookie(name, value) { Path = "/" };
                    if (!string.IsNullOrEmpty(cookieDomain))
                        cookie.Domain = cookieDomain;
                    _cookieContainer.Add(uri, cookie);
                }
                catch { /* 单条 Cookie 损坏时跳过，不影响其余 */ }
            }
        }
        catch (Exception)
        {
            _logger.LogWarning("设置 Cookie 失败: {Endpoint}, ErrorCode=CookieWriteFailed", GetSafeEndpoint(uri.AbsoluteUri));
        }
    }

    /// <summary>读取指定站点的 Cookie 字符串（如需要校验登录态时）</summary>
    public string GetCookies(Uri uri)
    {
        if (uri == null) return string.Empty;
        try
        {
            return _cookieContainer.GetCookieHeader(uri);
        }
        catch (Exception)
        {
            _logger.LogWarning("读取 Cookie 失败: {Endpoint}, ErrorCode=CookieReadFailed", GetSafeEndpoint(uri.AbsoluteUri));
            return string.Empty;
        }
    }

    /// <summary>清空会话 Cookie（退出登录时调用）</summary>
    /// <summary>清空会话 Cookie（退出登录时调用，覆盖已知平台域名）</summary>
    public void ClearCookies()
    {
        try
        {
            foreach (var host in new[] { "https://music.163.com/", "https://y.qq.com/", "https://kugou.com/", "https://www.kuwo.cn/" })
            {
                var cookies = _cookieContainer.GetCookies(new Uri(host));
                foreach (Cookie c in cookies) c.Expired = true;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Cookie cleanup failed: {ErrorType}",
                exception.GetType().Name);
        }
    }
    /// <summary>设置超时时间</summary>
    public void SetTimeout(TimeSpan timeout)
    {
        _httpClient.Timeout = timeout;
    }

    /// <summary>
    /// 设置 User-Agent
    /// </summary>
    public void SetUserAgent(string userAgent)
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    private static string GetSafeEndpoint(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            return $"{uri.Scheme}://{uri.IdnHost}{port}";
        }

        return "relative";
    }

    /// <summary>
    /// 閲婃斁璧勬簮
    /// </summary>
    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

