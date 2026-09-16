using System;
using System.Runtime.InteropServices;

namespace LXMusicAccessible.Services.Prism;

/// <summary>
/// Prism 原生库 P/Invoke 声明（https://github.com/ethindp/prism，MPL-2.0）。
/// 注意：随包分发的 prism.dll 对应旧版 PrismConfig（PRISM_CONFIG_VERSION=2），
/// 初始化时必须传 version=2（传 3 会返回 NULL）。
/// </summary>
internal static class PrismNative
{
    private const string DllName = "prism.dll";
    private const CallingConvention CallConv = CallingConvention.Cdecl;

    /// <summary>PrismConfig 结构（对齐 x64：version 后自动补 7 字节）</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PrismConfig
    {
        public byte version;
        public IntPtr registry;                // NULL = 默认注册表
        public IntPtr availabilityCallback;
        public IntPtr availabilityUserdata;
        public uint availabilityPollIntervalMs;
        public uint availabilityDebounceSamples;
        public uint availabilityBackoffMaxMs;
        public byte availabilityAutoPowerManage;
    }

    internal const byte PrismConfigVersion = 2;

    internal const int PrismOk = 0;
    internal const int PrismErrorNotImplemented = 3;
    internal const int PrismErrorAlreadyInitialized = 15;
    internal const int PrismErrorBackendNotAvailable = 16;

    // PrismBackendFeature 位
    internal const ulong FeatureSupportsSpeak = 1UL << 2;
    internal const ulong FeatureSupportsStop = 1UL << 7;
    internal const ulong FeatureSupportsSetRate = 1UL << 12;
    internal const ulong FeatureSupportsCountVoices = 1UL << 17;
    internal const ulong FeatureSupportsGetVoiceName = 1UL << 18;
    internal const ulong FeatureSupportsGetVoiceLanguage = 1UL << 19;
    internal const ulong FeatureSupportsSetVoice = 1UL << 21;

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern IntPtr prism_init(ref PrismConfig cfg);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern void prism_shutdown(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern IntPtr prism_registry_acquire_best(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern void prism_backend_free(IntPtr backend);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern IntPtr prism_backend_name(IntPtr backend);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern ulong prism_backend_get_features(IntPtr backend);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern int prism_backend_initialize(IntPtr backend);

    /// <summary>speak(text, interrupt)。C bool 为 1 字节，需显式 I1。</summary>
    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern int prism_backend_speak(IntPtr backend, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, [MarshalAs(UnmanagedType.I1)] bool interrupt);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern int prism_backend_stop(IntPtr backend);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern int prism_backend_set_rate(IntPtr backend, float rate);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern int prism_backend_set_voice(IntPtr backend, ulong voiceId);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern int prism_backend_count_voices(IntPtr backend, out ulong outCount);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern int prism_backend_get_voice_name(IntPtr backend, ulong voiceId, out IntPtr outName);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern int prism_backend_get_voice_language(IntPtr backend, ulong voiceId, out IntPtr outLanguage);

    [DllImport(DllName, CallingConvention = CallConv)]
    internal static extern IntPtr prism_error_string(int error);

    /// <summary>UTF-8 指针 → string（返回空字符串兜底）</summary>
    internal static string PtrToUtf8(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return "";
        var length = 0;
        while (Marshal.ReadByte(ptr, length) != 0) length++;
        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>错误码 → 可读文本</summary>
    internal static string ErrorString(int error)
    {
        try
        {
            var s = PtrToUtf8(prism_error_string(error));
            return string.IsNullOrEmpty(s) ? $"Error {error}" : s;
        }
        catch
        {
            return $"Error {error}";
        }
    }

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
}
