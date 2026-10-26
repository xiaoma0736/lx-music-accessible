using System.Reflection;
using LXMusicAccessible.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class AudioPlayerStallRecoveryTests
{
    [Fact]
    public void Bounded_stall_timeout_releases_temp_media_and_reports_error()
    {
        var stalledMedia = Path.Combine(Path.GetTempPath(), "lxmusic-stall-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(stalledMedia, [1, 2, 3]);
        using var player = new AudioPlayer();
        var now = DateTimeOffset.UtcNow;
        SetPrivate(player, "_tempFilePath", stalledMedia);
        SetPrivate(player, "_stallStartedUtc", now);
        player.State = AudioPlayerState.Loading;

        Recover(player, now.AddSeconds(29));

        Assert.Equal(AudioPlayerState.Loading, player.State);
        Assert.True(File.Exists(stalledMedia));

        Recover(player, now.AddSeconds(31));

        Assert.Equal(AudioPlayerState.Error, player.State);
        Assert.False(File.Exists(stalledMedia));
        Assert.Null(GetPrivate(player, "_loadedSource"));
    }

    [Fact]
    public void Failed_device_switch_cleans_temporary_media()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;
        var source = File.ReadAllText(Path.Combine(directory!.FullName, "LXMusicAccessible.Audio", "AudioPlayer.cs"));
        var failureCatch = source[source.IndexOf("public bool SwitchOutputDevice", StringComparison.Ordinal)..];
        Assert.Contains("ReleaseStream();", failureCatch, StringComparison.Ordinal);
        Assert.Contains("DeleteTempFile();", failureCatch, StringComparison.Ordinal);
        Assert.Contains("_loadedSource = null;", failureCatch, StringComparison.Ordinal);
    }

    private static void Recover(AudioPlayer player, DateTimeOffset now) =>
        typeof(AudioPlayer).GetMethod("RecoverIfStallExpired", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(player, [now]);

    private static void SetPrivate(AudioPlayer player, string name, object? value) =>
        typeof(AudioPlayer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(player, value);

    private static object? GetPrivate(AudioPlayer player, string name) =>
        typeof(AudioPlayer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(player);
}
