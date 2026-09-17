using Microsoft.Extensions.Logging;
using LXMusicAccessible.Models;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;

namespace LXMusicAccessible.Services;

/// <summary>
/// 音源管理服务 — 统一调度 5 大内置音源 + 外部脚本源
/// </summary>
public class MusicSourceService : IMusicSourceService, IMusicBrowseSource, IManualSourceGateway
{
    private readonly ILogger<MusicSourceService> _logger;
    private readonly NeteaseMusicService _netease;
    private readonly QqMusicService _qq;
    private readonly KugouMusicService _kugou;
    private readonly KuwoMusicService _kuwo;
    private readonly MiguMusicService _migu;
    private readonly ScriptMusicSource _scriptSource;
    private readonly LxScriptEngine _scriptEngine;
    private readonly IDataService _dataService;
    private readonly BackgroundWorkHost _backgroundWork;
    private readonly Dictionary<string, IMusicApi> _apiMap;
    private readonly List<MusicSource> _sources = new();
    private bool _scriptsInitialized = false;
    private bool _defaultTouchedByUser = false;
    private readonly HttpClient _playUrlProbeClient;
    private readonly Dictionary<string, ScriptHealth> _scriptHealth = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _scriptHealthGate = new();
    private const string BundledScriptId = "bundled_verified_fallback";
    private const string BundledScriptResource = "LXMusicAccessible.Core.MusicApi.Scripts.verified-fallback.js";
    private const string BundledScriptSha256 = "9d4aa71b7814860242f2cdfc3370da9cd6d415e290757c9cf122e74069641d50";
    private static readonly SubscriptionSource[] SubscriptionSources =
    [
        new(
            "hyw",
            "https://raw.githubusercontent.com/Macrohard0001/lx-ikun-music-sources/main/V260817/%E6%8E%A8%E8%8D%90/HYWmusic_beta_%E5%85%AC%E7%9B%8A%E6%B5%8B%E8%AF%95.js"),
        new(
            "jheapi",
            "https://api.music.lerd.dpdns.org/script.js"),
        new(
            "quandouyao",
            "https://raw.githubusercontent.com/Macrohard0001/lx-ikun-music-sources/main/V260817/%E5%85%B6%E4%BB%96/%E5%85%A8%E8%B1%86%E8%A6%81-%E8%81%9A%E5%90%88%E9%9F%B3%E6%BA%90-V4.1%EF%BC%88%E9%85%B7%E7%8B%97%E6%8C%82%E4%BA%86%EF%BC%89.js"),
        new(
            "nianxin",
            "https://raw.githubusercontent.com/Macrohard0001/lx-ikun-music-sources/main/V260817/%E5%85%B6%E4%BB%96/%E5%BF%B5%E5%BF%83%E9%9F%B3%E6%BA%90%20v1.0.2.js")
    ];

    /// <summary>音源列表变化事件（默认/启用/禁用/增删等），供界面刷新音源列表</summary>
    public event EventHandler? SourcesChanged;

    /// <summary>脚本调用 lx.send(updateAlert) 时上抛（供界面提示音源更新）</summary>
    public event EventHandler<LxScriptUpdateAlertEventArgs>? ScriptUpdateAlert;

    private void RaiseSourcesChanged() => SourcesChanged?.Invoke(this, EventArgs.Empty);

    // ============ 搜索会话（全局去重 + 持续累积“加载更多”） ============
    private readonly Dictionary<string, SearchSession> _searchSessions = new(StringComparer.OrdinalIgnoreCase);
    private long _searchSessionSeq;
    private const int MaxSearchSessions = 3;      // 最多保留的会话数（超出淘汰最旧）
    private const int MaxSearchFetchRounds = 8;   // 单次调用最多抓取轮数（防极端重复）

    // Search results are shown grouped by source in this fixed order: QQ, Kugou, Kuwo, Netease, Migu.
    private static readonly string[] SearchResultSourceOrder = { "qq", "kg", "kw", "netease", "migu" };

    private static int SourceDisplayRank(string source)
    {
        var idx = Array.IndexOf(SearchResultSourceOrder, source?.ToLowerInvariant() ?? "");
        return idx < 0 ? SearchResultSourceOrder.Length : idx;
    }


