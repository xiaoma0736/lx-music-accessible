using LXMusicAccessible.Models;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 平台个人歌单导入提供方：登录（粘贴Cookie）+ 拉取我的歌单 + 拉取歌单全部歌曲。
/// 安全要求：所有请求直连平台官方私有接口，凭据仅保存在本机（由外层服务 DPAPI 加密持久化），
/// 绝不上传任何第三方服务器。
/// </summary>
public interface IPlaylistImportProvider
{
    /// <summary>平台标识（netease/qq/kg/kw）</summary>
    string PlatformId { get; }

    /// <summary>平台显示名称</summary>
    string PlatformName { get; }

    /// <summary>用粘贴的 Cookie 登录并校验，成功返回账号信息（含需持久化的 AuthData）</summary>
    Task<AccountInfo?> LoginByCookieAsync(string cookieText);

    /// <summary>从持久化账号恢复会话（设置 Cookie/令牌、uid 等）</summary>
    Task LoadAccountAsync(AccountInfo account);

    /// <summary>退出登录（清空本机会话）</summary>
    Task LogoutAsync();

    /// <summary>获取我的歌单（我创建的/我收藏的/我喜欢的音乐）</summary>
    Task<List<RemotePlaylist>> GetMyPlaylistsAsync();

    /// <summary>获取歌单全部歌曲（内部自动分页）</summary>
    Task<List<MusicInfo>> GetPlaylistSongsAsync(RemotePlaylist playlist);
}
