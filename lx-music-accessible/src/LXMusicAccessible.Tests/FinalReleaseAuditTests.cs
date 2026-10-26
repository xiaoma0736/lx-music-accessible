using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>Regression coverage for the final 3.0 audit fixes.</summary>
public sealed class FinalReleaseAuditTests
{
    [Fact]
    public void Data_directory_failure_rolls_back_the_owned_target_and_can_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), "LXFinalData-" + Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "local");
        var selected = Path.Combine(root, "selected");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(selected);
        try
        {
            var current = AppDataPaths.PrepareDataDir(local);
            File.WriteAllText(Path.Combine(current, "a.txt"), "copied-first");
            var blocked = Path.Combine(current, "b.txt");
            File.WriteAllText(blocked, "locked");
            var workflow = CreateSettingsWorkflow();
            DataDirectoryChangeResult result;
            using (new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                result = workflow.ChangeDataDirectoryInRoot(selected, local);
            }

            var target = Path.Combine(selected, AppDataPaths.DefaultDirName);
            Assert.False(result.Succeeded);
            Assert.True(result.CleanupSucceeded, string.Join("; ", result.CleanupFailures ?? []));
            Assert.False(Directory.Exists(target), "A failed copy left an owned target behind.");

            var retry = workflow.ChangeDataDirectoryInRoot(selected, local);
            Assert.True(retry.Succeeded, retry.Message);
            Assert.True(File.Exists(Path.Combine(target, "a.txt")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Terminal_announcements_with_the_same_activity_are_not_permanently_swallowed()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var live = new Label();
            using var announcer = new WinFormsAccessibleAnnouncer(
                Array.Empty<ITtsService>(),
                NullLogger<WinFormsAccessibleAnnouncer>.Instance);
            announcer.Attach(live);

            announcer.Announce(new AccessibleAnnouncement("第一首", AnnouncementKind.Success, "playback-song"));
            announcer.Announce(new AccessibleAnnouncement("第二首", AnnouncementKind.Success, "playback-song"));

            Assert.Equal("第二首", live.AccessibleName);
            Assert.Equal("第二首", live.Text);
        });
    }

    [Fact]
    public void Track_scope_covers_automatic_advance_and_tool_outcomes_are_spoken()
    {
        var settings = new AccessibilitySettings { AnnounceTrackChange = false };
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("自动下一首", AnnouncementKind.Success, "playback-track:auto:1"),
            settings));
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("手动下一首", AnnouncementKind.Success, "playback-song"),
            settings));
        Assert.True(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("已删除所选任务", AnnouncementKind.Success, "download-manager-delete"),
            new AccessibilitySettings()));
        Assert.True(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("没有选中歌曲", AnnouncementKind.Status, "batch-selection-empty"),
            new AccessibilitySettings()));
    }

    [Fact]
    public void Play_after_stop_replays_the_current_song()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var playback = new ReplayProbe();
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = new SilentAnnouncer(),
                BrowseSource = new EmptyBrowseSource(),
                Playback = playback,
                TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-final-" + Guid.NewGuid().ToString("N"))
            });
            shell.Show();
            Application.DoEvents();

            var toggle = typeof(WinFormsShell).GetMethod("TogglePlayback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(toggle);
            toggle!.Invoke(shell, null);
            await Task.Delay(50);

            Assert.Equal(1, playback.ReplayCount);
        });
    }

    [Fact]
    public async Task Download_pause_survives_a_second_pause_after_resume()
    {
        var type = typeof(DownloadWorkflow).GetNestedType("DownloadOperation", BindingFlags.NonPublic);
        Assert.NotNull(type);
        using var operation = (IDisposable)Activator.CreateInstance(type!, nonPublic: true)!;
        var pause = type!.GetMethod("Pause", BindingFlags.Public | BindingFlags.Instance)!;
        var resume = type.GetMethod("Resume", BindingFlags.Public | BindingFlags.Instance)!;
        var wait = type.GetMethod("WaitIfPausedAsync", BindingFlags.Public | BindingFlags.Instance)!;

        Assert.True((bool)pause.Invoke(operation, null)!);
        Assert.True((bool)resume.Invoke(operation, null)!);
        Assert.True((bool)pause.Invoke(operation, null)!);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var pending = (Task)wait.Invoke(operation, [cancellation.Token])!;
        var finished = await Task.WhenAny(pending, Task.Delay(100));
        Assert.NotSame(pending, finished);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void Hiding_a_minimized_window_restores_it_normal()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = new SilentAnnouncer(),
                BrowseSource = new EmptyBrowseSource(),
                TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-final-" + Guid.NewGuid().ToString("N"))
            });
            using var desktop = new WinFormsDesktopIntegration(new SilentAnnouncer());
            shell.Show();
            Application.DoEvents();
            desktop.Attach(shell);
            shell.WindowState = FormWindowState.Minimized;
            Application.DoEvents();

            Assert.True(desktop.HideToTray());
            Assert.NotEqual(DesktopFocusTarget.None, desktop.RestoreWindow());
            Assert.True(desktop.IsWindowRestored);
            Assert.Equal(FormWindowState.Normal, shell.WindowState);
            Assert.True(shell.Visible);
        });
    }

    [Fact]
    public void Volume_hotkey_persists_the_new_default()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var settings = CreateSettingsWorkflow();
            var audio = new VolumeProbe { Volume = 0.5 };
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = new SilentAnnouncer(),
                BrowseSource = new EmptyBrowseSource(),
                Audio = audio,
                Settings = settings,
                TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-final-" + Guid.NewGuid().ToString("N"))
            });
            shell.Show();
            Application.DoEvents();

            var change = typeof(WinFormsShell).GetMethod("ChangeVolume", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(change);
            change!.Invoke(shell, [0.1d]);
            await Task.Delay(20);

            Assert.Equal(0.6, settings.Current.Settings.DefaultVolume, 3);
        });
    }

    [Fact]
    public void Data_cleanup_removes_application_backups_and_logs_but_preserves_unknown_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "LXFinalCleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var owned = Path.Combine(root, "owned");
            var id = AppDataOwnership.Create(owned);
            Directory.CreateDirectory(Path.Combine(owned, "logs"));
            File.WriteAllText(Path.Combine(owned, "lxmusic.db"), "db");
            File.WriteAllText(Path.Combine(owned, "lxmusic.db.pre-v2-20260101.bak"), "backup");
            File.WriteAllText(Path.Combine(owned, "logs", "app-20260101.log"), "log");
            File.WriteAllText(Path.Combine(owned, "keep.txt"), "unknown");

            var first = AppDataOwnership.Clean(owned, id);
            Assert.True(first.Succeeded);
            Assert.True(first.PreservedUnknownContent);
            Assert.False(File.Exists(Path.Combine(owned, "lxmusic.db")));
            Assert.False(File.Exists(Path.Combine(owned, "lxmusic.db.pre-v2-20260101.bak")));
            Assert.False(File.Exists(Path.Combine(owned, "logs", "app-20260101.log")));
            Assert.True(File.Exists(Path.Combine(owned, "keep.txt")));

            File.Delete(Path.Combine(owned, "keep.txt"));
            Assert.True(AppDataOwnership.Clean(owned, id).Succeeded);
            Assert.False(Directory.Exists(owned));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Third_party_media_urls_cannot_target_loopback_or_metadata()
    {
        Assert.False(await ScriptNetworkClient.IsAllowedMediaUrlAsync(new Uri("http://127.0.0.1/a.mp3")));
        Assert.False(await ScriptNetworkClient.IsAllowedMediaUrlAsync(new Uri("http://169.254.169.254/latest/meta-data")));
        Assert.False(await ScriptNetworkClient.IsAllowedMediaUrlAsync(new Uri("http://[::1]/a.mp3")));
        Assert.False(await ScriptNetworkClient.IsAllowedMediaUrlAsync(new Uri("file:///c:/windows/win.ini")));
        Assert.True(await ScriptNetworkClient.IsAllowedMediaUrlAsync(new Uri("https://cdn.example.com/a.mp3")));
    }

    [Fact]
    public void Netease_translation_uses_the_same_millisecond_timestamp()
    {
        var parse = typeof(NeteaseMusicService).GetMethod(
            "ParseLrcContent",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(parse);
        var lyric = (Lyric?)parse!.Invoke(null,
            ["[00:12.345]原文", "[00:12.345]译文"]);
        Assert.NotNull(lyric);
        var line = Assert.Single(lyric!.Lines);
        Assert.Equal("译文", line.Translation);
    }

    [Fact]
    public void Search_dedupe_keeps_distinct_titles_in_the_same_album()
    {
        var key = typeof(MusicSourceService).GetMethod(
            "BuildSearchDedupKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(key);
        var first = new MusicInfo { Title = "第一首", Album = "专辑", Artist = "歌手", Duration = "03:10" };
        var second = new MusicInfo { Title = "第二首", Album = "专辑", Artist = "歌手", Duration = "03:10" };
        Assert.NotEqual(key!.Invoke(null, [first]), key.Invoke(null, [second]));
    }

    private static SettingsWorkflow CreateSettingsWorkflow() => new(
        new MemorySettingsStore(),
        new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, new NoHotkeyAdapter()),
        NullLogger<SettingsWorkflow>.Instance);

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<UserSettings?>(new());
        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoHotkeyAdapter : IGlobalHotkeyAdapter
    {
        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers) => true;
        public bool Unregister(IntPtr windowHandle, int id) => true;
        public int LastRegisterError => 0;
    }

    private sealed class SilentAnnouncer : IAccessibleAnnouncer
    {
        public bool SpeechEnabled { get; set; }
        public void Announce(AccessibleAnnouncement announcement) { }
    }

    private sealed class ReplayProbe : IPlaybackCoordinator
    {
        public int ReplayCount { get; private set; }
        public bool IsPlaying => false;
        public bool IsPaused => false;
        public MusicInfo? CurrentMusic { get; } = new() { Id = "stopped", Source = "test", Title = "Stopped" };
        public PlayMode Mode { get; set; }
        public string DefaultQuality { get; set; } = "128k";
#pragma warning disable 67
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted;
#pragma warning restore 67

        public Task<PlaybackResult> PlayAsync(PlaybackRequest request) =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, request.Selection, request.Origin, "ok"));
        public Task<PlaybackResult> ReplayAsync()
        {
            ReplayCount++;
            return Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, CurrentMusic, PlaybackQueueOrigin.Static, "ok"));
        }
        public void Pause() { }
        public void Stop() { }
        public Task<PlaybackResult> NextAsync() => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no"));
        public Task<PlaybackResult> PreviousAsync() => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no"));
        public Task ResumeAsync() => Task.CompletedTask;
    }

    private sealed class VolumeProbe : IAudioPlaybackControl
    {
        public void SetVolume(double volume) => Volume = volume;
        public double Volume { get; set; } = 0.5;
        public void SyncVolumeFromDevice() { }
        public void SetPosition(TimeSpan position) { }
        public void SeekTo(TimeSpan position) { }
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public string? ActualQuality => null;
        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => [];
        public int CurrentOutputDeviceIndex => -1;
        public string CurrentOutputDeviceName => "系统默认";
        public string? SwitchToNextOutputDevice() => null;
        public string? SwitchToPreviousOutputDevice() => null;
        public string? SwitchOutputDevice(int index) => null;
    }

    private sealed class EmptyBrowseSource : IMusicBrowseSource
    {
        public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page, int pageSize, string searchType, string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(new List<PlaylistCategory>());
        public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<Playlist>.Empty(page, pageSize));
        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public Task<List<Ranking>> GetRankingsAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(new List<Ranking>());
        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
    }
}
