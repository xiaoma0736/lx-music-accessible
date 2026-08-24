using System.IO;
using System.Net;
using System.Net.Http;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class MusicBrowseWorkflowTests
{
    [Fact]
    public async Task Public_selection_commands_are_local_and_keep_focus()
    {
        await using var app = await StartBrowseHostAsync();
        var workflow = app.Services.GetRequiredService<MusicBrowseWorkflow>();

        var selections = new[]
        {
            workflow.SelectSearch("query", "song", "netease"),
            workflow.SelectCategorySource("netease", "网易云音乐"),
            workflow.SelectCategory(Category("all", "全部")),
            workflow.SelectPlaylist(Playlist("netease_1", "歌单一")),
            workflow.SelectRankingSource("netease", "网易云音乐"),
            workflow.SelectRanking(Ranking("netease_1", "榜单一"))
        };

        Assert.Empty(app.HttpHandler.Requests);
        Assert.All(selections, selection =>
        {
            Assert.Equal(BrowseFocusTarget.KeepCurrent, selection.FocusTarget);
            Assert.False(string.IsNullOrWhiteSpace(selection.Description));
        });
    }

    [Fact]
    public async Task Each_explicit_activation_sends_one_request_and_returns_focus_policy()
    {
        await using var app = await StartBrowseHostAsync();
        var workflow = app.Services.GetRequiredService<MusicBrowseWorkflow>();
        RegisterOk(app.HttpHandler,
            "categories/netease",
            "playlists/netease/all",
            "playlist/netease_1?page=1",
            "playlist/remote_1?page=1",
            "rankings/netease",
            "ranking/netease_1?page=1",
            "search/netease/song/query?page=1");

        workflow.SelectCategorySource("netease");
        AssertFocus(await workflow.ActivateCategorySourceAsync(), BrowseFocusTarget.CategoryList);
        workflow.SelectCategory(Category("all", "全部"));
        AssertFocus(await workflow.ActivateCategoryAsync(), BrowseFocusTarget.CategoryPlaylists);
        workflow.SelectPlaylist(Playlist("netease_1", "歌单一"));
        AssertFocus(await workflow.ActivatePlaylistAsync(), BrowseFocusTarget.CategorySongs);
        AssertFocus(
            await workflow.ActivateSavedPlaylistAsync(new Playlist
            {
                Id = "saved_1",
                Name = "收藏歌单",
                RemoteId = "remote_1",
                Type = PlaylistType.Cloud
            }),
            BrowseFocusTarget.SavedPlaylistSongs);
        workflow.SelectRankingSource("netease");
        AssertFocus(await workflow.ActivateRankingSourceAsync(), BrowseFocusTarget.RankingList);
        workflow.SelectRanking(Ranking("netease_1", "榜单一"));
        AssertFocus(await workflow.ActivateRankingAsync(), BrowseFocusTarget.RankingSongs);
        workflow.SelectSearch("query", "song", "netease");
        AssertFocus(await workflow.ActivateSearchAsync(), BrowseFocusTarget.SearchResults);

        Assert.Equal(7, app.HttpHandler.Requests.Count);
        Assert.Equal(7, app.HttpHandler.Requests.Select(request => request.Uri).Distinct().Count());
        Assert.Equal(7, app.Announcer.Announcements.Count(a => a.Kind == AnnouncementKind.Busy));
        Assert.Equal(7, app.Announcer.Announcements.Count(a => a.Kind == AnnouncementKind.Success));
    }

    [Fact]
    public async Task Rapid_selection_discards_late_old_response_and_only_latest_completes()
    {
        await using var app = await StartBrowseHostAsync();
        var workflow = app.Services.GetRequiredService<MusicBrowseWorkflow>();
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var firstUri = BrowseUri("playlists/netease/first");
        var secondUri = BrowseUri("playlists/netease/second");
        app.HttpHandler.RegisterAsync(HttpMethod.Get, firstUri, async _ =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task;
            return Ok();
        });
        app.HttpHandler.Register(HttpMethod.Get, secondUri, Ok);

        workflow.SelectCategory(Category("first", "旧分类"));
        var oldActivation = workflow.ActivateCategoryAsync();
        await firstStarted.Task;

        var selection = workflow.SelectCategory(Category("second", "新分类"));
        var latestActivation = workflow.ActivateCategoryAsync();
        releaseFirst.TrySetResult();

        var oldResult = await oldActivation;
        var latestResult = await latestActivation;

        Assert.Equal(BrowseFocusTarget.KeepCurrent, selection.FocusTarget);
        Assert.Equal(BrowseResultStatus.Superseded, oldResult.Status);
        Assert.Equal(BrowseResultStatus.Completed, latestResult.Status);
        Assert.Equal("second", Assert.Single(latestResult.Value!.Items).Id);
        Assert.True(workflow.IsCurrent(latestResult.Revision));
        workflow.SelectCategory(Category("third", "更新分类"));
        Assert.False(workflow.IsCurrent(latestResult.Revision));
        Assert.Equal(new[] { firstUri.AbsoluteUri, secondUri.AbsoluteUri },
            app.HttpHandler.Requests.Select(request => request.Uri));
        Assert.Single(app.Announcer.Announcements, a => a.Kind == AnnouncementKind.Success);
    }

    [Fact]
    public async Task Production_adapter_cancels_the_inflight_http_request_when_selection_changes()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var workflow = app.Services.GetRequiredService<MusicBrowseWorkflow>();
        var started = NewSignal();
        var cancelled = NewSignal();
        app.HttpHandler.RegisterAsync(
            HttpMethod.Post,
            new Uri("https://music.163.com/weapi/playlist/catalogue"),
            async cancellationToken =>
            {
                using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Ok();
            });

        workflow.SelectCategorySource("netease");
        var activation = workflow.ActivateCategorySourceAsync();
        await started.Task;

        workflow.SelectCategorySource("qq");

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BrowseResultStatus.Superseded, (await activation).Status);
        Assert.Single(app.HttpHandler.Requests);
    }

    [Fact]
    public async Task Current_failure_uses_the_shared_busy_and_error_announcements()
    {
        await using var app = await StartBrowseHostAsync();
        var workflow = app.Services.GetRequiredService<MusicBrowseWorkflow>();
        app.HttpHandler.RegisterAsync(
            HttpMethod.Get,
            BrowseUri("rankings/fail"),
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture")));

        workflow.SelectRankingSource("fail");
        var result = await workflow.ActivateRankingSourceAsync();

        Assert.Equal(BrowseResultStatus.Failed, result.Status);
        Assert.Equal(BrowseFocusTarget.KeepCurrent, result.FocusTarget);
        Assert.Collection(
            app.Announcer.Announcements,
            busy => Assert.Equal(AnnouncementKind.Busy, busy.Kind),
            error => Assert.Equal(AnnouncementKind.Error, error.Kind));
        Assert.Equal(
            app.Announcer.Announcements[0].ActivityId,
            app.Announcer.Announcements[1].ActivityId);
    }

    [Fact]
    public void Browse_controls_expose_explicit_action_semantics_without_selection_side_effects()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "LXMusicAccessible.App", "MainWindow.xaml"));
        var window = File.ReadAllText(Path.Combine(root, "src", "LXMusicAccessible.App", "MainWindow.xaml.cs"));

        Assert.Contains("AutomationProperties.HelpText=\"上下方向键选择音源，按 Enter 或刷新加载分类\"", xaml);
        Assert.Contains("KeyDown=\"CategorySourceCombo_KeyDown\"", xaml);
        Assert.Contains("AutomationProperties.HelpText=\"上下方向键选择，按 Enter 加载歌单\"", xaml);
        Assert.Contains("AutomationProperties.HelpText=\"上下方向键选择，按 Enter 加载歌曲\"", xaml);
        Assert.Contains("KeyDown=\"RankingSourceCombo_KeyDown\"", xaml);
        Assert.Contains("SelectionChanged=\"OnlinePlaylistList_SelectionChanged\"", xaml);

        foreach (var method in new[]
        {
            "CategorySourceCombo_SelectionChanged",
            "CategoryList_SelectionChanged",
            "CategoryPlaylistsListBox_SelectionChanged",
            "OnlinePlaylistList_SelectionChanged",
            "RankingSourceCombo_SelectionChanged",
            "RankingList_SelectionChanged"
        })
        {
            var body = ExtractMethod(window, method);
            Assert.DoesNotContain("await ", body, StringComparison.Ordinal);
            Assert.DoesNotContain("_musicSourceService", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Focus", body, StringComparison.Ordinal);
        }
    }

    private static async Task<TestApplicationHost> StartBrowseHostAsync() =>
        await TestApplicationHost.StartAsync(services =>
            services.Replace(ServiceDescriptor.Singleton<IMusicBrowseSource>(sp =>
                new HttpBrowseSourceAdapter(sp.GetRequiredService<HttpClient>()))));

    private static void AssertFocus<T>(BrowseResult<T> result, BrowseFocusTarget focus)
    {
        Assert.Equal(BrowseResultStatus.Completed, result.Status);
        Assert.Equal(focus, result.FocusTarget);
    }

    private static void RegisterOk(RejectingHttpMessageHandler handler, params string[] paths)
    {
        foreach (var path in paths) handler.Register(HttpMethod.Get, BrowseUri(path), Ok);
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("ok")
    };

    private static Uri BrowseUri(string path) => new($"https://browse.test/{path}");

    private static PlaylistCategory Category(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Tags = [new PlaylistTag { Id = id, Name = name }]
    };

    private static Playlist Playlist(string id, string name) => new() { Id = id, Name = name };
    private static Ranking Ranking(string id, string name) => new() { Id = id, Name = name, Source = "netease" };

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "verify.ps1")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string ExtractMethod(string source, string name)
    {
        var start = source.IndexOf(name, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method {name} was not found.");
        var next = source.IndexOf("\n    private ", start + name.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private sealed class HttpBrowseSourceAdapter(HttpClient client) : IMusicBrowseSource
    {
        public async Task<PagedResult<MusicInfo>> SearchAsync(
            string query,
            int page,
            int pageSize,
            string searchType,
            string sourceId,
            CancellationToken cancellationToken)
        {
            await TouchAsync($"search/{sourceId}/{searchType}/{Uri.EscapeDataString(query)}?page={page}", cancellationToken);
            return new PagedResult<MusicInfo>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = 1,
                Items = [new MusicInfo { Id = query, Title = query, Source = sourceId }]
            };
        }

        public async Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(
            MusicInfo album,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            await TouchAsync($"album/{album.Id}?page={page}", cancellationToken);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }

        public async Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(
            string sourceId,
            CancellationToken cancellationToken)
        {
            await TouchAsync($"categories/{sourceId}", cancellationToken);
            return [Category("all", "全部")];
        }

        public async Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(
            string category,
            int page,
            int pageSize,
            string sourceId,
            CancellationToken cancellationToken)
        {
            await TouchAsync($"playlists/{sourceId}/{category}", cancellationToken);
            return new PagedResult<Playlist>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = 1,
                Items = [Playlist(category, category)]
            };
        }

        public async Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(
            string playlistId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            await TouchAsync($"playlist/{playlistId}?page={page}", cancellationToken);
            return SongPage(playlistId, page, pageSize);
        }

        public async Task<List<Ranking>> GetRankingsAsync(
            string sourceId,
            CancellationToken cancellationToken)
        {
            await TouchAsync($"rankings/{sourceId}", cancellationToken);
            return [Ranking($"{sourceId}_1", "榜单一")];
        }

        public async Task<PagedResult<MusicInfo>> GetRankingDetailAsync(
            string rankingId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            await TouchAsync($"ranking/{rankingId}?page={page}", cancellationToken);
            return SongPage(rankingId, page, pageSize);
        }

        private async Task TouchAsync(string path, CancellationToken cancellationToken)
        {
            using var response = await client.GetAsync(BrowseUri(path), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            response.EnsureSuccessStatusCode();
        }

        private static PagedResult<MusicInfo> SongPage(string id, int page, int pageSize) => new()
        {
            CurrentPage = page,
            PageSize = pageSize,
            TotalItems = 1,
            Items = [new MusicInfo { Id = id, Title = id, Source = "netease" }]
        };
    }
}
