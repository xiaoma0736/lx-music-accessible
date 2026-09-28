using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LXMusicAccessible.Core;

namespace LXMusicAccessible.Setup;

/// <summary>
/// 落雪音乐无障碍版 安装程序（自包含，目标机无需预装 .NET）。
/// 将 payload.zip（含运行时完整应用）作为内嵌资源，双击运行。
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            return RunInstaller(args);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(
                "InstallerStartupException=" + ex.GetType().FullName +
                "; MissingFile=" + (ex.FileName ?? "<null>") +
                "; FusionLog=" + (ex.FusionLog ?? "<null>"));
            return 101;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "InstallerStartupException=" + ex.GetType().FullName +
                "; Message=" + ex.Message +
                "; Stack=" + ex.StackTrace);
            return 101;
        }
    }

    [STAThread]
    static int RunInstaller(string[] args)
    {
        if (args.Length == 0 || (args.Length > 0 && args[0] == "--install-parent"))
        {
            var runtimeCheck = DotNetFrameworkRequirement.Evaluate();
            var diagnosticPath = WriteRuntimeDiagnostics(runtimeCheck);
            if (!runtimeCheck.IsInstalled)
            {
                var runtimeTitle = "Microsoft .NET Framework 4.8 required";
                var runtimeMessage =
                    "This app requires Microsoft .NET Framework 4.8 (x64)." + Environment.NewLine +
                    "Install the runtime, then run Setup again:" + Environment.NewLine +
                    DotNetFrameworkRequirement.DownloadUrl + Environment.NewLine + Environment.NewLine +
                    "Diagnostics: " + (diagnosticPath ?? "not written");
                if (args.Length > 0)
                {
                    Console.Error.WriteLine(runtimeMessage);
                    return 2;
                }

                MessageBox.Show(
                    runtimeMessage,
                    runtimeTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.DefaultDesktopOnly);
                return 2;
            }
        }
        if (args.Length == 2 && args[0] == "--install-parent")
            return RunNonInteractiveInstall(args[1]);
        if (args.Length == 3 && args[0] == "--uninstall-target" && Guid.TryParse(args[2], out var ownershipId))
        {
            var result = InstallDirectoryOwnership.Clean(args[1], ownershipId);
            Console.WriteLine($"accepted={result.Accepted}; failures={result.Failures.Count}; preserved={result.PreservedUnknownContent}");
            return result.Succeeded ? 0 : 1;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 应用数据包已内嵌为程序集资源，直接读取，无需外部 payload.zip。
        using var payloadStream = typeof(Program).Assembly.GetManifestResourceStream("LXMusicAccessible.Setup.payload.zip");
        if (payloadStream == null)
        {
            MessageBox.Show("安装程序数据缺失，程序可能损坏，请重新下载。",
                "落雪音乐无障碍版 安装", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var (parent, createDesktopShortcut) = ShowInstallDialog();
        if (string.IsNullOrEmpty(parent)) return 0; // 用户取消

        try
        {
            var target = InstallDirectoryOwnership.ResolveTargetDirectory(parent);
            var exe = Path.Combine(target, "落雪音乐无障碍版V3.0.exe");
            var previousExecutable = Path.Combine(target, "落雪音乐无障碍版V2.0.exe");
            var isUpgrade = File.Exists(exe) || File.Exists(previousExecutable);
            if (isUpgrade)
            {
                var r = MessageBox.Show("检测到该目录已安装过，是否覆盖安装？",
                    "落雪音乐无障碍版 安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return 0;
                if (File.Exists(exe)) CloseInstalledApplication(exe);
                if (File.Exists(previousExecutable)) CloseInstalledApplication(previousExecutable);
            }

            MessageBox.Show($"\u6b63\u5728\u5b89\u88c5\u5230\uff1a\n{target}\n\n\u89e3\u538b\u5b8c\u6210\u540e\u4f1a\u81ea\u52a8\u521b\u5efa\u5f00\u59cb\u83dc\u5355\u5feb\u6377\u65b9\u5f0f\uff0c\u8bf7\u7a0d\u5019...",
                "\u843d\u96ea\u97f3\u4e50\u65e0\u969c\u788d\u7248 \u5b89\u88c5", MessageBoxButtons.OK, MessageBoxIcon.Information);

            if (isUpgrade)
            {
                WaitForFileAvailable(exe);
                WaitForFileAvailable(previousExecutable);
            }
            InstallDirectoryOwnership.InstallFromZip(payloadStream, parent);
            if (File.Exists(previousExecutable)) DeletePreviousExecutable(previousExecutable);

            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            // 旧版“卸载说明”快捷方式（仅打开目录）替换为真正的卸载入口
            DeleteShortcutsByName(startMenu, "落雪音乐无障碍版 卸载说明");
            CreateShortcut(Path.Combine(startMenu, "落雪音乐无障碍版.lnk"), exe, target);
            CreateShortcut(Path.Combine(startMenu, "卸载落雪音乐无障碍版.lnk"), exe, target,
                arguments: "/uninstall",
                description: "卸载落雪音乐无障碍版（可勾选是否删除全部用户数据）",
                iconLocation: exe);

            if (createDesktopShortcut)
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktop, "落雪音乐无障碍版.lnk"), exe, target);
            }

            RegisterUninstallEntry(exe, target);

            var done = $"安装完成！\n\n已创建开始菜单快捷方式{(createDesktopShortcut ? "和桌面快捷方式" : "")}\n是否立即启动？";
            var run = MessageBox.Show(done, "落雪音乐无障碍版 安装",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (run == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, UseShellExecute = true });
            }
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"安装失败：{ex.Message}", "落雪音乐无障碍版 安装",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>Removes the superseded V2 executable files after payloads and shortcuts point at V3.</summary>
    private static void DeletePreviousExecutable(string previousExecutable)
    {
        var baseName = Path.Combine(
            Path.GetDirectoryName(previousExecutable) ?? string.Empty,
            Path.GetFileNameWithoutExtension(previousExecutable));
        foreach (var path in new[] { $"{baseName}.exe", $"{baseName}.exe.config", $"{baseName}.pdb" })
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Previous executable cleanup failed: Path={path}; ErrorType={ex.GetType().Name}");
            }
        }
    }

    private static void CloseInstalledApplication(string installedExe)
    {
        var targetPath = Path.GetFullPath(installedExe);
        var processes = Process.GetProcesses().Where(process =>
        {
            try
            {
                var module = process.MainModule;
                return !string.IsNullOrEmpty(module?.FileName)
                    && string.Equals(Path.GetFullPath(module.FileName), targetPath,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // System, elevated, or exited processes can deny module access.
                return false;
            }
        }).ToArray();
        if (processes.Length == 0) return;

        MessageBox.Show("\u68c0\u6d4b\u5230\u843d\u96ea\u97f3\u4e50\u6b63\u5728\u8fd0\u884c\uff0c\u5c06\u5148\u5173\u95ed\u5b83\u3002", "\u843d\u96ea\u97f3\u4e50\u65e0\u969c\u788d\u7248 \u5b89\u88c5",
            MessageBoxButtons.OK, MessageBoxIcon.Information);

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited && process.CloseMainWindow() && process.WaitForExit(5000)) continue;
                if (!process.HasExited)
                {
                    process.Kill();
                    if (!process.WaitForExit(5000))
                        throw new IOException("\u65e7\u7684\u843d\u96ea\u97f3\u4e50\u8fdb\u7a0b\u672a\u80fd\u5728 5 \u79d2\u5185\u9000\u51fa\u3002");
                }
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void WaitForFileAvailable(string path)
    {
        if (!File.Exists(path)) return;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static string? WriteRuntimeDiagnostics(DotNetFrameworkCheck check)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LXMusicAccessible",
                "setup");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "net48-diagnostics.txt");
            File.WriteAllText(
                path,
                string.Format(
                    "{0} installed={1}; required={2}; release={3}; {4}{5}",
                    DateTimeOffset.UtcNow.ToString("O"),
                    check.IsInstalled,
                    check.RequiredRelease,
                    check.Release?.ToString() ?? "missing",
                    check.Diagnostic,
                    Environment.NewLine));
            return path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Runtime diagnostics write failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static int RunNonInteractiveInstall(string parent)
    {
        try
        {
            using var payload = typeof(Program).Assembly.GetManifestResourceStream("LXMusicAccessible.Setup.payload.zip")
                ?? throw new InvalidDataException("Installer payload is missing.");
            var installed = InstallDirectoryOwnership.InstallFromZip(payload, parent);
            Console.WriteLine($"path={installed.Path}; ownership={installed.OwnershipId}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>选择父目录；实际安装位置始终是该目录下的专属应用子目录。</summary>
    private static (string Dir, bool CreateDesktopShortcut) ShowInstallDialog()
    {
        using var form = new Form
        {
            Text = "落雪音乐无障碍版 安装",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(590, 185)
        };

        // 焦点顺序必须写出来，不能靠添加顺序：WinForms 对相同或省略的 TabIndex 按 z 序排队，
        // 而读屏用户只能靠 Tab 的方向来预期下一个控件。
        var lblDir = new Label { Text = "安装位置：", Location = new Point(18, 28), AutoSize = true, TabStop = false };
        var txtDir = new TextBox { Text = InstallDirectoryOwnership.DefaultParentDirectory, Location = new Point(105, 24), Width = 335, TabIndex = 1 };
        var btnBrowse = new Button { Text = "浏览...", Location = new Point(448, 22), Width = 102, TabIndex = 2 };
        var chkDesktop = new CheckBox { Text = "创建桌面快捷方式", Location = new Point(105, 62), AutoSize = true, Checked = true, TabIndex = 3 };
        var lblNote = new Label
        {
            Text = $"程序将在所选位置创建 {InstallDirectoryOwnership.ApplicationDirectoryName} 专属子目录。",
            Location = new Point(105, 92), AutoSize = true, TabStop = false
        };
        var btnOk = new Button { Text = "安装", DialogResult = DialogResult.OK, Location = new Point(366, 130), Width = 96, Height = 30, TabIndex = 4 };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(472, 130), Width = 86, Height = 30, TabIndex = 5 };

        btnBrowse.Click += (s, e) =>
        {
            try
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "请选择安装位置（程序会创建专属子目录）",
                };
                if (Directory.Exists(txtDir.Text.Trim())) dlg.SelectedPath = txtDir.Text.Trim();
                if (dlg.ShowDialog() == DialogResult.OK)
                    txtDir.Text = dlg.SelectedPath;
            }
            catch
            {
                // 部分精简环境 FolderBrowserDialog 不可用，忽略浏览
            }
        };

        form.Controls.AddRange(new Control[] { lblDir, txtDir, btnBrowse, chkDesktop, lblNote, btnOk, btnCancel });
        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        if (form.ShowDialog() != DialogResult.OK) return ("", false);

        var dir = txtDir.Text.Trim().TrimEnd('\\', '/').Trim();
        if (string.IsNullOrEmpty(dir)) dir = InstallDirectoryOwnership.DefaultParentDirectory;
        return (dir, chkDesktop.Checked);
    }

    private static void CreateShortcut(string lnkPath, string target, string workDir,
        string arguments = "", string description = "落雪音乐无障碍版", string? iconLocation = null)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            var shellObject = Activator.CreateInstance(shellType);
            if (shellObject == null) return;
            dynamic shell = shellObject;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = workDir;
            shortcut.Arguments = arguments;
            shortcut.Description = description;
            if (!string.IsNullOrEmpty(iconLocation))
                shortcut.IconLocation = iconLocation;
            shortcut.Save();
        }
        catch
        {
            // 快捷方式创建失败不阻塞安装（可直接到安装目录运行 exe）
        }
    }

    /// <summary>删除目录中指定名称（不含扩展名）的 .lnk 快捷方式，避免残留无效入口</summary>
    private static void DeleteShortcutsByName(string folder, string nameWithoutExt)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.GetFiles(folder, "*.lnk"))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(f), nameWithoutExt, StringComparison.Ordinal))
                {
                    try { File.Delete(f); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Shortcut cleanup failed: Operation=delete; ErrorType={ex.GetType().Name}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Shortcut cleanup failed: Operation=enumerate; ErrorType={ex.GetType().Name}");
        }
    }

    /// <summary>写入“程序和功能”(HKCU) 卸载注册表项，卸载入口指向应用 exe 的 /uninstall 模式</summary>
    private static void RegisterUninstallEntry(string exePath, string installDir)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LxMusicAccessible");
            if (key == null) return;
            key.SetValue("DisplayName", "落雪音乐无障碍版");
            key.SetValue("DisplayVersion", "3.0.0");
            key.SetValue("Publisher", "LX Music Accessible Team");
            key.SetValue("DisplayIcon", exePath);
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{exePath}\" /uninstall");
            key.SetValue("NoModify", 1);
            key.SetValue("NoRepair", 1);
        }
        catch
        {
            // 注册表写入失败不阻塞安装
        }
    }
}
