using System.Collections.Generic;
using System.IO;
using System.Linq;
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
