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
    // ConcurrentDictionary 不允许 null 值；未命中单独记录并带 TTL，网络故障不能永久禁用该歌曲的歌词。
    private readonly ConcurrentDictionary<string, long> _missed = new();
    private static readonly long MissRetryIntervalMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
    public LyricService(ILogger<LyricService> logger, IMusicSourceService musicSource)
    {
        _logger = logger;
        _musicSource = musicSource;
    }

    /// <summary>获取歌词：同平台优先，缺失时自动跨平台搜索；null 表示确定无歌词（已缓存，避免重复请求）</summary>
    public async Task<LyricResult?> GetLyricAsync(
        MusicInfo music,
        CancellationToken cancellationToken = default)
    {
        if (music == null || string.IsNullOrEmpty(music.Source) ||
            string.Equals(music.Source, "Local", StringComparison.OrdinalIgnoreCase))
            return null;

        var key = $"{music.Source}:{music.Id}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        if (_missed.TryGetValue(key, out var retryAt))
        {
            if (FrameworkCompatibility.CurrentTickMs < retryAt) return null;
            _missed.TryRemove(key, out _);
        }

        _logger.LogInformation("获取歌词: {Title} - {Artist} [{Source}]", music.Title, music.Artist, music.Source);

        // 1) 同平台歌词
        var own = await FetchFromApiAsync(music, cancellationToken);
        if (own is { Lines.Count: > 0 })
        {
            var r = new LyricResult { Lyric = own, SourceId = music.Source, FromSearch = false };
            _cache[key] = r;
            return r;
        }

        // 1.5) 脚本源歌词（同一首歌，由第三方音源脚本的 lyric 动作提供）
        var scripted = await FetchFromScriptsAsync(music, cancellationToken);
        if (scripted != null)
        {
            _cache[key] = scripted;
            _logger.LogInformation("已从脚本源解析到歌词: {Title}", music.Title);
            return scripted;
        }

        // 2) 跨平台搜索歌词
        var searched = await SearchOtherSourcesAsync(music, cancellationToken);
        if (searched != null)
        {
            _cache[key] = searched;
            _logger.LogInformation("已从其他音源 [{Source}] 搜索到歌词: {Title}", searched.SourceId, music.Title);
            return searched;
        }
        _missed[key] = FrameworkCompatibility.CurrentTickMs + MissRetryIntervalMs;
        _logger.LogInformation("未能在其他音源找到歌词: {Title}", music.Title);
        return null;
    }

    /// <summary>同平台歌词获取（失败静默）</summary>
    private async Task<Lyric?> FetchFromApiAsync(
        MusicInfo music,
        CancellationToken cancellationToken)
    {
        try
        {
            var api = _musicSource.GetApi(music.Source);
            return api == null
                ? null
                : await api.GetLyricAsync(music).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "同平台歌词获取失败: {Source}:{Id}", music.Source, music.Id);
            return null;
        }
    }

    /// <summary>脚本源歌词：遍历已启用脚本源，调用其 lyric 动作并解析 LRC</summary>
    private async Task<LyricResult?> FetchFromScriptsAsync(
        MusicInfo music,
        CancellationToken cancellationToken)
    {
        try
        {
            var raw = await _musicSource.GetLyricFromScriptsAsync(music, cancellationToken);
            if (raw == null || string.IsNullOrWhiteSpace(raw.Lyric)) return null;
            var lyric = ParseLrc(raw);
            if (lyric == null || lyric.Lines.Count == 0) return null;
            return new LyricResult { Lyric = lyric, SourceId = "script", FromSearch = true };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "脚本源歌词获取失败: {Title}", music.Title);
            return null;
        }
    }

    /// <summary>解析脚本源返回的原始 LRC 文本（含翻译），无时间戳时整段作为无时间歌词</summary>
    internal static Lyric? ParseLrc(LxLyricRaw raw)
    {
        var lines = new List<LyricLine>();
        var tMap = ParseTranslate(raw.Tlyric);
        foreach (var line in raw.Lyric.SplitTrimmed('\n'))
        {
            var trimmed = line.Trim();
            var matches = System.Text.RegularExpressions.Regex.Matches(
                trimmed,
                @"\[(\d{1,3}):(\d{2})(?:[.:](\d+))?\]");
            if (matches.Count == 0) continue;
            var text = trimmed[(matches[^1].Index + matches[^1].Length)..].Trim();
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var time = ParseTimestamp(match);
                lines.Add(new LyricLine
                {
                    Time = time,
                    Text = text,
                    Translation = tMap.TryGetValue((int)time.TotalMilliseconds, out var translation)
                        ? translation
                        : null
                });
            }
        }
        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(raw.Lyric))
            lines.Add(new LyricLine { Time = TimeSpan.Zero, Text = raw.Lyric.Trim() });
        return lines.Count > 0
            ? new Lyric { Lines = lines.OrderBy(line => line.Time).ToList() }
            : null;
    }

    private static Dictionary<int, string> ParseTranslate(string? tLrc)
    {
        var map = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(tLrc)) return map;
        foreach (var line in tLrc.SplitTrimmed('\n'))
        {
            var trimmed = line.Trim();
            var matches = System.Text.RegularExpressions.Regex.Matches(
                trimmed,
                @"\[(\d{1,3}):(\d{2})(?:[.:](\d+))?\]");
            if (matches.Count > 0)
            {
                var text = trimmed[(matches[^1].Index + matches[^1].Length)..].Trim();
                foreach (System.Text.RegularExpressions.Match match in matches)
                    map[(int)ParseTimestamp(match).TotalMilliseconds] = text;
            }
        }
        return map;
    }

    private static TimeSpan ParseTimestamp(System.Text.RegularExpressions.Match match)
    {
        var minutes = int.Parse(match.Groups[1].Value);
        var seconds = int.Parse(match.Groups[2].Value);
        var fraction = match.Groups[3].Success
            ? match.Groups[3].Value.PadRight(3, '0')[..3]
            : "0";
        return TimeSpan.FromMilliseconds(
            minutes * 60000L + seconds * 1000L + int.Parse(fraction));
    }

    /// <summary>跨平台搜索：按 标题+歌手 检索并对每个源取歌词，标题/歌手相似度打分取最优</summary>
    private async Task<LyricResult?> SearchOtherSourcesAsync(
        MusicInfo music,
        CancellationToken cancellationToken)
    {
        foreach (var sid in SearchOrder)
        {
            if (string.Equals(sid, music.Source, StringComparison.OrdinalIgnoreCase)) continue;
            var api = _musicSource.GetApi(sid);
            if (api == null) continue;
            try
            {
                var results = await _musicSource
                    .SearchAsync(MusicSearchMatcher.BuildSearchKeyword(music), 1, 8, "song", sid, cancellationToken)
                    .WaitAsync(cancellationToken);
                var best = MusicSearchMatcher.BestMatch(results.Items, music);
                if (best == null) continue;
                var lyric = await api.GetLyricAsync(best).WaitAsync(cancellationToken);
                if (lyric != null && lyric.Lines.Count > 0)
                    return new LyricResult { Lyric = lyric, SourceId = sid, FromSearch = true };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "跨平台歌词搜索失败: [{Source}] {Title}", sid, music.Title);
            }
        }
        return null;
    }

}
