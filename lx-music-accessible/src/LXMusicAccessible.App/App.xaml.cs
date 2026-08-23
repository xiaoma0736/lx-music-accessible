using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Windows;
using LXMusicAccessible.Services;
using LXMusicAccessible.Data;

namespace LXMusicAccessible.App;

public partial class App : Application
{
    private IHost? _host;
    private MainWindow? _mainWindow;

    // Single-instance support: a named mutex guards exclusivity; a named event lets
    // a later launch signal this instance to restore and foreground its window.
    private const string SingleInstanceMutexName = @"Local\LXMusicAccessible_V12_SingleInstance";
    private const string ActivateEventName = @"Local\LXMusicAccessible_V12_Activate";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateHandle;
    private Thread? _activateThread;
    private volatile bool _exiting;

    /// <summary>Second instance: notify the primary instance to restore its window, then exit.</summary>
    private void SignalExistingInstanceAndExit()
    {
        // The primary instance creates the named event slightly after the mutex, so
        // briefly retry opening it before giving up.
        for (int i = 0; i < 10; i++)
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
                evt.Set();
                break;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch
            {
                break;
            }
        }
        Shutdown();
    }

    /// <summary>Primary instance: listen for activation signals from later launches.</summary>
    private void StartActivateListener()
    {
        try
        {
            _activateHandle = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (Exception)
        {
            _activateHandle = null;
            return;
        }

        _activateThread = new Thread(() =>
        {
            while (!_exiting)
            {
                bool signaled = false;
                try
                {
                    signaled = _activateHandle != null && _activateHandle.WaitOne(500);
                }
                catch
                {
                    break;
                }
                if (_exiting) break;
                if (signaled && _mainWindow != null)
                {
                    try
                    {
                        Dispatcher.Invoke(() => _mainWindow.RestoreFromSecondInstance());
                    }
                    catch { }
                }
            }
        })
        { IsBackground = true };
        _activateThread.Start();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 卸载模式：被开始菜单“卸载落雪音乐无障碍版”快捷方式以 /uninstall 参数启动时执行卸载。
        // 必须在单实例检查之前处理，避免被“第二实例唤醒”逻辑吞掉。
        if (e.Args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            RunUninstall();
            Shutdown();
            return;
        }

        // Enforce a single running instance: a second launch signals the primary
        // instance to restore its window, then this process exits without a window.
        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                SignalExistingInstanceAndExit();
                return;
            }
            StartActivateListener();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Single-instance setup failed: {ex.Message}");
        }

        try
        {
            // 先创建主窗口，让用户能看到界面
            // Resolve the effective data directory before creating the window,
            // configuring logging, or opening the database.
            var dataDir = AppDataPaths.PrepareDataDir();
            Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

            _mainWindow = new MainWindow();
            _mainWindow.Show();

            // 配置日志
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(Path.Combine(dataDir, "logs", "lxmusic-.txt"), rollingInterval: RollingInterval.Day)
                .CreateLogger();

            Log.Information("应用程序启动");

            // Use the same service graph as the automated Host fixture.
            _host = ApplicationHost.CreateBuilder(dataDir).Build();

            await _host.StartAsync();
            Log.Information("服务已启动");

            // 初始化数据库
            var dbInitializer = _host.Services.GetRequiredService<DatabaseInitializer>();
            await dbInitializer.InitializeAsync();
            Log.Information("数据库初始化完成");

            // 异步初始化脚本源（不阻塞UI启动）
            var musicSrcSvc = _host.Services.GetRequiredService<IMusicSourceService>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await ((MusicSourceService)musicSrcSvc).InitializeScriptSourcesAsync();
                    Log.Information("脚本源初始化完成");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "脚本源初始化失败（不影响主功能）");
                }
            });

            // 初始化主窗口服务
            _mainWindow.InitializeServices(_host.Services);
            
            Log.Information("应用程序启动完成");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "应用程序启动失败");
            MessageBox.Show($"应用程序启动失败: {ex.Message}", "错误", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            
            Log.CloseAndFlush();
            Shutdown();
        }
    }

    /// <summary>卸载流程：确认 → 数据删除询问 → 清理快捷方式/注册表/用户数据 → 延迟删除安装目录</summary>
    private void RunUninstall()
    {
        try
        {
            var procName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            var running = System.Diagnostics.Process.GetProcesses()
                .Where(p =>
                {
                    try
                    {
                        return string.Equals(p.ProcessName, procName, StringComparison.OrdinalIgnoreCase)
                            && p.Id != System.Diagnostics.Process.GetCurrentProcess().Id;
                    }
                    catch { return false; }
                })
                .ToList();

            if (running.Count > 0)
            {
                var r = MessageBox.Show("检测到程序正在运行，卸载前需要关闭它。\n是否立即退出并继续卸载？",
                    "落雪音乐无障碍版 卸载", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (r != MessageBoxResult.Yes) return;
                foreach (var p in running)
                {
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(3000); } catch { }
                }
            }

            // 确认卸载
            var confirm = MessageBox.Show("确定要卸载 落雪音乐无障碍版 吗？",
                "落雪音乐无障碍版 卸载", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (confirm != MessageBoxResult.Yes) return;

            var delData = MessageBox.Show("是否同时删除所有用户数据（歌单、收藏、设置、音源、搜索历史等）？\n选择“否”将保留数据，便于以后重新安装后继续使用。",
                "落雪音乐无障碍版 卸载", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;

            DeleteShortcuts("落雪音乐无障碍版*.lnk");
            DeleteUninstallRegistry();

            if (delData && !DeleteUserData())
            {
                MessageBox.Show(
                    "部分或全部用户数据无法证明属于本应用，或清理时发生错误。为保护您的文件，这些数据已保留。",
                    "落雪音乐无障碍版 卸载",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            ScheduleInstallDirDeletion(installDir);

            MessageBox.Show("卸载完成。", "落雪音乐无障碍版 卸载", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"卸载过程中出现错误：{ex.Message}", "落雪音乐无障碍版 卸载",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private static void DeleteShortcuts(string pattern)
    {
        try
        {
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            foreach (var folder in new[] { startMenu, desktop })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var f in Directory.GetFiles(folder, pattern))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    private static void DeleteUninstallRegistry()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LxMusicAccessible", throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static bool DeleteUserData()
    {
        try
        {
            var logDirectory = Path.Combine(Path.GetTempPath(), "LXMusicAccessible");
            Directory.CreateDirectory(logDirectory);
            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(logDirectory, "uninstall-cleanup.log"), shared: true)
                .CreateLogger();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Cleanup logging unavailable: {ex.GetType().Name}");
        }

        IReadOnlyList<AppDataCleanupResult> results;
        try
        {
            results = AppDataPaths.CleanupOwnedData(failure =>
                Log.Warning(
                    "App data cleanup failed: Operation={Operation}, RelativePath={RelativePath}, ErrorType={ErrorType}",
                    failure.Operation,
                    failure.RelativePath,
                    failure.ErrorType));

            foreach (var result in results.Where(result => !result.Accepted))
            {
                Log.Warning("App data cleanup refused: Reason={Reason}", result.Refusal);
            }

            if (results.Any(result => result.PreservedUnknownContent))
                Log.Information("App data cleanup preserved unknown content");
        }
        finally
        {
            Log.CloseAndFlush();
        }

        return results.Count > 0
            && results.All(result => result.Succeeded && !result.PreservedUnknownContent);
    }
    private static void ScheduleInstallDirDeletion(string installDir)
    {
        try
        {
            var ps1 = Path.Combine(Path.GetTempPath(), $"lxmusic_uninstall_{Guid.NewGuid():N}.ps1");
            var escaped = installDir.Replace("'", "''");
            var script = $"Start-Sleep -Seconds 3\nRemove-Item -LiteralPath '{escaped}' -Recurse -Force -ErrorAction SilentlyContinue\nRemove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue\n";
            File.WriteAllText(ps1, script, new System.Text.UTF8Encoding(false));
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }
    protected override async void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);

        // Release the single-instance mutex and activation handle, and let the
        // activation-listener thread exit.
        _exiting = true;
        try { _activateHandle?.Set(); } catch { }
        try { _activateHandle?.Dispose(); } catch { }
        _activateHandle = null;
        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            try { _singleInstanceMutex.Dispose(); } catch { }
            _singleInstanceMutex = null;
        }

        Log.Information("应用程序正在关闭");

        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        Log.CloseAndFlush();
    }
}

