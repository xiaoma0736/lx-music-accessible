using Microsoft.Extensions.Logging;
using LXMusicAccessible.Models;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;

namespace LXMusicAccessible.Services;

/// <summary>
/// 音源管理服务 — 统一调度 5 大内置音源 + 外部脚本源
/// </summary>
public class MusicSourceService : IMusicSourceService
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
    private const string BundledScriptId = "bundled_verified_fallback";
    private const string BundledScriptResource = "LXMusicAccessible.Core.MusicApi.Scripts.verified-fallback.js";
    private const string BundledScriptSha256 = "9d4aa71b7814860242f2cdfc3370da9cd6d415e290757c9cf122e74069641d50";

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
                IsSystemHidden = true,   // 官方API：仅作搜索/歌单/排行/歌词底层，不在音源管理列表展示
                Priority = kvp.Key switch { "netease" => 1, "qq" => 2, "kg" => 3, "kw" => 4, "migu" => 5, _ => 99 },
                AddedDate = DateTime.Now, LastUpdateDate = DateTime.Now
            });
        }
        _logger.LogInformation("初始化 {Count} 个内置官方API完成（系统隐藏，仅作底层数据源）", _sources.Count);
    }

    /// <summary>只从随版本资源和已验证持久化内容初始化；冷启动绝不下载脚本。</summary>
    public async Task InitializeScriptSourcesAsync(CancellationToken cancellationToken = default)
    {
        if (_scriptsInitialized) return;
        _scriptsInitialized = true;

        // 先恢复持久化配置，再用随版本资源校正固定源。
        await LoadPersistedSourcesAsync(cancellationToken);

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
        existing.LastUpdateDate = DateTime.Now;
    }

    // ================================================================
    //  搜索
    // ================================================================
    public async Task<PagedResult<MusicInfo>> SearchAsync(string query, int page = 1, int pageSize = 20, string searchType = "song", string sourceId = "all")
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
                await FetchNextSearchRoundAsync(session, enabled, query, searchType, batchSize);

            return new PagedResult<MusicInfo>
            {
                CurrentPage = page, PageSize = pageSize,
                TotalItems = session.Buffer.Count,
                HasMore = session.HasMore,
                Items = session.Buffer.Skip((page - 1) * pageSize).Take(pageSize).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "聚合搜索失败: {Query}", query);
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        }
    }

    /// <summary>从单个音源抓取一页原始搜索结果（不额外过滤，尊重平台原生搜索规则）</summary>
    private async Task<PagedResult<MusicInfo>> SearchSourcePageAsync(string sourceId, string query, int page, int pageSize, string searchType = "song")
    {
        if (!_apiMap.TryGetValue(sourceId, out var api))
            return PagedResult<MusicInfo>.Empty(page, pageSize);
        try { return await api.SearchAsync(query, page, pageSize, searchType); }
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
        var enabled = GetEnabledSources().Where(s => s.Type == SourceType.BuiltIn).ToList();
        if (!string.IsNullOrWhiteSpace(sourceId) && !string.Equals(sourceId, "all", StringComparison.OrdinalIgnoreCase))
            enabled = enabled.Where(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase)).ToList();
        return enabled;
    }

    /// <summary>抓取一轮：各源取下一页原始结果，全局去重后按固定源顺序追加到会话缓冲</summary>
    private async Task FetchNextSearchRoundAsync(SearchSession session, List<MusicSource> sources, string query, string searchType, int batchSize)
    {
        var tasks = sources.Select(s => SearchSourcePageAsync(s.Id, query, session.Cursor.GetValueOrDefault(s.Id, 0) + 1, batchSize, searchType)).ToArray();
        var pages = await Task.WhenAll(tasks);

        var anyMore = false;
        var seen = new HashSet<string>(session.Buffer.Select(BuildSearchDedupKey), StringComparer.OrdinalIgnoreCase);

        foreach (var srcId in SearchResultSourceOrder)
        {
            var idx = sources.FindIndex(s => string.Equals(s.Id, srcId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) continue;
            var page = pages[idx];

            session.Cursor[srcId] = session.Cursor.GetValueOrDefault(srcId, 0) + 1;
            session.Fetched[srcId] = session.Fetched.GetValueOrDefault(srcId, 0) + page.Items.Count;

            if (page.Items.Count >= batchSize) anyMore = true;
            if (page.TotalItems > 0 && page.TotalItems > session.Fetched[srcId]) anyMore = true;

            foreach (var m in page.Items)
            {
                if (seen.Add(BuildSearchDedupKey(m)))
                    session.Buffer.Add(m);
            }
        }
        session.HasMore = anyMore;
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
    public async Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50)
    {
        if (album == null) return PagedResult<MusicInfo>.Empty(page, pageSize);
        var source = string.IsNullOrWhiteSpace(album.Source) ? "netease" : album.Source;
        if (_apiMap.TryGetValue(source, out var api))
        {
            try { return await api.GetAlbumDetailAsync(album, page, pageSize); }
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
            if (defaultScript != null)
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
        // 只回退到“明确支持当前平台”的脚本（SupportedSources 为空视为未知，允许尝试），
        // 避免把网易的歌拿去酷我/酷狗脚本解析而串歌。
        var enabledScriptSources = _sources
            .Where(s => s.IsEnabled && !s.IsSystemHidden && !string.Equals(s.Id, defaultSourceId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Priority)
            .ToList();
        foreach (var src in enabledScriptSources)
        {
            var script = scripts.FirstOrDefault(s => string.Equals(s.Id, src.Id, StringComparison.OrdinalIgnoreCase));
            if (script == null) continue;
            // 仅当脚本支持当前平台（或支持列表未知为空）时才尝试，防止跨平台歌曲 ID 串歌
            if (script.SupportedSources.Count > 0 &&
                !script.SupportedSources.Contains(source, StringComparer.OrdinalIgnoreCase))
                continue;
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
        if (_apiMap.TryGetValue(source, out var api))
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
                await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
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
        if (!_scriptHealth.TryGetValue(id, out var h)) return false;
        return h.DegradedUntilTick > 0 && Environment.TickCount64 < h.DegradedUntilTick;
    }

    private void MarkScriptFailure(string id)
    {
        if (!_scriptHealth.TryGetValue(id, out var h)) { h = new ScriptHealth(); _scriptHealth[id] = h; }
        h.ConsecutiveFailures++;
        h.DegradedUntilTick = h.ConsecutiveFailures >= 2
            ? (long)(Environment.TickCount64 + TimeSpan.FromMinutes(5).TotalMilliseconds)
            : 0;
    }

    private void MarkScriptSuccess(string id)
    {
        if (_scriptHealth.TryGetValue(id, out var h))
        {
            h.ConsecutiveFailures = 0;
            h.DegradedUntilTick = 0;
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
                var r = await _scriptEngine.GetLyricAsync(
                    script,
                    music,
                    music.Source,
                    cancellationToken);
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

    /// <summary>通过脚本源的 pic 动作解析封面 URL（失败返回 null；当前 UI 暂无封面展示，仅供接口使用）</summary>
    public async Task<string?> GetPicFromScriptAsync(
        MusicInfo music,
        CancellationToken cancellationToken = default)
    {
        if (music == null || string.IsNullOrEmpty(music.Source)) return null;
        foreach (var script in GetEnabledPlaybackScripts())
        {
            try
            {
                var url = await _scriptEngine.GetPicAsync(
                    script,
                    music,
                    music.Source,
                    cancellationToken);
                if (!string.IsNullOrEmpty(url)) return url;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    "Script cover lookup failed ({ScriptId}): {ErrorType}",
                    script.Id,
                    exception.GetType().Name);
            }
        }
        return null;
    }

    // ================================================================
    //  分类 / 歌单 / 排行榜 — 委托给内置 API
    // ================================================================

    public async Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId = "netease")
    {
        if (_apiMap.TryGetValue(sourceId, out var api))
        {
            try { return await api.GetPlaylistCategoriesAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "获取歌单分类失败: {SourceId}", sourceId); }
        }
        return new List<PlaylistCategory>();
    }

    public async Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page = 1, int pageSize = 30, string sourceId = "netease")
    {
        if (_apiMap.TryGetValue(sourceId, out var api))
        {
            try { return await api.GetPlaylistsByCategoryAsync(category, page, pageSize); }
            catch (Exception ex) { _logger.LogError(ex, "获取歌单列表失败"); }
        }
        return PagedResult<Playlist>.Empty(page, pageSize);
    }

    public async Task<PagedResult<Playlist>> GetPlaylistsAsync(int page = 1, int pageSize = 20, string category = "全部")
        => await GetPlaylistsByCategoryAsync(category, page, pageSize, "netease");

    public async Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50)
    {
        var (sourceId, cleanId) = ParseSourceId(playlistId, "netease");
        if (_apiMap.TryGetValue(sourceId, out var api))
        {
            try { return await api.GetPlaylistDetailAsync(cleanId, page, pageSize); }
            catch (Exception ex) { _logger.LogError(ex, "获取歌单详情失败: {PlaylistId}", playlistId); }
        }
        return PagedResult<MusicInfo>.Empty(page, pageSize);
    }

    public async Task<List<Ranking>> GetRankingsAsync(string sourceId = "netease")
    {
        if (_apiMap.TryGetValue(sourceId, out var api))
        {
            try { return await api.GetRankingsAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "获取排行榜失败: {SourceId}", sourceId); }
        }
        return new List<Ranking>();
    }

    public async Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50)
    {
        var (sourceId, cleanId) = ParseSourceId(rankingId, "netease");
        if (_apiMap.TryGetValue(sourceId, out var api))
        {
            try { return await api.GetRankingDetailAsync(cleanId, page, pageSize); }
            catch (Exception ex) { _logger.LogError(ex, "获取排行榜详情失败: {RankingId}", rankingId); }
        }
        return PagedResult<MusicInfo>.Empty(page, pageSize);
    }

    public async Task<MusicInfo?> GetMusicInfoAsync(string musicId, string source)
    {
        if (_apiMap.TryGetValue(source, out var api))
        {
            try { var r = await api.SearchAsync(musicId, 1, 1); return r.Items.FirstOrDefault(); }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    "Music info lookup failed ({SourceId}): {ErrorType}",
                    source,
                    exception.GetType().Name);
            }
        }
        return null;
    }

    // ================================================================
    //  音源管理
    // ================================================================

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

    /// <summary>获取指定音源支持的音质列表</summary>
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
        if (s.Type == SourceType.BuiltIn)
        {
            s.IsEnabled = true;
            await _dataService.SaveMusicSourcesAsync(_sources);
            RaiseSourcesChanged();
            return new(true, ScriptActivationStatus.Activated, "内置音源已启用");
        }

        if (string.IsNullOrEmpty(s.ScriptContent))
            return new(false, ScriptActivationStatus.ContentMissing, "暂存内容缺失，未启用");
        if (string.IsNullOrWhiteSpace(s.ContentSha256)
            || !string.Equals(
                ScriptMusicSource.ComputeSha256(s.ScriptContent),
                s.ContentSha256,
                StringComparison.OrdinalIgnoreCase))
            return new(false, ScriptActivationStatus.DigestMismatch, "脚本内容已变化或摘要不匹配，未启用");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = _scriptSource.StageFromContent(
                s.ScriptContent,
                s.Id,
                s.ScriptUrl,
                s.Type is SourceType.Custom or SourceType.Local or SourceType.Bundled);
            if (candidate is null || !await _scriptEngine.TestLoadAsync(candidate, cancellationToken))
                return new(false, ScriptActivationStatus.ValidationFailed, "脚本无法注册音乐请求处理器，未启用");

            var previous = _scriptSource.GetScript(s.Id);
            var previousEnabled = s.IsEnabled;
            var previousTrust = s.TrustState;
            if (!_scriptSource.RegisterVerified(candidate, s.ContentSha256))
                return new(false, ScriptActivationStatus.DigestMismatch, "脚本摘要不匹配，未启用");

            s.IsEnabled = true;
            s.TrustState = s.Type == SourceType.Bundled
                ? ScriptTrustState.Bundled
                : ScriptTrustState.UserApproved;
            try
            {
                await _dataService.SaveMusicSourcesAsync(_sources);
            }
            catch (Exception ex)
            {
                s.IsEnabled = previousEnabled;
                s.TrustState = previousTrust;
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
    public void ClearCache() { _searchSessions.Clear(); }
    public async Task<bool> UpdateSourceAsync(MusicSource s) { s.LastUpdateDate = DateTime.Now; ClearCache(); await PersistSourcesAsync(); RaiseSourcesChanged(); return true; }

    public async Task<MusicSource?> ImportFromUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var scriptId = $"script_{Guid.NewGuid():N}"[..16];
        var info = await _scriptSource.StageFromUrlAsync(url, scriptId, cancellationToken);
        return await PersistStagedSourceAsync(info, url, SourceType.Network, cancellationToken);
    }

    public async Task<MusicSource?> ImportFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return null;
        var scriptId = $"script_{Guid.NewGuid():N}"[..16];
        var info = await _scriptSource.StageFromFileAsync(filePath, scriptId, cancellationToken);
        return await PersistStagedSourceAsync(info, filePath, SourceType.Custom, cancellationToken);
    }

    private async Task<MusicSource?> PersistStagedSourceAsync(
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
        try
        {
            await _dataService.SaveMusicSourcesAsync(_sources);
        }
        catch
        {
            _sources.Remove(source);
            throw;
        }
        ClearCache();
        RaiseSourcesChanged();
        return source;
    }

    public async Task<bool> TestSourceAsync(MusicSource s)
    {
        try
        {
            if (s.Type == SourceType.BuiltIn)
            {
                if (!_apiMap.TryGetValue(s.Id, out var api)) return false;
                var r = await api.SearchAsync("测试", 1, 1);
                return r != null && r.Items != null && r.Items.Count > 0;
            }

            // 脚本源：Jint 加载校验通过即有效
            var info = _scriptSource.GetScript(s.Id);
            if (info != null && await _scriptEngine.TestLoadAsync(info))
                return true;

            return await ReloadSourceAsync(s);
        }
        catch { return false; }
    }

    /// <summary>只从已验证的持久化内容重新加载，绝不回源下载。</summary>
    public async Task<bool> ReloadSourceAsync(MusicSource s)
    {
        try
        {
            if (s.Type == SourceType.BuiltIn) return true;
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

    private void StartPersistence() =>
        _backgroundWork.StartLatest(
            "script-source.persist",
            context => PersistSourcesAsync(context.CancellationToken));

    /// <summary>恢复持久化状态；未信任或摘要失败的脚本只展示为禁用，不进入执行器。</summary>
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

                if (src.TrustState == ScriptTrustState.Untrusted)
                {
                    src.IsEnabled = false;
                    src.IsDefault = false;
                }
                _sources.Add(src);
                if (src.IsEnabled && !await TryLoadTrustedScriptAsync(src, cancellationToken))
                {
                    src.IsEnabled = false;
                    src.IsDefault = false;
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
            && await _scriptEngine.TestLoadAsync(info, cancellationToken)
            && _scriptSource.RegisterVerified(info, source.ContentSha256);
    }

    // ================================================================
    //  辅助
    // ================================================================

    private static (string source, string cleanId) ParseSourceId(string id, string defaultSource)
    {
        var parts = id.Split('_', 2);
        if (parts.Length == 2 && parts[0].ToLower() is "qq" or "kg" or "kw" or "migu" or "netease")
            return (parts[0].ToLower(), parts[1]);
        return (defaultSource, id);
    }

    public IMusicApi? GetApi(string id) => _apiMap.TryGetValue(id, out var a) ? a : null;
    public ScriptMusicSource GetScriptSource() => _scriptSource;
}
