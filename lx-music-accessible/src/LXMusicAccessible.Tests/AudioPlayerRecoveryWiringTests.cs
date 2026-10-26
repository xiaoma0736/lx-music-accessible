using System.Reflection;
using LXMusicAccessible.Audio;
using ManagedBass;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class AudioPlayerRecoveryWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LXAudioRecovery-" + Guid.NewGuid().ToString("N"));

    public AudioPlayerRecoveryWiringTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Production_stall_observer_enters_loading_then_cleans_after_timeout()
    {
        var temp = CreateTempMedia();
        var player = CreatePlayer();
        player.State = AudioPlayerState.Playing;
        Set(player, "_tempFilePath", temp);
        Set(player, "_loadedSource", temp);
        var now = DateTimeOffset.UtcNow;

        InvokeObserve(player, PlaybackState.Stalled, now);

        Assert.Equal(AudioPlayerState.Loading, player.State);
        Assert.True(File.Exists(temp));

        InvokeObserve(player, PlaybackState.Stalled, now.AddSeconds(31));

        Assert.Equal(AudioPlayerState.Error, player.State);
        Assert.False(File.Exists(temp));
        Assert.Null((string?)Get(player, "_loadedSource"));
        Assert.Null((DateTimeOffset?)Get(player, "_stallStartedUtc"));
    }

    [Fact]
    public void Production_stall_observer_resumes_when_playback_recovers()
    {
        var player = CreatePlayer();
        player.State = AudioPlayerState.Playing;
        var now = DateTimeOffset.UtcNow;

        InvokeObserve(player, PlaybackState.Stalled, now);
        InvokeObserve(player, PlaybackState.Playing, now.AddMilliseconds(250));

        Assert.Equal(AudioPlayerState.Playing, player.State);
        Assert.Null((DateTimeOffset?)Get(player, "_stallStartedUtc"));
    }

    [Fact]
    public void Failed_device_switch_cleanup_removes_temp_media()
    {
        var temp = CreateTempMedia();
        var player = CreatePlayer();
        player.State = AudioPlayerState.Playing;
        Set(player, "_tempFilePath", temp);
        Set(player, "_loadedSource", temp);

        Invoke(player, "CleanupFailedDeviceSwitch");

        Assert.Equal(AudioPlayerState.Stopped, player.State);
        Assert.False(File.Exists(temp));
        Assert.Null((string?)Get(player, "_loadedSource"));
    }

    private AudioPlayer CreatePlayer() => new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, NullLogger<AudioPlayer>.Instance);

    private string CreateTempMedia()
    {
        var path = Path.Combine(_root, "media-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static void InvokeObserve(AudioPlayer player, PlaybackState state, DateTimeOffset now) =>
        Invoke(player, "ObservePlaybackState", state, now);

    private static void Invoke(AudioPlayer player, string name, params object?[] args) =>
        typeof(AudioPlayer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(player, args);

    private static void Set(AudioPlayer player, string name, object? value) =>
        typeof(AudioPlayer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(player, value);

    private static object? Get(AudioPlayer player, string name) =>
        typeof(AudioPlayer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(player);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
