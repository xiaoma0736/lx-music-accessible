using System.IO;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class MainWindowShellTests
{
    [Fact]
    public void User_documents_describe_the_final_navigation_and_tool_window_contract()
    {
        var root = FindRepositoryRoot();
        var documents = new[]
        {
            File.ReadAllText(Path.Combine(root, "README.md")),
            File.ReadAllText(Path.Combine(root, "KEYBOARD_NAVIGATION_GUIDE.md")),
            File.ReadAllText(Path.Combine(root, "src", "LXMusicAccessible.App", "Docs", "使用说明.md"))
        };

        foreach (var document in documents)
        {
            Assert.Contains("Ctrl+1", document, StringComparison.Ordinal);
            Assert.Contains("Ctrl+4", document, StringComparison.Ordinal);
            Assert.Contains("Ctrl+5", document, StringComparison.Ordinal);
            Assert.Contains("Ctrl+8", document, StringComparison.Ordinal);
            Assert.Contains("工具→下载管理", document, StringComparison.Ordinal);
            Assert.Contains("工具→导入个人歌单", document, StringComparison.Ordinal);
            Assert.Contains("工具→音源管理", document, StringComparison.Ordinal);
            Assert.Contains("工具→设置", document, StringComparison.Ordinal);
            Assert.Contains("常规、播放、歌词、下载、存储与缓存、无障碍、快捷键", document,
                StringComparison.Ordinal);
            Assert.Contains("Esc", document, StringComparison.Ordinal);
            Assert.Contains("Alt+F4", document, StringComparison.Ordinal);
        }

        var guide = documents[2];
        Assert.DoesNotContain("主窗口共 7 个页面", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("按 Alt+3 直接切换到“导入个人歌单”", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("“下载管理”页", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("“设置”页", guide, StringComparison.Ordinal);

        var releaseChecklist = File.ReadAllText(Path.Combine(
            root, "tasks", "comprehensive-remediation-spec-2026-08-23.md"));
        foreach (var expected in new[]
        {
            "真实 Alt 菜单", "Tab、Shift+Tab", "Esc", "Alt+F4",
            "100%、150% 和 200%", "Narrator", "NVDA", "JAWS"
        })
        {
            Assert.Contains(expected, releaseChecklist, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "verify.ps1"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
