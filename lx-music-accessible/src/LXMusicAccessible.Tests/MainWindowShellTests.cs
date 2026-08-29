using System.IO;
using System.Xml.Linq;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class MainWindowShellTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Main_window_contains_only_the_four_music_pages_and_playback_status_stays_outside()
    {
        var main = LoadMainWindow();
        var tabs = main.Descendants()
            .Single(element => Id(element) == "MainTabControl");

        Assert.Equal(
            ["搜索", "播放列表", "分类歌单", "排行榜"],
            tabs.Elements()
                .Where(element => element.Name.LocalName == "TabItem")
                .Select(element => (string?)element.Attribute("Header")));
        Assert.DoesNotContain(tabs.Descendants(), element => Id(element) is
            "HotkeyListBox" or "SourceList" or "DownloadTaskList" or "PlatformNeteaseBtn");
        Assert.DoesNotContain(tabs.Descendants(), element => Id(element) is "CurrentTitle" or "StatusText");
        Assert.Single(main.Descendants(), element => Id(element) == "CurrentTitle");
        Assert.Single(main.Descendants(), element => Id(element) == "StatusText");
    }

    [Fact]
    public void Tools_have_one_menu_path_in_the_required_order_and_only_four_page_shortcuts()
    {
        var root = LoadMainWindow();
        var tools = root.Descendants()
            .Single(element => (string?)element.Attribute("Header") == "工具(_T)");
        var expected = new[]
        {
            "下载管理(_D)", "导入个人歌单(_I)", "Separator", "音源管理(_M)", "设置(_S)"
        };

        Assert.Equal(expected, tools.Elements().Select(element =>
            element.Name.LocalName == "Separator"
                ? "Separator"
                : (string?)element.Attribute("Header")));
        foreach (var header in expected.Where(header => header != "Separator"))
            Assert.Single(root.Descendants(), element => (string?)element.Attribute("Header") == header);

        var code = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "LXMusicAccessible.App", "MainWindow.xaml.cs"));
        foreach (var number in Enumerable.Range(1, 4))
        {
            Assert.Contains($"Key.D{number} or Key.NumPad{number} => {number - 1}", code,
                StringComparison.Ordinal);
        }
        foreach (var number in Enumerable.Range(5, 4))
        {
            Assert.DoesNotContain($"Key.D{number}", code, StringComparison.Ordinal);
            Assert.DoesNotContain($"Key.NumPad{number}", code, StringComparison.Ordinal);
        }
        Assert.Contains("case 0: FocusControl(SearchBox)", code, StringComparison.Ordinal);
        Assert.Contains("case 1: FocusListContent(FavoriteSongsList)", code, StringComparison.Ordinal);
        Assert.Contains("case 2: FocusListContent(CategoryList)", code, StringComparison.Ordinal);
        Assert.Contains("case 3: FocusListContent(RankingList)", code, StringComparison.Ordinal);
    }

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
            Assert.Contains("Alt+1", document, StringComparison.Ordinal);
            Assert.Contains("Alt+4", document, StringComparison.Ordinal);
            Assert.Contains("Alt+5", document, StringComparison.Ordinal);
            Assert.Contains("Alt+8", document, StringComparison.Ordinal);
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

    private static XElement LoadMainWindow() => XDocument.Load(Path.Combine(
        FindRepositoryRoot(), "src", "LXMusicAccessible.App", "MainWindow.xaml")).Root!;

    private static string Id(XElement element) => element.Attribute(Xaml + "Name")?.Value ?? string.Empty;

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
