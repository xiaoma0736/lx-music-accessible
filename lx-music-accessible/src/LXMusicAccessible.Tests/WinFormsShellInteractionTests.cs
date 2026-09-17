using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
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

public sealed class WinFormsShellInteractionTests
{
    [Fact]
    public void Category_and_ranking_pages_load_content_when_opened()
    {
        var source = new InteractionBrowseSource();
        var announcer = new RecordingAnnouncer();
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var shell = CreateShell(source, announcer);
            shell.Show();
            Application.DoEvents();

            var shellLists = Lists(shell);
            Tabs(shell).SelectedIndex = 2;
            var categories = await AwaitList(shellLists[3], () => source.CategoryCalls > 0);
            Assert.Equal("netease", source.LastBrowseSource);
            Assert.NotEmpty(categories.Items);

            Select(categories, 0);
            shell.LoadCategoryPlaylistsForTests();
            var playlists = await AwaitList(shellLists[4], () => source.PlaylistCalls > 0);
            Assert.NotEmpty(playlists.Items);

            Select(playlists, 0);
            await shell.LoadCategorySongsForTests();
            var categorySongs = await AwaitList(shellLists[5], () => source.PlaylistSongCalls > 0);
            Assert.NotEmpty(categorySongs.Items);

            Tabs(shell).SelectedIndex = 3;
            var rankings = await AwaitList(shellLists[6], () => source.RankingCalls > 0);
            Assert.NotEmpty(rankings.Items);

            Select(rankings, 0);
            await shell.LoadRankingSongsForTests();
            var rankingSongs = await AwaitList(shellLists[7], () => source.RankingSongCalls > 0);
            Assert.NotEmpty(rankingSongs.Items);
        });
    }

    [Fact]
    public void Search_result_enter_plays_the_selected_song()
    {
        var source = new InteractionBrowseSource();
        var playback = new RecordingPlayback();
        var announcer = new RecordingAnnouncer();
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var shell = CreateShell(source, announcer, playback);
            shell.Show();
            Application.DoEvents();
            Assert.Null(shell.AcceptButton);

            var searchBox = Descendants<TextBox>(shell).Single();
            searchBox.Text = "song";
            await InvokeAsync(shell, "SearchAsync", true);
            var results = await AwaitList(Lists(shell)[0], () => source.SearchCalls > 0);
            Assert.NotEmpty(results.Items);
            Select(results, 0);

            RaiseKeyDown(results, Keys.Enter);
            await WaitFor(() => playback.Requests.Count == 1);
            Assert.Equal("song-1", playback.Requests.Single().Selection.Id);
            Assert.Equal(PlaybackQueueOrigin.Search, playback.Requests.Single().Origin);
        });
    }

    [Fact]
    public void Playback_control_bar_is_removed()
    {
        RunSta(() =>
        {
            using var shell = CreateShell(new InteractionBrowseSource(), new RecordingAnnouncer());
            foreach (var name in new[] { "播放/暂停", "停止", "上一首", "下一首" })
                Assert.Empty(shell.Controls.Find(name, true));
            Assert.DoesNotContain(shell.Controls.OfType<Control>(), control => control is SplitContainer);
        });
    }

    [Theory]
    [InlineData("categories")]
    [InlineData("rankings")]
    [InlineData("browse-status")]
    [InlineData("music-browse:1")]
    [InlineData("search-results")]
    [InlineData("settings")]
    [InlineData("welcome")]
    public void Optional_browse_and_settings_messages_do_not_duplicate_screen_reader_speech(string activityId)
    {
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(new AccessibleAnnouncement("状态", ActivityId: activityId)));
    }

    [Theory]
    [InlineData("playback-song")]
    [InlineData("lyrics")]
    [InlineData("download:1")]
    public void High_value_playback_download_and_lyric_messages_can_speak(string activityId)
    {
        Assert.True(AccessibleSpeechPolicy.ShouldSpeak(new AccessibleAnnouncement("状态", ActivityId: activityId)));
    }

    private static WinFormsShell CreateShell(InteractionBrowseSource source, RecordingAnnouncer announcer, RecordingPlayback? playback = null) =>
        new(new WinFormsShellServices
        {
            Announcer = announcer,
            BrowseSource = source,
            Playback = playback ?? new RecordingPlayback(),
            TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-shell-interaction-" + Guid.NewGuid().ToString("N"))
        });

    private static TabControl Tabs(Form shell) => Descendants<TabControl>(shell).Single();

    private static List<ListBox> Lists(Form shell) => Descendants<ListBox>(shell).ToList();

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed) yield return typed;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }

    /// <summary>Waits for background work to land in the list without blocking the message loop.</summary>
    private static async Task<ListBox> AwaitList(ListBox list, Func<bool> loaded)
    {
        await WaitFor(loaded);
        return list;
    }

    private static void Select(ListBox list, int index)
    {
        list.SelectedIndex = index;
        Application.DoEvents();
    }

    /// <summary>
    /// Invokes a shell command that starts background work and awaits it, so a command whose continuation
    /// belongs to this STA thread can never deadlock the test against its own message loop.
    /// </summary>
    private static async Task InvokeAsync(Form shell, string method, params object?[] arguments)
    {
        var target = shell.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(candidate => candidate.Name == method && candidate.GetParameters().Length >= arguments.Length);
        var parameters = target.GetParameters();
        var invokeArguments = parameters.Select((parameter, index) => index < arguments.Length
            ? arguments[index]
            : parameter.HasDefaultValue ? parameter.DefaultValue : Type.Missing).ToArray();
        await (Task)target.Invoke(shell, invokeArguments)!;
    }

    private static void RaiseKeyDown(Control control, Keys key)
    {
        control.Focus();
        Application.DoEvents();
        var field = typeof(Control).GetField("EventKeyDown", BindingFlags.NonPublic | BindingFlags.Static);
        var keyObject = field?.GetValue(control);
        if (keyObject is null) return;
        var eventsProperty = typeof(Control).GetProperty("Events", BindingFlags.NonPublic | BindingFlags.Instance);
        var events = (EventHandlerList?)eventsProperty?.GetValue(control);
        var handler = events?[keyObject] as KeyEventHandler;
        handler?.Invoke(control, new KeyEventArgs(key));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "shell condition was not reached");
    }

    private static void RunSta(Action test) => WinFormsTestRunner.Run(test);
}

