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

    [Fact]
    public void Application_is_net48_x64_and_preserves_upgrade_identity_with_version_3()
    {
        var path = Path.Combine(SourceDirectory(), "LXMusicAccessible.App", "LXMusicAccessible.App.csproj");
        var project = XDocument.Load(path).Root!;

        Assert.Equal("net48", project.Elements("PropertyGroup").Elements("TargetFramework").Single().Value);
        Assert.Equal("x64", project.Elements("PropertyGroup").Elements("PlatformTarget").Single().Value);
        Assert.Equal("win-x64", project.Elements("PropertyGroup").Elements("RuntimeIdentifier").Single().Value);
        Assert.Equal("落雪音乐无障碍版V2.0", project.Elements("PropertyGroup").Elements("AssemblyName").Single().Value);
        Assert.Equal("3.0.0", project.Elements("PropertyGroup").Elements("Version").Single().Value);
    }

    [Fact]
    public void Installer_is_x64_and_version_3()
    {
        var path = Path.Combine(SourceDirectory(), "LXMusicAccessible.Setup", "LXMusicAccessible.Setup.csproj");
        var project = XDocument.Load(path).Root!;

        Assert.Equal("net48", project.Elements("PropertyGroup").Elements("TargetFramework").Single().Value);
        Assert.Equal("x64", project.Elements("PropertyGroup").Elements("PlatformTarget").Single().Value);
        Assert.Equal("3.0.0", project.Elements("PropertyGroup").Elements("Version").Single().Value);
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
        Assert.DoesNotContain("\"publish\", $appProject", script);

        Assert.Contains("ClearScriptV8.win-x64.dll", script);
        Assert.Contains("ClearScript.V8.ICUData.dll", script);
        Assert.Contains("WebView2Loader.dll", script);
        Assert.Contains("Assert-NativeArchitecture", script);
        Assert.Contains("@($frameworkZip, $setupArtifact))", normalizedScript);
        Assert.DoesNotContain("@($frameworkZip, $setupArtifact, $manifestPath))", normalizedScript);
        Assert.Contains("528040", script);
    }

    [Fact]
    public void Setup_source_checks_net48_before_install_and_records_diagnostics()
    {
        var setup = File.ReadAllText(Path.Combine(SourceDirectory(), "LXMusicAccessible.Setup", "Program.cs"));
        Assert.Contains("DotNetFrameworkRequirement.Evaluate()", setup);
        Assert.Contains("WriteRuntimeDiagnostics", setup);
        Assert.Contains("DotNetFrameworkRequirement.DownloadUrl", setup);
        Assert.Contains("RunInstaller(args)", setup);
        Assert.Contains("catch (FileNotFoundException", setup);
        Assert.Contains("MissingFile", setup);
        var setupProjectPath = Path.Combine(SourceDirectory(), "LXMusicAccessible.Setup", "LXMusicAccessible.Setup.csproj");
        var setupProject = File.ReadAllText(setupProjectPath);
        Assert.Contains("LXMusicAccessible.Core\\DotNetFrameworkRequirement.cs", setupProject);
        Assert.Contains("LXMusicAccessible.Core\\InstallDirectoryOwnership.cs", setupProject);
        Assert.DoesNotContain("<ProjectReference Include=\"..\\LXMusicAccessible.Core\\LXMusicAccessible.Core.csproj\" />", setupProject);
    }
}
