using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>Issue #78: loading more search results moves the native reading cursor to the first newly appended song.</summary>
public sealed class Issue76SearchLoadMoreCaretTests
{
    [Fact]
    public void LoadMoreWithAnotherPageMovesCaretToFirstAppendedSongAndPreservesSelection()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new PagedSearchSource(new[]
            {
                new[] { Song("song-1"), Song("song-2") },
                new[] { Song("song-3"), Song("song-4"), Song("song-5") },
                new[] { Song("song-6") }
            });
            var playback = new RecordingPlayback();
            using var shell = CreateShell(source, playback);
            shell.Show();
            Application.DoEvents();

            var results = RequiredListBox(shell);
            var searchBox = Descendants<TextBox>(shell).Single();
            searchBox.Text = "query";
            await InvokeSearchAsync(shell, reset: true);
            await WaitFor(() => results.Items.Count == 3);

            var markerIndex = results.Items.Count - 1;
            WinFormsUi.SelectOnly(results, markerIndex);

            await InvokeSearchAsync(shell, reset: false);
            await WaitFor(() => results.Items.Count == 6);
            await WaitFor(() => source.Calls == 2);
            var expectedSong = Assert.IsType<ObjectBox<MusicInfo>>(results.Items[2]).Value;
            Assert.Equal("song-3", expectedSong.Id);
            Assert.Equal(expectedSong, FocusedSong(results));
            Assert.NotEqual(results.Items.Count - 1, ListCaretIndex(results));
            Assert.Equal(new[] { "song-3" }, SelectedSongIds(results));
            Assert.False(results.GetSelected(results.Items.Count - 1), "The load-more marker must not remain selected.");
            Assert.Empty(playback.Requests);
        });
    }

    [Fact]
    public void LoadMoreAtEndMovesCaretToFirstAppendedSongAndRemovesMarker()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new PagedSearchSource(new[]
            {
                new[] { Song("song-1"), Song("song-2") },
                new[] { Song("song-3"), Song("song-4"), Song("song-5") }
            });
            var playback = new RecordingPlayback();
            using var shell = CreateShell(source, playback);
            shell.Show();
            Application.DoEvents();

            var results = RequiredListBox(shell);
            var searchBox = Descendants<TextBox>(shell).Single();
            searchBox.Text = "query";
            await InvokeSearchAsync(shell, reset: true);
            await WaitFor(() => results.Items.Count == 3);

            var markerIndex = results.Items.Count - 1;
            WinFormsUi.SelectOnly(results, markerIndex);
            SetCaretIndex(results, markerIndex);

            await InvokeSearchAsync(shell, reset: false);
            await WaitFor(() => results.Items.Count == 5);
            await WaitFor(() => source.Calls == 2);

            var expectedSong = Assert.IsType<ObjectBox<MusicInfo>>(results.Items[2]).Value;
            Assert.Equal("song-3", expectedSong.Id);
            Assert.Equal(expectedSong, FocusedSong(results));
            Assert.All(results.Items.OfType<object>(), item => Assert.DoesNotContain("加载更多", item.ToString()!, StringComparison.Ordinal));
            Assert.Equal("song-3", WinFormsUi.Selected<MusicInfo>(results)!.Id);
        });
    }

    private static WinFormsShell CreateShell(PagedSearchSource source, RecordingPlayback? playback = null) => new(new WinFormsShellServices
    {
        Announcer = new RecordingAnnouncer(),
        BrowseSource = source,
        Playback = playback ?? new RecordingPlayback(),
        TemporaryDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lx-issue76-" + Guid.NewGuid().ToString("N"))
    });

    private static MusicInfo Song(string id) => new()
    {
        Id = id,
        Title = id,
        Source = "test-source"
    };

    private static ListBox RequiredListBox(Control root) =>
        Assert.IsType<ListBox>(Descendants<Control>(root).Single(control => control.Name == "SearchResults"));

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed) yield return typed;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
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
    private static async Task InvokeSearchAsync(WinFormsShell shell, bool reset)
    {
        var method = typeof(WinFormsShell).GetMethod(
            "SearchAsync",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            [typeof(bool)],
            null);
        Assert.NotNull(method);
        var task = method!.Invoke(shell, [reset]) as Task ?? throw new InvalidOperationException("SearchAsync did not return a Task.");
        await task;
        Application.DoEvents();
    }

    private static IReadOnlyList<string> SelectedSongIds(ListBox list) =>
        list.SelectedItems.Cast<object>()
            .OfType<ObjectBox<MusicInfo>>()
            .Select(item => item.Value.Id)
            .ToArray();

    private static MusicInfo? FocusedSong(ListBox list)
    {
        var item = list.Items[ListCaretIndex(list)];
        return item is ObjectBox<MusicInfo> box ? box.Value : null;
    }

    private static int ListCaretIndex(ListBox list) =>
        SendMessage(list.Handle, LBGetCaretIndex, IntPtr.Zero, IntPtr.Zero).ToInt32();

    private static void SetCaretIndex(ListBox list, int index) =>
        SendMessage(list.Handle, LBSetCaretIndex, (IntPtr)index, IntPtr.Zero);

    private const int LBGetCaretIndex = 0x019F;
    private const int LBSetCaretIndex = 0x019E;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    private sealed class PagedSearchSource(IReadOnlyList<IReadOnlyList<MusicInfo>> pages) : IMusicBrowseSource
    {
        public int Calls { get; private set; }

        public Task<PagedResult<MusicInfo>> SearchAsync(
            string query,
            int page,
            int pageSize,
            string searchType,
            string sourceId,
            CancellationToken cancellationToken)
        {
            var values = Calls < pages.Count ? pages[Calls] : Array.Empty<MusicInfo>();
            Calls++;
            return Task.FromResult(new PagedResult<MusicInfo>
            {
                CurrentPage = Calls,
                PageSize = 20,
                TotalItems = pages.Sum(items => items.Count),
                HasMore = Calls < pages.Count,
                Items = values.ToList()
            });
        }

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