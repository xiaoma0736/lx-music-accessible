using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Services;

/// <summary>
/// 语音后端自动路由服务：按“保益(byctrl) → 争渡(ZDSR) → Prism(读屏) → SAPI”顺序探测，
/// 取首个可用后端作为当前活动后端；用户在“朗读语音”下拉选择具体 SAPI5 语音时强制走 SAPI。
/// 注：保益悦听优先——其可用性判定（BoyCtrlIsReaderRunning）可靠且已实测可用；争渡 InitTTS
/// 在未真正就绪时也可能返回 0，故保益悦听运行时优先选用保益，仅未装/未运行保益时再回退争渡。
/// 另：NVDA 读屏下 Prism 的 NVDA 后端朗读实测不生效，检测到 NVDA 运行时自动回退 SAPI5 保底，
/// 保证语音提示始终可听。
/// </summary>
public sealed class RoutingTtsService : ITtsService, IDisposable
{
    private readonly ILogger<RoutingTtsService> _logger;
    private readonly ZdsrSpeechService _zdsr;
    private readonly BaoyiSpeechService _baoyi;
    private readonly PrismSpeechService _prism;
    private readonly TtsService _sapi;
    private ITtsService? _activeBackend;
    private bool _forceSapiVoice;
    private string _voiceId = "";
    private double _rate = 1.0;

    private long _lastNvdaCheckTick; // FrameworkCompatibility.CurrentTickMs, ms
    private bool _lastNvdaState;
    private const long NvdaRecheckIntervalMs = 10000;

    public RoutingTtsService(ILogger<RoutingTtsService> logger)
    {
        _logger = logger;
        _zdsr = new ZdsrSpeechService(logger);
        _baoyi = new BaoyiSpeechService(logger);
        _prism = new PrismSpeechService(logger);
        _sapi = new TtsService(logger);

        if (_baoyi.IsAvailable) _activeBackend = _baoyi;
        else if (_zdsr.IsAvailable) _activeBackend = _zdsr;
        else if (_prism.IsAvailable) _activeBackend = _prism;
        else _activeBackend = null;

        // NVDA 读屏下 prism 的 NVDA 后端朗读实测不生效：检测到 NVDA 运行时强制回退 SAPI5 保底，
        // 保证语音提示始终可听。保益/争渡为独立通道且可用，不受影响。
        if (NvdaSapiFallbackActive)
        {
            _logger.LogWarning("检测到 NVDA 读屏运行，语音后端回退 SAPI5（Prism 的 NVDA 后端朗读不生效）");
        }

        _logger.LogInformation("语音后端自动检测完成，活动后端：{Backend}", ActiveBackendName);
    }

    /// <summary>SAPI 兜底始终可用，故总为 true</summary>
    public bool IsAvailable => true;

    /// <summary>当前活动朗读后端显示名</summary>
    public string ActiveBackendName => _forceSapiVoice
        ? "SAPI（手动指定）"
        : NvdaSapiFallbackActive
            ? "SAPI（NVDA 读屏回退）"
            : _activeBackend switch
            {
                ZdsrSpeechService => _zdsr.ActiveBackendName,
                BaoyiSpeechService => _baoyi.ActiveBackendName,
                PrismSpeechService p => $"读屏（{p.BackendName}）",
                _ => "SAPI"
            };

    public async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_forceSapiVoice || _activeBackend == null || NvdaSapiFallbackActive)
        {
            await _sapi.SpeakAsync(text);
            return;
        }
        await _activeBackend.SpeakAsync(text);
    }

    public async Task SpeakQueuedAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_forceSapiVoice || _activeBackend == null || NvdaSapiFallbackActive)
        {
            await _sapi.SpeakQueuedAsync(text);
            return;
        }
        await _activeBackend.SpeakQueuedAsync(text);
    }

    public void Stop()
    {
        _sapi.Stop();
        _activeBackend?.Stop();
    }

    /// <summary>NVDA 运行且活动后端为 Prism（会选 NVDA 后端）时回退 SAPI5。</summary>
    private bool NvdaSapiFallbackActive => _activeBackend == _prism && IsNvdaActive();

    /// <summary>NVDA 运行状态（带 10 秒缓存，避免每次朗读都探测命名管道）</summary>
    private bool IsNvdaActive()
    {
        var now = FrameworkCompatibility.CurrentTickMs;
        if (now - _lastNvdaCheckTick >= NvdaRecheckIntervalMs)
        {
            _lastNvdaState = ScreenReaderDetector.IsNvdaRunning();
            _lastNvdaCheckTick = now;
        }
        return _lastNvdaState;
    }

    public void SetRate(double rate)
    {
        _rate = FrameworkCompatibility.Clamp(rate, 0.5, 1.5);
        _sapi.SetRate(_rate);
        _activeBackend?.SetRate(_rate);
    }

    public void SetVoice(string voiceId)
    {
        _voiceId = voiceId ?? "";
        if (_voiceId.StartsWith("sapi:", StringComparison.OrdinalIgnoreCase))
        {
            _sapi.SetVoice(_voiceId["sapi:".Length..]);
            _forceSapiVoice = true;
            _logger.LogInformation("已选择 SAPI5 语音，朗读由 SAPI 直接发声");
            return;
        }
        // 默认/自动检测：转发给活动后端（Prism 支持选语音；争渡/保益忽略）
        _forceSapiVoice = false;
        _activeBackend?.SetVoice(_voiceId);
    }

    public List<VoiceInfo> GetAvailableVoices()
    {
        // 仅暴露 SAPI5 语音供用户手动选择（选后强制走 SAPI）；自动检测后端不枚举语音
        var voices = new List<VoiceInfo>();
        foreach (var v in _sapi.GetAvailableVoices())
        {
            voices.Add(new VoiceInfo
            {
                Id = "sapi:" + v.Id,
                Name = v.Name,
                Culture = v.Culture,
                Gender = v.Gender,
                Age = v.Age
            });
        }
        return voices;
    }

    public void Dispose()
    {
        _zdsr.Dispose();
        _baoyi.Dispose();
        _prism.Dispose();
        _sapi.Dispose();
    }
}