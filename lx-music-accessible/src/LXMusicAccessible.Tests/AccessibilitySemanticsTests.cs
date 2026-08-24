using System.IO;
using System.Xml.Linq;
using LXMusicAccessible.Models;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed partial class AccessibilitySemanticsTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly IReadOnlyDictionary<string, string[]> RoleTerms =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Button"] = ["按钮"],
            ["CheckBox"] = ["复选框"],
            ["ComboBox"] = ["组合框", "下拉框"],
            ["ContextMenu"] = ["菜单"],
            ["GroupBox"] = ["区域"],
            ["Image"] = ["图片", "图像", "大图"],
            ["Label"] = ["标签"],
            ["ListBox"] = ["列表"],
            ["Menu"] = ["菜单"],
            ["MenuItem"] = ["菜单", "菜单项"],
            ["RadioButton"] = ["单选按钮"],
            ["Slider"] = ["滑块"],
            ["StatusBar"] = ["状态栏"],
            ["TabControl"] = ["标签页", "选项卡"],
            ["TabItem"] = ["标签页", "选项卡"],
            ["TextBox"] = ["输入框", "文本框", "编辑框"],
            ["Window"] = ["窗口", "对话框"]
        };

    private static readonly IReadOnlySet<string> NecessaryHelpText = new HashSet<string>(StringComparer.Ordinal)
    {
        "MainWindow.xaml|SearchBox",
        "MainWindow.xaml|SearchResultsList",
        "MainWindow.xaml|OnlinePlaylistList",
        "MainWindow.xaml|FavoriteSongsList",
        "MainWindow.xaml|CategorySourceCombo",
        "MainWindow.xaml|CategoryList",
        "MainWindow.xaml|CategoryPlaylistsListBox",
        "MainWindow.xaml|CategorySongsList",
        "MainWindow.xaml|RankingSourceCombo",
        "MainWindow.xaml|RankingList",
        "MainWindow.xaml|RankingSongsList",
        "MainWindow.xaml|HotkeyListBox",
        "MainWindow.xaml|SourceList",
        "SongDetailDialog.xaml|DetailsList"
    };

    [Fact]
    public void Role_word_diagnostic_distinguishes_control_types_from_business_terms()
    {
        Assert.True(ContainsRedundantRole("Button", "搜索按钮"));
        Assert.True(ContainsRedundantRole("TabItem", "搜索标签页"));
        Assert.True(ContainsRedundantRole("TextBox", "搜索输入框"));
        Assert.False(ContainsRedundantRole("MenuItem", "播放列表"));
        Assert.False(ContainsRedundantRole("TabItem", "播放列表"));
        Assert.False(ContainsRedundantRole("TextBlock", "播放列表"));
    }

    [Fact]
    public void Wpf_accessible_names_are_short_native_and_role_free()
    {
        var issues = new List<string>();
        foreach (var (file, element) in LoadXamlElements())
        {
            var name = Attribute(element, "AutomationProperties.Name");
            if (string.IsNullOrWhiteSpace(name) || IsMarkupExtension(name)) continue;

            var visible = VisibleText(element);
            if (!string.IsNullOrWhiteSpace(visible))
                issues.Add($"{file.Name}|{ElementRole(element)}|{name}|duplicates visible {visible}");
            else if (ContainsRedundantRole(ElementRole(element), name))
                issues.Add($"{file.Name}|{ElementRole(element)}|{name}|contains control role");
        }

        Assert.Empty(issues);
    }

    [Fact]
    public void Help_text_is_limited_to_documented_task_instructions()
    {
        var actual = LoadXamlElements()
            .Where(item => !string.IsNullOrWhiteSpace(Attribute(item.Element, "AutomationProperties.HelpText")))
            .Select(item => $"{item.File.Name}|{ElementId(item.Element)}")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(actual.Except(NecessaryHelpText));
        Assert.Empty(NecessaryHelpText.Except(actual));
        Assert.DoesNotContain(LoadXamlElements(), item =>
            item.Element.Name.LocalName == "Setter"
            && Attribute(item.Element, "Property") == "AutomationProperties.HelpText");
    }

    [Fact]
    public void Focusable_controls_keep_native_or_explicit_names()
    {
        var issues = new List<string>();
        foreach (var (file, root) in LoadXamlDocuments())
        {
            foreach (var element in root.DescendantsAndSelf().Where(IsInteractive))
            {
                if (HasNameSource(root, element)) continue;
                issues.Add($"{file.Name}|{ElementRole(element)}|{ElementId(element)}");
            }
        }

        Assert.Empty(issues);
    }

    [Fact]
    public void Code_created_user_interfaces_use_native_content_without_descriptions()
    {
        var root = FindRepositoryRoot();
        var files = new[] { "LXMusicAccessible.App", "LXMusicAccessible.Setup" }
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories));
        var occurrences = files
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (File: Path.GetFileName(file), Line: index + 1, Text: line))
                .Where(item => item.Text.Contains("AutomationProperties.SetName(", StringComparison.Ordinal)
                    || item.Text.Contains("AutomationProperties.SetHelpText(", StringComparison.Ordinal)
                    || item.Text.Contains("AccessibleDescription", StringComparison.Ordinal)))
            .Select(item => $"{item.File}:{item.Line}")
            .ToArray();

        Assert.Empty(occurrences);
    }

    [Fact]
    public void Composite_list_items_separate_names_from_dynamic_state()
    {
        var hotkey = new HotkeySettingItem
        {
            ActionName = "播放",
            ShortcutText = "F5",
            HasConflict = true
        };
        Assert.Equal("播放：F5", hotkey.AccessibleText);
        Assert.Equal("全局注册冲突", hotkey.ConflictStatus);

        var source = new MusicSource
        {
            Name = "示例音源",
            Version = "1.0",
            Type = SourceType.Custom,
            IsEnabled = true,
            TrustState = ScriptTrustState.UserApproved
        };
        Assert.Equal("示例音源，版本 1.0，自定义", source.AccessibleName);
        Assert.Equal("已启用", source.StatusText);

        var download = new DownloadTask
        {
            Music = new MusicInfo { Artist = "歌手", Title = "歌曲", Source = "netease" },
            ActualQuality = "mp3",
            Status = DownloadTaskStatus.Downloading,
            Progress = 25
        };
        Assert.Equal("歌手 - 歌曲 [mp3]", download.AccessibleName);
        Assert.Equal("25%，下载中", download.AccessibleStatus);

        var mainXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "LXMusicAccessible.App", "MainWindow.xaml"));
        Assert.Contains("Property=\"AutomationProperties.ItemStatus\" Value=\"{Binding ConflictStatus}\"", mainXaml);
        Assert.Contains("Property=\"AutomationProperties.ItemStatus\" Value=\"{Binding AccessibleStatus}\"", mainXaml);
        Assert.Contains("Property=\"AutomationProperties.ItemStatus\" Value=\"{Binding StatusText}\"", mainXaml);
    }

    private static bool ContainsRedundantRole(string role, string name)
    {
        if (!RoleTerms.TryGetValue(NormalizeRole(role), out var terms)) return false;
        if (name == "播放列表") return false;
        return terms.Any(term => name.Contains(term, StringComparison.Ordinal));
    }

    private static bool HasNameSource(XElement root, XElement element)
    {
        if (!string.IsNullOrWhiteSpace(Attribute(element, "AutomationProperties.Name"))) return true;
        if (!string.IsNullOrWhiteSpace(VisibleText(element))) return true;

        var id = ElementId(element);
        return id.Length > 0 && root.Descendants().Any(label =>
            label.Name.LocalName == "Label"
            && Attribute(label, "Target").Contains($"ElementName={id}", StringComparison.Ordinal));
    }

    private static bool IsInteractive(XElement element)
    {
        var role = NormalizeRole(ElementRole(element));
        if (role is not ("Button" or "CheckBox" or "ComboBox" or "ListBox" or "MenuItem"
            or "RadioButton" or "Slider" or "TabItem" or "TextBox")) return false;
        if (role == "ListBox") return true;
        return Attribute(element, "IsTabStop") != "False";
    }

    private static string VisibleText(XElement element) =>
        new[] { "Content", "Header", "Text", "Title" }
            .Select(attribute => Attribute(element, attribute))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !IsMarkupExtension(value))
        ?? string.Empty;

    private static bool IsMarkupExtension(string value) => value.StartsWith('{');

    private static string Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value ?? string.Empty;

    private static string ElementId(XElement element) =>
        element.Attribute(Xaml + "Name")?.Value ?? string.Empty;

    private static string ElementRole(XElement element) => element.Name.LocalName;

    private static string NormalizeRole(string role) => role switch
    {
        "InvisibleToScreenReaderButton" => "Button",
        "SingleReadListBox" => "ListBox",
        _ => role
    };

    private static IEnumerable<(FileInfo File, XElement Element)> LoadXamlElements() =>
        LoadXamlDocuments().SelectMany(item =>
            item.Root.DescendantsAndSelf().Select(element => (item.File, element)));

    private static IEnumerable<(FileInfo File, XElement Root)> LoadXamlDocuments()
    {
        var directory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot(), "src", "LXMusicAccessible.App"));
        return directory.EnumerateFiles("*.xaml", SearchOption.AllDirectories)
            .OrderBy(file => file.FullName, StringComparer.Ordinal)
            .Select(file => (file, XDocument.Load(file.FullName).Root!));
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
