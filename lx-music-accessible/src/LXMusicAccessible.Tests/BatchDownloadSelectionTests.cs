using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>Issue #74: native song-list selection and explicit batch download boundaries.</summary>
public sealed class BatchDownloadSelectionTests
{
    public static System.Collections.Generic.IEnumerable<object[]> SongLists =>
        new[]
        {
            new object[] { "SearchResults", "搜索结果选中歌曲" },
            new object[] { "FavoriteSongs", "收藏歌曲选中歌曲" },
            new object[] { "CategorySongs", "分类歌曲选中歌曲" },
            new object[] { "RankingSongs", "排行榜歌曲选中歌曲" }
        };
    [Fact]
    public void Four_song_lists_support_explicit_multi_selection()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new RecordingBatchDownloads());
            foreach (var list in SongLists)
                Assert.Equal(SelectionMode.MultiSimple, RequiredListBox(shell, (string)list[0]).SelectionMode);
        });
    }

    [Fact]
    public void Ctrl_space_toggles_only_the_focused_song()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new RecordingBatchDownloads());
            var list = RequiredListBox(shell, "SearchResults");
            FillSongs(list, 3);
            WinFormsUi.SelectOnly(list, 1);

            RaiseKeyDown(list, Keys.Control | Keys.Space);
            Assert.Empty(list.SelectedIndices);

            RaiseKeyDown(list, Keys.Control | Keys.Space);
            Assert.Equal(new[] { 1 }, list.SelectedIndices.Cast<int>().ToArray());

            WinFormsUi.SelectOnly(list, 0);
            SendMessage(list.Handle, 0x019E, (IntPtr)1, IntPtr.Zero);
            RaiseKeyDown(list, Keys.Control | Keys.Space);
            Assert.Equal(new[] { 0, 1 }, list.SelectedIndices.Cast<int>().ToArray());
        });
    }

    [Fact]
    public void Ctrl_a_selects_every_song_in_the_focused_list_only()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new RecordingBatchDownloads());
            var search = RequiredListBox(shell, "SearchResults");
            var ranking = RequiredListBox(shell, "RankingSongs");
            FillSongs(search, 3);
            FillSongs(ranking, 2);
            search.Focus();

            RaiseKeyDown(search, Keys.Control | Keys.A);

            Assert.Equal(new[] { 0, 1, 2 }, search.SelectedIndices.Cast<int>().ToArray());
            Assert.Empty(ranking.SelectedIndices);
        });
    }

    [Fact]
    public void Ctrl_a_skips_non_song_load_more_rows()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new RecordingBatchDownloads());
            var list = RequiredListBox(shell, "SearchResults");
            FillSongs(list, 2);
            list.Items.Add(new ObjectBox<string>("加载更多", "load-more"));
            list.Focus();

            RaiseKeyDown(list, Keys.Control | Keys.A);

            Assert.Equal(new[] { 0, 1 }, list.SelectedIndices.Cast<int>().ToArray());
        });
    }

    [Fact]
    public void Arrow_keys_move_focus_without_changing_explicit_selection()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new RecordingBatchDownloads());
            var list = RequiredListBox(shell, "SearchResults");
            FillSongs(list, 4);
            WinFormsUi.SelectOnly(list, 0);
            list.SetSelected(2, true);
            SendMessage(list.Handle, 0x019E, IntPtr.Zero, IntPtr.Zero);
            var before = list.SelectedIndices.Cast<int>().ToArray();

            RaiseKeyDown(list, Keys.Down);
            Assert.Equal(1, FocusedIndex(list));
            Assert.Equal(before, list.SelectedIndices.Cast<int>().ToArray());

            RaiseKeyDown(list, Keys.Up);
            Assert.Equal(0, FocusedIndex(list));
            Assert.Equal(before, list.SelectedIndices.Cast<int>().ToArray());
        });
    }

    [Fact]
    public void Plain_space_preserves_selection_and_does_not_toggle_playback()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var playback = new PlaybackSpy();
            using var shell = CreateShell(new RecordingBatchDownloads(), playback);
            shell.Show();
            Application.DoEvents();
            var list = RequiredListBox(shell, "SearchResults");
            FillSongs(list, 3);
            list.SetSelected(0, true);
            list.SetSelected(2, true);
            list.Focus();

            var args = RaiseKeyDown(list, Keys.Space);
            Assert.True(args.Handled);
            await Task.Delay(50);

            Assert.Equal(0, playback.ReplayCount);
            Assert.Equal(new[] { 0, 2 }, list.SelectedIndices.Cast<int>().ToArray());
        });
    }

    [Theory]
    [InlineData(Keys.Down, new[] { 0, 1 })]
    [InlineData(Keys.Up, new[] { 0 })]
    public void Shift_arrow_extends_selection_from_the_anchor(Keys key, int[] expected)
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new RecordingBatchDownloads());
            var list = RequiredListBox(shell, "SearchResults");
            FillSongs(list, 4);
            WinFormsUi.SelectOnly(list, 0);

            RaiseKeyDown(list, Keys.Shift | key);

            Assert.Equal(expected, list.SelectedIndices.Cast<int>().ToArray());
            Assert.Equal(key == Keys.Down ? expected[^1] : expected[0], FocusedIndex(list));
        });
    }

    [Fact]
    public void Enter_plays_the_focused_song_and_shift_enter_does_not_play()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var playback = new PlaybackSpy();
            using var shell = CreateShell(new RecordingBatchDownloads(), playback);
            shell.Show();
            Application.DoEvents();
            var list = RequiredListBox(shell, "SearchResults");
            FillSongs(list, 3);
            list.SetSelected(0, true);
            list.SetSelected(2, true);
            SendMessage(list.Handle, 0x019E, (IntPtr)2, IntPtr.Zero);
            list.Focus();

            RaiseKeyDown(list, Keys.Enter);
            await WaitFor(() => playback.ReplayRequests.Count == 1);
            Assert.Equal("song-2", playback.ReplayRequests.Single().Selection?.Id);

            RaiseKeyDown(list, Keys.Shift | Keys.Enter);
            await Task.Delay(50);
            Assert.Single(playback.ReplayRequests);
        });
    }

    [Fact]
    public void Context_popup_renames_single_download_and_reports_explicit_count()
    {
        WinFormsTestRunner.Run(() =>
        {
            var downloads = new RecordingBatchDownloads();
            var source = new FakeBrowseSource();
            using var shell = CreateShell(downloads, source: source);
            var list = RequiredListBox(shell, "SearchResults");
            FillSongs(list, 3);
            list.SetSelected(0, true);
            list.SetSelected(2, true);
            var selection = list.SelectedIndices.Cast<int>().ToArray();

            InvokePopup(list.ContextMenu);

            var items = list.ContextMenu!.MenuItems.Cast<MenuItem>().ToArray();
            Assert.Equal("下载当前歌曲", Assert.Single(items, item => item.Text == "下载当前歌曲").Text);
            Assert.Single(items, item => item.Text == "下载单曲音质");
            Assert.Single(items, item => item.Text == "播放音质");
            Assert.DoesNotContain(items, item => item.Text == "下载音质");
            var batch = Assert.Single(items, item => item.Text == "批量下载选中歌曲(2)");
            Assert.True(batch.Enabled);
            Assert.Equal(selection, list.SelectedIndices.Cast<int>().ToArray());
            Assert.Empty(downloads.Requests);
            Assert.Equal(0, source.SearchCalls);
        });
    }

    [Fact]
    public void Batch_menu_is_disabled_with_one_focused_song()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new RecordingBatchDownloads());
            var list = RequiredListBox(shell, "FavoriteSongs");
            FillSongs(list, 2);
            list.SelectedIndex = 1;

            InvokePopup(list.ContextMenu);

            var batch = RequiredBatchItem(list.ContextMenu!);
            Assert.Contains("批量下载选中歌曲(1)", batch.Text, StringComparison.Ordinal);
            Assert.False(batch.Enabled);
        });
    }

    [Theory]
    [MemberData(nameof(SongLists))]
    public void Batch_command_passes_only_explicit_selections(string listName, string expectedLabel)
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var downloads = new RecordingBatchDownloads();
            using var shell = CreateShell(downloads);
            shell.Show();
            Application.DoEvents();
            shell.SelectTabForTests(listName switch
            {
                "FavoriteSongs" => 1,
                "CategorySongs" => 2,
                "RankingSongs" => 3,
                _ => 0
            });
            Application.DoEvents();
            var list = RequiredListBox(shell, listName);
            FillSongs(list, 4);
            list.SetSelected(1, true);
            list.SetSelected(2, true);
            var selection = list.SelectedIndices.Cast<int>().ToArray();
            list.Focus();

            var batch = RequiredBatchItem(list.ContextMenu!);
            batch.PerformClick();
            await WaitFor(() => downloads.Requests.Count == 1);

            var request = downloads.Requests.Single();
            Assert.Equal(expectedLabel, request.Label);
            Assert.Equal(new[] { "song-1", "song-2" }, request.Songs.Select(song => song.Id).ToArray());
            Assert.Equal(selection, list.SelectedIndices.Cast<int>().ToArray());
            Assert.Same(list, shell.ActiveControl);
        });
    }

    [Fact]
    public void Fewer_than_two_explicit_songs_never_start_a_batch()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var downloads = new RecordingBatchDownloads();
            using var shell = CreateShell(downloads);
            var list = RequiredListBox(shell, "CategorySongs");
            FillSongs(list, 3);
            list.SelectedIndex = 2;

            InvokePopup(list.ContextMenu);
            var batch = RequiredBatchItem(list.ContextMenu!);
            if (batch.Enabled) batch.PerformClick();
            await Task.Delay(25);

            Assert.False(batch.Enabled);
            Assert.Empty(downloads.Requests);
        });
    }

    [Fact]
    public void Download_and_play_quality_menus_show_all_playable_states()
    {
        var probe = new StaticQualityProbe(new QualityProbeResult(
            "qq|song-1",
            [
                new("320k", "极高", QualityAvailability.Playable, "Interface A", "320k"),
                new("flac24bit", "Hi-Res 无损", QualityAvailability.Unavailable, null, null)
            ],
            "done"));
        using var shell = CreateShell(new RecordingBatchDownloads(), qualityProbe: probe);
        var list = RequiredListBox(shell, "SearchResults");
        FillSongs(list, 2);
        list.SelectedIndex = 1;
        _ = list.Handle;
        SendMessage(list.Handle, 0x019E, (IntPtr)1, IntPtr.Zero);

        var download = RequiredMenuItem(list.ContextMenu!, "下载单曲音质");
        RaisePopup(download);
        var downloadCount = download.MenuItems.Count;
        Assert.True(downloadCount > 0);
        var downloadItem = Assert.Single(download.MenuItems.Cast<MenuItem>(), item => item.Text == "极高，可播放，Interface A");
        var unavailableItem = Assert.Single(download.MenuItems.Cast<MenuItem>(), item => item.Text == "Hi-Res 无损，不可用");
        Assert.Equal(1, probe.ProbeCalls);

        var play = RequiredMenuItem(list.ContextMenu!, "播放音质");
        RaisePopup(play);
        var playCount = play.MenuItems.Count;
        Assert.Equal(downloadCount, playCount);
        Assert.Single(play.MenuItems.Cast<MenuItem>(), item => item.Text == "极高，可播放，Interface A");
        Assert.Single(play.MenuItems.Cast<MenuItem>(), item => item.Text == "Hi-Res 无损，不可用");
        Assert.Equal(1, play.MenuItems.Cast<MenuItem>().Count(item => item.Enabled));
        Assert.Same(downloadItem, Assert.Single(download.MenuItems.Cast<MenuItem>(), item => item.Text == "极高，可播放，Interface A"));
        Assert.Same(unavailableItem, Assert.Single(download.MenuItems.Cast<MenuItem>(), item => item.Text == "Hi-Res 无损，不可用"));
        Assert.Equal(downloadCount, download.MenuItems.Count);

        var items = list.ContextMenu!.MenuItems.Cast<MenuItem>().ToArray();
        Assert.DoesNotContain(items, item => item.Text == "下载音质");
    }

    [Fact]
    public void Context_menu_provides_stable_quality_submenus_and_enter_activates_selected_quality()
    {
        WinFormsTestRunner.Run(() =>
        {
            var probe = new DelayedQualityProbe();
            var downloads = new RecordingBatchDownloads();
            var playback = new PlaybackSpy();
            using var shell = CreateShell(downloads, playback, qualityProbe: probe);
            var list = RequiredListBox(shell, "SearchResults");
            FillSongs(list, 2);
            WinFormsUi.SelectOnly(list, 0);
            SendMessage(list.Handle, 0x019E, (IntPtr)1, IntPtr.Zero);
            list.Focus();
            var selection = list.SelectedIndices.Cast<int>().ToArray();

            // The outer popup must synchronously prepare both child menus. A child popup that only starts
            // network work after the user reaches it cannot reliably become a native submenu on first Entry.
            InvokePopup(list.ContextMenu);
            var download = RequiredMenuItem(list.ContextMenu!, "下载单曲音质");
            var play = RequiredMenuItem(list.ContextMenu!, "播放音质");
            Assert.True(download.MenuItems.Count > 0, "Download quality submenu was not ready when the context menu opened.");
            Assert.True(play.MenuItems.Count > 0, "Play quality submenu was not ready when the context menu opened.");
            Assert.All(download.MenuItems.Cast<MenuItem>(), item => Assert.False(item.Enabled));
            Assert.All(play.MenuItems.Cast<MenuItem>(), item => Assert.False(item.Enabled));
            var downloadCount = download.MenuItems.Count;
            var playCount = play.MenuItems.Count;

            probe.Complete(new QualityProbeResult(
                "test|song-1",
                [
                    new("320k", "极高", QualityAvailability.Playable, "Interface A", "320k"),
                    new("flac24bit", "Hi-Res 无损", QualityAvailability.Unavailable, null, null)
                ],
                "done"));
            WaitForUi(() => download.MenuItems.Cast<MenuItem>().Any(item => item.Enabled));
            WaitForUi(() => play.MenuItems.Cast<MenuItem>().Any(item => item.Enabled));
            Assert.Equal(downloadCount, download.MenuItems.Count);
            Assert.Equal(playCount, play.MenuItems.Count);

            var downloadOption = Assert.Single(download.MenuItems.Cast<MenuItem>(), item => item.Enabled);
            var playOption = Assert.Single(play.MenuItems.Cast<MenuItem>(), item => item.Enabled);
            downloadOption.PerformClick();
            WaitForUi(() => downloads.SingleRequests.Count == 1);
            var downloadRequest = downloads.SingleRequests.Single();
            Assert.Equal("song-1", downloadRequest.Music.Id);
            Assert.Equal("320k", downloadRequest.Quality);

            playOption.PerformClick();
            WaitForUi(() => playback.ReplayRequests.Count == 1);
            var playRequest = playback.ReplayRequests.Single();
            Assert.Equal("song-1", playRequest.Selection?.Id);
            Assert.Equal("320k", playRequest.Quality);
            Assert.True(playRequest.ForceResolve);

            Assert.Equal(selection, list.SelectedIndices.Cast<int>().ToArray());
        });
    }

    [Fact]
    public async Task Album_and_batch_downloads_keep_the_configured_default_quality()
    {
        using var host = await TestApplicationHost.StartAsync();
        var settings = host.Services.GetRequiredService<SettingsWorkflow>();
        await settings.LoadAsync(IntPtr.Zero);
        var edited = settings.Edit(current => current.DefaultDownloadQuality = "320k");
        Assert.True(edited.Accepted);
        var source = new SinglePageAlbumSource();
        var downloads = new RecordingBatchDownloads();
        using var shell = new WinFormsShell(new WinFormsShellServices
        {
            Announcer = new RecordingAnnouncer(),
            BrowseSource = source,
            Playback = new PlaybackSpy(),
            Downloads = downloads,
            Settings = settings,
            TemporaryDirectory = host.DataDirectory
        });
        shell.Show();
        Application.DoEvents();
        var list = RequiredListBox(shell, "SearchResults");
        list.Items.Add(new ObjectBox<MusicInfo>(source.Album.AccessibleName, source.Album));
        _ = list.Handle;
        SendMessage(list.Handle, 0x019E, IntPtr.Zero, IntPtr.Zero);
        list.Focus();

        var browseField = typeof(WinFormsShell).GetField("_browse", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(browseField);
        var browse = Assert.IsType<MusicBrowseWorkflow>(browseField!.GetValue(shell));
        await browse.ActivateAlbumAsync(source.Album);
        var albumField = typeof(WinFormsShell).GetField("_openAlbum", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(albumField);
        albumField!.SetValue(shell, source.Album);
        Application.DoEvents();

        var album = RequiredMenuItem(list.ContextMenu!, "下载整张专辑");
        RaisePopup(album);
        Assert.True(album.Enabled);
        album.PerformClick();
        await WaitFor(() => downloads.Requests.Count == 1);
        var albumRequest = Assert.Single(downloads.ListRequests);
        Assert.Equal("320k", albumRequest.Quality);
        Assert.Empty(downloads.SingleRequests);

        FillSongs(list, 2);
        WinFormsUi.SelectOnly(list, 0);
        list.SetSelected(1, true);
        list.Focus();
        InvokePopup(list.ContextMenu);

        var batch = RequiredBatchItem(list.ContextMenu!);
        batch.PerformClick();
        await WaitFor(() => downloads.Requests.Count == 2);
        Assert.All(downloads.Requests, request => Assert.Equal("320k", request.Quality));
    }

    [Fact]
    public void Whole_album_command_is_present_and_disabled_outside_album_context()
    {
        using var shell = CreateShell(new RecordingBatchDownloads());
        var list = RequiredListBox(shell, "SearchResults");
        FillSongs(list, 2);
        list.SelectedIndex = 0;

        InvokePopup(list.ContextMenu);
        var album = RequiredMenuItem(list.ContextMenu!, "下载整张专辑");
        RaisePopup(album);

        Assert.False(album.Enabled);
    }

    private static WinFormsShell CreateShell(
        RecordingBatchDownloads downloads,
        IPlaybackCoordinator? playback = null,
        FakeBrowseSource? source = null,
        IQualityProbeWorkflow? qualityProbe = null) => new(new WinFormsShellServices
        {
            Announcer = new RecordingAnnouncer(),
            BrowseSource = source ?? new FakeBrowseSource(),
            Playback = playback ?? new RecordingPlayback(),
            Downloads = downloads,
            QualityProbe = qualityProbe,
            TemporaryDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lx-batch-selection-" + Guid.NewGuid().ToString("N"))
        });

    private static ListBox RequiredListBox(Control root, string name) =>
        Assert.IsType<ListBox>(root.Controls.Find(name, true).Single());

    private static MenuItem RequiredMenuItem(ContextMenu menu, string text) =>
        Assert.Single(menu.MenuItems.Cast<MenuItem>(), item => item.Text == text);

    private static void RaisePopup(MenuItem item)
    {
        var method = typeof(MenuItem).GetMethod("OnPopup", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(item, new object[] { EventArgs.Empty });
        Application.DoEvents();
    }

    private static MenuItem RequiredBatchItem(ContextMenu menu) =>
        Assert.Single(menu.MenuItems.Cast<MenuItem>(), item =>
            item.Text?.StartsWith("批量下载选中歌曲(", StringComparison.Ordinal) == true);

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

    private static KeyEventArgs RaiseKeyDown(Control control, Keys key)
    {
        var method = typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var args = new KeyEventArgs(key);
        method!.Invoke(control, new object[] { args });
        Application.DoEvents();
        return args;
    }

    private static int FocusedIndex(ListBox list)
    {
        var result = SendMessage(list.Handle, 0x019F, IntPtr.Zero, IntPtr.Zero);
        return result.ToInt32();
    }

    private static void InvokePopup(ContextMenu? menu)
    {
        Assert.NotNull(menu);
        var method = typeof(ContextMenu).GetMethod("OnPopup", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(menu, new object[] { EventArgs.Empty });
        Application.DoEvents();
    }

    private static void WaitForUi(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            Application.DoEvents();
            if (condition()) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException("The awaited UI condition was not reached.");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Application.DoEvents();
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "The awaited UI condition was not reached.");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    private sealed class RecordingBatchDownloads : IDownloadWorkflow
    {
        private readonly System.Collections.ObjectModel.ObservableCollection<DownloadTask> _tasks = new();
        public System.Collections.ObjectModel.ReadOnlyObservableCollection<DownloadTask> Tasks { get; }
        public List<DownloadListRequest> Requests { get; } = new();

        public RecordingBatchDownloads() =>
            Tasks = new(_tasks);

        public List<DownloadListRequest> ListRequests { get; } = new();

        public void ApplyPreferences(DownloadPreferences preferences) { }
        public List<DownloadRequest> SingleRequests { get; } = new();

        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default)
        {
            SingleRequests.Add(request);
            return Task.FromResult(new DownloadTask());
        }
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            ListRequests.Add(request);
            return Task.FromResult(new DownloadListResult(
                request.Songs.Count,
                request.Songs.Count,
                0,
                0,
                0,
                Array.Empty<DownloadTask>(),
                "批量下载测试结束"));
        }
        public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void Cancel(DownloadTask task) { }
    public void Pause(DownloadTask task) { }
    public void Resume(DownloadTask task) { }
    public bool Delete(DownloadTask task, bool deleteSourceFile) => true;
        public void ClearCompleted() { }
        public string ResolveDownloadDirectory(string? requested = null) => requested ?? System.IO.Path.GetTempPath();
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExternalPlaybackTarget?>(null);
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalPlaybackResult(true, "ok"));
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }

    private sealed class DelayedQualityProbe : IQualityProbeWorkflow
    {
        private readonly TaskCompletionSource<QualityProbeResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProbeCalls { get; private set; }

        public Task<QualityProbeResult> ProbeAsync(MusicInfo music, CancellationToken cancellationToken = default)
        {
            ProbeCalls++;
            return _completion.Task;
        }

        public QualityProbeResult GetCached(MusicInfo music) => new("test|song-1", [], null);

        public void Invalidate(MusicInfo music) { }

        public void Complete(QualityProbeResult result) => _completion.TrySetResult(result);
    }

    private sealed class SinglePageAlbumSource : IMusicBrowseSource
    {
        public MusicInfo Album { get; } = new()
        {
            Id = "album-1",
            Source = "test",
            Title = "Test Album",
            Artist = "Artist",
            IsAlbum = true
        };

        public Task<PagedResult<MusicInfo>> SearchAsync(
            string query,
            int page,
            int pageSize,
            string searchType,
            string sourceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<MusicInfo>());

        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(
            MusicInfo album,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new PagedResult<MusicInfo>
            {
                Items = [new() { Id = "album-song", Source = "test", Title = "Album Song", Artist = "Artist" }],
                CurrentPage = 1,
                TotalItems = 1,
                HasMore = false
            });
        }

        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(
            string sourceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new List<PlaylistCategory>());

        public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(
            string category,
            int page,
            int pageSize,
            string sourceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<Playlist>());

        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(
            string playlistId,
            int page,
            int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<MusicInfo>());

        public Task<List<Ranking>> GetRankingsAsync(
            string sourceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new List<Ranking>());

        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(
            string rankingId,
            int page,
            int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<MusicInfo>());
    }
    private sealed class StaticQualityProbe(QualityProbeResult result) : IQualityProbeWorkflow
    {
        public int ProbeCalls { get; private set; }

        public Task<QualityProbeResult> ProbeAsync(MusicInfo music, CancellationToken cancellationToken = default)
        {
            ProbeCalls++;
            return Task.FromResult(result);
        }

        public QualityProbeResult GetCached(MusicInfo music) => result;

        public void Invalidate(MusicInfo music) { }
    }

    private sealed class PlaybackSpy : IPlaybackCoordinator
    {
        public List<PlaybackRequest> ReplayRequests { get; } = new();
        public int ReplayCount { get; private set; }
        public bool IsPlaying => false;
        public bool IsPaused => false;
        public MusicInfo? CurrentMusic { get; private set; }
        public PlayMode Mode { get; set; }
        public string DefaultQuality { get; set; } = "128k";
#pragma warning disable 67
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted;
#pragma warning restore 67

        public Task<PlaybackResult> PlayAsync(PlaybackRequest request)
        {
            ReplayRequests.Add(request);
            CurrentMusic = request.Selection;
            return Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, request.Selection, request.Origin, "playing"));
        }
        public Task<PlaybackResult> ReplayAsync()
        {
            ReplayCount++;
            return Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
        }
        public void Pause() { }
        public void Stop() { }
        public Task<PlaybackResult> NextAsync() => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
        public Task<PlaybackResult> PreviousAsync() => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
        public Task ResumeAsync() => Task.CompletedTask;
    }
}
