using System;

namespace LXMusicAccessible.Models;

/// <summary>
/// 平台账号登录态（凭据仅本地、DPAPI 加密存储，绝不上传第三方）
/// </summary>
public class AccountInfo
{
    /// <summary>平台标识（netease/qq/kg/kw）</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>平台用户 id</summary>
    public string Uid { get; set; } = string.Empty;

    /// <summary>平台昵称</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>明文登录凭据 JSON（仅内存中存在，例如网易云 MUSIC_U Cookie）</summary>
    public string? AuthData { get; set; }

    /// <summary>DPAPI 加密后的凭据（持久化字段，读取时解密到 AuthData）</summary>
    public string? EncryptedAuth { get; set; }

    public DateTime LoginTime { get; set; } = DateTime.Now;

    public DateTime ExpireTime { get; set; } = DateTime.Now.AddDays(7);

    /// <summary>登录是否已过期</summary>
    public bool IsExpired => DateTime.Now >= ExpireTime;

    /// <summary>读屏播报名称</summary>
    public override string ToString()
    {
        var name = string.IsNullOrEmpty(DisplayName) ? Platform : DisplayName;
        return IsExpired ? $"{name}（登录已过期）" : $"{name}（已登录）";
    }
}

/// <summary>
/// 远程个人歌单（我创建的 / 我收藏的 / 我喜欢的音乐）
/// </summary>
public class RemotePlaylist
{
    public string Platform { get; set; } = string.Empty;
    public string RemoteId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int TrackCount { get; set; }
    public bool IsLiked { get; set; }
    public bool IsCollected { get; set; }
    public string? CoverUrl { get; set; }

    /// <summary>读屏单次朗读的列表项名称</summary>
    public string AccessibleName
    {
        get
        {
            var suffix = IsLiked ? "（我喜欢的音乐）" : IsCollected ? "（我收藏的歌单）" : "（我创建的歌单）";
            return $"{Name}{suffix}，共 {TrackCount} 首";
        }
    }

    public override string ToString() => AccessibleName;
}

/// <summary>
/// 个人歌单导入结果
/// </summary>
public class PlaylistImportResult
{
    public string PlaylistId { get; set; } = string.Empty;
    public string PlaylistName { get; set; } = string.Empty;
    public int ImportedCount { get; set; }
    public int SkippedCount { get; set; }

    public override string ToString() =>
        $"歌单“{PlaylistName}”导入完成，新增 {ImportedCount} 首，跳过重复 {SkippedCount} 首";
}