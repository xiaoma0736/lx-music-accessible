using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Services;

/// <summary>搜索与在线浏览所需的最小数据接缝。</summary>
public interface IMusicBrowseSource
{
    Task<PagedResult<MusicInfo>> SearchAsync(
        string query,
        int page,
        int pageSize,
        string searchType,
        string sourceId,
        CancellationToken cancellationToken);

    Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(
        MusicInfo album,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(
        string sourceId,
        CancellationToken cancellationToken);

    Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(
        string category,
        int page,
        int pageSize,
        string sourceId,
        CancellationToken cancellationToken);

    Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(
        string playlistId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<List<Ranking>> GetRankingsAsync(
        string sourceId,
        CancellationToken cancellationToken);

    Task<PagedResult<MusicInfo>> GetRankingDetailAsync(
        string rankingId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

public enum BrowseFocusTarget
{
    KeepCurrent,
    SearchResults,
    CategoryList,
    CategoryPlaylists,
    CategorySongs,
    SavedPlaylistSongs,
    RankingList,
    RankingSongs
}

public enum BrowseResultStatus
{
    Completed,
    Superseded,
    InvalidSelection,
    Failed
}

public sealed record BrowseSelection(
    string Description,
    long Revision,
    BrowseFocusTarget FocusTarget = BrowseFocusTarget.KeepCurrent);

public sealed record BrowseResult<T>(
    BrowseResultStatus Status,
    T? Value,
    string Message,
    BrowseFocusTarget FocusTarget,
    long Revision)
{
    public bool Succeeded => Status == BrowseResultStatus.Completed;
}

public sealed record SearchBrowseSnapshot(
    IReadOnlyList<MusicInfo> Items,
    int PreviousCount,
    int Page,
    bool HasMore,
    string Query);

public sealed record PlaylistBrowseSnapshot(
    Playlist Playlist,
    IReadOnlyList<MusicInfo> Items,
    int Page,
    int TotalItems);

public sealed record RankingBrowseSnapshot(
    Ranking Ranking,
    IReadOnlyList<MusicInfo> Items,
    int Page,
    int TotalItems);

public readonly record struct SearchHistoryNavigation(bool Handled, string Text);

public sealed record BrowsePlaybackContinuation(
    PlaybackQueueOrigin Origin,
    SearchBrowseSnapshot? Search = null,
    PlaylistBrowseSnapshot? Playlist = null,
    RankingBrowseSnapshot? Ranking = null);

/// <summary>
/// Owns online search/browse selection, cancellation, latest-wins commits and announcements.
/// UI callers only render returned data and apply the returned focus target.
/// </summary>
public sealed class MusicBrowseWorkflow
{
    private const string WorkSlot = "music-browse";
    private const int SearchPageSize = 20;
    private const int BrowsePageSize = 20;
    private readonly object _gate = new();
    private readonly IMusicBrowseSource _source;
    private readonly BackgroundWorkHost _backgroundWork;
    private readonly IAccessibleAnnouncer _announcer;
    private readonly IPlaybackCoordinator _playback;
    private readonly LocalSearchHistoryStore _historyStore;
    private readonly ILogger<MusicBrowseWorkflow> _logger;
    private long _revision;
    private long _activitySequence;
    private SearchRequest _search = new(string.Empty, "song", "all");
    private SearchBrowseSnapshot _searchSnapshot = new([], 0, 0, false, string.Empty);
    private List<SearchHistory> _history = [];
    private int _historyIndex = -1;
    private string _historyDraft = string.Empty;
    private string _categorySource = "netease";
    private PlaylistCategory? _category;
    private Playlist? _playlist;
    private PlaylistBrowseSnapshot? _playlistSnapshot;
    private string _rankingSource = "netease";
    private Ranking? _ranking;
    private RankingBrowseSnapshot? _rankingSnapshot;
    private bool _continuingPlayback;

    public MusicBrowseWorkflow(
        IMusicBrowseSource source,
        BackgroundWorkHost backgroundWork,
        IAccessibleAnnouncer announcer,
        IPlaybackCoordinator playback,
        LocalSearchHistoryStore historyStore,
        ILogger<MusicBrowseWorkflow> logger)
    {
        _source = source;
        _backgroundWork = backgroundWork;
        _announcer = announcer;
        _playback = playback;
        _historyStore = historyStore;
        _logger = logger;
    }

    public SearchBrowseSnapshot CurrentSearch
    {
        get
        {
            lock (_gate)
            {
                return _searchSnapshot with { Items = _searchSnapshot.Items.ToArray() };
            }
        }
    }

    public PlaylistBrowseSnapshot? CurrentPlaylistPage
    {
        get
        {
            lock (_gate)
            {
                return _playlistSnapshot is null
                    ? null
                    : _playlistSnapshot with { Items = _playlistSnapshot.Items.ToArray() };
            }
        }
    }

    public RankingBrowseSnapshot? CurrentRankingPage
    {
        get
        {
            lock (_gate)
            {
                return _rankingSnapshot is null
                    ? null
                    : _rankingSnapshot with { Items = _rankingSnapshot.Items.ToArray() };
            }
        }
    }

    public async Task LoadSearchHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var loaded = await _historyStore.LoadAsync(cancellationToken);
            lock (_gate)
            {
                _history = loaded
                    .Where(item => !string.IsNullOrWhiteSpace(item.Keyword))
                    .GroupBy(item => item.Keyword, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(item => item.SearchTime).First())
                    .OrderByDescending(item => item.SearchTime)
                    .Take(20)
                    .ToList();
                ResetHistoryNavigation();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Search history load failed: {ErrorType}", exception.GetType().Name);
        }
    }

    public SearchHistoryNavigation NavigateSearchHistory(string currentText, int step)
    {
        lock (_gate)
        {
            if (_history.Count == 0) return new(false, currentText);
            if (step > 0)
            {
                if (_historyIndex < 0)
                {
                    _historyDraft = currentText;
                    _historyIndex = 0;
                }
                else if (_historyIndex < _history.Count - 1)
                {
                    _historyIndex++;
                }
                return new(true, _history[_historyIndex].Keyword);
            }

            if (_historyIndex < 0) return new(false, currentText);
            if (_historyIndex > 0)
            {
                _historyIndex--;
                return new(true, _history[_historyIndex].Keyword);
            }

            _historyIndex = -1;
            var draft = _historyDraft;
            _historyDraft = string.Empty;
            return new(true, draft);
        }
    }

    public async Task RecordSearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;
        var normalized = keyword.Trim();
        IReadOnlyList<SearchHistory> snapshot;
        lock (_gate)
        {
            _history.RemoveAll(item => string.Equals(item.Keyword, normalized, StringComparison.OrdinalIgnoreCase));
            _history.Insert(0, new SearchHistory { Keyword = normalized, SearchTime = DateTime.Now });
            if (_history.Count > 20) _history.RemoveRange(20, _history.Count - 20);
            ResetHistoryNavigation();
            snapshot = _history.ToArray();
        }
        await SaveHistoryAsync(snapshot, cancellationToken);
    }

    public async Task<string?> DeleteSearchHistoryAsync(string currentText, CancellationToken cancellationToken = default)
    {
        string? target;
        IReadOnlyList<SearchHistory> snapshot;
        lock (_gate)
        {
            target = _historyIndex >= 0 && _historyIndex < _history.Count
                ? _history[_historyIndex].Keyword
                : _history.FirstOrDefault(item => string.Equals(item.Keyword, currentText?.Trim(), StringComparison.OrdinalIgnoreCase))?.Keyword;
            if (string.IsNullOrWhiteSpace(target)) return null;
            _history.RemoveAll(item => string.Equals(item.Keyword, target, StringComparison.OrdinalIgnoreCase));
            ResetHistoryNavigation();
            snapshot = _history.ToArray();
        }
        await SaveHistoryAsync(snapshot, cancellationToken);
        return target;
    }

    public async Task<bool> ClearSearchHistoryAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SearchHistory> previous;
        lock (_gate)
        {
            if (_history.Count == 0) return false;
            previous = _history.ToArray();
            _history.Clear();
            ResetHistoryNavigation();
        }
        try
        {
            await _historyStore.ClearAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (_history.Count == 0) _history = previous.ToList();
            }
            throw;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (_history.Count == 0) _history = previous.ToList();
            }
            _logger.LogWarning("Search history clear failed: {ErrorType}", exception.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Confirms that a completed result still belongs to the latest selection.
    /// UI callers use this immediately before rendering or applying focus so a
    /// selection made after the background commit cannot be overwritten.
    /// </summary>
    public bool IsCurrent(long revision)
    {
        lock (_gate) return revision == _revision;
    }

    public BrowseSelection SelectSearch(string query, string searchType, string sourceId) =>
        Select(
            () =>
            {
                _search = new(
                    query?.Trim() ?? string.Empty,
                    string.IsNullOrWhiteSpace(searchType) ? "song" : searchType,
                    string.IsNullOrWhiteSpace(sourceId) ? "all" : sourceId);
                _searchSnapshot = new([], 0, 0, false, _search.Query);
            },
            string.IsNullOrWhiteSpace(query)
                ? "请输入搜索关键词"
                : $"已选择搜索关键词：{query.Trim()}，按 Enter 或搜索按钮开始搜索");

    public BrowseSelection ClearSearch() => Select(
        () =>
        {
            _search = new(string.Empty, "song", "all");
            _searchSnapshot = new([], 0, 0, false, string.Empty);
        },
        "搜索已清空");

    public BrowseSelection SelectCategorySource(string sourceId, string? displayName = null) =>
        Select(
            () =>
            {
                _categorySource = RequireId(sourceId);
                _category = null;
                _playlist = null;
                _playlistSnapshot = null;
            },
            $"已选择歌单音源：{displayName ?? sourceId}，按 Enter 或刷新按钮加载分类");

    public BrowseSelection SelectCategory(PlaylistCategory? category) =>
        Select(
            () =>
            {
                _category = category;
                _playlist = null;
                _playlistSnapshot = null;
            },
            category is null ? "未选择歌单分类" : $"已选择分类：{category.Name}，按 Enter 加载歌单");

    public BrowseSelection SelectPlaylist(Playlist? playlist) =>
        Select(
            () =>
            {
                _playlist = playlist;
                _playlistSnapshot = null;
            },
            playlist is null ? "未选择歌单" : $"已选择歌单：{playlist.Name}，按 Enter 加载歌曲");

    public BrowseSelection SelectRankingSource(string sourceId, string? displayName = null) =>
        Select(
            () =>
            {
                _rankingSource = RequireId(sourceId);
                _ranking = null;
                _rankingSnapshot = null;
            },
            $"已选择排行榜音源：{displayName ?? sourceId}，按 Enter 或刷新按钮加载排行榜");

    public BrowseSelection SelectRanking(Ranking? ranking) =>
        Select(
            () =>
            {
                _ranking = ranking;
                _rankingSnapshot = null;
            },
            ranking is null ? "未选择排行榜" : $"已选择排行榜：{ranking.Name}，按 Enter 加载歌曲");

    public Task<BrowseResult<SearchBrowseSnapshot>> ActivateSearchAsync(bool reset = true)
    {
        SearchRequest request;
        SearchBrowseSnapshot previous;
        long revision;
        lock (_gate)
        {
            request = _search;
            previous = reset
                ? new([], 0, 0, false, request.Query)
                : _searchSnapshot with { Items = _searchSnapshot.Items.ToArray() };
            revision = ++_revision;
        }

        if (string.IsNullOrWhiteSpace(request.Query))
            return Task.FromResult(Invalid<SearchBrowseSnapshot>(revision, "请输入搜索关键词"));

        var nextPage = previous.Page + 1;
        return RunLatestAsync(
            revision,
            "正在搜索",
            async cancellationToken =>
            {
                var page = await _source.SearchAsync(
                    request.Query,
                    nextPage,
                    SearchPageSize,
                    request.SearchType,
                    request.SourceId,
                    cancellationToken);
                var items = previous.Items.Concat(page.Items).ToArray();
                return new SearchBrowseSnapshot(
                    items,
                    previous.Items.Count,
                    nextPage,
                    page.HasMore,
                    request.Query);
            },
            snapshot => snapshot.Items.Count == 0
                ? $"未找到与 {request.Query} 相关的结果"
                : snapshot.HasMore
                    ? $"已加载 {snapshot.Items.Count} 首，还有更多"
                    : $"已加载全部结果，共 {snapshot.Items.Count} 首",
            BrowseFocusTarget.SearchResults,
            snapshot => _searchSnapshot = snapshot);
    }

    public Task<BrowseResult<IReadOnlyList<MusicInfo>>> ActivateAlbumAsync(MusicInfo album)
    {
        ArgumentNullException.ThrowIfNull(album);
        var revision = BeginActivation();
        return RunLatestAsync(
            revision,
            $"正在打开专辑《{album.Title}》",
            async cancellationToken =>
            {
                var songs = new List<MusicInfo>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                const int pageSize = 50;
                for (var page = 1; page <= 200; page++)
                {
                    var result = await _source.GetAlbumDetailAsync(album, page, pageSize, cancellationToken);
                    var before = songs.Count;
                    foreach (var song in result.Items)
                    {
                        if (seen.Add($"{song.Source}|{song.Id}")) songs.Add(song);
                    }
                    if (result.Items.Count == 0 || songs.Count == before) break;
                }
                return (IReadOnlyList<MusicInfo>)songs;
            },
            songs => songs.Count == 0
                ? $"专辑《{album.Title}》暂无歌曲可显示"
                : $"专辑《{album.Title}》共 {songs.Count} 首歌曲",
            BrowseFocusTarget.SearchResults);
    }

    public Task<BrowseResult<IReadOnlyList<PlaylistCategory>>> ActivateCategorySourceAsync()
    {
        var (revision, sourceId) = Snapshot(() => _categorySource);
        return RunLatestAsync(
            revision,
            "正在加载歌单分类",
            async cancellationToken =>
                (IReadOnlyList<PlaylistCategory>)await _source.GetPlaylistCategoriesAsync(sourceId, cancellationToken),
            categories => categories.Count == 0 ? "暂无歌单分类" : $"已加载 {categories.Count} 个歌单分类",
            BrowseFocusTarget.CategoryList);
    }

    public Task<BrowseResult<PagedResult<Playlist>>> ActivateCategoryAsync()
    {
        var (revision, selection) = Snapshot(() => (_categorySource, _category));
        if (selection._category is null)
            return Task.FromResult(Invalid<PagedResult<Playlist>>(revision, "请选择歌单分类"));

        var tag = selection._category.Tags.FirstOrDefault()?.Id ?? "all";
        return RunLatestAsync(
            revision,
            $"正在加载分类：{selection._category.Name}",
            cancellationToken => _source.GetPlaylistsByCategoryAsync(
                tag,
                1,
                BrowsePageSize,
                selection._categorySource,
                cancellationToken),
            result => result.Items.Count == 0 ? "该音源暂无可用歌单" : $"已加载 {result.Items.Count} 个歌单",
            BrowseFocusTarget.CategoryPlaylists);
    }

    public Task<BrowseResult<PagedResult<MusicInfo>>> ActivatePlaylistAsync(int page = 1)
    {
        var (revision, playlist) = Snapshot(() => _playlist);
        if (playlist is null)
            return Task.FromResult(Invalid<PagedResult<MusicInfo>>(revision, "请选择歌单"));

        return RunLatestAsync(
            revision,
            $"正在加载歌单：{playlist.Name}",
            cancellationToken => _source.GetPlaylistDetailAsync(
                playlist.Id,
                Math.Max(1, page),
                BrowsePageSize,
                cancellationToken),
            result => result.Items.Count == 0 ? "该歌单暂无歌曲" : $"已加载 {result.Items.Count} 首歌单歌曲",
            BrowseFocusTarget.CategorySongs,
            result => _playlistSnapshot = new(
                playlist,
                result.Items.ToArray(),
                Math.Max(1, page),
                result.TotalItems));
    }

    public Task<BrowseResult<PagedResult<MusicInfo>>> ActivateSavedPlaylistAsync(Playlist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        var revision = BeginActivation();
        if (string.IsNullOrWhiteSpace(playlist.RemoteId))
            return Task.FromResult(Invalid<PagedResult<MusicInfo>>(revision, "该歌单没有远程内容"));

        return RunLatestAsync(
            revision,
            $"正在加载远程歌单：{playlist.Name}",
            cancellationToken => playlist.Type == PlaylistType.Recommend
                ? _source.GetRankingDetailAsync(playlist.RemoteId, 1, 50, cancellationToken)
                : _source.GetPlaylistDetailAsync(playlist.RemoteId, 1, 50, cancellationToken),
            result => result.Items.Count == 0 ? "该歌单暂无歌曲" : $"已加载 {result.Items.Count} 首收藏歌曲",
            BrowseFocusTarget.SavedPlaylistSongs);
    }

    public Task<BrowseResult<IReadOnlyList<Ranking>>> ActivateRankingSourceAsync()
    {
        var (revision, sourceId) = Snapshot(() => _rankingSource);
        return RunLatestAsync(
            revision,
            "正在加载排行榜",
            async cancellationToken =>
                (IReadOnlyList<Ranking>)await _source.GetRankingsAsync(sourceId, cancellationToken),
            rankings => rankings.Count == 0 ? "暂无排行榜" : $"已加载 {rankings.Count} 个排行榜",
            BrowseFocusTarget.RankingList);
    }

    public Task<BrowseResult<PagedResult<MusicInfo>>> ActivateRankingAsync(int page = 1)
    {
        var (revision, ranking) = Snapshot(() => _ranking);
        if (ranking is null)
            return Task.FromResult(Invalid<PagedResult<MusicInfo>>(revision, "请选择排行榜"));

        return RunLatestAsync(
            revision,
            $"正在加载排行榜：{ranking.Name}",
            cancellationToken => _source.GetRankingDetailAsync(
                ranking.Id,
                Math.Max(1, page),
                BrowsePageSize,
                cancellationToken),
            result => result.Items.Count == 0 ? "该排行榜暂无歌曲" : $"已加载 {result.Items.Count} 首排行榜歌曲",
            BrowseFocusTarget.RankingSongs,
            result => _rankingSnapshot = new(
                ranking,
                result.Items.ToArray(),
                Math.Max(1, page),
                result.TotalItems));
    }

    public async Task<BrowsePlaybackContinuation?> ContinuePlaybackAsync(PlaybackQueueOrigin origin)
    {
        lock (_gate)
        {
            if (_continuingPlayback) return null;
            _continuingPlayback = true;
        }

        try
        {
            return origin switch
            {
                PlaybackQueueOrigin.Search => await ContinueSearchPlaybackAsync(),
                PlaybackQueueOrigin.Category => await ContinuePlaylistPlaybackAsync(),
                PlaybackQueueOrigin.Ranking => await ContinueRankingPlaybackAsync(),
                _ => null
            };
        }
        finally
        {
            lock (_gate) _continuingPlayback = false;
        }
    }

    private async Task<BrowsePlaybackContinuation?> ContinueSearchPlaybackAsync()
    {
        var before = CurrentSearch;
        if (!before.HasMore) return null;
        var result = await ActivateSearchAsync(reset: false);
        if (!result.Succeeded || result.Value is null || result.Value.Items.Count <= before.Items.Count)
            return null;
        var selection = result.Value.Items[before.Items.Count];
        await _playback.PlayAsync(new(
            result.Value.Items,
            selection,
            _playback.DefaultQuality,
            PlaybackQueueOrigin.Search,
            Trigger: PlaybackTrigger.AutoContinuation));
        return new(PlaybackQueueOrigin.Search, Search: result.Value);
    }

    private async Task<BrowsePlaybackContinuation?> ContinuePlaylistPlaybackAsync()
    {
        var before = CurrentPlaylistPage;
        if (before is null || !HasNextPage(before.TotalItems, before.Page, before.Items.Count)) return null;
        var result = await ActivatePlaylistAsync(before.Page + 1);
        var page = CurrentPlaylistPage;
        if (!result.Succeeded || page is null || page.Items.Count == 0) return null;
        _announcer.Announce(new($"已自动翻页到第 {page.Page} 页，继续播放"));
        await _playback.PlayAsync(new(
            page.Items,
            page.Items[0],
            _playback.DefaultQuality,
            PlaybackQueueOrigin.Category,
            Trigger: PlaybackTrigger.AutoContinuation));
        return new(PlaybackQueueOrigin.Category, Playlist: page);
    }

    private async Task<BrowsePlaybackContinuation?> ContinueRankingPlaybackAsync()
    {
        var before = CurrentRankingPage;
        if (before is null || !HasNextPage(before.TotalItems, before.Page, before.Items.Count)) return null;
        var result = await ActivateRankingAsync(before.Page + 1);
        var page = CurrentRankingPage;
        if (!result.Succeeded || page is null || page.Items.Count == 0) return null;
        _announcer.Announce(new($"已自动翻页到第 {page.Page} 页，继续播放"));
        await _playback.PlayAsync(new(
            page.Items,
            page.Items[0],
            _playback.DefaultQuality,
            PlaybackQueueOrigin.Ranking,
            Trigger: PlaybackTrigger.AutoContinuation));
        return new(PlaybackQueueOrigin.Ranking, Ranking: page);
    }

    private static bool HasNextPage(int totalItems, int page, int loadedCount) =>
        totalItems > 0 ? page * BrowsePageSize < totalItems : loadedCount >= BrowsePageSize;

    private async Task SaveHistoryAsync(
        IReadOnlyList<SearchHistory> history,
        CancellationToken cancellationToken)
    {
        try { await _historyStore.SaveAsync(history, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Search history save failed: {ErrorType}", exception.GetType().Name);
        }
    }

    private void ResetHistoryNavigation()
    {
        _historyIndex = -1;
        _historyDraft = string.Empty;
    }

    private BrowseSelection Select(Action update, string description)
    {
        long revision;
        lock (_gate)
        {
            update();
            revision = ++_revision;
        }
        _backgroundWork.Cancel(WorkSlot);
        return new(description, revision);
    }

    private long BeginActivation()
    {
        lock (_gate) return ++_revision;
    }

    private (long Revision, T Value) Snapshot<T>(Func<T> capture)
    {
        lock (_gate) return (++_revision, capture());
    }

    private async Task<BrowseResult<T>> RunLatestAsync<T>(
        long revision,
        string busyMessage,
        Func<CancellationToken, Task<T>> operation,
        Func<T, string> successMessage,
        BrowseFocusTarget focusTarget,
        Action<T>? commit = null)
    {
        var activityId = $"music-browse:{Interlocked.Increment(ref _activitySequence)}";
        _announcer.Announce(new(busyMessage, AnnouncementKind.Busy, activityId));
        BrowseResult<T>? outcome = null;

        await _backgroundWork.RunLatestAsync(WorkSlot, async context =>
        {
            try
            {
                var value = await operation(context.CancellationToken);
                var committed = false;
                context.TryCommit(() =>
                {
                    lock (_gate)
                    {
                        if (revision != _revision) return;
                        commit?.Invoke(value);
                        var message = successMessage(value);
                        outcome = new(
                            BrowseResultStatus.Completed,
                            value,
                            message,
                            focusTarget,
                            revision);
                        committed = true;
                    }
                });
                if (committed)
                {
                    _announcer.Announce(new(
                        outcome!.Message,
                        AnnouncementKind.Success,
                        activityId));
                }
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                // A newer selection or activation owns the visible result.
            }
            catch (Exception exception)
            {
                var committed = false;
                context.TryCommit(() =>
                {
                    lock (_gate)
                    {
                        if (revision != _revision) return;
                        outcome = new(
                            BrowseResultStatus.Failed,
                            default,
                            "加载失败，请检查网络",
                            BrowseFocusTarget.KeepCurrent,
                            revision);
                        committed = true;
                    }
                });
                if (committed)
                {
                    _logger.LogWarning(
                        "Music browse activation failed: {ErrorType}",
                        exception.GetType().Name);
                    _announcer.Announce(new(
                        outcome!.Message,
                        AnnouncementKind.Error,
                        activityId));
                }
            }
        });

        return outcome ?? new(
            BrowseResultStatus.Superseded,
            default,
            string.Empty,
            BrowseFocusTarget.KeepCurrent,
            revision);
    }

    private static BrowseResult<T> Invalid<T>(long revision, string message) => new(
        BrowseResultStatus.InvalidSelection,
        default,
        message,
        BrowseFocusTarget.KeepCurrent,
        revision);

    private static string RequireId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }

    private sealed record SearchRequest(string Query, string SearchType, string SourceId);
}
