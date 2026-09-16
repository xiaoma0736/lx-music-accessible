using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Services;

/// <summary>
/// 争渡读屏语音后端（ZDSRAPI）：
/// 动态加载“C:\Program Files (x86)\zdsr\zdsr\ZDSRAPI_x64.dll”（注册表 Uninstall 键定位安装目录兜底），
/// 使用独立通道朗读，避免打断争渡读屏主通道。
/// 接口详见 ZDSRAPI.txt：InitTTS / Speak / GetSpeakState / StopSpeak。
/// </summary>
public sealed class ZdsrSpeechService : ITtsService, IDisposable
{
    private const string DefaultDllPath = @"C:\Program Files (x86)\zdsr\zdsr\ZDSRAPI_x64.dll";
    private const string ChannelName = "LXMusic";

    private readonly ILogger _logger;
    private IntPtr _lib;
    private bool _available;
    private double _rate = 1.0;

    private delegate int InitTtsDelegate(int type, [MarshalAs(UnmanagedType.LPWStr)] string? channelName, bool bKeyDownInterrupt);
    private delegate int SpeakDelegate([MarshalAs(UnmanagedType.LPWStr)] string text, bool bInterrupt);
    private delegate int GetSpeakStateDelegate();
    private delegate void StopSpeakDelegate();

    private InitTtsDelegate? _initTts;
    private SpeakDelegate? _speak;
    private GetSpeakStateDelegate? _getSpeakState;
    private StopSpeakDelegate? _stopSpeak;

    public ZdsrSpeechService(ILogger logger)
    {
        _logger = logger;
        try
        {
            var dllPath = LocateDll();
            if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
            {
                _logger.LogWarning("争渡读屏 DLL 未找到，ZDSR 后端不可用");
                return;
            }
            _lib = LoadLibrary(dllPath);
            _initTts = GetProc<InitTtsDelegate>("InitTTS");
            _speak = GetProc<SpeakDelegate>("Speak");
            _getSpeakState = GetProc<GetSpeakStateDelegate>("GetSpeakState");
            _stopSpeak = GetProc<StopSpeakDelegate>("StopSpeak");
            if (_initTts == null || _speak == null || _getSpeakState == null || _stopSpeak == null)
            {
                _logger.LogWarning("争渡读屏 DLL 缺少必要导出函数，ZDSR 后端不可用");
                return;
            }
            // 独立通道“LXMusic”；bKeyDownInterrupt=FALSE（按键不打断）
            if (_initTts(1, ChannelName, false) != 0)
            {
                _logger.LogWarning("争渡 InitTTS 失败（未运行或未授权），ZDSR 后端不可用");
                return;
            }
            _available = true;
            _logger.LogInformation("争渡读屏后端就绪");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "争渡读屏初始化失败，ZDSR 后端不可用");
        }
    }

    /// <summary>争渡后端是否可用（DLL 加载成功且 InitTTS 成功）</summary>
    public bool IsAvailable => _available;

    /// <summary>争渡语音后端显示名</summary>
    public string ActiveBackendName => "争渡";

    public async Task SpeakAsync(string text)
    {
        if (!_available || string.IsNullOrWhiteSpace(text)) return;
        try { _speak!(text, true); } // 打断当前朗读
        catch (Exception ex) { _logger.LogWarning(ex, "争渡朗读失败"); }
        await Task.CompletedTask;
    }

    public async Task SpeakQueuedAsync(string text)
    {
        if (!_available || string.IsNullOrWhiteSpace(text)) return;
        try { _speak!(text, false); } // 排队朗读
        catch (Exception ex) { _logger.LogWarning(ex, "争渡排队朗读失败"); }
        await Task.CompletedTask;
    }

    public void Stop()
    {
        if (!_available) return;
        try { _stopSpeak!(); }
        catch (Exception ex) { _logger.LogDebug("争渡停止朗读失败: ErrorType={ErrorType}", ex.GetType().Name); }
    }

    public void SetRate(double rate)
    {
        // 争渡读屏语速由读屏自身控制，本接口暂不支持；仅记录
        _rate = FrameworkCompatibility.Clamp(rate, 0.5, 1.5);
    }

    public void SetVoice(string voiceId)
    {
        // 争渡语音由读屏选择，本接口不支持切换
    }

    public List<VoiceInfo> GetAvailableVoices() => new();

    /// <summary>定位争渡安装目录下的 ZDSRAPI_x64.dll：默认路径优先，其次扫描注册表 Uninstall 键的 InstallLocation</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string fileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr library);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr library, string name);

    private static bool TryGetExport(IntPtr library, string name, out IntPtr address)
    {
        address = GetProcAddress(library, name);
        return address != IntPtr.Zero;
    }

    private static string LocateDll()
    {
        if (File.Exists(DefaultDllPath)) return DefaultDllPath;
        try
        {
            var roots = new[]
            {
                @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (var root in roots)
            {
                using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    root.StartsWith("HKLM") ? Microsoft.Win32.RegistryHive.LocalMachine : Microsoft.Win32.RegistryHive.CurrentUser,
                    Microsoft.Win32.RegistryView.Registry64);
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var sk = key.OpenSubKey(sub);
                    var loc = sk?.GetValue("InstallLocation") as string;
                    var display = sk?.GetValue("DisplayName") as string ?? "";
                    if (string.IsNullOrEmpty(loc)) continue;
                    var candidate = Path.Combine(loc, "ZDSRAPI_x64.dll");
                    if (File.Exists(candidate)) return candidate;
                    if (display.Contains("争渡") && Directory.Exists(loc))
                    {
                        var found = Directory.GetFiles(loc, "ZDSRAPI_x64.dll", SearchOption.AllDirectories).FirstOrDefault();
                        if (found != null) return found;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"定位争渡 DLL 失败: {ex.Message}");
        }
        return string.Empty;
    }

    private T? GetProc<T>(string name) where T : Delegate
    {
        if (_lib == IntPtr.Zero) return null;
        return TryGetExport(_lib, name, out var ptr) ? Marshal.GetDelegateForFunctionPointer<T>(ptr) : null;
    }

    public void Dispose()
    {
        if (_lib != IntPtr.Zero)
        {
            FreeLibrary(_lib);
            _lib = IntPtr.Zero;
        }
    }
}
