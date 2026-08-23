using System.IO;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class WorkflowConsolidationTests
{
    [Fact]
    public async Task Search_history_has_one_owner_and_persists_without_the_window()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var workflow = app.Services.GetRequiredService<MusicBrowseWorkflow>();

        await workflow.LoadSearchHistoryAsync();
        await workflow.RecordSearchAsync("first");
        await workflow.RecordSearchAsync("second");
        await workflow.RecordSearchAsync("first");

        Assert.Equal("first", workflow.NavigateSearchHistory("draft", 1).Text);
        Assert.Equal("second", workflow.NavigateSearchHistory("first", 1).Text);
        Assert.Equal("first", workflow.NavigateSearchHistory("second", -1).Text);
        Assert.Equal("draft", workflow.NavigateSearchHistory("first", -1).Text);
        Assert.Equal("first", await workflow.DeleteSearchHistoryAsync("first"));
        Assert.Equal("second", Assert.Single(
            await app.Services.GetRequiredService<LocalSearchHistoryStore>().LoadAsync()).Keyword);
        Assert.True(await workflow.ClearSearchHistoryAsync());
        Assert.Empty(await app.Services.GetRequiredService<LocalSearchHistoryStore>().LoadAsync());
    }

    [Fact]
    public async Task Saved_playlist_commands_share_one_state_and_atomic_data_path()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var workflow = app.Services.GetRequiredService<SavedPlaylistWorkflow>();

        Assert.NotNull((await workflow.LoadAsync()).Snapshot.Selected);
        var created = await workflow.CreateAsync("fixture");
        Assert.Equal("fixture", created.Snapshot.Selected!.Name);
        var renamed = await workflow.RenameSelectedAsync("renamed");
        Assert.Equal("renamed", renamed.Snapshot.Selected!.Name);

        var song = new MusicInfo { Id = "song-1", Title = "Song", Source = "netease" };
        var added = await workflow.AddSongsAsync([song, song]);
        Assert.Contains("已添加 1 首", added.Message);
        var activated = await workflow.ActivateAsync();
        Assert.Equal("song-1", Assert.Single(activated.Snapshot.Songs).Id);
        Assert.Empty((await workflow.RemoveSongAsync(song)).Snapshot.Songs);

        var remote = new RemotePlaylistFavorite("remote", "netease", "remote-1", PlaylistType.Cloud);
        Assert.True((await workflow.CollectRemoteAsync(remote)).Succeeded);
        Assert.False((await workflow.CollectRemoteAsync(remote)).Succeeded);
    }

    [Fact]
    public async Task Browse_module_owns_paged_playback_continuation()
    {
        var playback = new RecordingPlayback();
        await using var app = await TestApplicationHost.StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IMusicBrowseSource>(new PagedBrowseSource()));
            services.Replace(ServiceDescriptor.Singleton<IPlaybackCoordinator>(playback));
        });
        var workflow = app.Services.GetRequiredService<MusicBrowseWorkflow>();

        workflow.SelectSearch("query", "song", "fixture");
        Assert.True((await workflow.ActivateSearchAsync()).Succeeded);
        var continuation = await workflow.ContinuePlaybackAsync(PlaybackQueueOrigin.Search);

        Assert.NotNull(continuation?.Search);
        Assert.Equal(21, continuation!.Search!.Items.Count);
        Assert.Equal("page-2", playback.LastRequest!.Selection.Id);
        Assert.Equal(PlaybackQueueOrigin.Search, playback.LastRequest.Origin);

        workflow.SelectSearch("different", "song", "fixture");
        Assert.Null(await workflow.ContinuePlaybackAsync(PlaybackQueueOrigin.Search));
    }

    [Fact]
    public void Main_window_is_only_a_wpf_and_platform_adapter()
    {
        var root = FindRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "MainWindow.xaml.cs"));

        foreach (var forbidden in new[]
        {
            "IDataService", "IDataStorageService", "IProcessLauncher", "HttpClient",
            "Sqlite", "AppDataPaths", "File.", "Directory.", "PersistShutdownAsync",
            "SaveMusicSourcesAsync", "_onlinePlaylists", "_selectedOnlinePlaylist",
            "_searchHistory", "_categoryCurrentPage", "_rankingCurrentPage", "_autoPaging"
        })
        {
            Assert.DoesNotContain(forbidden, window, StringComparison.Ordinal);
        }

        Assert.Contains("_browseWorkflow.ContinuePlaybackAsync", window, StringComparison.Ordinal);
        Assert.Contains("_savedPlaylists", window, StringComparison.Ordinal);
        Assert.DoesNotContain("(HostWindow as MainWindow)", File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "PlaylistImportView.xaml.cs")), StringComparison.Ordinal);
        Assert.Empty(Directory.Exists(Path.Combine(root, ".github", "workflows"))
            ? Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"))
            : []);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "verify.ps1"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class PagedBrowseSource : IMusicBrowseSource
    {
        public Task<PagedResult<MusicInfo>> SearchAsync(
            string query, int page, int pageSize, string searchType, string sourceId,
            CancellationToken cancellationToken)
        {
            var count = page == 1 ? 20 : 1;
            return Task.FromResult(new PagedResult<MusicInfo>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = 21,
                HasMore = page == 1,
                Items = Enumerable.Range(1, count)
                    .Select(index => new MusicInfo
                    {
                        Id = page == 2 ? "page-2" : $"page-1-{index}",
                        Title = "Song",
                        Source = sourceId
                    }).ToList()
            });
        }

        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken token) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken token) =>
            Task.FromResult(new List<PlaylistCategory>());
        public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken token) =>
            Task.FromResult(PagedResult<Playlist>.Empty(page, pageSize));
        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page, int pageSize, CancellationToken token) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public Task<List<Ranking>> GetRankingsAsync(string sourceId, CancellationToken token) =>
            Task.FromResult(new List<Ranking>());
        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page, int pageSize, CancellationToken token) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
    }

    private sealed class RecordingPlayback : IPlaybackCoordinator
    {
        public PlaybackRequest? LastRequest { get; private set; }
        public bool IsPlaying => false;
        public bool IsPaused => false;
        public MusicInfo? CurrentMusic => LastRequest?.Selection;
        public PlayMode Mode { get; set; }
        public string DefaultQuality { get; set; } = "128k";
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted { add { } remove { } }
        public Task<PlaybackResult> PlayAsync(PlaybackRequest request)
        {
            LastRequest = request;
            return Task.FromResult(new PlaybackResult(
                PlaybackResultStatus.Playing, request.Selection, request.Origin, "playing"));
        }
        public Task<PlaybackResult> ReplayAsync() => Task.FromResult(NoChange());
        public void Pause() { }
        public void Stop() { }
        public Task<PlaybackResult> NextAsync() => Task.FromResult(NoChange());
        public Task<PlaybackResult> PreviousAsync() => Task.FromResult(NoChange());
        public Task ResumeAsync() => Task.CompletedTask;
        private static PlaybackResult NoChange() => new(
            PlaybackResultStatus.NoChange, null, PlaybackQueueOrigin.Static, string.Empty);
    }
}
