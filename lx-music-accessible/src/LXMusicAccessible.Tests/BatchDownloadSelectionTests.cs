using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
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
    public void Plain_space_toggles_playback_and_preserves_selection()
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

            RaiseKeyDown(list, Keys.Space);
            await WaitFor(() => playback.ReplayCount == 1);

            Assert.Equal(new[] { 0, 2 }, list.SelectedIndices.Cast<int>().ToArray());
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
            list.SetSelected(1, true);
            list.Focus();

            RaiseKeyDown(list, Keys.Enter);
            await WaitFor(() => playback.ReplayRequests.Count == 1);
            Assert.Equal("song-1", playback.ReplayRequests.Single().Selection?.Id);

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

    private static WinFormsShell CreateShell(
        RecordingBatchDownloads downloads,
        IPlaybackCoordinator? playback = null,
        FakeBrowseSource? source = null) => new(new WinFormsShellServices
    {
        Announcer = new RecordingAnnouncer(),
        BrowseSource = source ?? new FakeBrowseSource(),
        Playback = playback ?? new RecordingPlayback(),
        Downloads = downloads,
        TemporaryDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lx-batch-selection-" + Guid.NewGuid().ToString("N"))
    });

    private static ListBox RequiredListBox(Control root, string name) =>
        Assert.IsType<ListBox>(root.Controls.Find(name, true).Single());

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

    private static void RaiseKeyDown(Control control, Keys key)
    {
        var method = typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(control, new object[] { new KeyEventArgs(key) });
        Application.DoEvents();
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

        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DownloadTask());
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
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
        public void ClearCompleted() { }
        public string ResolveDownloadDirectory(string? requested = null) => requested ?? System.IO.Path.GetTempPath();
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExternalPlaybackTarget?>(null);
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalPlaybackResult(true, "ok"));
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
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