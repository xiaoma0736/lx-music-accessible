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
    public bool ThrowCategoryPlaylists { get; init; }
    public bool DelayCategoryPlaylists { get; init; }
    public bool Started => _started.Task.IsCompleted;
    public bool Cancelled => _cancelled.Task.IsCompleted;

    public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page, int pageSize, string searchType, string sourceId, CancellationToken cancellationToken) =>
        Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));

    public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken cancellationToken) =>
        Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));

    public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken cancellationToken)
    {
        CategoryCalls++;
        LastCategoryPage = 0;
        return Task.FromResult(new List<PlaylistCategory> { new() { Id = sourceId, Name = sourceId } });
    }

    public async Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken cancellationToken)
    {
        CategoryCalls++;
        LastCategoryPage = page;
        if (ThrowCategoryPlaylists) throw new HttpRequestException("fixture network");
        if (DelayCategoryPlaylists)
        {
            _started.TrySetResult(null);
            // CancellationToken.Register callbacks are delivered on the cancelling thread's context, which
            // an STA message pump does not drain. Observe the cancellation through the awaited delay instead.
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _cancelled.TrySetResult(null);
                throw;
            }
        }
        var playlist = new Playlist { Id = "playlist-" + page, Name = "Playlist " + page };
        return new PagedResult<Playlist>
        {
            CurrentPage = page,
            PageSize = pageSize,
            TotalItems = 2,
            Items = new List<Playlist> { playlist }
        };
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
