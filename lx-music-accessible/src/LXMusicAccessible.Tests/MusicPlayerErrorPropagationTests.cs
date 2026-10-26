using LXMusicAccessible.Audio;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class MusicPlayerErrorPropagationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MusicPlayerError-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Audio_error_state_propagates_and_announces_during_playback()
    {
        Directory.CreateDirectory(_directory);
        var announcer = new RecordingAnnouncer();
        var audio = new RecordingAudioPlayer();
        using var background = new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance);
        using var player = new MusicPlayerService(
            NullLogger<MusicPlayerService>.Instance,
            audio,
            new FixedPlaybackSource("https://fixture.invalid/audio.mp3"),
            announcer,
            background,
            new HttpClient(new RejectingHttpMessageHandler()),
            _directory);
        var changes = new List<PlayerStateChangedEventArgs>();
        player.StateChanged += (_, change) => changes.Add(change);
        SetPrivate(player, "_state", PlayerState.Playing);
        SetPrivate(player, "_currentMusic", new MusicInfo { Id = "error", Source = "test", Title = "Error song" });

        audio.State = AudioPlayerState.Error;

        var change = Assert.Single(changes, item => item.NewState == PlayerState.Error);
        Assert.Contains("停滞超时", change.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(announcer.Announcements, item => item.Kind == AnnouncementKind.Error && item.Text.Contains("停滞超时", StringComparison.Ordinal));
    }

    private sealed class FixedPlaybackSource(string url) : IPlaybackSourceAdapter
    {
        public IReadOnlyList<string> GetAlternativeSourceIds(string currentSource) => [];
        public Task<PlaybackSourceResolution?> ResolveAsync(MusicInfo music, string quality, CancellationToken cancellationToken) =>
            Task.FromResult(url.ToResolution(music.Source, quality));
        public Task<IReadOnlyList<MusicInfo>> FindAlternativesAsync(MusicInfo music, string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MusicInfo>>([]);
        public Task<PlaybackSourceResolution?> ResolveExactAsync(MusicInfo music, string sourceId, string quality, CancellationToken cancellationToken) =>
            Task.FromResult(url.ToResolution(music.Source, quality));
    }
    private static void SetPrivate(MusicPlayerService player, string name, object? value) =>
        typeof(MusicPlayerService).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(player, value);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}



