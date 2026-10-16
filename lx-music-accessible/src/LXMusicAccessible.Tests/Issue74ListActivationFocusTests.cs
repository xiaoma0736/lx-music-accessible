using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>Issue #74: explicit list activation both loads data and moves focus to the result list.</summary>
public sealed class Issue74ListActivationFocusTests
{
    [Fact]
    public void Search_box_enter_loads_results_and_moves_focus_to_search_results()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new Issue72BrowseNavigationTests.PagedBrowseSource { SearchTotal = 3 };
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();

            var search = RequiredTextBox(shell);
            search.Text = "query";
            shell.Activate();
            search.Focus();
            Application.DoEvents();
            RaiseKeyDown(search, Keys.Enter);

            await WaitFor(() => source.SearchCalls == 1);
            var results = RequiredListBox(shell, "SearchResults");
            await WaitForList(results, 1);
            await WaitFor(() => shell.ActiveControl == results);
        });
    }

    [Fact]
    public void Search_load_more_entry_activates_on_double_click()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new Issue72BrowseNavigationTests.PagedBrowseSource { SearchTotal = 3 };
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();

            var search = RequiredTextBox(shell);
            search.Text = "query";
            RaiseKeyDown(search, Keys.Enter);
            await WaitFor(() => source.SearchCalls == 1);
            var results = RequiredListBox(shell, "SearchResults");
            await WaitForList(results, 2);

            WinFormsUi.SelectOnly(results, results.Items.Count - 1);
            InvokeDoubleClick(results);

            await WaitFor(() => source.SearchCalls == 2);
            Assert.Same(results, shell.ActiveControl);
        });
    }

    [Fact]
    public void Search_load_more_uses_focused_marker_in_single_select_list()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new Issue72BrowseNavigationTests.PagedBrowseSource { SearchTotal = 3 };
            using var shell = CreateShell(source);
            shell.Show();
            Application.DoEvents();

            var search = RequiredTextBox(shell);
            search.Text = "query";
            RaiseKeyDown(search, Keys.Enter);
            await WaitFor(() => source.SearchCalls == 1);
            var results = RequiredListBox(shell, "SearchResults");
            await WaitForList(results, 2);

            var markerIndex = results.Items.Count - 1;
            results.SetSelected(markerIndex, true);
            results.Focus();
            Application.DoEvents();
            RaiseKeyDown(results, Keys.Enter);
            await WaitFor(() => source.SearchCalls == 2);

            var newestSong = results.Items.Count - 2;
            Assert.True(results.GetSelected(newestSong), "The newest appended song must be selected.");
            Assert.False(results.GetSelected(results.Items.Count - 1), "The load-more marker must not stay selected.");
            Assert.Same(results, shell.ActiveControl);
        });
    }
    [Fact]
    public void Category_list_enter_moves_to_playlist_list_and_playlist_enter_moves_to_songs()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new Issue72BrowseNavigationTests.PagedBrowseSource();
            using var shell = CreateShell(source);
            shell.Show();
            LoadCategories(shell);
            var categories = RequiredListBox(shell, "CategoryList");
            await WaitForList(categories, 1);
            categories.SelectedIndex = 0;

            RaiseKeyDown(categories, Keys.Enter);
            await WaitFor(() => source.CategoryPlaylistCalls == 1);
            var playlists = RequiredListBox(shell, "CategoryPlaylists");
            await WaitForList(playlists, 1);
            Assert.Same(playlists, shell.ActiveControl);

            playlists.SelectedIndex = 0;
            RaiseKeyDown(playlists, Keys.Enter);
            await WaitFor(() => source.PlaylistSongCalls == 1);
            var songs = RequiredListBox(shell, "CategorySongs");
            await WaitForList(songs, 1);
            Assert.Same(songs, shell.ActiveControl);
        });
    }

    [Fact]
    public void Ranking_list_enter_moves_to_ranking_songs()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new Issue72BrowseNavigationTests.PagedBrowseSource();
            using var shell = CreateShell(source);
            shell.Show();
            LoadRankings(shell);
            var rankings = RequiredListBox(shell, "RankingList");
            await WaitForList(rankings, 1);
            rankings.SelectedIndex = 0;

            RaiseKeyDown(rankings, Keys.Enter);
            await WaitFor(() => source.RankingSongCalls == 1);
            var songs = RequiredListBox(shell, "RankingSongs");
            await WaitForList(songs, 1);
            Assert.Same(songs, shell.ActiveControl);
        });
    }

    private static WinFormsShell CreateShell(Issue72BrowseNavigationTests.PagedBrowseSource source) => new(new WinFormsShellServices
    {
        Announcer = new RecordingAnnouncer(),
        BrowseSource = source,
        Playback = new RecordingPlayback(),
        TemporaryDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lx-issue74-" + Guid.NewGuid().ToString("N"))
    });

    private static TextBox RequiredTextBox(Control root) => Descendants<TextBox>(root).Single();

    private static ListBox RequiredListBox(Control root, string name) =>
        Assert.IsType<ListBox>(Descendants<Control>(root).Single(control => control.Name == name));

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void LoadCategories(Control shell)
    {
        var method = shell.GetType().GetMethod("LoadCategories", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(shell, Array.Empty<object?>());
        Application.DoEvents();
    }

    private static void LoadRankings(Control shell)
    {
        var method = shell.GetType().GetMethod("LoadRankings", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(shell, Array.Empty<object?>());
        Application.DoEvents();
    }

    private static void RaiseKeyDown(Control control, Keys key)
    {
        control.Focus();
        Application.DoEvents();
        var method = control.GetType().GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(control, [new KeyEventArgs(key)]);
        Application.DoEvents();
    }

    private static void InvokeDoubleClick(Control control)
    {
        var method = control.GetType().GetMethod("OnDoubleClick", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(control, [EventArgs.Empty]);
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

    private static async Task WaitForList(ListBox list, int count) => await WaitFor(() => list.Items.Count >= count);
}
