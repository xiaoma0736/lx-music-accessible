using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Services;

/// <summary>
/// 保益悦听三方朗读后端（byctrl）：P/Invoke byctrl-x64.dll（随包输出到程序根目录）。
/// 使用独立通道（useSlave=true, slaveName="LXMusic"）朗读，避免打断保益读屏主通道。
/// 接口详见：http://open.bjbyhd.com:2603/service?name=保益三方朗读服务
/// </summary>
public sealed class BaoyiSpeechService : ITtsService, IDisposable
{
    private const string SlaveName = "LXMusic";

    private readonly ILogger _logger;
    private bool _available;
    private bool _initialized;
    private double _rate = 1.0;

    // 错误码
    private const int ErrSuccess = 0;
    private const int ErrUnavailable = 3;

    [DllImport("byctrl-x64.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int BoyCtrlInitialize(string? logPath);

    [DllImport("byctrl-x64.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int BoyCtrlSpeak2(string text, bool useSlave, string? slaveName, bool append, bool allowBreak, IntPtr onCompletion);

    [DllImport("byctrl-x64.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int BoyCtrlStopSpeaking2(bool useSlave, string? slaveName);

    [DllImport("byctrl-x64.dll", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BoyCtrlIsReaderRunning();

    [DllImport("byctrl-x64.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void BoyCtrlUninitialize();

    public BaoyiSpeechService(ILogger logger)
    {
        _logger = logger;
        try
        {
            var err = BoyCtrlInitialize(null);
            if (err != ErrSuccess)
            {
                _logger.LogWarning("保益 BoyCtrlInitialize 失败（{Err}），保益后端不可用", err);
                return;
            }
            _initialized = true;
            if (!BoyCtrlIsReaderRunning())
            {
                _logger.LogWarning("保益悦听未运行，保益后端不可用");
                return;
            }
            _available = true;
            _logger.LogInformation("保益悦听后端就绪");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保益初始化失败（DLL 缺失或未安装），保益后端不可用");
        }
    }

    /// <summary>保益后端是否可用（DLL 加载成功、初始化成功且保益悦听正在运行）</summary>
    public bool IsAvailable => _available;

    /// <summary>保益语音后端显示名</summary>
    public string ActiveBackendName => "保益";

    public async Task SpeakAsync(string text)
    {
        if (!_available || string.IsNullOrWhiteSpace(text)) return;
        try { BoyCtrlSpeak2(text, true, SlaveName, false, true, IntPtr.Zero); } // 打断朗读
        catch (Exception ex) { _logger.LogWarning(ex, "保益朗读失败"); }
        await Task.CompletedTask;
    }

    public async Task SpeakQueuedAsync(string text)
    {
        if (!_available || string.IsNullOrWhiteSpace(text)) return;
        try { BoyCtrlSpeak2(text, true, SlaveName, true, true, IntPtr.Zero); } // 排队朗读
        catch (Exception ex) { _logger.LogWarning(ex, "保益排队朗读失败"); }
        await Task.CompletedTask;
    }

    public void Stop()
    {
        if (!_available) return;
        try { BoyCtrlStopSpeaking2(true, SlaveName); } catch { }
    }

    public void SetRate(double rate)
    {
        // 保益语速由读屏自身控制，本接口不支持切换；仅记录
        _rate = Math.Clamp(rate, 0.5, 1.5);
    }

    public void SetVoice(string voiceId)
    {
        // 保益语音由读屏选择，本接口不支持切换
    }

    public List<VoiceInfo> GetAvailableVoices() => new();

    public void Dispose()
    {
        if (_initialized)
        {
            try { BoyCtrlUninitialize(); } catch { }
            _initialized = false;
        }
    }
}