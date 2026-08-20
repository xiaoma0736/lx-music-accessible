using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 增强版HTTP客户端 - 支持重试、超时、错误处理
/// </summary>
public class EnhancedMusicHttpClient : IDisposable
{
    private readonly ILogger<EnhancedMusicHttpClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Random _random = new Random();

    // 默认配置
    private const int DefaultTimeoutSeconds = 15;
    private const int MaxRetryAttempts = 3;
    private const int BaseRetryDelayMs = 1000;

    public EnhancedMusicHttpClient(ILogger<EnhancedMusicHttpClient> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds)
        };

        // 设置通用请求头
        _httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", 
            "application/json, text/javascript, */*; q=0.01");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", 
            "zh-CN,zh;q=0.9,en;q=0.8");

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// GET请求 - 支持重试
    /// </summary>
    public async Task<string> GetAsync(string url, Dictionary<string, string>? parameters = null, int maxRetries = MaxRetryAttempts)
    {
        var fullUrl = BuildUrl(url, parameters);
        return await ExecuteWithRetryAsync(async () => await _httpClient.GetStringAsync(fullUrl), maxRetries, fullUrl);
    }

    /// <summary>
    /// POST请求 - 支持重试
    /// </summary>
    public async Task<string> PostAsync(string url, Dictionary<string, string>? parameters = null, int maxRetries = MaxRetryAttempts)
    {
        var content = parameters != null ? new FormUrlEncodedContent(parameters) : null;
        return await ExecuteWithRetryAsync(async () => 
        {
            var response = await _httpClient.PostAsync(url, content);
            return await response.Content.ReadAsStringAsync();
        }, maxRetries, url);
    }

    /// <summary>
    /// 获取JSON数据
    /// </summary>
    public async Task<T?> GetJsonAsync<T>(string url, Dictionary<string, string>? parameters = null, int maxRetries = MaxRetryAttempts)
    {
        var response = await GetAsync(url, parameters, maxRetries);
        return JsonSerializer.Deserialize<T>(response, _jsonOptions);
    }

    /// <summary>
    /// POST JSON数据
    /// </summary>
    public async Task<T?> PostJsonAsync<T>(string url, Dictionary<string, string>? parameters = null, int maxRetries = MaxRetryAttempts)
    {
        var response = await PostAsync(url, parameters, maxRetries);
        return JsonSerializer.Deserialize<T>(response, _jsonOptions);
    }

    /// <summary>
    /// 带重试机制的执行器
    /// </summary>
    private async Task<string> ExecuteWithRetryAsync(Func<Task<string>> action, int maxRetries, string url)
    {
        Exception? lastException = null;
        
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogDebug("HTTP请求尝试 {Attempt}/{MaxRetries}: {Url}", attempt + 1, maxRetries + 1, url);
                var result = await action();
                
                if (attempt > 0)
                {
                    _logger.LogInformation("HTTP请求重试成功: {Url}, 尝试次数: {Attempt}", url, attempt + 1);
                }
                
                return result;
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "HTTP请求失败 (尝试 {Attempt}/{MaxRetries}): {Url}", attempt + 1, maxRetries + 1, url);
                
                if (attempt < maxRetries)
                {
                    var delay = CalculateBackoffDelay(attempt);
                    await Task.Delay(delay);
                }
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                lastException = ex;
                _logger.LogWarning("HTTP请求超时 (尝试 {Attempt}/{MaxRetries}): {Url}", attempt + 1, maxRetries + 1, url);
                
                if (attempt < maxRetries)
                {
                    var delay = CalculateBackoffDelay(attempt);
                    await Task.Delay(delay);
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogError(ex, "HTTP请求发生未预期的错误: {Url}", url);
                break; // 非网络错误，不重试
            }
        }

        throw new HttpRequestException($"HTTP请求失败，已重试 {maxRetries} 次: {url}", lastException);
    }

    /// <summary>
    /// 计算指数退避延迟时间
    /// </summary>
    private TimeSpan CalculateBackoffDelay(int attempt)
    {
        // 指数退避：2^attempt * base_delay + random_jitter
        var exponentialDelay = (long)(Math.Pow(2, attempt) * BaseRetryDelayMs);
        var jitter = _random.Next(0, 500); // 0-500ms随机抖动
        return TimeSpan.FromMilliseconds(exponentialDelay + jitter);
    }

    /// <summary>
    /// 构建完整URL
    /// </summary>
    private string BuildUrl(string baseUrl, Dictionary<string, string>? parameters)
    {
        if (parameters == null || parameters.Count == 0)
        {
            return baseUrl;
        }

        var queryString = string.Join("&", parameters.Select(kvp => 
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        
        return $"{baseUrl}?{queryString}";
    }

    /// <summary>
    /// 设置超时时间
    /// </summary>
    public void SetTimeout(TimeSpan timeout)
    {
        _httpClient.Timeout = timeout;
    }

    /// <summary>
    /// 设置请求头
    /// </summary>
    public void SetHeader(string name, string value)
    {
        _httpClient.DefaultRequestHeaders.Remove(name);
        _httpClient.DefaultRequestHeaders.Add(name, value);
    }

    /// <summary>
    /// 移除请求头
    /// </summary>
    public void RemoveHeader(string name)
    {
        _httpClient.DefaultRequestHeaders.Remove(name);
    }

    /// <summary>
    /// 获取JSON选项
    /// </summary>
    public JsonSerializerOptions GetJsonOptions() => _jsonOptions;

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
