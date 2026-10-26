using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class CurrentSongQualityMenuTests
{
    [Fact]
    public void Current_song_direct_download_commands_are_removed()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new RecordingDownloads());
            var list = shell.SearchResultsForTests;
            FillSongs(list, 2);
            list.SelectedIndex = 1;
            var menu = shell.BuildSongContextMenu(list);

            Assert.DoesNotContain(
                menu.MenuItems.Cast<MenuItem>(),
                item => item.Text == "下载当前歌曲");
            Assert.DoesNotContain(
                AllMenuItems(shell.Menu),
                item => item.Text?.Contains("下载当前歌曲", StringComparison.Ordinal) == true);
            Assert.Single(
                menu.MenuItems.Cast<MenuItem>(),
                item => item.Text == "下载单曲音质");
        });
    }

    [Fact]
    public void Download_quality_item_activates_the_exact_declared_quality()
    {
        WinFormsTestRunner.Run(() =>
        {
            var downloads = new RecordingDownloads();
            var probe = new StaticQualityProbe(new QualityProbeResult(
                "test|song-1",
                [
                    new("flac", QualityCatalog.Label("flac"), QualityAvailability.Playable, "测试接口", null),
                    new("320k", QualityCatalog.Label("320k"), QualityAvailability.Playable, "测试接口", null),
                ],
                "音源声明音质已加载"));
            using var shell = CreateShell(downloads, probe);
            var list = RequiredListBox(shell, "SearchResults");
            FillSongs(list, 2);
            list.SelectedIndex = 1;

            var download = RequiredMenuItem(list.ContextMenu!, "下载单曲音质");
            RaisePopup(download);

            Assert.Collection(
                download.MenuItems.Cast<MenuItem>(),
                item =>
                {
                    Assert.Equal("无损 FLAC，可下载，测试接口", item.Text);
                    Assert.True(item.Enabled);
                },
                item =>
                {
                    Assert.Equal("极高，可下载，测试接口", item.Text);
                    Assert.True(item.Enabled);
                });

            download.MenuItems[0].PerformClick();
            Application.DoEvents();

            var request = Assert.Single(downloads.SingleRequests);
            Assert.Equal("song-1", request.Music.Id);
            Assert.Equal("flac", request.Quality);
            Assert.Empty(downloads.ListRequests);
        });
    }

    [Fact]
    public void F2_downloads_the_current_playing_song_at_its_playback_quality()
    {
        WinFormsTestRunner.Run(() =>
        {
            var downloads = new RecordingDownloads();
            using var shell = CreateShell(downloads);
            var playing = new MusicInfo
            {
                Id = "playing-song",
                Source = "test",
                Title = "Playing Song",
                Artist = "Artist"
            };
            typeof(WinFormsShell)
                .GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(shell, playing);
            typeof(WinFormsShell)
                .GetField("_currentPlaybackQuality", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(shell, "flac");
            var list = RequiredListBox(shell, "SearchResults");
            FillSongs(list, 2);
            list.SelectedIndex = 0;

            var method = typeof(WinFormsShell).GetMethod(
                "ExecuteLocalHotkey",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var handled = Assert.IsType<bool>(method!.Invoke(shell, [Keys.F2]));
            Application.DoEvents();

            Assert.True(handled);
            var request = Assert.Single(downloads.SingleRequests);
            Assert.Equal("playing-song", request.Music.Id);
            Assert.Equal("flac", request.Quality);
            Assert.Empty(downloads.ListRequests);
        });
    }
    [Fact]
    public void Automatic_track_change_refreshes_title_and_announces_song()
    {
        WinFormsTestRunner.Run(() =>
        {
            var downloads = new RecordingDownloads();
            var playback = new StatefulPlayback();
            var announcer = new RecordingAnnouncer();
            using var shell = CreateShell(downloads, playback: playback, announcer: announcer);
            shell.Show();
            Application.DoEvents();

            var music = new MusicInfo
            {
                Id = "auto-song",
                Source = "test",
                Title = "自动歌曲",
                Artist = "自动歌手"
            };
            playback.RaisePlaying(music, PlaybackTrigger.AutoContinuation, requestedQuality: "flac", actualQuality: "flac24bit", interfaceId: "test-interface", interfaceName: "测试接口");
            Application.DoEvents();

            Assert.Contains("自动歌曲", shell.Text, StringComparison.Ordinal);
            Assert.Contains("自动歌手", shell.Text, StringComparison.Ordinal);
            var current = Assert.IsType<MusicInfo>(typeof(WinFormsShell)
                .GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(shell));
            Assert.Equal("auto-song", current.Id);
            Assert.Equal("flac", typeof(WinFormsShell).GetField("_currentPlaybackQuality", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell));
            Assert.Equal("flac24bit", typeof(WinFormsShell).GetField("_currentPlaybackActualQuality", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell));
            Assert.Equal("test-interface", typeof(WinFormsShell).GetField("_currentPlaybackInterfaceId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell));
            Assert.Equal("测试接口", typeof(WinFormsShell).GetField("_currentPlaybackInterfaceName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell));
            Assert.Contains(announcer.Announcements, item =>
                item.Text.Contains("正在播放：自动歌手，自动歌曲", StringComparison.Ordinal)
                && item.ActivityId?.StartsWith("playback-track:auto:", StringComparison.Ordinal) == true);
        });
    }
    [Fact]
    public void Song_details_show_interface_qualities_and_current_playback_state()
    {
        WinFormsTestRunner.Run(() =>
        {
            var downloads = new RecordingDownloads();
            var candidates = new DetailCandidateSource();
            using var shell = CreateShell(downloads, qualityCandidates: candidates);
            var playing = new MusicInfo { Id = "playing", Source = "qq", Title = "Playing", Artist = "Artist" };
            typeof(WinFormsShell).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shell, playing);
            typeof(WinFormsShell).GetField("_currentPlaybackQuality", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shell, "flac");
            typeof(WinFormsShell).GetField("_currentPlaybackActualQuality", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shell, "flac24bit");
            typeof(WinFormsShell).GetField("_currentPlaybackInterfaceId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shell, "current");
            typeof(WinFormsShell).GetField("_currentPlaybackInterfaceName", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shell, "当前接口");

            var method = typeof(WinFormsShell).GetMethod(
                "BuildSongDetailLinesAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var result = Assert.IsType<Task<IReadOnlyList<string>>>(method!.Invoke(shell, [playing]));
            var lines = result.GetAwaiter().GetResult();

            Assert.Contains(lines, line => line == "当前音源接口：当前接口");
            Assert.Contains(lines, line => line == "当前请求播放音质：无损 FLAC");
            Assert.Contains(lines, line => line == "当前实际播放音质：Hi-Res 无损");
            Assert.Contains(lines, line => line == "当前接口可提供音质：无损 FLAC、极高");
        });
    }
    private static WinFormsShell CreateShell(
        RecordingDownloads downloads,
        IQualityProbeWorkflow? probe = null,
        IPlaybackCoordinator? playback = null,
        RecordingAnnouncer? announcer = null,
        IQualityCandidateSource? qualityCandidates = null) => new(new WinFormsShellServices
    {
        Announcer = announcer ?? new RecordingAnnouncer(),
        BrowseSource = new FakeBrowseSource(),
        Playback = playback ?? new RecordingPlayback(),
        Downloads = downloads,
        QualityProbe = probe,
        QualityCandidates = qualityCandidates,
        TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-current-quality-" + Guid.NewGuid().ToString("N"))
    });

    private sealed class DetailCandidateSource : IQualityCandidateSource
    {
        public Task<IReadOnlyList<SourceQualityCandidate>> GetCandidatesAsync(
            MusicInfo music,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SourceQualityCandidate>>([
            new("current", "当前接口", 1, "flac"),
            new("current", "当前接口", 1, "320k"),
            new("other", "其他接口", 2, "128k")
        ]);

        public Task<string?> ResolveAsync(
            MusicInfo music,
            SourceQualityCandidate candidate,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
    private sealed class StatefulPlayback : IPlaybackCoordinator
    {
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
#pragma warning disable 67
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted;
#pragma warning restore 67
        public bool IsPlaying => true;
        public bool IsPaused => false;
        public MusicInfo? CurrentMusic { get; private set; }
        public PlayMode Mode { get; set; }
        public string DefaultQuality { get; set; } = "128k";

        public void RaisePlaying(
            MusicInfo music,
            PlaybackTrigger trigger,
            string? requestedQuality = null,
            string? actualQuality = null,
            string? interfaceId = null,
            string? interfaceName = null)
        {
            CurrentMusic = music;
            StateChanged?.Invoke(this, new PlayerStateChangedEventArgs
            {
                OldState = PlayerState.Paused,
                NewState = PlayerState.Playing,
                CurrentMusic = music,
                Trigger = trigger,
                RequestedQuality = requestedQuality ?? "128k",
                ActualQuality = actualQuality,
                InterfaceId = interfaceId,
                InterfaceName = interfaceName
            });
        }

        public Task<PlaybackResult> PlayAsync(PlaybackRequest request) =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, request.Selection, request.Origin, "playing"));
        public Task<PlaybackResult> ReplayAsync() =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
        public void Pause() { }
        public void Stop() { }
        public Task<PlaybackResult> NextAsync() =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
        public Task<PlaybackResult> PreviousAsync() =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
        public Task ResumeAsync() => Task.CompletedTask;
    }

    private static ListBox RequiredListBox(Control root, string name) =>
        Assert.IsType<ListBox>(root.Controls.Find(name, true).Single());

    private static MenuItem RequiredMenuItem(ContextMenu menu, string text) =>
        Assert.Single(menu.MenuItems.Cast<MenuItem>(), item => item.Text == text);

    private static IEnumerable<MenuItem> AllMenuItems(Menu? menu)
    {
        if (menu is null) yield break;
        foreach (MenuItem item in menu.MenuItems)
        {
            yield return item;
            foreach (var child in AllMenuItems(item)) yield return child;
        }
    }

    private static void InvokePopup(ContextMenu? menu)
    {
        if (menu is null) return;
        var method = typeof(Menu).GetMethod("OnPopup", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(menu, [EventArgs.Empty]);
        Application.DoEvents();
    }

    private static void RaisePopup(MenuItem item)
    {
        var method = typeof(MenuItem).GetMethod("OnPopup", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(item, [EventArgs.Empty]);
        Application.DoEvents();
    }

    private static void FillSongs(ListBox list, int count)
    {
        list.BeginUpdate();
        list.Items.Clear();
        for (var index = 0; index < count; index++)
        {
            var song = new MusicInfo
            {
                Id = "song-" + index,
                Source = "test",
                Title = "Song " + index,
                Artist = "Artist " + index
            };
            list.Items.Add(new ObjectBox<MusicInfo>(song.AccessibleName, song));
        }
        list.EndUpdate();
    }

    private sealed class RecordingDownloads : IDownloadWorkflow
    {
        private readonly ObservableCollection<DownloadTask> _tasks = new();
        public ReadOnlyObservableCollection<DownloadTask> Tasks { get; }
        public List<DownloadRequest> SingleRequests { get; } = [];
        public List<DownloadListRequest> ListRequests { get; } = [];

        public RecordingDownloads() => Tasks = new(_tasks);

        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default)
        {
            SingleRequests.Add(request);
            return Task.FromResult(new DownloadTask());
        }

        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default)
        {
            ListRequests.Add(request);
            return Task.FromResult(new DownloadListResult(
                request.Songs.Count,
                request.Songs.Count,
                0, 0, 0,
                Array.Empty<DownloadTask>(),
                "批量下载测试结束"));
        }

        public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RedownloadAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void Cancel(DownloadTask task) { }
        public void Pause(DownloadTask task) { }
        public void Resume(DownloadTask task) { }
        public bool Delete(DownloadTask task, bool deleteSourceFile) => true;
    public Task<bool> DeleteAsync(DownloadTask task, bool deleteSourceFile) => Task.FromResult(Delete(task, deleteSourceFile));
        public void ClearCompleted() { }
        public string ResolveDownloadDirectory(string? requested = null) => requested ?? Path.GetTempPath();
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExternalPlaybackTarget?>(null);
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalPlaybackResult(true, "ok"));
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }

    private sealed class StaticQualityProbe(QualityProbeResult result) : IQualityProbeWorkflow
    {
        public Task<QualityProbeResult> ProbeAsync(MusicInfo music, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public QualityProbeResult GetCached(MusicInfo music) => result;
        public void Invalidate(MusicInfo music) { }
    }
}
