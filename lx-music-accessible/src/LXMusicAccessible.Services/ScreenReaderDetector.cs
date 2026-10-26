using System;
using System.Runtime.InteropServices;

namespace LXMusicAccessible.Services;

/// <summary>
/// 读屏运行状态检测辅助。
/// 目前用于检测 NVDA：通过官方控制器客户端命名管道探测，比进程名匹配更可靠。
/// </summary>
public static class ScreenReaderDetector
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPipeBusy = 231;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>NVDA 是否正在运行。打开其控制器管道：管道被占用（ERROR_PIPE_BUSY）即为运行中。</summary>
    public static bool IsNvdaRunning()
    {
        try
        {
            IntPtr handle = CreateFileW(
                @"\\.\pipe\nvdaControllerRemote",
                GenericRead | GenericWrite, 0,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == new IntPtr(-1))
            {
                // ERROR_PIPE_BUSY -> NVDA 在运行；FILE_NOT_FOUND -> 未运行
                return Marshal.GetLastWin32Error() == ErrorPipeBusy;
            }
            CloseHandle(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
