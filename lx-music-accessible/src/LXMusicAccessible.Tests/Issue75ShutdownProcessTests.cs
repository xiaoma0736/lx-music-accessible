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
        Assert.Contains("exitCode = RunCoreAsync(args).GetAwaiter().GetResult();", source);
        Assert.Contains("Environment.Exit(exitCode);", source);
        Assert.Contains("Log.CloseAndFlush();", source);
        Assert.True(source.IndexOf("Environment.Exit(exitCode);") > source.IndexOf("Log.CloseAndFlush();"));
    }

    [Fact]
    public void Inno_installer_closes_the_application_before_upgrade_and_does_not_restart_it()
    {
        var source = File.ReadAllText(Source("..", "packaging", "inno", "LXMusicAccessible.iss"));
        Assert.Contains("CloseApplications=yes", source);
        Assert.Contains("RestartApplications=no", source);
        Assert.DoesNotContain("RestartApplications=yes", source);
    }
}
