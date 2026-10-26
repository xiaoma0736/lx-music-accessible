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
    private static int Main(string[] args)
    {
        ConfigureLogging();
        // WebView2 requires an STA apartment. Do not mark an async Main instead: the compiler-generated
        // real entry point loses [STAThread], the process starts as MTA, and WebView2 environment
        // creation fails with RPC_E_CHANGED_MODE (0x80010106) on every platform login page.
        Log.Information("Application startup thread apartment: {ApartmentState}", Thread.CurrentThread.GetApartmentState());
        int exitCode;
        try
        {
            // Startup and shutdown pump their own message queues while incomplete, so by the time the
            // async pipeline yields a completed task it is safe for this STA entry point to collect it.
            exitCode = RunCoreAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            // A slow native shutdown or a long-lived modal warning must not escape as a Windows
            // unhandled-exception crash after the main loop has already ended.
            Log.Error(exception, "Fatal application pipeline failure");
            exitCode = 1;
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

        // Native audio threads must not keep the process (and bass.dll) alive after the
        // managed host has been released. Cleanup and logging are complete at this point.
        Environment.Exit(exitCode);
        return exitCode;
    }

    private static async Task<int> RunCoreAsync(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            RunUninstall();
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Startup awaits must resume on this thread: the main window and the global-hotkey message
        // window are only ever pumped by this thread's message loop. Without an installed WinForms
        // context the first await hops to an MTA thread-pool thread, where the message window is
        // created but never receives WM_HOTKEY and the main window never responds.
        WindowsFormsSynchronizationContext.AutoInstall = true;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExisting();
            return 0;
        }
        StartActivationListener();
        AppDataPaths.MigrateLegacyNestedDataDirectory();

        var runtime = ApplicationHost.Create();
        try
        {
            var outcome = await PumpAsync(runtime.StartAsync());
            if (outcome == ApplicationStartOutcome.ExitRequested) return 0;
            _shell = runtime.Services!.GetRequiredService<WinFormsShell>();
            Application.Run(_shell);
            await PumpAsync(runtime.StopAsync());
            return 0;
        }
        finally
        {
            await PumpDisposalAsync(runtime);
        }
    }

    /// <summary>
    /// Waits for startup or shutdown work whose continuations are posted back to this thread. The
    /// thread has to keep pumping while it waits: <see cref="Application.Run(Form)"/> does not own the
    /// message loop yet, so a blocked thread would wait forever for a continuation it never dispatches.
    /// The bound keeps a stuck pipeline from hanging the process indefinitely.
    /// </summary>
    private static async Task<T> PumpAsync<T>(Task<T> task)
    {
        PumpWhileIncomplete(task);
        return await task;
    }

    private static async Task PumpAsync(Task task)
    {
        PumpWhileIncomplete(task);
        await task;
    }

    /// <summary>
    /// Pumps the message queue by hand until the awaited pipeline finishes. The loop is synchronous on
    /// purpose: the work it waits for posts continuations back to this thread, and only
    /// <see cref="Application.DoEvents"/> can dispatch them before the main message loop starts.
    /// </summary>
    private static void PumpWhileIncomplete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("启动或退出流程超时。");
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Releases the host through the same pumped wait. A disposal failure is diagnostic only: the
    /// shutdown pipeline already reported its own result and the process is about to exit.
    /// </summary>
    private static async Task PumpDisposalAsync(ApplicationRuntime runtime)
    {
        try { await PumpAsync(runtime.DisposeAsync().AsTask()); }
        catch (Exception ex) { Debug.WriteLine("Runtime disposal failed: " + ex.GetType().Name); }
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
                    try { shell.BeginInvoke(shell.RestoreForSecondLaunch); } catch (Exception ex) { Debug.WriteLine(ex.GetType().Name); }
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
