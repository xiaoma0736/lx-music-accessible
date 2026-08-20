using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Services;

/// <summary>
/// 个人歌单导入服务：编排各平台 Provider + 本地账号存储 + 歌单落库。
/// 安全设计：登录凭据仅在本机生成/粘贴，DPAPI 加密存入本地数据库，绝不发送到任何第三方服务器。
/// </summary>
public interface IPlaylistImportService
{
    /// <summary>所有已注册的平台 Provider</summary>
    IReadOnlyList<IPlaylistImportProvider> Providers { get; }

    /// <summary>按平台 id 获取 Provider（netease/qq/kg/kw）</summary>
    IPlaylistImportProvider? GetProvider(string platformId);

    /// <summary>确保已从本地数据库恢复已保存的登录态（打开导入窗口前调用一次）</summary>
    Task EnsureAccountsLoadedAsync();

    /// <summary>当前已保存的账号（含解密后的 AuthData；未登录返回 null）</summary>
    AccountInfo? GetAccount(string platformId);

    /// <summary>用粘贴的 Cookie 登录并持久化；失败返回 null</summary>
    Task<AccountInfo?> LoginWithCookieAsync(string platformId, string cookieText);

    /// <summary>用 Cookie + 浏览器页面内拉取结果登录并持久化（酷我专用：反爬只能在页面内通过）；失败返回 null</summary>
    Task<AccountInfo?> LoginWithBrowserResultAsync(string platformId, string cookieText, string? displayName, List<RemotePlaylist>? playlists);

    /// <summary>退出登录（删除本地账号并清空会话）</summary>
    Task LogoutAsync(string platformId);

    /// <summary>获取我的歌单（需已登录；未登录抛异常）</summary>
    Task<List<RemotePlaylist>> GetMyPlaylistsAsync(string platformId);

    /// <summary>获取远程歌单的全部歌曲（仅预览，不落库；供导入窗口回车查看歌曲列表）</summary>
    Task<List<MusicInfo>> GetPlaylistSongsPreviewAsync(string platformId, RemotePlaylist remote);

    /// <summary>把指定远程歌单导入为本地快照歌单（已存在同名同源快照则覆盖更新），返回导入结果</summary>
    Task<PlaylistImportResult> ImportPlaylistAsync(string platformId, RemotePlaylist remote, Action<string>? announce = null);
}

/// <summary>
/// 个人歌单导入服务实现
/// </summary>
public class PlaylistImportService : IPlaylistImportService
{
    private readonly IEnumerable<IPlaylistImportProvider> _providers;
    private readonly IDataService _dataService;
    private readonly ILogger<PlaylistImportService> _logger;
    private readonly Dictionary<string, AccountInfo> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _restoreLock = new();
    private bool _accountsRestored;

    public PlaylistImportService(
        IEnumerable<IPlaylistImportProvider> providers,
        IDataService dataService,
        ILogger<PlaylistImportService> logger)
    {
        _providers = providers;
        _dataService = dataService;
        _logger = logger;
    }

    public IReadOnlyList<IPlaylistImportProvider> Providers => _providers.ToList();

    public IPlaylistImportProvider? GetProvider(string platformId) =>
        _providers.FirstOrDefault(p => string.Equals(p.PlatformId, platformId, StringComparison.OrdinalIgnoreCase));

    public AccountInfo? GetAccount(string platformId) =>
        _accounts.TryGetValue(platformId ?? string.Empty, out var a) ? a : null;

