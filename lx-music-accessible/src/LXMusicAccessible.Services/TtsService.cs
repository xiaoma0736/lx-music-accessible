using Microsoft.Extensions.Logging;
using System.Speech.Synthesis;

namespace LXMusicAccessible.Services;

/// <summary>
/// TTS服务实现
/// </summary>
public class TtsService : ITtsService
{
    private readonly ILogger _logger;
    private readonly SpeechSynthesizer _synthesizer;
    private string _selectedVoice = string.Empty;
    private double _rate = 1.0;

    /// <summary>SAPI 兜底始终可用（构造成功即 true）</summary>
    public bool IsAvailable => true;

    /// <summary>当前活动朗读后端显示名</summary>
    public string ActiveBackendName => "SAPI";

    public TtsService(ILogger logger)
    {
        _logger = logger;
        _synthesizer = new SpeechSynthesizer();
        
        // 设置默认语音
        var voices = GetAvailableVoices();
        if (voices.Count > 0)
        {
            _selectedVoice = voices[0].Id;
            try
            {
                _synthesizer.SelectVoice(voices[0].Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "无法选择默认语音");
            }
        }
    }

    public Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("TTS文本为空");
            return Task.CompletedTask;
        }

        _logger.LogInformation("朗读文本: {Text}", text);
        
        try
        {
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.SpeakAsync(new Prompt(text));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "朗读失败");
        }

        return Task.CompletedTask;
    }

    public async Task SpeakQueuedAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // 排队模式：不调用 SpeakAsyncCancelAll，让新文本追加到当前朗读之后，不打断正在朗读的内容
        try
        {
            _synthesizer.SpeakAsync(new Prompt(text));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "排队朗读失败");
        }
        await Task.CompletedTask;
    }

    public void Stop()
    {
        _logger.LogInformation("停止朗读");
        _synthesizer.SpeakAsyncCancelAll();
    }

    public void SetRate(double rate)
    {
        if (rate < 0.1 || rate > 10.0)
        {
            _logger.LogWarning("语速超出范围: {Rate}", rate);
            return;
        }

        _rate = rate;
        _synthesizer.Rate = (int)((rate - 1.0) * 10);
        _logger.LogInformation("设置语速: {Rate}", rate);
    }

    public void SetVoice(string voiceId)
    {
        var voices = GetAvailableVoices();
        var voice = voices.FirstOrDefault(v => v.Id == voiceId);
        
        if (voice != null)
        {
            try
            {
                _synthesizer.SelectVoice(voice.Name);
                _selectedVoice = voiceId;
                _logger.LogInformation("设置语音: {Name}", voice.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置语音失败");
            }
        }
        else
        {
            _logger.LogWarning("找不到语音: {Id}", voiceId);
        }
    }

    public List<VoiceInfo> GetAvailableVoices()
    {
        var voices = new List<VoiceInfo>();
        
        try
        {
            foreach (var installedVoice in _synthesizer.GetInstalledVoices())
            {
                if (installedVoice.Enabled)
                {
                    var voiceInfo = installedVoice.VoiceInfo;
                    voices.Add(new VoiceInfo
                    {
                        Id = voiceInfo.Id,
                        Name = voiceInfo.Name,
                        Culture = voiceInfo.Culture.Name,
                        Gender = voiceInfo.Gender.ToString(),
                        Age = voiceInfo.Age.GetHashCode()
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取语音列表失败");
        }

        return voices;
    }

    public string GetSelectedVoice()
    {
        return _selectedVoice;
    }

    public double GetRate()
    {
        return _rate;
    }

    public void Dispose()
    {
        _synthesizer?.Dispose();
    }
}
