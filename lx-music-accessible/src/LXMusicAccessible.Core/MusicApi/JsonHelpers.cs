using System.Text.Json;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// JSON 容错读取辅助 —— 专辑搜索/详情等接口返回形状不稳定时统一使用。
/// </summary>
public static class JsonHelpers
{
    /// <summary>安全读取字符串字段（String/Number 均转字符串，缺失返回 null）</summary>
    public static string? GetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    /// <summary>取专辑歌手名：优先 artist.name / artist 字符串，其次 artists[0..].name</summary>
    public static string GetAlbumArtist(JsonElement alb)
    {
        if (alb.ValueKind != JsonValueKind.Object) return "";
        if (alb.TryGetProperty("artist", out var artist))
        {
            if (artist.ValueKind == JsonValueKind.String) return artist.GetString() ?? "";
            if (artist.ValueKind == JsonValueKind.Object)
            {
                var n = GetString(artist, "name");
                if (!string.IsNullOrEmpty(n)) return n!;
            }
        }
        if (alb.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            var names = artists.EnumerateArray()
                .Select(a => GetString(a, "name"))
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
            if (names.Count > 0) return string.Join("、", names);
        }
        return "";
    }

    /// <summary>规范化专辑名/歌手名：去首尾空白、忽略大小写</summary>
    public static string NormalizeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s!.Trim();
    }

    /// <summary>判断搜索结果歌曲是否属于指定专辑（规范化后相等或互相包含）</summary>
    public static bool MatchesAlbum(string? songAlbum, string? albumName)
    {
        var a = NormalizeName(songAlbum);
        var b = NormalizeName(albumName);
        if (a.Length == 0 || b.Length == 0) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || a.Contains(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>构造专辑搜索行（读屏朗读“专辑名（专辑），歌手”）</summary>
    public static MusicInfo BuildAlbumRow(string id, string name, string artist, string source, string coverUrl)
    {
        return new MusicInfo
        {
            Id = id ?? "",
            Title = string.IsNullOrEmpty(name) ? "未知专辑" : name,
            Album = name ?? "",
            Artist = artist ?? "",
            Source = source,
            CoverUrl = coverUrl ?? "",
            IsAlbum = true
        };
    }
}
