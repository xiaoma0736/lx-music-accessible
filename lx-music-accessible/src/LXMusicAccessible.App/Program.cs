using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LXMusicAccessible.Core;
using LXMusicAccessible.Data;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace LXMusicAccessible.App;

internal static class Program
{
    private const string MutexName = @"Local\LXMusicAccessible_V12_SingleInstance";
    private const string ActivateEventName = @"Local\LXMusicAccessible_V12_Activate";
    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static WinFormsShell? _shell;
    private static bool _exiting;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        ConfigureLogging();
        try
        {
            if (args.Any(arg => string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                RunUninstall();
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            _mutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                SignalExisting();
                return 0;
            }
            StartActivationListener();

            await using var runtime = ApplicationHost.Create();
            var outcome = runtime.StartAsync().GetAwaiter().GetResult();
            if (outcome == ApplicationStartOutcome.ExitRequested) return 0;
            _shell = runtime.Services!.GetRequiredService<WinFormsShell>();
            Application.Run(_shell);
            runtime.StopAsync().GetAwaiter().GetResult();
            return 0;
        }
        finally
        {
            _exiting = true;
            try { _activate?.Set(); } catch (Exception ex) { Debug.WriteLine(ex.GetType().Name); }
            try { _activate?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex.GetType().Name); }
            try { _mutex?.ReleaseMutex(); } catch (Exception ex) { Debug.WriteLine(ex.GetType().Name); }
            try { _mutex?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex.GetType().Name); }
            Log.CloseAndFlush();
        }
    }

    private static void SignalExisting()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(ActivateEventName);
                handle.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(100); }
            catch { return; }
        }
    }

    private static void StartActivationListener()
    {
        try { _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName); }
        catch { _activate = null; return; }
        var thread = new Thread(() =>
        {
            while (!_exiting && _activate is not null)
            {
                try
                {
                    if (!_activate.WaitOne(500)) continue;
                }
                catch { return; }
                if (_exiting) return;
                var shell = _shell;
                if (shell is { IsDisposed: false })
                {
                    try { shell.BeginInvoke(() => shell.Visible = true); } catch (Exception ex) { Debug.WriteLine(ex.GetType().Name); }
                }
            }
        }) { IsBackground = true };
        thread.Start();
    }

    private static void ConfigureLogging()
    {
        try
        {
            var logDirectory = Path.Combine(AppDataPaths.ResolveDataDir(), "logs");
            Directory.CreateDirectory(logDirectory);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(Path.Combine(logDirectory, "app-.log"), rollingInterval: RollingInterval.Day, shared: true)
                .CreateLogger();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("File logging unavailable: " + ex.GetType().Name);
        }
    }

    private static void RunUninstall()
    {
        var running = Process.GetCurrentProcess().ProcessName;
        var others = Process.GetProcesses().Where(process =>
            string.Equals(process.ProcessName, running, StringComparison.OrdinalIgnoreCase) &&
            process.Id != Process.GetCurrentProcess().Id).ToList();
        if (others.Count > 0 && MessageBox.Show(
                "检测到程序正在运行，卸载前需要关闭它。是否立即退出并继续卸载？",
                "落雪音乐无障碍版 卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var process in others)
        {
            try { process.Kill(); process.WaitForExit(3000); } catch (Exception ex) { Debug.WriteLine(ex.GetType().Name); }
        }
        if (MessageBox.Show("确定要卸载落雪音乐无障碍版吗？", "落雪音乐无障碍版 卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!InstallDirectoryOwnership.TryRead(installDirectory, out var owned))
        {
            MessageBox.Show("无法证明当前目录属于本安装器，已保留文件和卸载记录。", "卸载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var deleteData = MessageBox.Show("是否同时删除所有用户数据？选择否将保留数据。", "卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        if (!ScheduleCleanup(owned))
        {
            MessageBox.Show("无法启动安全清理程序，安装目录和卸载记录已保留。", "卸载", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LxMusicAccessible", false); } catch (Exception ex) { Debug.WriteLine(ex.GetType().Name); }
        DeleteShortcuts("落雪音乐无障碍版*.lnk");
        if (deleteData)
        {
            AppDataPaths.CleanupOwnedData(failure => Log.Warning("App data cleanup failed: {Operation}", failure.Operation));
        }
        MessageBox.Show("关闭此提示后将完成安装文件清理。", "卸载", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static bool ScheduleCleanup(OwnedInstallDirectory owned)
    {
        var source = Path.Combine(owned.Path, "UninstallCleanup.ps1");
        if (!File.Exists(source)) return false;
        var script = Path.Combine(Path.GetTempPath(), $"lxmusic_uninstall_{Guid.NewGuid():N}.ps1");
        File.Copy(source, script, true);
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -InstallDirectory \"{owned.Path}\" -OwnershipId \"{owned.OwnershipId:D}\" -WaitForProcessId {Process.GetCurrentProcess().Id}",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return Process.Start(start) is not null;
    }

    private static void DeleteShortcuts(string pattern)
    {
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + "\\Programs", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.GetFiles(folder, pattern))
            {
                try { File.Delete(file); } catch (Exception ex) { Debug.WriteLine(ex.GetType().Name); }
            }
        }
    }
}
