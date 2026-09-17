using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class Net48MigrationTests
{
    public static TheoryData<string> BusinessProjects() => new()
    {
        "LXMusicAccessible.Core",
        "LXMusicAccessible.Models",
        "LXMusicAccessible.Data",
        "LXMusicAccessible.Audio",
        "LXMusicAccessible.Services",
        "LXMusicAccessible.TestConsole",
        "LXMusicAccessible.Tests"
    };

    [Theory]
    [MemberData(nameof(BusinessProjects))]
    public void Business_projects_target_net_framework_48_without_wpf(string projectName)
    {
        var path = FindProject(projectName);
        var text = File.ReadAllText(path);

        Assert.Contains("<TargetFramework>net48</TargetFramework>", text);
        Assert.DoesNotContain("<UseWPF>true</UseWPF>", text);
    }

    [Fact]
    public void Net48_core_binds_to_patched_system_text_json_assembly()
    {
        var corePath = Path.Combine(SourceDirectory(), "LXMusicAccessible.Core", "bin", "Release", "net48", "LXMusicAccessible.Core.dll");
        Assert.True(File.Exists(corePath), $"Build Core Release first: {corePath}");

        var references = Assembly.LoadFrom(corePath).GetReferencedAssemblies();
        var reference = Assert.Single(references, item => item.Name == "System.Text.Json");
        Assert.Equal(new Version(8, 0, 0, 6), reference.Version);
    }

    [Fact]
    public void Non_ui_layers_do_not_reference_wpf_or_sockets_http_handler()
    {
        var roots = new[]
        {
            "LXMusicAccessible.Core",
            "LXMusicAccessible.Models",
            "LXMusicAccessible.Data",
            "LXMusicAccessible.Audio",
            "LXMusicAccessible.Services"
        };
        var forbidden = new[] { "System.Windows.Input", "System.Windows.", "SocketsHttpHandler" };

        foreach (var rootName in roots)
        {
            var root = Path.Combine(SourceDirectory(), rootName);
            foreach (var source in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (source.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || source.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                foreach (var token in forbidden)
                {
                    Assert.True(
                        !File.ReadAllText(source).Contains(token, StringComparison.Ordinal),
                        $"{source} must not contain {token}.");
                }
            }
        }
    }

    private static string SourceDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && current.Name != "src") current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate src directory.");
    }

    private static string FindProject(string projectName) => Directory.EnumerateFiles(
        SourceDirectory(),
        $"{projectName}.csproj",
        SearchOption.AllDirectories).Single();
}
