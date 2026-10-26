using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>
/// Regression coverage for issue #69: routine playlist, ranking, category, search, album, saved-playlist
/// and personal-playlist browse announcements stay concise. Loaded item counts (“已加载 3 项”,
/// “已预览 5 首歌曲”) are forbidden, while selection, loading, empty-result, error and completion state
/// keeps being announced - including after an earlier browse outcome of the same session.
/// </summary>
public sealed class Issue69Tests
{
    /// <summary>Loaded item counts such as “3 项”, “2 首”, “5 个歌单” or “4 条” must never be spoken by a browse page.</summary>
    private static readonly Regex ForbiddenItemCount = new(@"\d+\s*(项|首|个|条)", RegexOptions.Compiled);

    [Fact]
    public async Task Empty_browse_results_keep_concise_state_without_item_counts()
    {
        var announcer = new RecordingAnnouncer();
        var workflow = CreateWorkflow(new Issue69BrowseSource { Empty = true }, announcer);

        workflow.SelectSearch("zzz", "song", "netease");
        await workflow.ActivateSearchAsync();
        workflow.SelectCategorySource("netease", "网易云音乐");
        await workflow.ActivateCategorySourceAsync();
        workflow.SelectCategory(Category());
        await workflow.ActivateCategoryAsync();
        workflow.SelectPlaylist(new Playlist { Id = "playlist-1", Name = "Playlist", Source = "netease" });
        await workflow.ActivatePlaylistAsync();
        workflow.SelectRankingSource("netease", "网易云音乐");
        await workflow.ActivateRankingSourceAsync();
        workflow.SelectRanking(new Ranking { Id = "ranking-1", Name = "Ranking", Source = "netease" });
        await workflow.ActivateRankingAsync();
        await workflow.ActivateAlbumAsync(new MusicInfo { Id = "album-1", Title = "空专辑", Source = "netease", IsAlbum = true });
        await workflow.ActivateSavedPlaylistAsync(SavedPlaylist());

        AssertAnnouncedConciseState(
            announcer,
            "未找到与 zzz 相关的结果",
            "暂无歌单分类",
            "该音源暂时没有可用歌单",
            "该歌单暂无歌曲",
            "暂无排行榜",
            "该排行榜暂无歌曲",
            "专辑《空专辑》暂无歌曲可显示");
        Assert.True(
            announcer.Announcements.Any(item => item.Kind == AnnouncementKind.Busy && item.Text.Contains("正在搜索", StringComparison.Ordinal)),
            "The loading state must stay announced without a count.");
    }

    [Fact]
    public async Task Completed_browse_pages_keep_concise_completion_without_item_counts()
    {
        var announcer = new RecordingAnnouncer();
        var workflow = CreateWorkflow(new Issue69BrowseSource(), announcer);

        workflow.SelectSearch("song", "song", "netease");
        await workflow.ActivateSearchAsync();
        await workflow.ActivateSearchAsync(reset: false);
        workflow.SelectCategory(Category());
        await workflow.ActivateCategoryAsync();
        workflow.SelectRanking(new Ranking { Id = "ranking-1", Name = "Ranking", Source = "netease" });
        await workflow.ActivateRankingAsync();
        await workflow.ActivateAlbumAsync(new MusicInfo { Id = "album-1", Title = "Album", Source = "netease", IsAlbum = true });
        await workflow.ActivateSavedPlaylistAsync(SavedPlaylist());

        AssertAnnouncedConciseState(announcer, "加载完成，还有更多", "加载完成");
    }

    [Fact]
    public void Browse_selection_descriptions_keep_context_without_item_counts()
    {
        var workflow = CreateWorkflow(new Issue69BrowseSource());
        var messages = new[]
        {
            workflow.SelectSearch("song", "song", "netease").Description,
            workflow.SelectCategorySource("netease", "网易云音乐").Description,
            workflow.SelectCategory(Category()).Description,
            workflow.SelectPlaylist(new Playlist { Id = "playlist-1", Name = "Playlist", Source = "netease" }).Description,
            workflow.SelectRankingSource("netease", "网易云音乐").Description,
            workflow.SelectRanking(new Ranking { Id = "ranking-1", Name = "Ranking", Source = "netease" }).Description
        };

        Assert.All(messages, message => Assert.False(ForbiddenItemCount.IsMatch(message), "Selection speech must not read a loaded item count: " + message));
        Assert.Contains("已选择分类：Tag，按 Enter 加载歌单", messages);
        Assert.Contains("已选择歌单：Playlist，按 Enter 加载歌曲", messages);
        Assert.Contains("已选择排行榜：Ranking，按 Enter 加载歌曲", messages);
    }