    public async Task EnsureAccountsLoadedAsync()
    {
        if (_accountsRestored) return;
        lock (_restoreLock)
        {
            if (_accountsRestored) return;
            _accountsRestored = true;
        }
        foreach (var p in _providers)
        {
            try
            {
                var acc = await _dataService.GetAccountAsync(p.PlatformId);
                if (acc != null)
                {
                    _accounts[p.PlatformId] = acc;
                    await p.LoadAccountAsync(acc);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复账号 {Platform} 失败", p.PlatformId);
            }
        }
    }
    public async Task<AccountInfo?> LoginWithCookieAsync(string platformId, string cookieText)
    {
        var p = GetProvider(platformId) ?? throw new InvalidOperationException("不支持的平台");
        var acc = await p.LoginByCookieAsync(cookieText);
        if (acc != null)
        {
            await _dataService.SaveAccountAsync(acc);
            _accounts[platformId] = acc;
        }
        return acc;
    }

    /// <summary>用 Cookie + 浏览器页面内拉取结果登录并持久化（酷我专用）。</summary>
    public async Task<AccountInfo?> LoginWithBrowserResultAsync(string platformId, string cookieText, string? displayName, List<RemotePlaylist>? playlists)
    {
        var p = GetProvider(platformId) ?? throw new InvalidOperationException("不支持的平台");

        // 酷我反爬只能在页面内通过：把网页登录窗口拉取到的歌单/昵称先交给 Provider 缓存
        if (p is KuwoPlaylistProvider kw)
        {
            kw.SetBrowserLoginResult(displayName, playlists);
        }

        var acc = await p.LoginByCookieAsync(cookieText);
        if (acc != null)
        {
            if (!string.IsNullOrEmpty(displayName)) acc.DisplayName = displayName;
            await _dataService.SaveAccountAsync(acc);
            _accounts[platformId] = acc;
        }
        return acc;
    }

    public async Task LogoutAsync(string platformId)
    {
        var p = GetProvider(platformId);
        if (p != null) await p.LogoutAsync();
        await _dataService.DeleteAccountAsync(platformId);
        _accounts.Remove(platformId);
    }

    public async Task<List<RemotePlaylist>> GetMyPlaylistsAsync(string platformId)
    {
        var p = GetProvider(platformId) ?? throw new InvalidOperationException("不支持的平台");
        return await p.GetMyPlaylistsAsync();
    }

    public async Task<List<MusicInfo>> GetPlaylistSongsPreviewAsync(string platformId, RemotePlaylist remote)
    {
        if (remote == null) throw new ArgumentNullException(nameof(remote));
        var p = GetProvider(platformId) ?? throw new InvalidOperationException("不支持的平台");
        return await p.GetPlaylistSongsAsync(remote);
    }

    public async Task<PlaylistImportResult> ImportPlaylistAsync(string platformId, RemotePlaylist remote, Action<string>? announce = null)
    {
        if (remote == null) throw new ArgumentNullException(nameof(remote));
        var p = GetProvider(platformId) ?? throw new InvalidOperationException("不支持的平台");

        announce?.Invoke($"正在获取歌单《{remote.Name}》的歌曲，请稍候...");
        var songs = await p.GetPlaylistSongsAsync(remote);

        // 已存在同名同源同远端 id 的快照歌单则覆盖更新，否则新建
        var remoteKey = $"{platformId}_{remote.RemoteId}";
        var existing = (await _dataService.GetPlaylistsAsync())
            .FirstOrDefault(pl => pl.IsSnapshot
                && string.Equals(pl.Source, platformId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pl.RemoteId, remoteKey, StringComparison.OrdinalIgnoreCase));

        Playlist playlist;
        if (existing != null)
        {
            playlist = existing;
            await _dataService.ClearPlaylistItemsAsync(playlist.Id);
            playlist.Name = remote.Name;
            playlist.ModifiedDate = DateTime.Now;
            await _dataService.SavePlaylistAsync(playlist);
        }
        else
        {
            playlist = new Playlist
            {
                Id = Guid.NewGuid().ToString(),
                Name = remote.Name,
                Description = $"{p.PlatformName}导入的个人歌单",
                Type = PlaylistType.User,
                IsLocal = false,
                Source = platformId,
                RemoteId = remoteKey,
                IsSnapshot = true,
                CreatedDate = DateTime.Now,
                ModifiedDate = DateTime.Now
            };
            await _dataService.SavePlaylistAsync(playlist);
        }

        await _dataService.AddSongsToPlaylistAsync(playlist.Id, songs);

        return new PlaylistImportResult
        {
            PlaylistId = playlist.Id,
            PlaylistName = playlist.Name,
            ImportedCount = songs.Count,
            SkippedCount = 0
        };
    }
}
