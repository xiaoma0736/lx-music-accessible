using Microsoft.Extensions.Logging;
using LXMusicAccessible.Models;
using System.Collections.Concurrent;

namespace LXMusicAccessible.Services;

/// <summary>
/// 歌词服务实现 — 同平台歌词优先，缺失时自动跨平台搜索（netease→qq→kg→kw→migu），带会话缓存
/// </summary>
public class LyricService : ILyricService
{
    private readonly ILogger<LyricService> _logger;
    private readonly IMusicSourceService _musicSource;
    private static readonly string[] SearchOrder = { "netease", "qq", "kg", "kw", "migu" };
    private readonly ConcurrentDictionary<string, LyricResult> _cache = new();
    // ConcurrentDictionary 不允许 null 值，未命中（已搜索无果）单独用标记集记录，避免同一首歌反复搜索
    private readonly ConcurrentDictionary<string, byte> _missed = new();
    private Lyric? _currentLyric;
    private int _currentIndex = -1;

    public event EventHandler<LyricLineChangedEventArgs>? LineChanged;

    public LyricService(ILogger<LyricService> logger, IMusicSourceService musicSource)
    {
        _logger = logger;
        _musicSource = musicSource;
    }

    /// <summary>获取歌词：同平台优先，缺失时自动跨平台搜索；null 表示确定无歌词（已缓存，避免重复请求）</summary>
    public async Task<LyricResult?> GetLyricAsync(MusicInfo music)
    {
        if (music == null || string.IsNullOrEmpty(music.Source) ||
            string.Equals(music.Source, "Local", StringComparison.OrdinalIgnoreCase))
            return null;

        var key = $"{music.Source}:{music.Id}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        if (_missed.ContainsKey(key))
            return null;

        _logger.LogInformation("获取歌词: {Title} - {Artist} [{Source}]", music.Title, music.Artist, music.Source);

        // 1) 同平台歌词
        var own = await FetchFromApiAsync(music);
        if (own is { Lines.Count: > 0 })
        {
            var r = new LyricResult { Lyric = own, SourceId = music.Source, FromSearch = false };
            _cache[key] = r;
            return r;
        }

        // 1.5) 脚本源歌词（同一首歌，由第三方音源脚本的 lyric 动作提供）
        var scripted = await FetchFromScriptsAsync(music);
        if (scripted != null)
        {
            _cache[key] = scripted;
            _logger.LogInformation("已从脚本源解析到歌词: {Title}", music.Title);
            return scripted;
        }

        // 2) 跨平台搜索歌词
        var searched = await SearchOtherSourcesAsync(music);
        if (searched != null)
        {
            _cache[key] = searched;
            _logger.LogInformation("已从其他音源 [{Source}] 搜索到歌词: {Title}", searched.SourceId, music.Title);
            return searched;
        }
        _missed.TryAdd(key, 0);
        _logger.LogInformation("未能在其他音源找到歌词: {Title}", music.Title);
        return null;
    }