    public MusicSourceService(
        ILogger<MusicSourceService> logger,
        NeteaseMusicService netease,
        QqMusicService qq,
        KugouMusicService kugou,
        KuwoMusicService kuwo,
        MiguMusicService migu,
        ScriptMusicSource scriptSource,
        LxScriptEngine scriptEngine,
        IDataService dataService,
        BackgroundWorkHost backgroundWork,
        HttpClient httpClient)
    {
        _logger = logger;
        _netease = netease;
        _qq = qq;
        _kugou = kugou;
        _kuwo = kuwo;
        _migu = migu;
        _scriptSource = scriptSource;
        _scriptEngine = scriptEngine;
        _dataService = dataService;
        _backgroundWork = backgroundWork;
        _playUrlProbeClient = httpClient;

        _apiMap = new Dictionary<string, IMusicApi>(StringComparer.OrdinalIgnoreCase)
        {
            ["netease"] = _netease, ["qq"] = _qq, ["kg"] = _kugou,
            ["kw"] = _kuwo, ["migu"] = _migu
        };

        InitializeDefaultSources();
        _scriptEngine.UpdateAlert += (_, e) =>
        {
            try { ScriptUpdateAlert?.Invoke(this, e); }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Script update notification failed: {ErrorType}",
                    exception.GetType().Name);
            }
        };
    }

    // ================================================================
    //  初始化
    // ================================================================

    private void InitializeDefaultSources()
    {
        foreach (var kvp in _apiMap)
        {
            _sources.Add(new MusicSource
            {
                Id = kvp.Value.SourceId, Name = kvp.Value.SourceName,
                Version = "2.0.0", Type = SourceType.BuiltIn, IsEnabled = true,
                TrustState = ScriptTrustState.UserApproved,
                IsSystemHidden = true,   // 官方API：仅作搜索/歌单/排行/歌词底层，不在音源管理列表展示
                Priority = kvp.Key switch { "netease" => 1, "qq" => 2, "kg" => 3, "kw" => 4, "migu" => 5, _ => 99 },
                AddedDate = DateTime.Now, LastUpdateDate = DateTime.Now
            });
        }
        _logger.LogInformation("初始化 {Count} 个内置官方API完成（系统隐藏，仅作底层数据源）", _sources.Count);
    }

    /// <summary>恢复本地可信内容，再后台刷新固定的免费音源订阅。</summary>
    public async Task InitializeScriptSourcesAsync(CancellationToken cancellationToken = default)
    {
        if (_scriptsInitialized) return;
        _scriptsInitialized = true;

        // 用户订阅保持正式版的优先顺序；随版本源只作最后的离线备用。
        await LoadPersistedSourcesAsync(cancellationToken);

        await RefreshSubscriptionsAsync(cancellationToken);

        await LoadBundledSourceAsync(cancellationToken);

        await PersistSourcesAsync(cancellationToken);
        RaiseSourcesChanged();
        _logger.LogInformation("脚本源初始化完成，共 {Count} 个音源", _sources.Count);
    }

    private async Task LoadBundledSourceAsync(CancellationToken cancellationToken)
    {
        var info = await _scriptSource.StageBundledAsync(
            BundledScriptResource,
            BundledScriptId,
            cancellationToken);
        if (info?.RawScript is null
            || !string.Equals(
                ScriptMusicSource.ComputeSha256(info.RawScript),
                BundledScriptSha256,
                StringComparison.OrdinalIgnoreCase)
            || !await _scriptEngine.TestLoadAsync(info, cancellationToken)
            || !_scriptSource.RegisterVerified(info, BundledScriptSha256))
        {
            _scriptSource.Remove(BundledScriptId);
            _logger.LogError("随版本音源缺失、摘要不匹配或无法执行，已拒绝加载");
            return;
        }

        var existing = _sources.FirstOrDefault(source =>
            string.Equals(source.Id, BundledScriptId, StringComparison.OrdinalIgnoreCase));
        var enabled = existing?.TrustState == ScriptTrustState.Bundled
            ? existing.IsEnabled
            : true;
        if (existing is null)
        {
            existing = new MusicSource
            {
                Id = BundledScriptId,
                Priority = 10 + _sources.Count,
                AddedDate = DateTime.Now
            };
            _sources.Add(existing);
        }

        existing.Name = info.Name;
        existing.Version = info.Version ?? "1.0.0";
        existing.ScriptUrl = info.SourceUrl;
        existing.ScriptContent = info.RawScript;
        existing.Type = SourceType.Bundled;
        existing.TrustState = ScriptTrustState.Bundled;
        existing.ContentSha256 = BundledScriptSha256;
        existing.IsEnabled = enabled;
        existing.Priority = _sources
            .Where(source => !ReferenceEquals(source, existing) && !source.IsSystemHidden)
            .Select(source => source.Priority)
            .DefaultIfEmpty(9)
            .Max() + 1;
        existing.LastUpdateDate = DateTime.Now;
    }

    private async Task RefreshSubscriptionsAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in SubscriptionSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sources.Any(source => string.Equals(
                    source.Id,
                    subscription.Id,
                    StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                await RefreshSubscriptionAsync(subscription, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "订阅音源刷新失败，保留现有版本: SourceId={SourceId}, ErrorType={ErrorType}",
                    subscription.Id,
                    exception.GetType().Name);
            }
        }
    }

    private async Task RefreshSubscriptionAsync(
        SubscriptionSource subscription,
        CancellationToken cancellationToken)
    {
        var existing = _sources.FirstOrDefault(source =>
                string.Equals(source.Id, subscription.Id, StringComparison.OrdinalIgnoreCase))
            ?? _sources.FirstOrDefault(source =>
                string.Equals(source.ScriptUrl, subscription.Url, StringComparison.OrdinalIgnoreCase));
        var sourceId = existing?.Id ?? subscription.Id;
        var candidate = await _scriptSource.StageFromUrlAsync(
            subscription.Url,
            sourceId,
            cancellationToken);
        if (candidate?.RawScript is null
            || (candidate.Kind is not LxSourceType.JsonManifest
                && !await _scriptEngine.TestLoadAsync(candidate, cancellationToken)))
        {
            _logger.LogWarning(
                "订阅音源无有效更新，保留现有版本: SourceId={SourceId}",
                subscription.Id);
            return;
        }

        var digest = ScriptMusicSource.ComputeSha256(candidate.RawScript);
        var previousScript = existing is null ? null : _scriptSource.GetScript(existing.Id);
        var previous = existing is null ? null : SourceSnapshot.Capture(existing);
        var source = existing ?? new MusicSource
        {
            Id = sourceId,
            Priority = 10 + _sources.Count,
            AddedDate = DateTime.Now
        };
        var preserveDisabled = existing is not null
            && existing.TrustState != ScriptTrustState.Untrusted
            && !existing.IsEnabled;

        source.Name = candidate.Name;
        source.Version = candidate.Version
            ?? (string.IsNullOrEmpty(source.Version) ? "1.0" : source.Version);
        source.ScriptUrl = subscription.Url;
        source.ScriptContent = candidate.RawScript;
        source.ContentSha256 = digest;
        source.Type = SourceType.Network;
        source.TrustState = ScriptTrustState.UserApproved;
        source.IsEnabled = !preserveDisabled;
        source.LastUpdateDate = DateTime.Now;
        if (existing is null) _sources.Add(source);

        if (!_scriptSource.RegisterVerified(candidate, digest))
        {
            if (existing is null) _sources.Remove(source);
            else previous!.Restore(existing);
            return;
        }

        try
        {
            await _dataService.SaveMusicSourcesAsync(_sources, cancellationToken);
        }
        catch
        {
            if (existing is null) _sources.Remove(source);
            else previous!.Restore(existing);
            if (previousScript?.RawScript is not null)
            {
                _scriptSource.RegisterVerified(
                    previousScript,
                    ScriptMusicSource.ComputeSha256(previousScript.RawScript));
            }
            else
            {
                _scriptSource.Remove(sourceId);
            }
            throw;
        }

        ClearCache();
        _logger.LogInformation(
            "订阅音源已更新: SourceId={SourceId}, Name={Name}, Version={Version}",
            subscription.Id,
            candidate.Name,
            candidate.Version);
    }

    // ================================================================
    //  搜索
    // ================================================================
    public async Task<PagedResult<MusicInfo>> SearchAsync(
        string query,
        int page = 1,
        int pageSize = 20,
        string searchType = "song",
        string sourceId = "all",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return PagedResult<MusicInfo>.Empty(page, pageSize);
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;

        try
        {
            // 搜索会话：同一 (query/type/source) 复用缓冲，支持“加载更多”持续累积、跨页全局去重
            var session = GetOrCreateSearchSession(query, searchType, sourceId);
            var enabled = GetSearchSources(sourceId);
            if (enabled.Count == 0)
                return new PagedResult<MusicInfo>
                {
                    CurrentPage = page, PageSize = pageSize,
                    TotalItems = session.Buffer.Count, HasMore = session.HasMore,
                    Items = new List<MusicInfo>()
                };

            // 循环抓取，直到缓冲足以覆盖本页，或没有更多结果 / 达到轮数上限
            var batchSize = Math.Max(40, pageSize * 2);
            for (int round = 0; round < MaxSearchFetchRounds && session.Buffer.Count < page * pageSize && session.HasMore; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await FetchNextSearchRoundAsync(
                    session,
                    enabled,
                    query,
                    searchType,
                    batchSize,
                    sourceId,
                    cancellationToken);
            }

            return new PagedResult<MusicInfo>
            {
                CurrentPage = page, PageSize = pageSize,
                TotalItems = session.Buffer.Count,
                HasMore = session.HasMore,
                Items = session.Buffer.Skip((page - 1) * pageSize).Take(pageSize).ToList()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "聚合搜索失败: {Query}", query);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    /// <summary>从单个音源抓取一页原始搜索结果（不额外过滤，尊重平台原生搜索规则）</summary>
    private async Task<PagedResult<MusicInfo>> SearchSourcePageAsync(
        string sourceId,
        string query,
        int page,
        int pageSize,
        string searchType,
        CancellationToken cancellationToken)
    {
        if (!_apiMap.TryGetValue(sourceId, out var api))
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        try { return await api.SearchAsync(query, page, pageSize, searchType, cancellationToken).WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogError(ex, "音源 [{SourceId}] 搜索失败", sourceId); return PagedResult<MusicInfo>.Empty(page, pageSize); }
    }

    /// <summary>跨源去重键：专辑 + 歌手 + 时长（规范化后）</summary>
    private static string BuildSearchDedupKey(MusicInfo m)
    {
        var album = string.IsNullOrWhiteSpace(m.Album) ? "" : m.Album.Trim();
        var artist = string.IsNullOrWhiteSpace(m.Artist) ? "" : m.Artist.Trim();
        var duration = m.Duration ?? "";
        return $"{album}||{artist}||{duration}";
    }
    /// <summary>获取（或创建）搜索会话：键变化时新建，超出上限淘汰最旧</summary>
    private SearchSession GetOrCreateSearchSession(string query, string searchType, string sourceId)
    {
        var key = BuildSearchSessionKey(query, searchType, sourceId);
        if (_searchSessions.TryGetValue(key, out var existing))
            return existing;

        var session = new SearchSession { Key = key, Sequence = ++_searchSessionSeq };
        _searchSessions[key] = session;

        // 超出上限：淘汰除当前外序列号最小的会话
        if (_searchSessions.Count > MaxSearchSessions)
        {
            var oldestKey = _searchSessions
                .Where(kv => !string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Value.Sequence)
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (oldestKey != null)
                _searchSessions.Remove(oldestKey);
        }
        return session;
    }

    private static string BuildSearchSessionKey(string query, string searchType, string sourceId)
        => $"{(query ?? "").Trim()}|{searchType ?? "song"}|{sourceId ?? "all"}";

    /// <summary>确定参与搜索的启用内置源（单音源或全部音源）</summary>
    private List<MusicSource> GetSearchSources(string sourceId)
    {
        var requestedSourceId = string.IsNullOrWhiteSpace(sourceId) ? "all" : sourceId;
        var enabled = GetEnabledSources().Where(s => s.Type == SourceType.BuiltIn).ToList();
        if (!string.Equals(requestedSourceId, "all", StringComparison.OrdinalIgnoreCase))
            enabled = enabled.Where(s => string.Equals(s.Id, requestedSourceId, StringComparison.OrdinalIgnoreCase)).ToList();

        // JSON manifests participate after built-ins, only when they explicitly support the requested platform.
        var jsonSources = GetEnabledSources()
            .Where(s => s.Type != SourceType.BuiltIn)
            .Where(s =>
            {
                var script = _scriptSource.GetScript(s.Id);
                return script?.Kind is LxSourceType.JsonManifest
                    && (string.Equals(requestedSourceId, "all", StringComparison.OrdinalIgnoreCase)
                        || SupportsRequestedSource(script, requestedSourceId));
            });
        enabled.AddRange(jsonSources);
        return enabled;
    }
    /// <summary>抓取一轮：内置源和 JSON 清单各取下一页，全局去重后按固定源顺序追加到会话缓冲</summary>
    private async Task FetchNextSearchRoundAsync(
        SearchSession session,
        List<MusicSource> sources,
        string query,
        string searchType,
        int batchSize,
        string requestedSourceId,
        CancellationToken cancellationToken)
    {
        var tasks = sources.Select(s =>
        {
            var script = _scriptSource.GetScript(s.Id);
            return script?.Kind is LxSourceType.JsonManifest
                ? SearchJsonSourcePageAsync(
                    s,
                    requestedSourceId,
                    query,
                    session.Cursor.GetValueOrDefault(s.Id, 0) + 1,
                    batchSize,
                    searchType,
                    cancellationToken)
                : SearchSourcePageAsync(
                    s.Id,
                    query,
                    session.Cursor.GetValueOrDefault(s.Id, 0) + 1,
                    batchSize,
                    searchType,
                    cancellationToken);
        }).ToArray();
        var pages = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();

        var anyMore = false;
        var seen = new HashSet<string>(session.Buffer.Select(BuildSearchDedupKey), StringComparer.OrdinalIgnoreCase);

        void AppendPage(int index)
        {
            var source = sources[index];
            var page = pages[index];
            session.Cursor[source.Id] = session.Cursor.GetValueOrDefault(source.Id, 0) + 1;
            session.Fetched[source.Id] = session.Fetched.GetValueOrDefault(source.Id, 0) + page.Items.Count;

            if (page.Items.Count >= batchSize) anyMore = true;
            if (page.TotalItems > 0 && page.TotalItems > session.Fetched[source.Id]) anyMore = true;

            foreach (var item in page.Items)
            {
                if (seen.Add(BuildSearchDedupKey(item)))
                    session.Buffer.Add(item);
            }
        }

        foreach (var sourceId in SearchResultSourceOrder)
        {
            var index = sources.FindIndex(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) AppendPage(index);
        }

        // Manifest sources are appended after the built-ins and never remap a song to another platform.
        for (var index = 0; index < sources.Count; index++)
        {
            var script = _scriptSource.GetScript(sources[index].Id);
            if (script?.Kind is LxSourceType.JsonManifest) AppendPage(index);
        }

        session.HasMore = anyMore;
    }

    private async Task<PagedResult<MusicInfo>> SearchJsonSourcePageAsync(
        MusicSource source,
        string requestedSourceId,
        string query,
        int page,
        int pageSize,
        string searchType,
        CancellationToken cancellationToken)
    {
        var runtime = _scriptSource.GetJsonSource(source.Id);
        var script = _scriptSource.GetScript(source.Id);
        if (runtime is null || script?.Kind is not LxSourceType.JsonManifest)
            return PagedResult<MusicInfo>.Empty(page, pageSize);

        var platforms = string.Equals(requestedSourceId, "all", StringComparison.OrdinalIgnoreCase)
            ? script.SupportedSources
            : [requestedSourceId];
        var results = new List<MusicInfo>();
        try
        {
            foreach (var platform in platforms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await runtime.SearchAsync(
                    platform,
                    query,
                    page,
                    pageSize,
                    searchType,
                    cancellationToken);
                results.AddRange(result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "JSON 音源搜索失败: {SourceId}", source.Id);
        }

        return new PagedResult<MusicInfo>
        {
            CurrentPage = page,
            PageSize = pageSize,
            TotalItems = results.Count,
            HasMore = results.Count >= pageSize,
            Items = results
        };
    }
    /// <summary>搜索会话：累积的全局去重结果 + 每源抓取游标/已抓条数</summary>
    private sealed class SearchSession
    {
        public string Key = string.Empty;
        public long Sequence;
        public List<MusicInfo> Buffer = new();
        public Dictionary<string, int> Cursor = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Fetched = new(StringComparer.OrdinalIgnoreCase);
        public bool HasMore = true;
    }

    /// <summary>脚本源健康状态：连续失败计数 + 降级截止时间（TickCount64）</summary>
    private sealed class ScriptHealth
    {
        public int ConsecutiveFailures;
        public long DegradedUntilTick;
    }


    /// <summary>获取专辑详情（歌曲列表），按专辑行的来源路由到对应平台</summary>
    public async Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(
        MusicInfo album,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (album == null) return PagedResult<MusicInfo>.Empty(page, pageSize);
        var source = string.IsNullOrWhiteSpace(album.Source) ? "netease" : album.Source;
        if (_apiMap.TryGetValue(source, out var api))
        {
            try { return await api.GetAlbumDetailAsync(album, page, pageSize, cancellationToken).WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "获取专辑详情失败: {Album}", album.Title); }
        }
        return PagedResult<MusicInfo>.Empty(page, pageSize);
    }

    // ================================================================
    //  播放 URL — 脚本源(JS)优先 → 当前源内置 → 跨源
    // ================================================================

    public Task<string?> GetPlayUrlAsync(
        string musicId,
        string source,
        string quality = "128k",
        CancellationToken cancellationToken = default)
        => GetPlayUrlCoreAsync(musicId, source, quality, null, cancellationToken);

    public Task<string?> GetPlayUrlAsync(
        MusicInfo music,
        string source,
        string quality = "128k",
        CancellationToken cancellationToken = default)
        => GetPlayUrlCoreAsync(music.Id, source, quality, music, cancellationToken);

    private async Task<string?> GetPlayUrlCoreAsync(
        string musicId,
        string source,
        string quality,
        MusicInfo? music,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("获取播放URL: {MusicId}, 来源: {Source}, 音质: {Quality}", musicId, source, quality);

        // 解析顺序：默认源 → 其余已启用脚本源（按优先级，超时保护） → 官方API（当前源优先，再跨源）
        var defaultSourceId = _sources.FirstOrDefault(s => s.IsDefault)?.Id;
        var scripts = _scriptSource.GetAllScripts();

        // 第1级: 默认音源（限时 3 秒，超时回退，避免卡住播放）
        if (!string.IsNullOrEmpty(defaultSourceId))
        {
            var defaultScript = scripts.FirstOrDefault(s => string.Equals(s.Id, defaultSourceId, StringComparison.OrdinalIgnoreCase));
            if (defaultScript != null
                && SupportsRequestedSource(defaultScript, source)
                && SupportsQuality(defaultScript, source, quality))
            {
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    attempt.CancelAfter(TimeSpan.FromSeconds(5));
                    var url = await TryResolveScriptUrlAsync(
                        defaultScript,
                        musicId,
                        source,
                        quality,
                        music,
                        attempt.Token);
                    if (!string.IsNullOrEmpty(url))
                    {
                        _logger.LogInformation("默认脚本源 [{Script}] 获取播放URL成功: {Source}", defaultScript.Name, source);
                        return url;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    _logger.LogDebug(
                        "Default script URL resolution failed ({ScriptId}): {ErrorType}",
                        defaultScript.Id,
                        exception.GetType().Name);
                }
            }
        }

        // 第2级: 其余已启用脚本源（按优先级依次尝试；跳过默认源与系统隐藏源；每源限时 10 秒）。
        // 只回退到“明确支持当前平台”的脚本；SupportedSources 为空视为未声明，直接拒绝，
        // 避免把网易的歌拿去酷我/酷狗脚本解析而串歌。
        var enabledScriptSources = _sources
            .Where(s => s.IsEnabled && !s.IsSystemHidden && !string.Equals(s.Id, defaultSourceId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Priority)
            .ToList();
        foreach (var src in enabledScriptSources)
        {
            var script = scripts.FirstOrDefault(s => string.Equals(s.Id, src.Id, StringComparison.OrdinalIgnoreCase));
            if (script == null) continue;
            // Scripts must explicitly declare the requested platform; empty support is rejected.
            if (!SupportsRequestedSource(script, source)) continue;
            if (!SupportsQuality(script, source, quality)) continue;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(10));
                var url = await TryResolveScriptUrlAsync(
                    script,
                    musicId,
                    source,
                    quality,
                    music,
                    attempt.Token);
                if (!string.IsNullOrEmpty(url))
                {
                    _logger.LogInformation("脚本源 [{Script}] 获取播放URL成功: {Source}", script.Name, source);
                    return url;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    "Script URL resolution failed ({ScriptId}): {ErrorType}",
                    script.Id,
                    exception.GetType().Name);
            }
        }

        // 第3级: 官方API —— 当前音源内置（用该平台正确歌曲 ID，不会串歌）
        if (_apiMap.TryGetValue(source, out var api)
            && api.GetSupportedQualities().Contains(quality, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var url = await api.GetPlayUrlAsync(musicId, quality, cancellationToken);
                if (!string.IsNullOrEmpty(url)) return url;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Built-in source URL resolution failed ({SourceId}): {ErrorType}",
                    source,
                    exception.GetType().Name);
            }
        }

        // 注意：不再跨平台回退官方 API —— 跨平台的歌曲 ID 不通用，
        // 用网易的 ID 去查 QQ/酷我等必然返回另一首歌（导致“切换接口后播放另外歌曲”）。
        return null;
    }

    private static bool SupportsRequestedSource(LxScriptInfo script, string source) =>
        script.SupportedSources.Contains(source, StringComparer.OrdinalIgnoreCase)
        && (!script.ActionMap.TryGetValue(ToLxSourceId(source), out var actions)
            || actions.Count == 0
            || actions.Contains("musicUrl", StringComparer.OrdinalIgnoreCase));

    private static string ToLxSourceId(string source) => source.ToLowerInvariant() switch
    {
        "qq" => "tx",
        "netease" => "wy",
        "migu" => "mg",
        _ => source.ToLowerInvariant()
    };

    private static bool SupportsQuality(LxScriptInfo script, string source, string quality) =>
        !script.QualityMap.TryGetValue(source, out var declared)
        || declared.Count == 0
        || declared.Contains(quality, StringComparer.OrdinalIgnoreCase);

    /// <summary>解析单个脚本源的播放 URL：正则快路径优先，Jint 执行脚本兜底；过滤占位链接并做可播性校验</summary>
    private async Task<string?> TryResolveScriptUrlAsync(
        LxScriptInfo script,
        string musicId,
        string source,
        string quality,
        MusicInfo? music,
        CancellationToken cancellationToken)
    {
        // 快速失败：本会话已连续失败被降级的源直接跳过，不再发起请求
        if (IsScriptDegraded(script.Id))
        {
            _logger.LogDebug("脚本源 [{Script}] 已降级，跳过", script.Name);
            return null;
        }

        var url = await _scriptSource.GetPlayUrlAsync(
            script.Id,
            musicId,
            source,
            quality,
            cancellationToken);
        if (string.IsNullOrEmpty(url) && music != null && !string.IsNullOrEmpty(script.RawScript))
            url = await _scriptEngine.GetPlayUrlAsync(
                script,
                music,
                source,
                quality,
                cancellationToken);

        // 脚本拿不到歌曲 ID 时常返回 id=undefined/null 之类的占位 URL，视为解析失败，继续尝试其他音源
        if (!string.IsNullOrEmpty(url) &&
            (url.Contains("undefined", StringComparison.OrdinalIgnoreCase) ||
             url.Contains("null", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogDebug("脚本源 [{Script}] 返回无效占位 URL，忽略", script.Name);
            MarkScriptFailure(script.Id);
            return null;
        }

        // 可播性校验：坏 URL（403/404/空响应等）快速跳过，避免坏 URL 抢先、可用源轮不到
        if (!string.IsNullOrEmpty(url))
        {
            var playable = await IsPlayableAsync(url, cancellationToken);
            if (!playable)
            {
                _logger.LogDebug("脚本源 [{Script}] 返回不可播 URL，忽略", script.Name);
                MarkScriptFailure(script.Id);
                return null;
            }
            MarkScriptSuccess(script.Id);
        }
        return url;
    }

    /// <summary>轻量可播性校验：Range GET 首段，2xx/206 且 audio/* 内容类型、或长度>64KB、或首字节为音频魔数即通过</summary>
    private async Task<bool> IsPlayableAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 131071);
            using var resp = await _playUrlProbeClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)resp.StatusCode;
            if (status is not (200 or 206)) return false;
            var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (ct.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return true;
            if (resp.Content.Headers.ContentLength is long len && len > 65536) return true;
            // 无 audio 类型时读前 4 字节看音频魔数
            try
            {
                using var s = await resp.Content.ReadAsStreamAsync();
                var buf = new byte[4];
                var n = await s.ReadAsync(buf, 0, 4, cts.Token);
                if (n >= 3)
                {
                    var head = System.Text.Encoding.ASCII.GetString(buf, 0, n);
                    if (n == 4 &&
                        (head.StartsWith("ID3", StringComparison.Ordinal) ||
                         head.StartsWith("RIFF", StringComparison.Ordinal) ||
                         head.StartsWith("fLaC", StringComparison.Ordinal) ||
                         head.StartsWith("OggS", StringComparison.Ordinal) ||
                         (buf[0] == 0xFF && (buf[1] & 0xE0) == 0xE0)))
                        return true;
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    "Audio probe header read failed: {ErrorType}",
                    exception.GetType().Name);
            }
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _logger.LogDebug(
                "Audio probe failed: {ErrorType}",
                exception.GetType().Name);
            return false;
        }
    }

    /// <summary>脚本源连续失败 ≥2 次 → 本会话降级（快速跳过）一段时间，成功即复位</summary>
    private bool IsScriptDegraded(string id)
    {
        lock (_scriptHealthGate)
        {
            if (!_scriptHealth.TryGetValue(id, out var h)) return false;
            return h.DegradedUntilTick > 0 && FrameworkCompatibility.CurrentTickMs < h.DegradedUntilTick;
        }
    }

    private void MarkScriptFailure(string id)
    {
        lock (_scriptHealthGate)
        {
            if (!_scriptHealth.TryGetValue(id, out var h))
            {
                h = new ScriptHealth();
                _scriptHealth[id] = h;
            }
            h.ConsecutiveFailures++;
            h.DegradedUntilTick = h.ConsecutiveFailures >= 2
                ? (long)(FrameworkCompatibility.CurrentTickMs + TimeSpan.FromMinutes(5).TotalMilliseconds)
                : 0;
        }
    }

    private void MarkScriptSuccess(string id)
    {
        lock (_scriptHealthGate)
        {
            if (_scriptHealth.TryGetValue(id, out var h))
            {
                h.ConsecutiveFailures = 0;
                h.DegradedUntilTick = 0;
            }
        }
    }

    /// <summary>已启用的脚本源（按优先级排序），供 lyric/pic 等动作遍历</summary>
    private List<LxScriptInfo> GetEnabledPlaybackScripts()
        => _sources
            .Where(s => s.IsEnabled && !s.IsSystemHidden)
            .OrderBy(s => s.Priority)
            .Select(s => _scriptSource.GetScript(s.Id))
            .Where(x => x != null)
            .Cast<LxScriptInfo>()
            .ToList();

    /// <summary>通过脚本源的 lyric 动作解析歌词（失败/无歌词返回 null）</summary>
    public async Task<LxLyricRaw?> GetLyricFromScriptsAsync(
        MusicInfo music,
        CancellationToken cancellationToken = default)
    {
        if (music == null || string.IsNullOrEmpty(music.Source)) return null;
        foreach (var script in GetEnabledPlaybackScripts())
        {
            try
            {
                LxLyricRaw? r;
                if (script.Kind is LxSourceType.JsonManifest)
                {
                    var jsonSource = _scriptSource.GetJsonSource(script.Id);
                    var jsonLyric = jsonSource is null
                        ? null
                        : await jsonSource.GetLyricAsync(script.SupportedSources.FirstOrDefault(p =>
                            string.Equals(p, music.Source, StringComparison.OrdinalIgnoreCase))
                            ?? music.Source,
                            music,
                            cancellationToken);
                    r = jsonLyric is null ? null : new LxLyricRaw
                    {
                        Lyric = jsonLyric.Lyric,
                        Tlyric = jsonLyric.Translation,
                        Rlyric = jsonLyric.Romanization,
                        Lxlyric = jsonLyric.LxLyric
                    };
                }
                else
                {
                    r = await _scriptEngine.GetLyricAsync(
                        script,
                        music,
                        music.Source,
                        cancellationToken);
                }
                if (r != null && !string.IsNullOrWhiteSpace(r.Lyric)) return r;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    "Script lyric lookup failed ({ScriptId}): {ErrorType}",
                    script.Id,
                    exception.GetType().Name);
            }
        }
        return null;
    }

    public async Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(
        string sourceId = "netease",
        CancellationToken cancellationToken = default)
    {
        if (_apiMap.TryGetValue(sourceId, out var api))
        {
            try { return await api.GetPlaylistCategoriesAsync(cancellationToken).WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "获取歌单分类失败: {SourceId}", sourceId); throw; }
        }
        return new List<PlaylistCategory>();
    }

    public async Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(
        string category,
        int page = 1,
        int pageSize = 30,
        string sourceId = "netease",
        CancellationToken cancellationToken = default)
    {
        if (_apiMap.TryGetValue(sourceId, out var api))
        {
            try { return await api.GetPlaylistsByCategoryAsync(category, page, pageSize, cancellationToken).WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "获取歌单列表失败"); throw; }
        }
        return PagedResult<Playlist>.Empty(page, pageSize);
    }

    public async Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(
        string playlistId,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var (sourceId, cleanId) = ParseSourceId(playlistId, "netease");
        if (_apiMap.TryGetValue(sourceId, out var api))
        {
            try { return await api.GetPlaylistDetailAsync(cleanId, page, pageSize, cancellationToken).WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "获取歌单详情失败: {PlaylistId}", playlistId); throw; }
        }
        return PagedResult<MusicInfo>.Empty(page, pageSize);
    }

    public async Task<List<Ranking>> GetRankingsAsync(
        string sourceId = "netease",
        CancellationToken cancellationToken = default)
    {
        if (_apiMap.TryGetValue(sourceId, out var api))
        {
            try { return await api.GetRankingsAsync(cancellationToken).WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "获取排行榜失败: {SourceId}", sourceId); throw; }
        }
        return new List<Ranking>();
    }

    public async Task<PagedResult<MusicInfo>> GetRankingDetailAsync(
        string rankingId,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var (sourceId, cleanId) = ParseSourceId(rankingId, "netease");
        if (_apiMap.TryGetValue(sourceId, out var api))
        {
            try { return await api.GetRankingDetailAsync(cleanId, page, pageSize, cancellationToken).WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "获取排行榜详情失败: {RankingId}", rankingId); throw; }
        }
        return PagedResult<MusicInfo>.Empty(page, pageSize);
    }

    public List<MusicSource> GetAllSources() => _sources.Where(s => !s.IsSystemHidden).ToList();
    public List<MusicSource> GetEnabledSources() => _sources.Where(s => s.IsEnabled).OrderBy(s => s.Priority).ToList();

    /// <summary>获取默认音源</summary>
    public MusicSource? GetDefaultSource()
        => _sources.FirstOrDefault(s => s.IsDefault) ?? _sources.FirstOrDefault(s => string.Equals(s.Id, "netease", StringComparison.OrdinalIgnoreCase));

    /// <summary>设置默认音源</summary>
    public void SetDefaultSource(string id)
    {
        var target = _sources.FirstOrDefault(source =>
            string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase));
        if (target?.TrustState == ScriptTrustState.Untrusted) return;
        _defaultTouchedByUser = true;
        foreach (var s in _sources)
            s.IsDefault = string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase);
        ClearCache();
        StartPersistence();
        RaiseSourcesChanged();
    }

    /// <summary>设置唯一默认音源并立即持久化；保存失败时回滚并保持原默认音源。</summary>
    public async Task<bool> SetDefaultSourceAsync(string id, CancellationToken cancellationToken = default)
    {
        var target = _sources.FirstOrDefault(source =>
            string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.TrustState == ScriptTrustState.Untrusted) return false;

        var previous = _sources.Select(source => (Source: source, source.IsDefault)).ToArray();
        foreach (var source in _sources)
            source.IsDefault = string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase);
        try
        {
            await _dataService.SaveMusicSourcesAsync(_sources, cancellationToken);
        }
        catch (Exception ex)
        {
            foreach (var (source, wasDefault) in previous) source.IsDefault = wasDefault;
            _logger.LogError(ex, "默认音源保存失败，已回滚: {Id}", id);
            return false;
        }

        _defaultTouchedByUser = true;
        ClearCache();
        RaiseSourcesChanged();
        return true;
    }

    /// <summary>获取指定音源支持的音质列表</summary>

    public IReadOnlyList<ManualSourceInterface> GetPlaybackInterfaces(MusicInfo target)
    {
        FrameworkCompatibility.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(target.Source)) return [];

        var interfaces = new List<ManualSourceInterface>();
        foreach (var (platform, api) in _apiMap)
        {
            var source = _sources.FirstOrDefault(item =>
                string.Equals(item.Id, platform, StringComparison.OrdinalIgnoreCase));
            if (source?.IsEnabled == true)
            {
                interfaces.Add(ManualSourceInterface.BuiltIn(
                    source.Id,
                    string.IsNullOrWhiteSpace(source.Name) ? api.SourceName : source.Name,
                    platform,
                    api.GetSupportedQualities()));
            }
        }

        foreach (var source in _sources
                     .Where(item => item.IsEnabled && !item.IsSystemHidden)
                     .OrderBy(item => item.Priority))
        {
            var script = _scriptSource.GetScript(source.Id);
            if (script is null
                || !SupportsRequestedSource(script, target.Source))
                continue;
            interfaces.Add(ManualSourceInterface.Script(
                source.Id,
                string.IsNullOrWhiteSpace(source.Name) ? script.Name : source.Name,
                target.Source,
                GetScriptQualities(script, target.Source)));
        }

        return interfaces;
    }

    public async Task<IReadOnlyList<MusicInfo>> SearchCandidatesAsync(
        MusicInfo target,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_apiMap.TryGetValue(sourceId, out var api))
        {
            var source = _sources.FirstOrDefault(item =>
                string.Equals(item.Id, sourceId, StringComparison.OrdinalIgnoreCase)
                && item.IsEnabled
                && !item.IsSystemHidden);
            var script = source is null ? null : _scriptSource.GetScript(source.Id);
            if (script is null
                || !SupportsRequestedSource(script, target.Source)
                || !MusicSearchMatcher.IsSameSong(target, target))
                return [];
            return [target];
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(8));
        var result = await api.SearchAsync(
            MusicSearchMatcher.BuildSearchKeyword(target),
            1,
            30,
            "song",
            attempt.Token);
        return MusicSearchMatcher.FindBestMatches(result.Items, target)
            .Where(candidate => MusicSearchMatcher.IsSameSong(candidate, target))
            .ToArray();
    }

    public async Task<string?> ResolveExactAsync(
        MusicInfo candidate,
        string sourceId,
        string quality,
        CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(quality)) return null;
        cancellationToken.ThrowIfCancellationRequested();

        if (_apiMap.TryGetValue(sourceId, out var api)
            && api.GetSupportedQualities().Contains(quality, StringComparer.OrdinalIgnoreCase))
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(8));
            var url = await api.GetPlayUrlAsync(candidate.Id, quality, attempt.Token);
            return string.IsNullOrWhiteSpace(url) || !await IsPlayableAsync(url, attempt.Token)
                ? null
                : url;
        }

        var source = _sources.FirstOrDefault(item =>
            string.Equals(item.Id, sourceId, StringComparison.OrdinalIgnoreCase)
            && item.IsEnabled
            && !item.IsSystemHidden);
        var script = source is null ? null : _scriptSource.GetScript(source.Id);
        if (script is null
            || !SupportsRequestedSource(script, candidate.Source)
            || !SupportsQuality(script, candidate.Source, quality))
            return null;

        using var scriptAttempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scriptAttempt.CancelAfter(TimeSpan.FromSeconds(10));
        return await TryResolveScriptUrlAsync(
            script,
            candidate.Id,
            candidate.Source,
            quality,
            candidate,
            scriptAttempt.Token);
    }

    private IReadOnlyList<string> GetScriptQualities(LxScriptInfo script, string source)
    {
        if (script.QualityMap.TryGetValue(source, out var declared)
            && declared.Count > 0)
            return declared.ToArray();
        return _apiMap.TryGetValue(source, out var api)
            ? api.GetSupportedQualities()
            : ["128k"];
    }

    public Task<List<string>> GetQualitiesForSourceAsync(string sourceId)
    {
        var list = new List<string>();
        // QualityMap 的键已在解析时映射为我们的源ID（如 qq/netease/migu），直接按键查询
        var key = sourceId.ToLower();
        foreach (var script in _scriptSource.GetAllScripts())
        {
            if (script.QualityMap.TryGetValue(key, out var qs))
                foreach (var q in qs) if (!list.Contains(q)) list.Add(q);
        }
        if (_apiMap.TryGetValue(key, out var api))
            foreach (var q in api.GetSupportedQualities())
                if (!list.Contains(q)) list.Add(q);
        if (!list.Contains("128k")) list.Insert(0, "128k");
        return Task.FromResult(list);
    }

    public async Task<ScriptActivationResult> EnableSourceAsync(
        MusicSource s,
        CancellationToken cancellationToken = default)
    {
        if (IsOfficialBuiltIn(s))
        {
            var previousEnabled = s.IsEnabled;
            var previousTrust = s.TrustState;
            s.IsEnabled = true;
            s.TrustState = ScriptTrustState.UserApproved;
            try
            {
                await _dataService.SaveMusicSourcesAsync(_sources, cancellationToken);
            }
            catch (Exception ex)
            {
                s.IsEnabled = previousEnabled;
                s.TrustState = previousTrust;
                _logger.LogError(ex, "内置音源启用持久化失败，已回滚: {Id}", s.Id);
                return new(false, ScriptActivationStatus.PersistenceFailed, "保存失败，已恢复原状态");
            }
            RaiseSourcesChanged();
            return new(true, ScriptActivationStatus.Activated, "内置音源已启用");
        }

        if (string.IsNullOrEmpty(s.ScriptContent))
            return new(false, ScriptActivationStatus.ContentMissing, "暂存内容缺失，未启用");
        var actualDigest = ScriptMusicSource.ComputeSha256(s.ScriptContent);
        var expectedDigest = s.ContentSha256;
        if (string.IsNullOrWhiteSpace(expectedDigest))
        {
            if (s.TrustState != ScriptTrustState.Untrusted)
                return new(false, ScriptActivationStatus.DigestMismatch, "脚本摘要缺失，未启用");
            expectedDigest = actualDigest;
        }
        else if (!string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
            return new(false, ScriptActivationStatus.DigestMismatch, "脚本内容已变化或摘要不匹配，未启用");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = _scriptSource.StageFromContent(
                s.ScriptContent,
                s.Id,
                s.ScriptUrl,
                s.Type is SourceType.Custom or SourceType.Local or SourceType.Bundled);
            if (candidate is null
                || (candidate.Kind is not LxSourceType.JsonManifest
                && !await _scriptEngine.TestLoadAsync(candidate, cancellationToken)))
                return new(false, ScriptActivationStatus.ValidationFailed, "脚本无法注册音乐请求处理器，未启用");

            var previous = _scriptSource.GetScript(s.Id);
            var previousEnabled = s.IsEnabled;
            var previousTrust = s.TrustState;
            var previousDigest = s.ContentSha256;
            if (!_scriptSource.RegisterVerified(candidate, expectedDigest))
                return new(false, ScriptActivationStatus.DigestMismatch, "脚本摘要不匹配，未启用");

            s.IsEnabled = true;
            s.TrustState = s.Type == SourceType.Bundled
                ? ScriptTrustState.Bundled
                : ScriptTrustState.UserApproved;
            s.ContentSha256 = expectedDigest;
            try
            {
                await _dataService.SaveMusicSourcesAsync(_sources);
            }
            catch (Exception ex)
            {
                s.IsEnabled = previousEnabled;
                s.TrustState = previousTrust;
                s.ContentSha256 = previousDigest;
                if (previous?.RawScript is not null)
                    _scriptSource.RegisterVerified(previous, ScriptMusicSource.ComputeSha256(previous.RawScript));
                else
                    _scriptSource.Remove(s.Id);
                _logger.LogError(ex, "音源激活持久化失败，已回滚: {Id}", s.Id);
                return new(false, ScriptActivationStatus.PersistenceFailed, "保存失败，已恢复原可信版本");
            }

            ClearCache();
            RaiseSourcesChanged();
            return new(true, ScriptActivationStatus.Activated, "脚本已验证并启用");
        }
        catch (OperationCanceledException)
        {
            return new(false, ScriptActivationStatus.Cancelled, "启用已取消，原可信版本保持不变");
        }
    }

    public void DisableSource(MusicSource s) { s.IsEnabled = false; StartPersistence(); RaiseSourcesChanged(); }
    public void RemoveSource(MusicSource s)
    {
        _sources.Remove(s);
        _scriptSource.Remove(s.Id);
        ClearCache();
        StartPersistence();
        RaiseSourcesChanged();
    }

    /// <summary>禁用音源并立即持久化；保存失败时回滚启用状态。</summary>
    public async Task<bool> DisableSourceAsync(MusicSource s, CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(s);
        var previousEnabled = s.IsEnabled;
        s.IsEnabled = false;
        try
        {
            await _dataService.SaveMusicSourcesAsync(_sources, cancellationToken);
        }
        catch (Exception ex)
        {
            s.IsEnabled = previousEnabled;
            _logger.LogError(ex, "音源禁用保存失败，已回滚: {Id}", s.Id);
            return false;
        }

        ClearCache();
        RaiseSourcesChanged();
        return true;
    }

    /// <summary>删除音源并立即持久化；保存失败时恢复音源位置与已注册脚本。</summary>
    public async Task<bool> RemoveSourceAsync(MusicSource s, CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(s);
        var index = _sources.IndexOf(s);
        if (index < 0) return false;
        var script = _scriptSource.GetScript(s.Id);
        _sources.RemoveAt(index);
        _scriptSource.Remove(s.Id);
        try
        {
            await _dataService.SaveMusicSourcesAsync(_sources, cancellationToken);
        }
        catch (Exception ex)
        {
            _sources.Insert(index, s);
            if (script?.RawScript is not null)
                _scriptSource.RegisterVerified(script, ScriptMusicSource.ComputeSha256(script.RawScript));
            _logger.LogError(ex, "音源删除保存失败，已恢复: {Id}", s.Id);
            return false;
        }

        ClearCache();
        RaiseSourcesChanged();
        return true;
    }

    private void ClearCache() { _searchSessions.Clear(); }
    public async Task<bool> UpdateSourceAsync(MusicSource s)
    {
        var previousUpdateDate = s.LastUpdateDate;
        s.LastUpdateDate = DateTime.Now;
        try
        {
            await PersistSourcesOrThrowAsync();
        }
        catch (Exception ex)
        {
            s.LastUpdateDate = previousUpdateDate;
            _logger.LogError(ex, "音源更新保存失败，已回滚: {Id}", s.Id);
            return false;
        }

        ClearCache();
        RaiseSourcesChanged();
        return true;
    }

    public async Task<bool> RefreshSourceAsync(
        MusicSource source,
        CancellationToken cancellationToken = default)
    {
        if (IsOfficialBuiltIn(source)
            || source.Type is SourceType.Bundled or SourceType.Local
            || !HasHttpScriptUrl(source)
            || !_sources.Any(item => string.Equals(
                item.Id,
                source.Id,
                StringComparison.OrdinalIgnoreCase)))
            return false;

        var candidate = await _scriptSource.StageFromUrlAsync(
            source.ScriptUrl,
            source.Id,
            cancellationToken);
        if (candidate?.RawScript is null
            || (candidate.Kind is not LxSourceType.JsonManifest
                && !await _scriptEngine.TestLoadAsync(candidate, cancellationToken)))
            return false;

        var digest = ScriptMusicSource.ComputeSha256(candidate.RawScript);
        var previousScript = _scriptSource.GetScript(source.Id);
        var previous = SourceSnapshot.Capture(source);

        source.Name = candidate.Name;
        source.Version = candidate.Version
            ?? (string.IsNullOrEmpty(source.Version) ? "1.0" : source.Version);
        source.ScriptContent = candidate.RawScript;
        source.ContentSha256 = digest;
        source.Type = SourceType.Network;
        source.TrustState = ScriptTrustState.UserApproved;
        source.LastUpdateDate = DateTime.Now;

        if (!_scriptSource.RegisterVerified(candidate, digest))
        {
            previous.Restore(source);
            return false;
        }

        try
        {
            await _dataService.SaveMusicSourcesAsync(_sources, cancellationToken);
        }
        catch
        {
            previous.Restore(source);
            if (previousScript?.RawScript is not null)
            {
                _scriptSource.RegisterVerified(
                    previousScript,
                    ScriptMusicSource.ComputeSha256(previousScript.RawScript));
            }
            else
            {
                _scriptSource.Remove(source.Id);
            }
            throw;
        }

        ClearCache();
        RaiseSourcesChanged();
        return true;
    }

    public async Task<MusicSource?> ImportFromUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var scriptId = $"script_{Guid.NewGuid():N}"[..16];
        var info = await _scriptSource.StageFromUrlAsync(url, scriptId, cancellationToken);
        return await ImportValidatedSourceAsync(info, url, SourceType.Network, cancellationToken);
    }

    public async Task<MusicSource?> ImportFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return null;
        var scriptId = $"script_{Guid.NewGuid():N}"[..16];
        var info = await _scriptSource.StageFromFileAsync(filePath, scriptId, cancellationToken);
        return await ImportValidatedSourceAsync(info, filePath, SourceType.Custom, cancellationToken);
    }

    private async Task<MusicSource?> ImportValidatedSourceAsync(
        LxScriptInfo? info,
        string location,
        SourceType type,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (info?.RawScript is null) return null;
        var source = new MusicSource
        {
            Id = info.Id,
            Name = info.Name,
            Version = info.Version ?? "1.0",
            ScriptUrl = location,
            ScriptContent = info.RawScript,
            ContentSha256 = ScriptMusicSource.ComputeSha256(info.RawScript),
            TrustState = ScriptTrustState.Untrusted,
            Type = type,
            IsEnabled = false,
            Priority = _sources.Count + 1,
            AddedDate = DateTime.Now,
            LastUpdateDate = DateTime.Now
        };
        _sources.Add(source);
        var activation = await EnableSourceAsync(source, cancellationToken);
        if (!activation.Succeeded)
        {
            _sources.Remove(source);
            _scriptSource.Remove(source.Id);
            _logger.LogWarning(
                "音源导入验证失败: SourceId={SourceId}, Status={Status}",
                source.Id,
                activation.Status);
            return null;
        }
        return source;
    }

    public async Task<bool> TestSourceAsync(MusicSource s)
    {
        try
        {
            if (IsOfficialBuiltIn(s))
            {
                if (!_apiMap.TryGetValue(s.Id, out var api)) return false;
                var r = await api.SearchAsync("测试", 1, 1);
                return r != null && r.Items != null && r.Items.Count > 0;
            }

            // 脚本源：Jint 加载校验通过即有效
            var info = _scriptSource.GetScript(s.Id);
            if (info is { Kind: LxSourceType.JsonManifest, JsonSource: not null })
                return true;
            if (info != null && await _scriptEngine.TestLoadAsync(info))
                return true;

            return await ReloadSourceAsync(s);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Music source test failed: SourceId={SourceId}, ErrorType={ErrorType}",
                s.Id, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>只从已验证的持久化内容重新加载，绝不回源下载。</summary>
    private async Task<bool> ReloadSourceAsync(MusicSource s)
    {
        try
        {
            if (IsOfficialBuiltIn(s)) return true;
            return await TryLoadTrustedScriptAsync(s, CancellationToken.None);
        }
        catch (Exception ex) { _logger.LogError(ex, "音源脚本重载失败: {Id}", s.Id); return false; }
    }

    /// <summary>持久化全部音源配置，失败时记录日志</summary>
    public async Task PersistSourcesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _dataService.SaveMusicSourcesAsync(_sources);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogError(ex, "音源配置保存失败"); }
    }

    /// <summary>持久化全部音源配置，失败时向上抛出，供退出流程播报并保留既有数据</summary>
    public async Task PersistSourcesOrThrowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _dataService.SaveMusicSourcesAsync(_sources, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void StartPersistence() =>
        _backgroundWork.StartLatest(
            "script-source.persist",
            context => PersistSourcesAsync(context.CancellationToken));

    /// <summary>恢复持久化状态；旧版有效脚本自动建立摘要并启用，摘要失败的脚本不进入执行器。</summary>
    private async Task LoadPersistedSourcesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _dataService.GetMusicSourcesAsync();
            if (stored == null || stored.Count == 0) return;

            foreach (var src in stored)
            {
                var existing = _sources.FirstOrDefault(s => string.Equals(s.Id, src.Id, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.IsEnabled = src.IsEnabled;
                    // user manually set a default this session; do not clobber it with the persisted old default
                    if (!_defaultTouchedByUser) existing.IsDefault = src.IsDefault;
                    existing.Priority = src.Priority;
                    continue;
                }

                var establishTrustFromStoredContent = src.TrustState == ScriptTrustState.Untrusted
                    && !string.IsNullOrEmpty(src.ScriptContent);
                if (establishTrustFromStoredContent)
                {
                    var actualDigest = ScriptMusicSource.ComputeSha256(src.ScriptContent!);
                    if (string.IsNullOrWhiteSpace(src.ContentSha256))
                        src.ContentSha256 = actualDigest;
                    if (string.Equals(src.ContentSha256, actualDigest, StringComparison.OrdinalIgnoreCase))
                    {
                        src.TrustState = ScriptTrustState.UserApproved;
                        src.IsEnabled = true;
                    }
                }
                _sources.Add(src);
                if (src.IsEnabled
                    && !IsOfficialBuiltIn(src)
                    && !await TryLoadTrustedScriptAsync(src, cancellationToken))
                {
                    src.IsEnabled = false;
                    src.IsDefault = false;
                    if (establishTrustFromStoredContent)
                        src.TrustState = ScriptTrustState.Untrusted;
                    _logger.LogWarning("持久化脚本未通过摘要或执行校验，已禁用: {Id}", src.Id);
                }
            }
            _logger.LogInformation("已加载持久化音源配置");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogError(ex, "加载持久化音源配置失败"); }
    }

    private async Task<bool> TryLoadTrustedScriptAsync(
        MusicSource source,
        CancellationToken cancellationToken)
    {
        if (source.TrustState == ScriptTrustState.Untrusted
            || string.IsNullOrEmpty(source.ScriptContent)
            || string.IsNullOrWhiteSpace(source.ContentSha256)
            || !string.Equals(
                ScriptMusicSource.ComputeSha256(source.ScriptContent),
                source.ContentSha256,
                StringComparison.OrdinalIgnoreCase))
            return false;

        var info = _scriptSource.StageFromContent(
            source.ScriptContent,
            source.Id,
            source.ScriptUrl,
            source.Type is SourceType.Custom or SourceType.Local or SourceType.Bundled);
        return info is not null
            && (info.Kind is LxSourceType.JsonManifest
                || await _scriptEngine.TestLoadAsync(info, cancellationToken))
            && _scriptSource.RegisterVerified(info, source.ContentSha256);
    }

    // ================================================================
    //  辅助
    // ================================================================

    private static (string source, string cleanId) ParseSourceId(string id, string defaultSource)
    {
        var separator = id.IndexOf('_');
        if (separator > 0)
        {
            var source = id.Substring(0, separator).ToLower();
            if (source is "qq" or "kg" or "kw" or "migu" or "netease")
                return (source, id.Substring(separator + 1));
        }
        return (defaultSource, id);
    }

    public IMusicApi? GetApi(string id) => _apiMap.TryGetValue(id, out var a) ? a : null;
    public ScriptMusicSource GetScriptSource() => _scriptSource;

    private static bool HasHttpScriptUrl(MusicSource source)
    {
        return Uri.TryCreate(source.ScriptUrl, UriKind.Absolute, out var url)
            && (string.Equals(url.Scheme, "http", StringComparison.Ordinal)
                || string.Equals(url.Scheme, "https", StringComparison.Ordinal));
    }

    private bool IsOfficialBuiltIn(MusicSource source) =>
        source.Type == SourceType.BuiltIn && _apiMap.ContainsKey(source.Id);

    private sealed record SubscriptionSource(string Id, string Url);


}
