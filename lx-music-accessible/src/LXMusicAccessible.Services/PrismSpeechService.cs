using Microsoft.Extensions.Logging;
using LXMusicAccessible.Services.Prism;

namespace LXMusicAccessible.Services;

/// <summary>
/// Prism 语音朗读服务 — 通过 prism.dll 路由到当前可用的最高优先级后端
/// （NVDA/JAWS/OneCore/SAPI 等，读屏运行时自动优先读屏）。
/// Prism 初始化失败时自动回退到 SAPI（System.Speech）。
/// </summary>
public class PrismSpeechService : ITtsService, IDisposable
{
    private readonly ILogger _logger;
    private readonly TtsService _sapiFallback;

    private IntPtr _context;
    private IntPtr _backend;
    private ulong _features;
    private string _backendName = "";
    private bool _usePrism;
    private double _rate = 1.0;
    private string _voiceId = "";
    private bool _useSapiVoice;
    private const string PrismVoicePrefix = "prism:";
    private const string SapiVoicePrefix = "sapi:";

    public PrismSpeechService(ILogger logger)
    {
        _logger = logger;
        _sapiFallback = new TtsService(logger);
        try
        {
            InitializePrism();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prism 初始化异常，回退 SAPI");
            _usePrism = false;
        }
    }

    /// <summary>Prism 后端或 SAPI 兜底任一可用即为 true</summary>
    public bool IsAvailable => _usePrism || _sapiFallback != null;

    /// <summary>当前活动朗读后端显示名</summary>
    public string ActiveBackendName => _usePrism && !string.IsNullOrEmpty(_backendName) ? _backendName : "SAPI";

    private void InitializePrism()
    {
        var cfg = new PrismNative.PrismConfig { version = PrismNative.PrismConfigVersion };
        _context = PrismNative.prism_init(ref cfg);
        if (_context == IntPtr.Zero)
        {
            _logger.LogWarning("prism_init 返回空，回退 SAPI");
            _usePrism = false;
            return;
        }

        _backend = PrismNative.prism_registry_acquire_best(_context);
        if (_backend == IntPtr.Zero)
        {
            _logger.LogWarning("未获取到可用后端，回退 SAPI");
            _usePrism = false;
            return;
        }

        var err = PrismNative.prism_backend_initialize(_backend);
        if (err != PrismNative.PrismOk && err != PrismNative.PrismErrorAlreadyInitialized)
        {
            _logger.LogWarning("后端初始化失败（{Err}），回退 SAPI", PrismNative.ErrorString(err));
            _usePrism = false;
            return;
        }

        _backendName = PrismNative.PtrToUtf8(PrismNative.prism_backend_name(_backend));
        _features = PrismNative.prism_backend_get_features(_backend);
        _usePrism = true;
        _logger.LogInformation("Prism 就绪，后端: {Backend}", _backendName);
    }