    /// <summary>同平台歌词获取（失败静默）</summary>
    private async Task<Lyric?> FetchFromApiAsync(MusicInfo music)
    {
        try
        {
            var api = _musicSource.GetApi(music.Source);
            return api == null ? null : await api.GetLyricAsync(music);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "同平台歌词获取失败: {Source}:{Id}", music.Source, music.Id);
            return null;
        }
    }

    /// <summary>脚本源歌词：遍历已启用脚本源，调用其 lyric 动作并解析 LRC</summary>
    private async Task<LyricResult?> FetchFromScriptsAsync(MusicInfo music)
    {
        try
        {
            var raw = await _musicSource.GetLyricFromScriptsAsync(music);
            if (raw == null || string.IsNullOrWhiteSpace(raw.Lyric)) return null;
            var lyric = ParseLrc(raw);
            if (lyric == null || lyric.Lines.Count == 0) return null;
            return new LyricResult { Lyric = lyric, SourceId = "script", FromSearch = true };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "脚本源歌词获取失败: {Title}", music.Title);
            return null;
        }
    }

    /// <summary>解析脚本源返回的原始 LRC 文本（含翻译），无时间戳时整段作为无时间歌词</summary>
    private static Lyric? ParseLrc(LxLyricRaw raw)
    {
        var lines = new List<LyricLine>();
        var tMap = ParseTranslate(raw.Tlyric);
        foreach (var line in raw.Lyric.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^\[(\d{2}):(\d{2})(?:\.(\d+))?\](.*)");
            if (!m.Success) continue;
            var min = int.Parse(m.Groups[1].Value);
            var sec = int.Parse(m.Groups[2].Value);
            var ms = m.Groups[3].Success ? int.Parse(m.Groups[3].Value.PadRight(3, '0')[..3]) : 0;
            var idx = min * 60000 + sec * 1000 + ms;
            lines.Add(new LyricLine
            {
                Time = new TimeSpan(0, 0, min, sec, ms),
                Text = m.Groups[4].Value.Trim(),
                Translation = tMap.TryGetValue(idx, out var t) ? t : null
            });
        }
        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(raw.Lyric))
            lines.Add(new LyricLine { Time = TimeSpan.Zero, Text = raw.Lyric.Trim() });
        return lines.Count > 0 ? new Lyric { Lines = lines } : null;
    }

    private static Dictionary<int, string> ParseTranslate(string? tLrc)
    {
        var map = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(tLrc)) return map;
        foreach (var line in tLrc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^\[(\d{2}):(\d{2})(?:\.(\d+))?\](.*)");
            if (m.Success)
            {
                var idx = int.Parse(m.Groups[1].Value) * 60000 + int.Parse(m.Groups[2].Value) * 1000;
                map[idx] = m.Groups[4].Value.Trim();
            }
        }
        return map;
    }

    /// <summary>跨平台搜索：按 标题+歌手 检索并对每个源取歌词，标题/歌手相似度打分取最优</summary>
    private async Task<LyricResult?> SearchOtherSourcesAsync(MusicInfo music)
    {
        foreach (var sid in SearchOrder)
        {
            if (string.Equals(sid, music.Source, StringComparison.OrdinalIgnoreCase)) continue;
            var api = _musicSource.GetApi(sid);
            if (api == null) continue;
            try
            {
                var results = await _musicSource.SearchAsync(MusicSearchMatcher.BuildSearchKeyword(music), 1, 8, "song", sid);
                var best = MusicSearchMatcher.BestMatch(results.Items, music);
                if (best == null) continue;
                var lyric = await api.GetLyricAsync(best);
                if (lyric != null && lyric.Lines.Count > 0)
                    return new LyricResult { Lyric = lyric, SourceId = sid, FromSearch = true };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "跨平台歌词搜索失败: [{Source}] {Title}", sid, music.Title);
            }
        }
        return null;
    }

    // ================================================================
    //  当前播放歌词状态（供界面高亮/朗读）
    // ================================================================

    public LyricLine? GetCurrentLine(TimeSpan position)
    {
        if (_currentLyric == null || _currentLyric.Lines.Count == 0)
            return null;

        for (int i = 0; i < _currentLyric.Lines.Count; i++)
        {
            if (position >= _currentLyric.Lines[i].Time)
            {
                if (i != _currentIndex)
                {
                    var previousIndex = _currentIndex;
                    var previousLine = previousIndex >= 0 ? _currentLyric.Lines[previousIndex] : null;
                    _currentIndex = i;
                    LineChanged?.Invoke(this, new LyricLineChangedEventArgs
                    {
                        CurrentLine = _currentLyric.Lines[i],
                        PreviousLine = previousLine,
                        CurrentIndex = i,
                        PreviousIndex = previousIndex
                    });
                }
                return _currentLyric.Lines[i];
            }
        }
        return null;
    }

    public List<LyricLine> GetAllLines() => _currentLyric?.Lines ?? new List<LyricLine>();

    public void SetLyric(Lyric lyric)
    {
        _currentLyric = lyric;
        _currentIndex = -1;
        _logger.LogInformation("设置歌词，共 {Count} 行", lyric.Lines.Count);
    }

    public void Reset()
    {
        _currentLyric = null;
        _currentIndex = -1;
    }

    public Lyric? GetCurrentLyric() => _currentLyric;

    public int GetCurrentIndex() => _currentIndex;
}
