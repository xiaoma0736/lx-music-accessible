using System.IO;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class Issue75ShutdownProcessTests
{
    private static string Source(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && current.Name != "src") current = current.Parent!;
        return Path.Combine([current!.FullName, .. parts]);
    }

    [Fact]
    public void App_force_exits_after_cleanup_so_native_audio_threads_cannot_keep_process_alive()
    {
        var source = File.ReadAllText(Source("LXMusicAccessible.App", "Program.cs"));
        Assert.Contains("exitCode = await RunCoreAsync(args);", source);
        Assert.Contains("Environment.Exit(exitCode);", source);
        Assert.Contains("Log.CloseAndFlush();", source);
        Assert.True(source.IndexOf("Environment.Exit(exitCode);") > source.IndexOf("Log.CloseAndFlush();"));
    }

    [Fact]
    public void Installer_closes_and_waits_for_the_installed_process_before_extracting_payload()
    {
        var source = File.ReadAllText(Source("LXMusicAccessible.Setup", "Program.cs"));
        Assert.Contains("CloseInstalledApplication(exe);", source);
        Assert.Contains("WaitForFileAvailable(exe);", source);
        Assert.Contains("CloseMainWindow()", source);
        Assert.Contains("Kill()", source);
        Assert.True(source.IndexOf("CloseInstalledApplication(exe);") < source.IndexOf("InstallDirectoryOwnership.InstallFromZip(payloadStream, parent);"));
    }
}