public sealed class RecordingPlayback : IPlaybackCoordinator
{
    public List<PlaybackRequest> Requests { get; } = new();
    public bool IsPlaying => true;
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
        Requests.Add(request);
        CurrentMusic = request.Selection;
        return Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, request.Selection, request.Origin, "playing"));
    }
    public Task<PlaybackResult> ReplayAsync() => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
    public void Pause() { }
    public void Stop() { }
    public Task<PlaybackResult> NextAsync() => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
    public Task<PlaybackResult> PreviousAsync() => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
    public Task ResumeAsync() => Task.CompletedTask;
}

public sealed class InteractionBrowseSource : IMusicBrowseSource
{
    public int SearchCalls { get; private set; }
    public int CategoryCalls { get; private set; }
    public int PlaylistCalls { get; private set; }
    public int PlaylistSongCalls { get; private set; }
    public int RankingCalls { get; private set; }
    public int RankingSongCalls { get; private set; }
    public string LastBrowseSource { get; private set; } = "";

    public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page, int pageSize, string searchType, string sourceId, CancellationToken cancellationToken)
    {
        SearchCalls++;
        return Task.FromResult(Page(new MusicInfo { Id = "song-1", Title = "Song", Artist = "Artist", Source = "netease" }, 1));
    }

    public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken cancellationToken) =>
        Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));

    public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken cancellationToken)
    {
        CategoryCalls++;
        LastBrowseSource = sourceId;
        return Task.FromResult(new List<PlaylistCategory>
        {
            new() { Id = sourceId, Name = "分类", Tags = new List<PlaylistTag> { new() { Id = "tag", Name = "标签" } } }
        });
    }

    public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken cancellationToken)
    {
        PlaylistCalls++;
        return Task.FromResult(Page(new Playlist { Id = "playlist", Name = "Playlist", Source = sourceId }, 1));
    }

    public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page, int pageSize, CancellationToken cancellationToken)
    {
        PlaylistSongCalls++;
        return Task.FromResult(Page(new MusicInfo { Id = "playlist-song", Title = "Playlist Song", Artist = "Artist", Source = "netease" }, 1));
    }

    public Task<List<Ranking>> GetRankingsAsync(string sourceId, CancellationToken cancellationToken)
    {
        RankingCalls++;
        LastBrowseSource = sourceId;
        return Task.FromResult(new List<Ranking> { new() { Id = "ranking", Name = "Ranking", Source = sourceId } });
    }

    public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page, int pageSize, CancellationToken cancellationToken)
    {
        RankingSongCalls++;
        return Task.FromResult(Page(new MusicInfo { Id = "ranking-song", Title = "Ranking Song", Artist = "Artist", Source = "netease" }, 1));
    }

    private static PagedResult<T> Page<T>(T item, int total) => new()
    {
        Items = new List<T> { item },
        CurrentPage = 1,
        PageSize = 1,
        TotalItems = total,
        HasMore = false
    };
}