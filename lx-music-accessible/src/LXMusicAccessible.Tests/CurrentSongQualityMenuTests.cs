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
    public void F2_does_not_download_the_current_song()
    {
        WinFormsTestRunner.Run(() =>
        {
            var downloads = new RecordingDownloads();
            using var shell = CreateShell(downloads);
            shell.Show();
            Application.DoEvents();
            var list = RequiredListBox(shell, "SearchResults");
            FillSongs(list, 2);
            list.SelectedIndex = 1;
            list.Focus();
            Application.DoEvents();

            var method = typeof(WinFormsShell).GetMethod(
                "ExecuteLocalHotkey",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var handled = Assert.IsType<bool>(method!.Invoke(shell, [Keys.F2]));

            Assert.False(handled);
            Assert.Empty(downloads.SingleRequests);
            Assert.Empty(downloads.ListRequests);
        });
    }

    private static WinFormsShell CreateShell(
        RecordingDownloads downloads,
        IQualityProbeWorkflow? probe = null) => new(new WinFormsShellServices
    {
        Announcer = new RecordingAnnouncer(),
        BrowseSource = new FakeBrowseSource(),
        Playback = new RecordingPlayback(),
        Downloads = downloads,
        QualityProbe = probe,
        TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-current-quality-" + Guid.NewGuid().ToString("N"))
    });

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
        public void Cancel(DownloadTask task) { }
        public void Pause(DownloadTask task) { }
        public void Resume(DownloadTask task) { }
        public bool Delete(DownloadTask task, bool deleteSourceFile) => true;
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
