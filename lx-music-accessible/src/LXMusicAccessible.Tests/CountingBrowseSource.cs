using System;
using System.Threading;
using System.Threading.Tasks;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.Tests;

public sealed class CountingBrowseSource : IMusicBrowseSource
{
    private readonly TaskCompletionSource<object?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CategoryCalls { get; private set; }
    public int LastCategoryPage { get; private set; }
    public int SongCalls { get; private set; }
    public int LastSongPage { get; private set; }
    public bool ThrowCategories { get; init; }
    public bool DelayCategories { get; init; }
    public bool Started => _started.Task.IsCompleted;
    public bool Cancelled => _cancelled.Task.IsCompleted;

    public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page, int pageSize, string searchType, string sourceId, CancellationToken cancellationToken) =>
        Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));

    public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken cancellationToken) =>
        Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));

    public async Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken cancellationToken)
    {
        CategoryCalls++;
        LastCategoryPage = 0;
        if (ThrowCategories) throw new HttpRequestException("fixture network");
        if (DelayCategories)
        {
            _started.TrySetResult(null);
            using var registration = cancellationToken.Register(() => _cancelled.TrySetResult(null));
            await Task.Delay(100, cancellationToken);
        }
        return new List<PlaylistCategory> { new() { Id = sourceId, Name = sourceId } };
    }

    public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken cancellationToken)
    {
        CategoryCalls++;
        LastCategoryPage = page;
        var playlist = new Playlist { Id = "playlist-" + page, Name = "Playlist " + page };
        return Task.FromResult(new PagedResult<Playlist>
        {
            CurrentPage = page,
            PageSize = pageSize,
            TotalItems = 2,
            Items = new List<Playlist> { playlist }
        });
    }

    public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page, int pageSize, CancellationToken cancellationToken)
    {
        SongCalls++;
        LastSongPage = page;
        return Task.FromResult(new PagedResult<MusicInfo>
        {
            CurrentPage = page,
            PageSize = pageSize,
            TotalItems = 2,
            Items = new List<MusicInfo> { new() { Id = "song-" + page, Title = "Song " + page, Source = "netease" } }
        });
    }

    public Task<List<Ranking>> GetRankingsAsync(string sourceId, CancellationToken cancellationToken) => Task.FromResult(new List<Ranking>());
    public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page, int pageSize, CancellationToken cancellationToken) =>
        Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
}
