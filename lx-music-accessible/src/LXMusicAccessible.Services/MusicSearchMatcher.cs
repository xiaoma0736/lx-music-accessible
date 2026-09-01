using System.Text.RegularExpressions;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Services;

/// <summary>
/// 跨平台同名歌曲匹配辅助 — 供歌词跨源搜索与播放失败自动换源共用。
/// 移植原版 lx-music-desktop 的 findMusic 严格匹配：规范化标题/歌手/专辑、
/// 多歌手排序比较、时长 ±5s、多轮严格度排序，确保“换源只换同一首”。
/// </summary>
public static class MusicSearchMatcher
{
    private static readonly Regex SingerSplitRxp = new(@"、|&|;|；|/|,|，|\|");
    private static readonly Regex FilterRxp = new(@"[\s'\.\,，\&\""\、\(\)（\）\\~\-<>\|\/\]\[!\！]");

    /// <summary>取严格匹配后排序最靠前的一个候选（可能与目标为同一首；无匹配返回 null）</summary>
    public static MusicInfo? BestMatch(IEnumerable<MusicInfo> items, MusicInfo target)
        => FindBestMatches(items, target).FirstOrDefault();

    /// <summary>严格匹配候选列表：仅保留“同一首”的歌曲，并按原版多轮规则排序（最严格在前）</summary>
    public static List<MusicInfo> FindBestMatches(IEnumerable<MusicInfo> items, MusicInfo target)
    {
        var result = new List<MusicInfo>();
        if (items == null || target == null) return result;

        var fMusicName = FilterStr(target.Title).ToLowerInvariant();
        var fSinger = FilterStr(SortSingle(target.Artist)).ToLowerInvariant();
        var fAlbumName = FilterStr(target.Album).ToLowerInvariant();
        var fInterval = GetIntv(target.Duration);
        if (fMusicName.Length == 0) return result;

        bool IsEqualsInterval(int intv)
        {
            if (fInterval == 0 || intv == 0) return true; // 有一方无时长时不强制
            return Math.Abs(fInterval - intv) < 5;
        }
        bool IsIncludesName(string name) => fMusicName.Length > 0 && (fMusicName.Contains(name) || name.Contains(fMusicName));
        bool IsIncludesSinger(string singer) => fSinger.Length == 0 || fSinger.Contains(singer) || singer.Contains(fSinger);
        bool IsEqualsAlbum(string album) => fAlbumName.Length == 0 || fAlbumName == album;

        // 候选：先按时长过滤（±5s），再收集规范化字段
        var candidates = new List<MatchCandidate>();
        foreach (var item in items)
        {
            if (item == null) continue;
            var cand = new MatchCandidate
            {
                Item = item,
                Name = FilterStr(item.Title).ToLowerInvariant(),
                Singer = FilterStr(SortSingle(item.Artist)).ToLowerInvariant(),
                Album = FilterStr(item.Album).ToLowerInvariant(),
                Interval = GetIntv(item.Duration)
            };
            if (!IsEqualsInterval(cand.Interval)) continue;
            candidates.Add(cand);
        }

        // 三轮宽松度递增的初选
        var matched = new List<MatchCandidate>();
        AddUnique(matched, candidates, c => c.Name == fMusicName && IsIncludesSinger(c.Singer));
        AddUnique(matched, candidates, c => c.Singer == fSinger && IsIncludesName(c.Name));
        AddUnique(matched, candidates, c => IsEqualsAlbum(c.Album) && IsIncludesSinger(c.Singer) && IsIncludesName(c.Name));

        // 严格度排序（最严格在前）
        var ordered = new List<MatchCandidate>();
        AddRanked(ordered, matched, c => c.Singer == fSinger && c.Name == fMusicName && c.Interval == fInterval);
        AddRanked(ordered, matched, c => c.Name == fMusicName && c.Singer == fSinger && c.Album == fAlbumName);
        AddRanked(ordered, matched, c => c.Singer == fSinger && c.Name == fMusicName);
        AddRanked(ordered, matched, c => c.Name == fMusicName && c.Interval == fInterval);
        AddRanked(ordered, matched, c => c.Singer == fSinger && c.Interval == fInterval);
        AddRanked(ordered, matched, c => c.Interval == fInterval);
        AddRanked(ordered, matched, c => c.Name == fMusicName);
        AddRanked(ordered, matched, c => c.Singer == fSinger);
        AddRanked(ordered, matched, c => c.Album == fAlbumName);
        ordered.AddRange(matched); // 其余按原顺序兜底

        foreach (var c in ordered)
        {
            if (result.Any(x => string.Equals(x.Source, c.Item.Source, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(x.Id, c.Item.Id, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(c.Item);
        }
        return result;
    }

    /// <summary>Strictly determines whether two entries are the same song.</summary>
    public static bool IsSameSong(MusicInfo item, MusicInfo target)
    {
        if (item is null || target is null) return false;

        var targetTitle = NormalizeTitle(target.Title);
        var candidateTitle = NormalizeTitle(item.Title);
        if (targetTitle.Length == 0
            || candidateTitle.Length == 0
            || !string.Equals(candidateTitle, targetTitle, StringComparison.Ordinal))
        {
            return false;
        }

        var targetArtists = SplitArtists(target.Artist);
        var candidateArtists = SplitArtists(item.Artist);
        if (targetArtists.Count == 0
            || candidateArtists.Count == 0
            || !candidateArtists.SetEquals(targetArtists))
        {
            return false;
        }

        var targetDuration = GetIntv(target.Duration);
        var candidateDuration = GetIntv(item.Duration);
        if (targetDuration <= 0
            || candidateDuration <= 0
            || Math.Abs(targetDuration - candidateDuration) > 5)
        {
            return false;
        }

        var targetAlbum = NormalizeTitle(target.Album);
        var candidateAlbum = NormalizeTitle(item.Album);
        return targetAlbum.Length == 0
            || candidateAlbum.Length == 0
            || string.Equals(candidateAlbum, targetAlbum, StringComparison.Ordinal);
    }

    /// <summary>规范化：去首尾空白与常见括号后缀（如“晴天(深情版)” → “晴天”）</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Trim();
        var idx = t.IndexOf('(');
        if (idx > 0) t = t[..idx];
        idx = t.IndexOf('（');
        if (idx > 0) t = t[..idx];
        return t.Trim();
    }

    /// <summary>组装跨平台搜索关键字：优先“标题 歌手”，歌手缺失时用标题</summary>
    public static string BuildSearchKeyword(MusicInfo music)
    {
        if (music == null) return "";
        return string.IsNullOrWhiteSpace(music.Artist) ? music.Title : $"{music.Title} {music.Artist}";
    }

    /// <summary>去空白与常见标点（对齐原版 filterStr），用于严格比较</summary>
    private static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return FilterStr(value).ToLowerInvariant();
    }

    private static HashSet<string> SplitArtists(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(
            SingerSplitRxp.Split(value)
                .Select(NormalizeTitle)
                .Where(artist => artist.Length > 0),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string FilterStr(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return FilterRxp.Replace(s, "").Trim();
    }

    /// <summary>多歌手按字符排序后以顿号连接（如“张三/李四”→“李四、张三”），用于集合相等比较</summary>
    private static string SortSingle(string? singer)
    {
        if (string.IsNullOrEmpty(singer)) return "";
        if (SingerSplitRxp.IsMatch(singer))
            return string.Join("、", SingerSplitRxp.Split(singer)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return singer;
    }

    /// <summary>时长字符串（mm:ss 或 mm:ss.xx）→ 秒数；无法解析返回 0</summary>
    private static int GetIntv(string? interval)
    {
        if (string.IsNullOrWhiteSpace(interval)) return 0;
        var parts = interval.Split(':');
        int intv = 0, unit = 1;
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            var clean = parts[i].Split('.')[0];
            if (!int.TryParse(clean, out var v)) return 0;
            intv += v * unit;
            unit *= 60;
        }
        return intv;
    }

    private static void AddUnique(List<MatchCandidate> dest, List<MatchCandidate> source, Func<MatchCandidate, bool> pred)
    {
        foreach (var c in source)
            if (pred(c) && !dest.Contains(c))
                dest.Add(c);
    }

    private static void AddRanked(List<MatchCandidate> dest, List<MatchCandidate> pool, Func<MatchCandidate, bool> pred)
    {
        for (int i = pool.Count - 1; i >= 0; i--)
        {
            if (pred(pool[i]))
            {
                dest.Add(pool[i]);
                pool.RemoveAt(i);
            }
        }
    }

    private sealed class MatchCandidate
    {
        public MusicInfo Item = new();
        public string Name = "";
        public string Singer = "";
        public string Album = "";
        public int Interval;
    }
}