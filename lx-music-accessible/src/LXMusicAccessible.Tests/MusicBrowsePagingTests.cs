using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using System.Threading.Tasks;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class MusicBrowsePagingTests
{
    [Fact]
    public async Task Category_playlists_request_each_page_and_track_the_selected_page()
    {
        var source = new PagedBrowseSource();
        var workflow = CreateWorkflow(source);
        workflow.SelectCategory(new PlaylistCategory { Id = "tag", Name = "Tag" });

        var first = await workflow.ActivateCategoryAsync(1);
        var last = await workflow.ActivateCategoryAsync(2);

        Assert.Equal(BrowseResultStatus.Completed, first.Status);
        Assert.Equal(BrowseResultStatus.Completed, last.Status);
        Assert.Equal(new[] { 1, 2 }, source.CategoryPages);
        Assert.Equal(2, workflow.CurrentCategoryPage!.Page);
        Assert.Single(last.Value!.Items, playlist => playlist.Id == "playlist-2");
    }

    [Fact]
    public async Task Selecting_a_new_category_supersedes_an_inflight_playlist_page()
    {
        var firstStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new PagedBrowseSource
        {
            DelayFirstPageAsync = async cancellation =>
            {
                firstStarted.TrySetResult(null);
                await Task.Delay(100, cancellation);
            }
        };
        var workflow = CreateWorkflow(source);
        workflow.SelectCategory(new PlaylistCategory { Id = "old", Name = "Old" });
        var oldActivation = workflow.ActivateCategoryAsync(1);
        await firstStarted.Task;

        var selection = workflow.SelectCategory(new PlaylistCategory { Id = "new", Name = "New" });
        var latestActivation = workflow.ActivateCategoryAsync(1);

        Assert.Equal(BrowseFocusTarget.KeepCurrent, selection.FocusTarget);
        Assert.Equal(BrowseResultStatus.Superseded, (await oldActivation).Status);
        Assert.Equal(BrowseResultStatus.Completed, (await latestActivation).Status);
        Assert.Equal("new", workflow.CurrentCategoryPage!.Category.Id);
    }

    [Fact]
    public async Task Production_browse_source_does_not_hide_provider_failures()
    {
        using var app = await LXMusicAccessible.Tests.Infrastructure.TestApplicationHost.StartAsync();
        var source = app.Services.GetRequiredService<IMusicBrowseSource>();
        await Assert.ThrowsAnyAsync<Exception>(() => source.GetPlaylistsByCategoryAsync("does-not-exist", 1, 20, "netease", CancellationToken.None));
    }

    private static MusicBrowseWorkflow CreateWorkflow(IMusicBrowseSource source) => new(
        source,
        new BackgroundWorkHost(Microsoft.Extensions.Logging.Abstractions.NullLogger<BackgroundWorkHost>.Instance),
        new RecordingAnnouncer(),
        new NoPlayback(),
        new LocalSearchHistoryStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "browse-tests-" + Guid.NewGuid().ToString("N"))),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicBrowseWorkflow>.Instance);

    private sealed class NoPlayback : IPlaybackCoordinator
    {
        public bool IsPlaying => false;
        public bool IsPaused => false;
        public MusicInfo? CurrentMusic => null;
        public PlayMode Mode { get; set; }
        public string DefaultQuality { get; set; } = "128k";
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted { add { } remove { } }
        public Task<PlaybackResult> PlayAsync(PlaybackRequest request) => Task.FromResult(
            new PlaybackResult(PlaybackResultStatus.Playing, request.Selection, request.Origin, "playing"));
        public Task<PlaybackResult> ReplayAsync() => Task.FromResult(NoChange());
        public void Pause() { }
        public void Stop() { }
        public Task<PlaybackResult> NextAsync() => Task.FromResult(NoChange());
        public Task<PlaybackResult> PreviousAsync() => Task.FromResult(NoChange());
        public Task ResumeAsync() => Task.CompletedTask;
        private static PlaybackResult NoChange() => new(PlaybackResultStatus.NoChange, null, PlaybackQueueOrigin.Static, string.Empty);
    }

    private sealed class PagedBrowseSource : IMusicBrowseSource
    {
        public List<int> CategoryPages { get; } = new();
        public Func<CancellationToken, Task>? DelayFirstPageAsync { get; init; }

        public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page, int pageSize, string searchType, string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<MusicInfo> { CurrentPage = page, PageSize = pageSize });

        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<MusicInfo> { CurrentPage = page, PageSize = pageSize });

        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(new List<PlaylistCategory>());

        public async Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken cancellationToken)
        {
CategoryPages.Add(page);
            if (page == 1 && DelayFirstPageAsync is not null) await DelayFirstPageAsync(cancellationToken);
            var playlists = new List<Playlist>();
            if (page <= 2) playlists.Add(new Playlist { Id = "playlist-" + page, Name = category + " " + page });
            return new PagedResult<Playlist>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = 2,
                Items = playlists
            };
        }

        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<MusicInfo> { CurrentPage = page, PageSize = pageSize });

        public Task<List<Ranking>> GetRankingsAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(new List<Ranking>());

        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<MusicInfo> { CurrentPage = page, PageSize = pageSize });
    }
}