    [Fact]
    public void Playlist_import_load_and_preview_replace_item_counts_with_concise_state()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var service = new Issue69ImportService();
            service.Playlists.Add(RemotePlaylist("A"));
            service.Playlists.Add(RemotePlaylist("B"));
            service.Playlists.Add(RemotePlaylist("C"));
            service.Songs.Add(Song("song-1"));
            service.Songs.Add(Song("song-2"));
            var announcer = new RecordingAnnouncer();
            using var form = CreateImportForm(service, announcer);

            Click(form, "网易云音乐");
            Click(form, "加载歌单");
            await WaitFor(() => Texts(announcer).Contains("已加载个人歌单，回车可预览。"), "the personal playlists to load");
            AssertNoItemCounts(announcer);

            var playlists = Assert.IsType<ListBox>(Required(form, "个人歌单"));
            playlists.SelectedIndex = 0;
            Click(form, "预览歌曲");
            await WaitFor(() => Texts(announcer).Contains("已预览歌曲。"), "the song preview to load");
            AssertNoItemCounts(announcer);
        });
    }

    [Fact]
    public void Playlist_import_keeps_empty_result_state_without_item_counts()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var emptyAnnouncer = new RecordingAnnouncer();
            using (var form = CreateImportForm(new Issue69ImportService(), emptyAnnouncer))
            {
                Click(form, "网易云音乐");
                Click(form, "加载歌单");
                await WaitFor(() => Texts(emptyAnnouncer).Contains("没有找到个人歌单。"), "the empty personal playlist list");
                AssertAnnouncedConciseState(emptyAnnouncer, "没有找到个人歌单。");
            }

            var service = new Issue69ImportService();
            service.Playlists.Add(RemotePlaylist("A"));
            var announcer = new RecordingAnnouncer();
            using var second = CreateImportForm(service, announcer);
            Click(second, "网易云音乐");
            Click(second, "加载歌单");
            await WaitFor(() => Texts(announcer).Contains("已加载个人歌单，回车可预览。"), "the personal playlists to load");
            Assert.IsType<ListBox>(Required(second, "个人歌单")).SelectedIndex = 0;
            Click(second, "预览歌曲");
            await WaitFor(() => Texts(announcer).Contains("没有找到歌曲。"), "the empty song preview");
            AssertAnnouncedConciseState(announcer, "已加载个人歌单，回车可预览。", "没有找到歌曲。");
        });
    }

    /// <summary>
    /// Previewing moves the reading position into the song list, which a screen reader names together with
    /// its selected item, so a second sentence repeating 已预览歌曲 only interrupts the reader. The empty
    /// result has no item to read and therefore keeps its own speech.
    /// </summary>
    [Fact]
    public void Song_preview_echo_stays_silent_when_the_preview_list_takes_focus()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var service = new Issue69ImportService();
            service.Playlists.Add(RemotePlaylist("A"));
            service.Songs.Add(Song("song-1"));
            var announcer = new RecordingAnnouncer();
            using var form = CreateImportForm(service, announcer);
            Click(form, "网易云音乐");
            Click(form, "加载歌单");
            await WaitFor(() => Texts(announcer).Contains("已加载个人歌单，回车可预览。"), "the personal playlists to load");
            Assert.IsType<ListBox>(Required(form, "个人歌单")).SelectedIndex = 0;
            Click(form, "预览歌曲");
            await WaitFor(() => Texts(announcer).Contains("已预览歌曲。"), "the song preview to load");

            var preview = announcer.Announcements.Single(item => item.Text == "已预览歌曲。");
            Assert.False(
                AccessibleSpeechPolicy.ShouldSpeak(preview),
                "The preview list is read by the screen reader, so the success echo must stay silent.");
            Assert.True(
                AccessibleSpeechPolicy.ShouldSpeak(new AccessibleAnnouncement("已预览歌曲。", ActivityId: "playlist-preview")),
                "The activity itself stays speakable: only this call site is quiet.");

            var withoutSongs = new Issue69ImportService();
            withoutSongs.Playlists.Add(RemotePlaylist("B"));
            var emptyAnnouncer = new RecordingAnnouncer();
            using var empty = CreateImportForm(withoutSongs, emptyAnnouncer);
            Click(empty, "网易云音乐");
            Click(empty, "加载歌单");
            await WaitFor(() => Texts(emptyAnnouncer).Contains("已加载个人歌单，回车可预览。"), "the personal playlists to load");
            Assert.IsType<ListBox>(Required(empty, "个人歌单")).SelectedIndex = 0;
            Click(empty, "预览歌曲");
            await WaitFor(() => Texts(emptyAnnouncer).Contains("没有找到歌曲。"), "the empty song preview");

            Assert.True(
                AccessibleSpeechPolicy.ShouldSpeak(emptyAnnouncer.Announcements.Single(item => item.Text == "没有找到歌曲。")),
                "An empty list reads as nothing, so that outcome still has to be spoken.");
        });
    }

    [Fact]
    public void Every_browse_outcome_uses_a_fresh_activity_id()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new Issue69BrowseSource();
            var announcer = new RecordingAnnouncer();
            using var shell = CreateShell(source, announcer);
            shell.Show();
            Application.DoEvents();
            AddCategory(shell);

            shell.LoadCategoryPlaylistsForTests();
            await WaitFor(() => announcer.Announcements.Count(item => item.Text == "加载完成") == 1, "the first category playlist page");
            shell.LoadCategoryPlaylistsForTests();
            await WaitFor(() => announcer.Announcements.Count(item => item.Text == "加载完成") == 2, "the second category playlist page");
            source.FailCategoryPlaylists = true;
            shell.LoadCategoryPlaylistsForTests();
            await WaitFor(() => announcer.Announcements.Any(item => item.Kind == AnnouncementKind.Error), "the failed category playlist page");

            var outcomes = announcer.Announcements
                .Where(item => item.Kind == AnnouncementKind.Success || item.Kind == AnnouncementKind.Error)
                .Where(item => item.Text == "加载完成" || item.Kind == AnnouncementKind.Error)
                .ToArray();
            Assert.Equal(3, outcomes.Length);
            Assert.All(outcomes, item => Assert.False(string.IsNullOrWhiteSpace(item.ActivityId), "Every browse outcome must carry a live-region activity id."));
            Assert.Equal(3, outcomes.Select(item => item.ActivityId).Distinct().Count());
            Assert.Equal("加载失败，请检查网络", shell.StatusTextForTests.Text);
            AssertNoItemCounts(announcer);
            Assert.Contains("加载失败，请检查网络", Texts(announcer));
        });
    }

    [Fact]
    public void Later_browse_outcomes_reach_the_live_region_after_an_earlier_completion()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var source = new Issue69BrowseSource();
            var announcer = new WinFormsAccessibleAnnouncer(Array.Empty<ITtsService>(), NullLogger<WinFormsAccessibleAnnouncer>.Instance);
            using var shell = CreateShell(source, announcer);
            shell.Show();
            Application.DoEvents();
            var liveRegion = LiveRegionOf(shell);
            AddCategory(shell);

            shell.LoadCategoryPlaylistsForTests();
            await WaitFor(() => liveRegion.Text == "加载完成", "the first completion to reach the live region");

            source.Empty = true;
            shell.LoadCategoryPlaylistsForTests();
            await WaitFor(() => liveRegion.Text == "该音源暂时没有可用歌单", "the second outcome to replace the first one");

            source.Empty = false;
            source.FailCategoryPlaylists = true;
            shell.LoadCategoryPlaylistsForTests();
            await WaitFor(() => liveRegion.Text == "加载失败，请检查网络", "the failure to replace the earlier completion");
        });
    }

    [Fact]
    public async Task Cancelled_browse_activation_announces_only_loading_and_completion()
    {
        var started = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var announcer = new RecordingAnnouncer();
        var source = new Issue69BrowseSource
        {
            DelaySearchAsync = async cancellationToken =>
            {
                started.TrySetResult(null);
                await Task.Delay(500, cancellationToken);
            }
        };
        var workflow = CreateWorkflow(source, announcer);

        workflow.SelectSearch("first", "song", "netease");
        var cancelled = workflow.ActivateSearchAsync();
        await started.Task;
        workflow.SelectSearch("second", "song", "netease");
        var completed = workflow.ActivateSearchAsync();

        Assert.Equal(BrowseResultStatus.Superseded, (await cancelled).Status);
        Assert.Equal(BrowseResultStatus.Completed, (await completed).Status);
        Assert.Equal(2, announcer.Announcements.Count(item => item.Kind == AnnouncementKind.Busy));
        var terminal = announcer.Announcements.Where(item => item.Kind is AnnouncementKind.Success or AnnouncementKind.Error).ToArray();
        Assert.Single(terminal);
        Assert.Equal("加载完成，还有更多", terminal[0].Text);
        AssertNoItemCounts(announcer);
    }

    private static IReadOnlyList<string> Texts(RecordingAnnouncer announcer) =>
        announcer.Announcements.Select(item => item.Text).ToArray();

    private static void AssertNoItemCounts(RecordingAnnouncer announcer) =>
        Assert.All(Texts(announcer), message => Assert.False(ForbiddenItemCount.IsMatch(message), "Browse speech must not read a loaded item count: " + message));

    private static void AssertAnnouncedConciseState(RecordingAnnouncer announcer, params string[] expected)
    {
        AssertNoItemCounts(announcer);
        foreach (var text in expected)
            Assert.True(Texts(announcer).Contains(text), "The concise announcement “" + text + "” was not delivered.");
    }

    private static PlaylistCategory Category() => new()
    {
        Id = "tag",
        Name = "Tag",
        Tags = new List<PlaylistTag> { new() { Id = "tag", Name = "Tag" } }
    };

    private static Playlist SavedPlaylist() => new()
    {
        Id = "saved-1",
        Name = "Saved",
        Description = "远程收藏歌单",
        Type = PlaylistType.User,
        IsLocal = false,
        Source = "netease",
        RemoteId = "69"
    };

    private static RemotePlaylist RemotePlaylist(string name) => new()
    {
        Platform = "netease",
        RemoteId = "69" + name,
        Name = name,
        TrackCount = 2
    };

    private static MusicInfo Song(string id) => new() { Id = id, Title = id, Artist = "Artist", Source = "netease" };

    private static MusicBrowseWorkflow CreateWorkflow(IMusicBrowseSource source, RecordingAnnouncer? announcer = null) => new(
        source,
        new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance),
        announcer ?? new RecordingAnnouncer(),
        new NoPlayback(),
        new LocalSearchHistoryStore(Path.Combine(Path.GetTempPath(), "issue69-" + Guid.NewGuid().ToString("N"))),
        NullLogger<MusicBrowseWorkflow>.Instance);

    private static WinFormsShell CreateShell(IMusicBrowseSource source, IAccessibleAnnouncer announcer) => new(new WinFormsShellServices
    {
        Announcer = announcer,
        BrowseSource = source,
        Playback = new NoPlayback(),
        TemporaryDirectory = Path.Combine(Path.GetTempPath(), "issue69-shell-" + Guid.NewGuid().ToString("N"))
    });

    private static PlaylistImportForm CreateImportForm(IPlaylistImportService service, RecordingAnnouncer announcer)
    {
        var form = new PlaylistImportForm(service, announcer, new NoLoginFactory(), _ => { });
        form.Show();
        Application.DoEvents();
        return form;
    }

    private static Control Required(Control root, string name)
    {
        var found = root.Controls.Find(name, true);
        Assert.True(found.Length == 1, "Expected exactly one control named “" + name + "”.");
        return found[0];
    }

    private static void Click(Control root, string name)
    {
        ((Button)Required(root, name)).PerformClick();
        Application.DoEvents();
    }

    private static void AddCategory(WinFormsShell shell)
    {
        var list = Assert.IsType<ListBox>(Required(shell, "CategoryList"));
        list.Items.Add(new ObjectBox<PlaylistCategory>("Tag", Category()));
        list.SelectedIndex = 0;
    }

    private static Label LiveRegionOf(WinFormsShell shell) =>
        Assert.IsType<Label>(typeof(WinFormsShell)
            .GetField("LiveRegion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(shell));

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition(), "Timed out waiting for " + because + ".");
    }

    /// <summary>Deterministic browse source whose every list and page can be emptied and whose category page can fail.</summary>
    private sealed class Issue69BrowseSource : IMusicBrowseSource
    {
        public bool Empty { get; set; }

        public bool FailCategoryPlaylists { get; set; }

        /// <summary>Lets a test hold the first search open so a newer selection cancels it.</summary>
        public Func<CancellationToken, Task>? DelaySearchAsync { get; set; }

        public async Task<PagedResult<MusicInfo>> SearchAsync(string query, int page, int pageSize, string searchType, string sourceId, CancellationToken cancellationToken)
        {
            if (DelaySearchAsync is not null) await DelaySearchAsync(cancellationToken);
            return SongPage(page, Empty ? 0 : 2);
        }

        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(SongPage(page, Empty ? 0 : 1));

        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(Empty ? new List<PlaylistCategory>() : new List<PlaylistCategory> { Category() });

        public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken cancellationToken)
        {
            if (FailCategoryPlaylists) throw new HttpRequestException("fixture network");
            return Task.FromResult(new PagedResult<Playlist>
            {
                CurrentPage = page,
                PageSize = 20,
                TotalItems = Empty ? 0 : 2,
                HasMore = !Empty && page < 2,
                Items = Empty
                    ? new List<Playlist>()
                    : new List<Playlist> { new() { Id = "playlist-" + page, Name = "Playlist " + page, Source = sourceId } }
            });
        }

        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(SongPage(page, Empty ? 0 : 2));

        public Task<List<Ranking>> GetRankingsAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(Empty
                ? new List<Ranking>()
                : new List<Ranking> { new() { Id = "ranking-1", Name = "Ranking", Source = sourceId } });

        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(SongPage(page, Empty ? 0 : 2));

        private static PagedResult<MusicInfo> SongPage(int page, int total) => new()
        {
            CurrentPage = page,
            PageSize = 20,
            TotalItems = total,
            HasMore = page < total,
            Items = total == 0
                ? new List<MusicInfo>()
                : new List<MusicInfo> { new() { Id = "song-" + page, Title = "Song " + page, Source = "netease" } }
        };
    }

    private sealed class NoPlayback : IPlaybackCoordinator
    {
        public bool IsPlaying => false;

        public bool IsPaused => false;

        public MusicInfo? CurrentMusic => null;

        public PlayMode Mode { get; set; }

        public string DefaultQuality { get; set; } = "128k";

        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted
        {
            add { }
            remove { }
        }

        public Task<PlaybackResult> PlayAsync(PlaybackRequest request) =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, null, PlaybackQueueOrigin.Static, string.Empty));

        public Task<PlaybackResult> ReplayAsync() => Task.FromResult(NoChange());

        public void Pause()
        {
        }

        public void Stop()
        {
        }

        public Task<PlaybackResult> NextAsync() => Task.FromResult(NoChange());

        public Task<PlaybackResult> PreviousAsync() => Task.FromResult(NoChange());

        public Task ResumeAsync() => Task.CompletedTask;

        private static PlaybackResult NoChange() =>
            new(PlaybackResultStatus.NoChange, null, PlaybackQueueOrigin.Static, string.Empty);
    }

    private sealed class NoLoginFactory : IPlaylistLoginWindowFactory
    {
        public PlaylistLoginMethod? SelectLoginMethod(string platform, Form? owner) => PlaylistLoginMethod.Cookie;

        public PlaylistLoginDialogResult ShowWebLogin(string platform, Form? owner) => new(false);

        public bool ShowKugouQrLogin(Form? owner) => false;
    }

    private sealed class Issue69ImportService : IPlaylistImportService
    {
        public List<RemotePlaylist> Playlists { get; } = new();

        public List<MusicInfo> Songs { get; } = new();

        public AccountInfo? Account { get; set; }

        public IReadOnlyList<IPlaylistImportProvider> Providers => Array.Empty<IPlaylistImportProvider>();

        public IPlaylistImportProvider? GetProvider(string platformId) => null;

        public Task EnsureAccountsLoadedAsync() => Task.CompletedTask;

        public AccountInfo? GetAccount(string platformId) => Account;

        public Task<AccountInfo?> LoginWithCookieAsync(string platformId, string cookieText) => Task.FromResult(Account);

        public Task<AccountInfo?> LoginWithBrowserResultAsync(string platformId, string cookieText, string? displayName, List<RemotePlaylist>? playlists) => Task.FromResult(Account);

        public Task LogoutAsync(string platformId) => Task.CompletedTask;

        public Task<List<RemotePlaylist>> GetMyPlaylistsAsync(string platformId) => Task.FromResult(new List<RemotePlaylist>(Playlists));

        public Task<List<MusicInfo>> GetPlaylistSongsPreviewAsync(string platformId, RemotePlaylist remote) => Task.FromResult(new List<MusicInfo>(Songs));

        public Task<PlaylistImportResult> ImportPlaylistAsync(string platformId, RemotePlaylist remote, Action<string>? announce = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PlaylistImportResult>> ImportPlaylistsAsync(string platformId, IReadOnlyList<RemotePlaylist> remotes, Action<string>? announce = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
