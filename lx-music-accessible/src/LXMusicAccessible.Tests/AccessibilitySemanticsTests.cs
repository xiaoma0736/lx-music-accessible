using System.IO;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Xml.Linq;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using Xunit;

namespace LXMusicAccessible.Tests;

[Collection("WPF accessibility semantics")]
public sealed partial class AccessibilitySemanticsTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly IReadOnlyDictionary<string, string> StaticLabeledInputNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SearchSourceCombo"] = "搜索音源",
            ["SearchTypeComboBox"] = "搜索类型",
            ["CategorySourceCombo"] = "分类歌单音源",
            ["RankingSourceCombo"] = "排行榜音源",
            ["DefaultQualityCombo"] = "默认播放音质",
            ["CloseBehaviorCombo"] = "按 Alt+F4 时",
            ["ThirdPartyPlayerPathBox"] = "第三方播放器路径",
            ["DownloadPathBox"] = "下载目录",
            ["DownloadQualityCombo"] = "默认下载音质",
            ["DownloadNameOrderCombo"] = "歌曲命名顺序",
            ["DataDirBox"] = "数据目录",
            ["VoiceCombo"] = "朗读语音",
            ["SpeechRateCombo"] = "朗读语速",
            ["ChapterCombo"] = "章节"
        };

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

    [Fact]
    public async Task Close_behavior_combo_exposes_task_name_separately_from_selection()
    {
        var mainWindow = LoadXamlDocuments()
            .Single(item => item.File.Name == "MainWindow.xaml")
            .Root;
        var comboMetadata = mainWindow.Descendants()
            .Single(element => ElementId(element) == "CloseBehaviorCombo");
        var labelMetadata = mainWindow.Descendants()
            .Single(element => Attribute(element, "Target")
                .Contains("ElementName=CloseBehaviorCombo", StringComparison.Ordinal));

        await RunOnStaThreadAsync(() =>
        {
            var selectedItem = new ComboBoxItem { Content = "关闭应用" };
            var combo = new ComboBox { Items = { selectedItem } };
            combo.SelectedIndex = 0;
            var label = new Label
            {
                Content = Attribute(labelMetadata, "Content"),
                Target = combo
            };
            var window = new System.Windows.Window
            {
                Content = new StackPanel { Children = { label, combo } },
                ShowInTaskbar = false
            };
            var explicitName = Attribute(comboMetadata, "AutomationProperties.Name");
            if (!string.IsNullOrWhiteSpace(explicitName))
                AutomationProperties.SetName(combo, explicitName);
            try
            {
                window.Show();
                window.UpdateLayout();
                var peer = new ComboBoxAutomationPeer(combo);
                var selection = Assert.IsAssignableFrom<ISelectionProvider>(
                    peer.GetPattern(PatternInterface.Selection));
                Assert.Single(selection.GetSelection());

                Assert.Equal("按 Alt+F4 时", peer.GetName());
                Assert.Equal("关闭应用", selectedItem.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Label_target_alone_does_not_supply_a_combo_peer_name()
    {
        await RunOnStaThreadAsync(() =>
        {
            var combo = new ComboBox();
            var label = new Label { Content = "按 Alt+F4 时", Target = combo };
            var panel = new StackPanel { Children = { label, combo } };
            var window = new System.Windows.Window
            {
                Content = panel,
                ShowInTaskbar = false
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.Empty(new ComboBoxAutomationPeer(combo).GetName());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task All_static_label_target_inputs_expose_task_name_through_real_wpf_peers()
    {
        var inputs = LoadXamlDocuments()
            .Where(item => item.File.Name != "TextInputDialog.xaml")
            .SelectMany(item => item.Root.Descendants()
                .Where(element => element.Name.LocalName == "Label")
                .Select(label => Regex.Match(Attribute(label, "Target"), @"ElementName=([^}\s]+)"))
                .Where(match => match.Success)
                .Select(match => item.Root.Descendants()
                    .Single(element => ElementId(element) == match.Groups[1].Value)))
            .ToArray();

        Assert.Equal(
            StaticLabeledInputNames.Keys.Order(StringComparer.Ordinal),
            inputs.Select(ElementId).Order(StringComparer.Ordinal));

        await RunOnStaThreadAsync(() =>
        {
            foreach (var input in inputs)
            {
                var id = ElementId(input);
                var expectedName = StaticLabeledInputNames[id];
                var declaredName = Attribute(input, "AutomationProperties.Name");
                Assert.Equal(expectedName, declaredName);

                FrameworkElement control;
                AutomationPeer peer;
                if (input.Name.LocalName == "ComboBox")
                {
                    var selectedItem = new ComboBoxItem { Content = "当前选项" };
                    var combo = new ComboBox { Items = { selectedItem }, SelectedIndex = 0 };
                    control = combo;
                    AutomationProperties.SetName(combo, declaredName);
                    peer = new ComboBoxAutomationPeer(combo);
                    Assert.IsAssignableFrom<ISelectionProvider>(
                        peer.GetPattern(PatternInterface.Selection));
                    Assert.Equal("当前选项", selectedItem.Content);
                }
                else
                {
                    var textBox = new TextBox { Text = "当前值" };
                    control = textBox;
                    AutomationProperties.SetName(textBox, declaredName);
                    peer = new TextBoxAutomationPeer(textBox);
                    var value = Assert.IsAssignableFrom<IValueProvider>(
                        peer.GetPattern(PatternInterface.Value));
                    Assert.Equal("当前值", value.Value);
                }

                Assert.Equal(expectedName, peer.GetName());
                Assert.Empty(AutomationProperties.GetHelpText(control));
            }
        });
    }

    [Fact]
    public async Task Dynamic_text_input_dialog_exposes_runtime_task_names_separately_from_values()
    {
        await RunOnStaThreadAsync(() =>
        {
            var dialog = new TextInputDialog(
                "从网络导入音源",
                "音源URL：",
                "https://example.invalid/source.js",
                "音源名称（可留空）：",
                "示例音源");
            try
            {
                AssertTextInputSemantics(
                    dialog.InputBox1,
                    "音源URL",
                    "https://example.invalid/source.js");
                AssertTextInputSemantics(
                    dialog.InputBox2,
                    "音源名称（可留空）",
                    "示例音源");
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public async Task Focused_source_item_name_includes_required_state_on_the_real_list_peer_path()
    {
        await RunOnStaThreadAsync(() =>
        {
            var sources = new[]
            {
                new MusicSource
                {
                    Name = "启用音源", Version = "1.0", Type = SourceType.Custom,
                    IsEnabled = true, TrustState = ScriptTrustState.UserApproved
                },
                new MusicSource
                {
                    Name = "禁用音源", Version = "1.0", Type = SourceType.Custom,
                    IsEnabled = false, TrustState = ScriptTrustState.UserApproved
                },
                new MusicSource
                {
                    Name = "默认音源", Version = "1.0", Type = SourceType.Custom,
                    IsEnabled = true, IsDefault = true,
                    TrustState = ScriptTrustState.UserApproved
                },
                new MusicSource
                {
                    Name = "待信任音源", Version = "1.0", Type = SourceType.Custom,
                    IsEnabled = false, TrustState = ScriptTrustState.Untrusted
                }
            };
            var expectedNames = new[]
            {
                "启用音源，版本 1.0，自定义，已启用",
                "禁用音源，版本 1.0，自定义，已禁用",
                "默认音源，版本 1.0，自定义，默认",
                "待信任音源，版本 1.0，自定义，已暂存，未信任"
            };
            var list = new SingleReadListBox
            {
                ItemContainerStyle = LoadListItemAutomationStyle("SourceList")
            };
            foreach (var source in sources) list.Items.Add(source);
            list.SelectedIndex = 0;
            var window = new System.Windows.Window
            {
                Content = list,
                ShowInTaskbar = false
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var listPeer = FrameworkElementAutomationPeer.CreatePeerForElement(list);
                var itemPeers = listPeer.GetChildren()!;
                Assert.Equal(expectedNames.Length, itemPeers.Count);
                for (var index = 0; index < expectedNames.Length; index++)
                {
                    var container = Assert.IsType<ListBoxItem>(
                        list.ItemContainerGenerator.ContainerFromIndex(index));
                    Assert.True(container.Focus());
                    Assert.Empty(itemPeers[index].GetChildren() ?? []);
                    Assert.Empty(itemPeers[index].GetItemStatus());
                    Assert.Equal(expectedNames[index], itemPeers[index].GetName());
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Hotkey_and_download_state_update_the_real_focused_item_name_channel()
    {
        await RunOnStaThreadAsync(() =>
        {
            var hotkey = new HotkeySettingItem
            {
                ActionName = "播放",
                ShortcutText = "F5",
                HasConflict = false
            };
            AssertFocusedItemNameUpdates(
                "HotkeyListBox",
                hotkey,
                "播放：F5，可用",
                () => hotkey.HasConflict = true,
                "播放：F5，全局注册冲突");

            var download = new DownloadTask
            {
                Music = new MusicInfo
                {
                    Artist = "歌手",
                    Title = "歌曲",
                    Source = "netease"
                },
                ActualQuality = "mp3"
            };
            AssertFocusedItemNameUpdates(
                "DownloadTaskList",
                download,
                "歌手 - 歌曲 [mp3]，等待中",
                () =>
                {
                    download.Status = DownloadTaskStatus.Downloading;
                    download.Progress = 25;
                },
                "歌手 - 歌曲 [mp3]，25%，下载中");
        });
    }

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
    public void Ordinary_controls_expose_no_help_text_or_tooltip_descriptions()
    {
        var actual = LoadXamlElements()
            .Where(item => IsHelpTextSource(item.Element))
            .Select(item => $"{item.File.Name}|{ElementRole(item.Element)}|{ElementId(item.Element)}")
            .ToArray();

        Assert.Empty(actual);
    }

    [Fact]
    public void Help_text_diagnostic_does_not_reject_state_or_live_region_channels()
    {
        Assert.True(IsHelpTextSource(XElement.Parse(
            "<Button AutomationProperties.HelpText=\"按回车执行\" />")));
        Assert.True(IsHelpTextSource(XElement.Parse(
            "<ToolTip>操作说明</ToolTip>")));
        Assert.True(IsHelpTextSource(XElement.Parse(
            "<Setter Property=\"ToolTip\" Value=\"操作说明\" />")));
        Assert.False(IsHelpTextSource(XElement.Parse(
            "<Setter Property=\"AutomationProperties.ItemStatus\" Value=\"{Binding Status}\" />")));
        Assert.False(IsHelpTextSource(XElement.Parse(
            "<TextBlock AutomationProperties.LiveSetting=\"Polite\" />")));
    }

    [Fact]
    public void Focusable_controls_keep_native_or_explicit_names()
    {
        var issues = new List<string>();
        foreach (var (file, root) in LoadXamlDocuments())
        {
            foreach (var element in root.DescendantsAndSelf().Where(IsInteractive))
            {
                if (HasNameSource(file, element)) continue;
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
        var sourceLines = files
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (File: Path.GetFileName(file), Line: index + 1, Text: line))
                .Where(item => item.Text.Contains("AutomationProperties.SetName(", StringComparison.Ordinal)
                    || item.Text.Contains("AutomationProperties.HelpText", StringComparison.Ordinal)
                    || item.Text.Contains("AccessibleDescription", StringComparison.Ordinal)
                    || item.Text.Contains("GetHelpTextCore(", StringComparison.Ordinal)
                    || Regex.IsMatch(item.Text, @"\bToolTip\s*=")
                    || Regex.IsMatch(item.Text, @"\.SetToolTip\s*\(")))
            .ToArray();
        var descriptions = sourceLines
            .Where(item => !item.Text.Contains("AutomationProperties.SetName(", StringComparison.Ordinal))
            .Select(item => $"{item.File}:{item.Line}")
            .ToArray();
        var runtimeNames = sourceLines
            .Where(item => item.Text.Contains("AutomationProperties.SetName(", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(descriptions);
        Assert.Equal(2, runtimeNames.Length);
        Assert.All(runtimeNames, item => Assert.Equal("TextInputDialog.xaml.cs", item.File));
        Assert.Contains(runtimeNames, item => item.Text.Contains("InputBox1", StringComparison.Ordinal));
        Assert.Contains(runtimeNames, item => item.Text.Contains("InputBox2", StringComparison.Ordinal));
    }

    [Fact]
    public void Composite_list_items_keep_required_state_in_the_single_focus_name_channel()
    {
        var hotkey = new HotkeySettingItem
        {
            ActionName = "播放",
            ShortcutText = "F5",
            HasConflict = true
        };
        Assert.Equal("播放：F5，全局注册冲突", hotkey.AccessibleText);
        Assert.Equal("全局注册冲突", hotkey.ConflictStatus);

        var source = new MusicSource
        {
            Name = "示例音源",
            Version = "1.0",
            Type = SourceType.Custom,
            IsEnabled = true,
            TrustState = ScriptTrustState.UserApproved
        };
        Assert.Equal("示例音源，版本 1.0，自定义，已启用", source.AccessibleName);
        Assert.Equal("已启用", source.StatusText);

        var download = new DownloadTask
        {
            Music = new MusicInfo { Artist = "歌手", Title = "歌曲", Source = "netease" },
            ActualQuality = "mp3",
            Status = DownloadTaskStatus.Downloading,
            Progress = 25
        };
        Assert.Equal("歌手 - 歌曲 [mp3]，25%，下载中", download.AccessibleName);
        Assert.Equal("25%，下载中", download.AccessibleStatus);

        download.ActualQuality = "128k";
        download.QualityFallback = "请求 flac，已回退到 128k";
        download.Status = DownloadTaskStatus.Completed;
        Assert.Equal(
            "歌手 - 歌曲 [128k]，已完成，请求 flac，已回退到 128k",
            download.AccessibleName);
        Assert.Equal("已完成，请求 flac，已回退到 128k", download.AccessibleStatus);

        var xaml = string.Join('\n', LoadXamlDocuments().Select(item => item.Root.ToString()));
        Assert.DoesNotContain("Property=\"AutomationProperties.ItemStatus\" Value=\"{Binding ConflictStatus}\"", xaml);
        Assert.DoesNotContain("Property=\"AutomationProperties.ItemStatus\" Value=\"{Binding AccessibleStatus}\"", xaml);
        Assert.DoesNotContain("Property=\"AutomationProperties.ItemStatus\" Value=\"{Binding StatusText}\"", xaml);
    }

    private static bool ContainsRedundantRole(string role, string name)
    {
        if (!RoleTerms.TryGetValue(NormalizeRole(role), out var terms)) return false;
        if (name == "播放列表") return false;
        return terms.Any(term => name.Contains(term, StringComparison.Ordinal));
    }

    private static bool IsHelpTextSource(XElement element) =>
        !string.IsNullOrWhiteSpace(Attribute(element, "AutomationProperties.HelpText"))
        || element.Name.LocalName == "ToolTip"
        || element.Attributes().Any(attribute =>
            attribute.Name.LocalName is "ToolTip" or "ToolTipService.ToolTip")
        || (element.Name.LocalName == "Setter"
            && Attribute(element, "Property") is
                "AutomationProperties.HelpText" or "ToolTip" or "ToolTipService.ToolTip");

    private static bool HasNameSource(FileInfo file, XElement element)
    {
        if (!string.IsNullOrWhiteSpace(Attribute(element, "AutomationProperties.Name"))) return true;
        if (!string.IsNullOrWhiteSpace(VisibleText(element))) return true;

        var id = ElementId(element);
        return file.Name == "TextInputDialog.xaml" && id is "InputBox1" or "InputBox2";
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

    private static Task RunOnStaThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void AssertTextInputSemantics(
        TextBox textBox,
        string expectedName,
        string expectedValue)
    {
        var peer = new TextBoxAutomationPeer(textBox);
        var value = Assert.IsAssignableFrom<IValueProvider>(
            peer.GetPattern(PatternInterface.Value));
        Assert.Equal(expectedName, peer.GetName());
        Assert.Equal(expectedValue, value.Value);
        Assert.Empty(AutomationProperties.GetHelpText(textBox));
    }

    private static Style LoadListItemAutomationStyle(string listId)
    {
        var list = LoadXamlDocuments()
            .SelectMany(item => item.Root.Descendants())
            .Single(element => ElementId(element) == listId);
        var style = new Style(typeof(ListBoxItem));
        foreach (var setter in list.Descendants()
                     .Where(element => element.Name.LocalName == "Setter"))
        {
            var property = Attribute(setter, "Property");
            if (property is not ("AutomationProperties.Name" or "AutomationProperties.ItemStatus"))
                continue;

            var binding = Regex.Match(Attribute(setter, "Value"), @"^\{Binding ([^,}]+)");
            Assert.True(binding.Success, $"{listId} {property} must use a direct binding.");
            var dependencyProperty = property == "AutomationProperties.Name"
                ? AutomationProperties.NameProperty
                : AutomationProperties.ItemStatusProperty;
            style.Setters.Add(new Setter(
                dependencyProperty,
                new Binding(binding.Groups[1].Value)));
        }
        return style;
    }

    private static void AssertFocusedItemNameUpdates(
        string listId,
        object item,
        string initialName,
        Action update,
        string updatedName)
    {
        var list = new SingleReadListBox
        {
            ItemContainerStyle = LoadListItemAutomationStyle(listId)
        };
        list.Items.Add(item);
        list.SelectedIndex = 0;
        var window = new System.Windows.Window
        {
            Content = list,
            ShowInTaskbar = false
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            var container = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.True(container.Focus());
            var listPeer = FrameworkElementAutomationPeer.CreatePeerForElement(list);
            var itemPeer = Assert.Single(listPeer.GetChildren()!);
            var nameChanges = 0;
            var descriptor = DependencyPropertyDescriptor.FromProperty(
                AutomationProperties.NameProperty,
                typeof(ListBoxItem));
            EventHandler changed = (_, _) => nameChanges++;
            descriptor!.AddValueChanged(container, changed);
            try
            {
                Assert.Empty(itemPeer.GetChildren() ?? []);
                Assert.Empty(itemPeer.GetItemStatus());
                Assert.Equal(initialName, itemPeer.GetName());

                update();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.DataBind);

                Assert.True(nameChanges > 0, $"{listId} did not notify its accessible Name change.");
                Assert.Equal(updatedName, AutomationProperties.GetName(container));
                Assert.Equal(updatedName, itemPeer.GetName());
            }
            finally
            {
                descriptor.RemoveValueChanged(container, changed);
            }
        }
        finally
        {
            window.Close();
        }
    }
}

[CollectionDefinition("WPF accessibility semantics", DisableParallelization = true)]
public sealed class WpfAccessibilitySemanticsCollection;