    public async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // 选择了 SAPI5 语音时直接由 SAPI 发声，用户可明确听到所选语音
        if (!_useSapiVoice && _usePrism && _backend != IntPtr.Zero)
        {
            try
            {
                var err = PrismNative.prism_backend_speak(_backend, text, true);
                if (err == PrismNative.PrismOk || err == PrismNative.PrismErrorNotImplemented)
                    return;
                _logger.LogWarning("Prism 朗读失败（{Err}），回退 SAPI", PrismNative.ErrorString(err));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prism 朗读异常，回退 SAPI");
            }
        }
        await _sapiFallback.SpeakAsync(text);
    }

    public async Task SpeakQueuedAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // 排队朗读：Prism interrupt=false（追加到语音队列末尾，不打断当前朗读）；SAPI 走排队版
        if (!_useSapiVoice && _usePrism && _backend != IntPtr.Zero)
        {
            try
            {
                var err = PrismNative.prism_backend_speak(_backend, text, false);
                if (err == PrismNative.PrismOk || err == PrismNative.PrismErrorNotImplemented)
                    return;
                _logger.LogWarning("Prism 排队朗读失败（{Err}），回退 SAPI", PrismNative.ErrorString(err));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prism 排队朗读异常，回退 SAPI");
            }
        }
        await _sapiFallback.SpeakQueuedAsync(text);
    }

    public void Stop()
    {
        if (_usePrism && _backend != IntPtr.Zero)
        {
            try { PrismNative.prism_backend_stop(_backend); }
            catch (Exception ex) { _logger.LogDebug("Prism 停止朗读失败: ErrorType={ErrorType}", ex.GetType().Name); }
        }
        _sapiFallback.Stop();
    }

    /// <summary>
    /// 设置语速。界面语义：1.0 = 标准（0.5~1.5 范围）；
    /// Prism 语义：0.5 = 默认（0.0~1.0），映射 0.2~0.8。
    /// </summary>
    public void SetRate(double rate)
    {
        _rate = FrameworkCompatibility.Clamp(rate, 0.5, 1.5);

        if (_usePrism && _backend != IntPtr.Zero)
        {
            float prismRate = (float)FrameworkCompatibility.Clamp(0.5 + (_rate - 1.0) * 0.5, 0.2, 0.8);
            try
            {
                var err = PrismNative.prism_backend_set_rate(_backend, prismRate);
                if (err != PrismNative.PrismOk && err != PrismNative.PrismErrorNotImplemented)
                    _logger.LogWarning("Prism 设置语速失败（{Err}）", PrismNative.ErrorString(err));
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Prism 设置语速异常"); }
        }
        _sapiFallback.SetRate(_rate);
    }

    /// <summary>解析语音 ID：prism:/sapi: 前缀或旧版格式（纯数字=Prism 索引，其他=SAPI 令牌 ID）</summary>
    private static (string Kind, string Id) ParseVoiceId(string voiceId)
    {
        if (string.IsNullOrEmpty(voiceId)) return ("", "");
        if (voiceId.StartsWith(PrismVoicePrefix, StringComparison.OrdinalIgnoreCase))
            return ("prism", voiceId[PrismVoicePrefix.Length..]);
        if (voiceId.StartsWith(SapiVoicePrefix, StringComparison.OrdinalIgnoreCase))
            return ("sapi", voiceId[SapiVoicePrefix.Length..]);
        return ulong.TryParse(voiceId, out _) ? ("prism", voiceId) : ("sapi", voiceId);
    }

    public void SetVoice(string voiceId)
    {
        _voiceId = voiceId ?? "";
        var (kind, id) = ParseVoiceId(_voiceId);

        if (kind == "sapi")
        {
            _sapiFallback.SetVoice(id);
            _useSapiVoice = true;
            _logger.LogInformation("已选择 SAPI5 语音（{Id}），朗读由 SAPI 直接发声", id);
        }
        else if (kind == "prism" && _usePrism && _backend != IntPtr.Zero && ulong.TryParse(id, out var prismId))
        {
            try
            {
                var err = PrismNative.prism_backend_set_voice(_backend, prismId);
                if (err != PrismNative.PrismOk && err != PrismNative.PrismErrorNotImplemented)
                    _logger.LogWarning("Prism 设置语音失败（{Err}）", PrismNative.ErrorString(err));
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Prism 设置语音异常"); }
            _useSapiVoice = false;
        }
        else
        {
            // “默认语音”：保持 Prism 优先（读屏运行时由读屏接管）
            _useSapiVoice = false;
        }
    }

    public List<VoiceInfo> GetAvailableVoices()
    {
        var voices = new List<VoiceInfo>();

        // Prism/读屏语音：后端支持枚举时并入（不支持枚举时不再导致列表为空）
        if (_usePrism && _backend != IntPtr.Zero)
        {
            try
            {
                if (PrismNative.prism_backend_count_voices(_backend, out var count) == PrismNative.PrismOk && count > 0)
                {
                    for (ulong i = 0; i < count; i++)
                    {
                        string name = "";
                        string lang = "";
                        if (PrismNative.prism_backend_get_voice_name(_backend, i, out var namePtr) == PrismNative.PrismOk)
                            name = PrismNative.PtrToUtf8(namePtr);
                        if (PrismNative.prism_backend_get_voice_language(_backend, i, out var langPtr) == PrismNative.PrismOk)
                            lang = PrismNative.PtrToUtf8(langPtr);
                        voices.Add(new VoiceInfo
                        {
                            Id = $"{PrismVoicePrefix}{i}",
                            Name = $"[读屏] {(string.IsNullOrEmpty(name) ? $"语音 {i}" : name)}",
                            Culture = lang,
                            Gender = "",
                            Age = 0
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "枚举 Prism 语音失败");
            }
        }

        // SAPI5 语音始终并入列表（Prism 不可用或后端不支持枚举时也保证有语音可选）
        try
        {
            foreach (var v in _sapiFallback.GetAvailableVoices())
            {
                voices.Add(new VoiceInfo
                {
                    Id = $"{SapiVoicePrefix}{v.Id}",
                    Name = $"[SAPI5] {v.Name}",
                    Culture = v.Culture,
                    Gender = v.Gender,
                    Age = v.Age
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举 SAPI5 语音失败");
        }

        return voices;
    }

    /// <summary>当前后端名称（仅调试/日志用）</summary>
    public string BackendName => _usePrism ? _backendName : "SAPI";

    public void Dispose()
    {
        if (_backend != IntPtr.Zero)
        {
            try { PrismNative.prism_backend_free(_backend); }
            catch (Exception ex) { _logger.LogDebug("Prism 释放后端失败: ErrorType={ErrorType}", ex.GetType().Name); }
            _backend = IntPtr.Zero;
        }
        if (_context != IntPtr.Zero)
        {
            try { PrismNative.prism_shutdown(_context); }
            catch (Exception ex) { _logger.LogDebug("Prism 关闭上下文失败: ErrorType={ErrorType}", ex.GetType().Name); }
            _context = IntPtr.Zero;
        }
        _sapiFallback.Dispose();
    }
}
