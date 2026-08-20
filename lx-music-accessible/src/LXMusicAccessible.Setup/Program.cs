using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Windows.Forms;

namespace LXMusicAccessible.Setup;

/// <summary>
/// 落雪音乐无障碍版 安装程序（自包含，目标机无需预装 .NET）。
/// 将 payload.zip（含运行时完整应用）作为内嵌资源，双击运行。
/// </summary>
static class Program
{
    private const string DefaultInstallDir = @"D:\Program Files\LxMusic";

    [STAThread]
    static int Main()
    {
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

        var (target, createDesktopShortcut) = ShowInstallDialog();
        if (string.IsNullOrEmpty(target)) return 0; // 用户取消

        try
        {
            Directory.CreateDirectory(target);
            var exe = Path.Combine(target, "落雪音乐无障碍版V2.0.exe");
            if (File.Exists(exe))
            {
                var r = MessageBox.Show("检测到该目录已安装过，是否覆盖安装？",
                    "落雪音乐无障碍版 安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return 0;
            }

            MessageBox.Show($"正在安装到：\n{target}\n\n解压完成后会自动创建开始菜单快捷方式，请稍候...",
                "落雪音乐无障碍版 安装", MessageBoxButtons.OK, MessageBoxIcon.Information);

            using (var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(target, overwriteFiles: true);
            }

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

    /// <summary>显示安装目录选择对话框：默认 D:\Program Files\LxMusic，可浏览修改，可勾选创建桌面快捷方式</summary>
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

        var lblDir = new Label { Text = "安装目录：", Location = new Point(18, 28), AutoSize = true };
        var txtDir = new TextBox { Text = DefaultInstallDir, Location = new Point(105, 24), Width = 335 };
        var btnBrowse = new Button { Text = "浏览...", Location = new Point(448, 22), Width = 102 };
        var chkDesktop = new CheckBox { Text = "创建桌面快捷方式", Location = new Point(105, 62), AutoSize = true, Checked = true };
        var lblNote = new Label
        {
            Text = "程序将安装到所选目录，并创建开始菜单快捷方式。",
            Location = new Point(105, 92), AutoSize = true, ForeColor = SystemColors.GrayText
        };
        var btnOk = new Button { Text = "安装", DialogResult = DialogResult.OK, Location = new Point(366, 130), Width = 96, Height = 30 };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(472, 130), Width = 86, Height = 30 };

        btnBrowse.Click += (s, e) =>
        {
            try
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "请选择 落雪音乐无障碍版 的安装目录",
                    UseDescriptionForTitle = true
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
        if (string.IsNullOrEmpty(dir)) dir = DefaultInstallDir;
        return (dir, chkDesktop.Checked);
    }

    private static void CreateShortcut(string lnkPath, string target, string workDir,
        string arguments = "", string description = "落雪音乐无障碍版", string? iconLocation = null)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType);
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
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
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
            key.SetValue("DisplayVersion", "2.0.0");
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