using Microsoft.Extensions.Logging;
using LXMusicAccessible.Models;
using LXMusicAccessible.Core.MusicApi;
using System.Collections.Concurrent;

namespace LXMusicAccessible.Services;

/// <summary>
/// 热门搜索词聚合服务
/// </summary>
public interface IHotSearchService
{
    Task<List<HotSearchWord>> GetHotSearchWordsAsync(string sourceId);
    Task<List<HotSearchWord>> GetAggregatedHotSearchWordsAsync();
    Task<List<HotSearchGroup>> GetGroupedHotSearchWordsAsync();
    void ClearCache();
    void ClearSourceCache(string sourceId);
}

/// <summary>
/// 热门搜索词聚合服务实现
/// </summary>
public class HotSearchService : IHotSearchService
{
    private readonly ILogger<HotSearchService> _logger;
    private readonly Dictionary<string, IMusicApi> _apiMap;
    private readonly ConcurrentDictionary<string, (List<HotSearchWord> words, DateTime updateTime)> _cache = new();
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(30);

    public HotSearchService(
        ILogger<HotSearchService> logger,
        NeteaseMusicService netease,
        QqMusicService qq,
        KugouMusicService kugou,
        KuwoMusicService kuwo,
        MiguMusicService migu)
    {
        _logger = logger;
        _apiMap = new Dictionary<string, IMusicApi>(StringComparer.OrdinalIgnoreCase)
        {
            ["netease"] = netease,
            ["qq"] = qq,
            ["kg"] = kugou,
            ["kw"] = kuwo,
            ["migu"] = migu
        };
    }

    /// <summary>
    /// 获取指定音源的热门搜索词
    /// </summary>
    public async Task<List<HotSearchWord>> GetHotSearchWordsAsync(string sourceId)
    {
        // 检查缓存
        if (_cache.TryGetValue(sourceId, out var cached) && 
            DateTime.Now - cached.updateTime < _cacheExpiration)
        {
            _logger.LogDebug("从缓存获取热门搜索词: {SourceId}", sourceId);
            return cached.words;
        }

        // 从API获取
        if (!_apiMap.TryGetValue(sourceId, out var api))
        {
            _logger.LogWarning("未找到音源: {SourceId}", sourceId);
            return new List<HotSearchWord>();
        }

        try
        {
            var words = await api.GetHotSearchWordsAsync();
            
            // 更新缓存
            _cache.AddOrUpdate(sourceId, 
                (words, DateTime.Now), 
                (key, old) => (words, DateTime.Now));

            _logger.LogInformation("获取热门搜索词成功: {SourceId}, 数量: {Count}", sourceId, words.Count);
            return words;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取热门搜索词失败: {SourceId}", sourceId);
            
            // 返回缓存的旧数据（如果有）
            if (_cache.TryGetValue(sourceId, out cached))
            {
                _logger.LogInformation("使用过期缓存数据: {SourceId}", sourceId);
                return cached.words;
            }
            
            return new List<HotSearchWord>();
        }
    }

    /// <summary>
    /// 获取聚合的热门搜索词（所有音源合并）
    /// </summary>
    public async Task<List<HotSearchWord>> GetAggregatedHotSearchWordsAsync()
    {
        var allWords = new Dictionary<string, HotSearchWord>();
        
        // 并行获取所有音源的热门词
        var tasks = _apiMap.Keys.Select(async sourceId =>
        {
            try
            {
                var words = await GetHotSearchWordsAsync(sourceId);
                foreach (var word in words)
                {
                    if (allWords.TryGetValue(word.Word, out var existing))
                    {
                        // 聚合评分
                        existing.Score += word.Score;
                        // 保留最高的趋势值
                        if (word.Trend.HasValue && (!existing.Trend.HasValue || Math.Abs(word.Trend.Value) > Math.Abs(existing.Trend.Value)))
                        {
                            existing.Trend = word.Trend;
                        }
                    }
                    else
                    {
                        allWords[word.Word] = new HotSearchWord
                        {
                            Word = word.Word,
                            Score = word.Score,
                            Source = "all",
                            UpdateTime = DateTime.Now,
                            Trend = word.Trend
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "聚合热门搜索词失败: {SourceId}", sourceId);
            }
        });

        await Task.WhenAll(tasks);

        // 按评分排序，返回前50个
        return allWords.Values
            .OrderByDescending(w => w.Score)
            .ThenBy(w => w.Word)
            .Take(50)
            .ToList();
    }

    /// <summary>
    /// 获取分组的热门搜索词（按音源分组）
    /// </summary>
    public async Task<List<HotSearchGroup>> GetGroupedHotSearchWordsAsync()
    {
        var groups = new List<HotSearchGroup>();
        
        foreach (var kvp in _apiMap)
        {
            try
            {
                var words = await GetHotSearchWordsAsync(kvp.Key);
                if (words.Any())
                {
                    groups.Add(new HotSearchGroup
                    {
                        Source = kvp.Key,
                        SourceName = kvp.Value.SourceName,
                        Words = words.Take(20).ToList(), // 每个音源取前20个
                        UpdateTime = DateTime.Now
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取分组热门搜索词失败: {SourceId}", kvp.Key);
            }
        }

        return groups.OrderBy(g => g.SourceName).ToList();
    }

    /// <summary>
    /// 清除所有缓存
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
        _logger.LogInformation("已清除所有热门搜索词缓存");
    }

    /// <summary>
    /// 清除指定音源的缓存
    /// </summary>
    public void ClearSourceCache(string sourceId)
    {
        _cache.TryRemove(sourceId, out _);
        _logger.LogInformation("已清除音源缓存: {SourceId}", sourceId);
    }
}
