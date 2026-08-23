namespace LXMusicAccessible.Models;

/// <summary>
/// 播放模式
/// </summary>
public enum PlayMode
{
    Sequential,     // 顺序播放
    Loop,           // 列表循环
    Shuffle,        // 随机播放
    SingleLoop      // 单曲循环（仅自然结束时重播当前歌曲）
}
