using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using LXMusicAccessible.Core;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class InstallDirectoryOwnershipTests
{
    [Fact]
    public void Selected_parent_resolves_to_a_dedicated_child_and_protected_parents_are_rejected()
    {
        using var fixture = new TemporaryDirectory();

        Assert.Equal(
            Path.Combine(fixture.Path, InstallDirectoryOwnership.ApplicationDirectoryName),
            InstallDirectoryOwnership.ResolveTargetDirectory(fixture.Path));
        Assert.Throws<InvalidOperationException>(() =>
            InstallDirectoryOwnership.ResolveTargetDirectory(Path.GetPathRoot(fixture.Path)!));
        Assert.Throws<InvalidOperationException>(() =>
            InstallDirectoryOwnership.ResolveTargetDirectory(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    [Fact]
    public void First_install_upgrade_and_repeated_uninstall_keep_parent_and_unknown_files()
    {
        using var fixture = new TemporaryDirectory();
        var parentSentinel = Path.Combine(fixture.Path, "parent-sentinel.txt");
        File.WriteAllText(parentSentinel, "keep");

        using var firstPayload = CreateZip(("app.exe", "v1"), ("Docs/readme.txt", "readme"));
        var first = InstallDirectoryOwnership.InstallFromZip(firstPayload, fixture.Path);
        var unknown = Path.Combine(first.Path, "personal.txt");
        File.WriteAllText(unknown, "keep");

        using var upgradePayload = CreateZip(("app.exe", "v2"), ("new.dll", "new"));
        var upgraded = InstallDirectoryOwnership.InstallFromZip(upgradePayload, fixture.Path);

        Assert.Equal(first.OwnershipId, upgraded.OwnershipId);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(first.Path, "app.exe")));
        Assert.False(File.Exists(Path.Combine(first.Path, "Docs", "readme.txt")));

        var preserved = InstallDirectoryOwnership.Clean(first.Path, first.OwnershipId);
        Assert.True(preserved.Succeeded);
        Assert.True(preserved.PreservedUnknownContent);
        Assert.True(File.Exists(unknown));
        Assert.True(File.Exists(parentSentinel));

        File.Delete(unknown);
        var completed = InstallDirectoryOwnership.Clean(first.Path, first.OwnershipId);
        var repeated = InstallDirectoryOwnership.Clean(first.Path, first.OwnershipId);

        Assert.True(completed.Succeeded);
        Assert.True(repeated.Succeeded);
        Assert.False(Directory.Exists(first.Path));
        Assert.True(File.Exists(parentSentinel));
    }

    [Fact]
    public void Legacy_portable_and_mismatched_installations_are_preserved()
    {
        using var fixture = new TemporaryDirectory();
        var target = Path.Combine(fixture.Path, InstallDirectoryOwnership.ApplicationDirectoryName);
        Directory.CreateDirectory(target);
        var portableSentinel = Path.Combine(target, "portable.exe");
        File.WriteAllText(portableSentinel, "legacy");

        using var payload = CreateZip(("app.exe", "owned"));
        Assert.Throws<InvalidOperationException>(() =>
            InstallDirectoryOwnership.InstallFromZip(payload, fixture.Path));
        var missing = InstallDirectoryOwnership.Clean(target, Guid.NewGuid());

        Assert.Equal(InstallDirectoryRefusal.MissingOwnership, missing.Refusal);
        Assert.True(File.Exists(portableSentinel));
        Assert.False(File.Exists(Path.Combine(target, InstallDirectoryOwnership.MarkerFileName)));
    }

    [Fact]
    public void Ownership_json_contract_remains_upgrade_compatible()
    {
        using var fixture = new TemporaryDirectory();
        using var payload = CreateZip(("app.exe", "owned"), ("sub/leaf.txt", "leaf"));
        var installed = InstallDirectoryOwnership.InstallFromZip(payload, fixture.Path);
        var markerText = File.ReadAllText(Path.Combine(installed.Path, InstallDirectoryOwnership.MarkerFileName));
        var manifestText = File.ReadAllText(Path.Combine(installed.Path, InstallDirectoryOwnership.ManifestFileName));

        Assert.Contains("\"ApplicationId\"", markerText);
        Assert.Contains("\"SchemaVersion\"", markerText);
        Assert.Contains("\"OwnershipId\"", markerText);
        Assert.Contains("\"DirectoryPath\"", markerText);
        Assert.Contains("\"Files\"", manifestText);
        Assert.Contains("\"RelativePath\"", manifestText);
        Assert.Contains("\"Sha256\"", manifestText);

        Assert.True(InstallDirectoryOwnership.Clean(installed.Path, installed.OwnershipId).Succeeded);
    }
    public void Forged_marker_and_wrong_identifier_are_rejected_before_file_deletion()
    {
        using var fixture = new TemporaryDirectory();
        using var payload = CreateZip(("app.exe", "owned"));
        var installed = InstallDirectoryOwnership.InstallFromZip(payload, fixture.Path);
        var application = Path.Combine(installed.Path, "app.exe");

        var mismatch = InstallDirectoryOwnership.Clean(installed.Path, Guid.NewGuid());
        Assert.Equal(InstallDirectoryRefusal.IdentifierMismatch, mismatch.Refusal);
        Assert.True(File.Exists(application));

        var marker = Path.Combine(installed.Path, InstallDirectoryOwnership.MarkerFileName);
        File.WriteAllText(marker, "{\"ApplicationId\":\"forged\"}");
        var forged = InstallDirectoryOwnership.Clean(installed.Path, installed.OwnershipId);

        Assert.Equal(InstallDirectoryRefusal.InvalidOwnership, forged.Refusal);
        Assert.True(File.Exists(application));
    }

    [Fact]
    public void Unsafe_payload_path_never_commits_install_ownership()
    {
        using var fixture = new TemporaryDirectory();
        using var payload = CreateZip(("app.exe", "partial"), ("../outside.txt", "escape"));
        var target = Path.Combine(fixture.Path, InstallDirectoryOwnership.ApplicationDirectoryName);

        Assert.Throws<InvalidDataException>(() =>
            InstallDirectoryOwnership.InstallFromZip(payload, fixture.Path));

        Assert.False(File.Exists(Path.Combine(fixture.Path, "outside.txt")));
        Assert.False(File.Exists(Path.Combine(target, InstallDirectoryOwnership.MarkerFileName)));

        using var reservedPayload = CreateZip((InstallDirectoryOwnership.MarkerFileName, "forged"));
        Assert.Throws<InvalidDataException>(() =>
            InstallDirectoryOwnership.InstallFromZip(reservedPayload, fixture.Path));
    }

    [Fact]
    public void Modified_manifest_file_is_preserved_instead_of_deleted()
    {
        using var fixture = new TemporaryDirectory();
        using var payload = CreateZip(("app.exe", "original"));
        var installed = InstallDirectoryOwnership.InstallFromZip(payload, fixture.Path);
        var application = Path.Combine(installed.Path, "app.exe");
        File.WriteAllText(application, "user replacement");

        var result = InstallDirectoryOwnership.Clean(installed.Path, installed.OwnershipId);

        Assert.True(result.Succeeded);
        Assert.True(result.PreservedUnknownContent);
        Assert.Equal("user replacement", File.ReadAllText(application));
    }

    [Fact]
    public void Cleanup_refuses_directory_links()
    {
        using var fixture = new TemporaryDirectory();
        using var payload = CreateZip(("app.exe", "owned"));
        var installed = InstallDirectoryOwnership.InstallFromZip(payload, fixture.Path);
        var link = Path.Combine(fixture.Path, "linked-install");
        try
        {
            Directory.CreateSymbolicLink(link, installed.Path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = InstallDirectoryOwnership.Clean(link, installed.OwnershipId);

        Assert.Equal(InstallDirectoryRefusal.ReparsePoint, result.Refusal);
        Assert.True(File.Exists(Path.Combine(installed.Path, "app.exe")));
        Directory.Delete(link, recursive: false);
    }

    [Fact]
    public void Packaged_cleanup_script_uses_the_same_manifest_and_preserves_unknown_content()
    {
        using var fixture = new TemporaryDirectory();
        var scriptSource = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LXMusicAccessible.App", "UninstallCleanup.ps1"));
        Assert.True(File.Exists(scriptSource));
        var scriptText = File.ReadAllText(scriptSource);
        using var payload = CreateZip(("app.exe", "owned"), ("UninstallCleanup.ps1", scriptText));
        var installed = InstallDirectoryOwnership.InstallFromZip(payload, fixture.Path);
        var unknown = Path.Combine(installed.Path, "unknown.txt");
        File.WriteAllText(unknown, "keep");
        var temporaryScript = Path.Combine(fixture.Path, "cleanup-copy.ps1");
        File.Copy(Path.Combine(installed.Path, "UninstallCleanup.ps1"), temporaryScript);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList =
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", temporaryScript,
                "-InstallDirectory", installed.Path,
                "-OwnershipId", installed.OwnershipId.ToString("D")
            },
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(30_000));

        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        Assert.True(File.Exists(unknown));
        Assert.False(File.Exists(Path.Combine(installed.Path, "app.exe")));
        Assert.False(File.Exists(temporaryScript));
    }

    private static MemoryStream CreateZip(params (string Path, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(file.Path).Open());
                writer.Write(file.Content);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() =>
            Path = Path.Combine(Path.GetTempPath(), "LXMusicAccessible.T10-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            var fullPath = System.IO.Path.GetFullPath(Path);
            var tempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(fullPath).StartsWith("LXMusicAccessible.T10-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Refusing unsafe test cleanup path: {fullPath}");
            }
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        }
    }
}
