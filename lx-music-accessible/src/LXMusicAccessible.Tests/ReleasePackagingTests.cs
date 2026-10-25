using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ReleasePackagingTests
{
    [Fact]
    public void System_text_json_uses_patched_net462_asset_with_binding_redirect()
    {
        var propsPath = Path.Combine(RepositoryDirectory(), "Directory.Build.props");
        var targetsPath = Path.Combine(RepositoryDirectory(), "Directory.Build.targets");
        var appPath = Path.Combine(SourceDirectory(), "LXMusicAccessible.App", "LXMusicAccessible.App.csproj");
        var props = File.ReadAllText(propsPath);
        var targets = File.ReadAllText(targetsPath);
        var app = File.ReadAllText(appPath);

        Assert.Contains("<PackageReference Include=\"System.Text.Json\" Version=\"8.0.6\" />", props);
        Assert.DoesNotContain("ExcludeAssets=\"compile;runtime\"", props);
        Assert.DoesNotContain("lib\\netstandard2.0\\System.Text.Json.dll", targets);
        Assert.Contains("<AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>", app);
        Assert.Contains("<GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>", app);
        var releaseScript = File.ReadAllText(Path.Combine(RepositoryDirectory(), "build_release.ps1"));
        Assert.Contains("Assert-SystemTextJsonRuntime", releaseScript);
        Assert.Contains("8.0.0.6", releaseScript);
    }

    [Fact]
    public void Application_is_net48_x64_and_names_executable_version_3()
    {
        var path = Path.Combine(SourceDirectory(), "LXMusicAccessible.App", "LXMusicAccessible.App.csproj");
        var project = XDocument.Load(path).Root!;

        Assert.Equal("net48", project.Elements("PropertyGroup").Elements("TargetFramework").Single().Value);
        Assert.Equal("x64", project.Elements("PropertyGroup").Elements("PlatformTarget").Single().Value);
        Assert.Equal("win-x64", project.Elements("PropertyGroup").Elements("RuntimeIdentifier").Single().Value);
        Assert.Equal("落雪音乐无障碍版V3.0", project.Elements("PropertyGroup").Elements("AssemblyName").Single().Value);
        Assert.Equal("3.0.0", project.Elements("PropertyGroup").Elements("Version").Single().Value);
    }

    [Fact]
    public void Inno_installer_targets_x64_and_checks_net48()
    {
        var path = Path.Combine(RepositoryDirectory(), "packaging", "inno", "LXMusicAccessible.iss");
        var script = File.ReadAllText(path);

        Assert.Contains("AppVersion={#AppVersion}", script);
        Assert.Contains("PrivilegesRequired=lowest", script);
        Assert.Contains("ArchitecturesAllowed=x64compatible", script);
        Assert.Contains("ArchitecturesInstallIn64BitMode=x64compatible", script);
        Assert.Contains("DefaultDirName={autopf}\\LXMusicAccessible", script);
        Assert.Contains("OutputBaseFilename=LXMusicAccessible-Setup-{#AppVersion}-win-x64", script);
        Assert.Contains("ChineseSimplified.isl", script);
        Assert.Contains("RequiredNet48Release = 528040", script);
    }

    [Fact]
    public void Release_script_produces_only_the_x64_framework_zip_installer_and_manifest()
    {
        var scriptPath = Path.Combine(RepositoryDirectory(), "build_release.ps1");
        var script = File.ReadAllText(scriptPath);
        var normalizedScript = Regex.Replace(script, @"\s+", " ");

        Assert.Contains("app-framework-dependent", script);
        Assert.Contains("win-x64-framework-dependent.zip", script);
        Assert.DoesNotContain("app-self-contained", script);
        Assert.DoesNotContain("\"--self-contained\", \"true\"", script);
        Assert.DoesNotContain("\"-p:PublishSingleFile=true\"", script);
        Assert.DoesNotContain("win-x64-self-contained.zip", script);

        Assert.Contains("\"publish\", $appProject", script);
        Assert.Contains("& $innoCompiler", script);
        Assert.Contains("packaging\\inno\\LXMusicAccessible.iss", script);
        Assert.DoesNotContain("$setupProject", script);
        Assert.DoesNotContain("$setupExecutable", script);
        Assert.Contains("@($frameworkZip, $setupArtifact))", normalizedScript);
        Assert.DoesNotContain("@($frameworkZip, $setupArtifact, $manifestPath))", normalizedScript);
        Assert.Contains("528040", script);
    }

    [Fact]
    public void Release_script_smoke_tests_inno_install_upgrade_and_preserving_uninstall()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryDirectory(), "build_release.ps1"));

        Assert.Contains("/VERYSILENT", script);
        Assert.Contains("/DIR=$smokeTarget", script);
        Assert.Contains("Installed application executable is missing.", script);
        Assert.Contains("Upgrade smoke test failed.", script);
        Assert.Contains("unins000.exe", script);
        Assert.Contains("unknown-user-file.txt", script);
        Assert.Contains("Uninstall removed unknown, parent, or retained user data.", script);
        Assert.Contains("Complete install smoke test failed.", script);
        Assert.Contains("Complete uninstall left the application directory behind.", script);
    }

    [Fact]
    public void Release_zip_and_manifest_metadata_are_deterministic()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryDirectory(), "build_release.ps1"));
        Assert.Contains("$entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)", script, StringComparison.Ordinal);
        Assert.Contains("$generatedAtUtcRaw = & git log -1 --format=%cI -- $appProject", script, StringComparison.Ordinal);
        Assert.DoesNotContain("generatedAtUtc = [DateTime]::UtcNow.ToString", script, StringComparison.Ordinal);
    }
    private static string SourceDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && current.Name != "src") current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate src directory.");
    }

    private static string RepositoryDirectory()
    {
        var src = SourceDirectory();
        return Directory.GetParent(src)!.FullName;
    }
}