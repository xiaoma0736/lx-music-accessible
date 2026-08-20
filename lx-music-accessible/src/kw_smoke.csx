using Microsoft.Extensions.DependencyInjection;
using LXMusicAccessible.Services;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;

// resolve services from the test console's host builder is complex; do a direct provider test
var loggerFactory = LoggerFactory.Create(b => { });
var http = new MusicHttpClient(loggerFactory.CreateLogger<MusicHttpClient>());
var kw = new KuwoPlaylistProvider(http, loggerFactory.CreateLogger<KuwoPlaylistProvider>());

// 1) songs fetch via nplserver (public)
try {
    var songs = await kw.GetPlaylistSongsAsync(new RemotePlaylist { RemoteId = "3601256457", Name = "smoke" });
    Console.WriteLine($"[kw] 歌单歌曲数={songs.Count} 首曲={songs.FirstOrDefault()?.Title}/{songs.FirstOrDefault()?.Artist}");
} catch (Exception ex) { Console.WriteLine($"[kw] 歌曲获取异常: {ex.Message}"); }

// 2) login rejection: no userid/websid -> null
try {
    var bad = await kw.LoginByCookieAsync("foo=bar; no_token=1");
    Console.WriteLine($"[kw] 无效Cookie结果={(bad == null ? "拒绝(预期)" : "意外成功")}");
} catch (Exception ex) { Console.WriteLine($"[kw] 无效Cookie异常: {ex.Message}"); }

// 3) login rejection: userid but no websid -> null
try {
    var bad2 = await kw.LoginByCookieAsync("userid=641794944; foo=1");
    Console.WriteLine($"[kw] 只有userid结果={(bad2 == null ? "拒绝(预期)" : "意外成功")}");
} catch (Exception ex) { Console.WriteLine($"[kw] 异常: {ex.Message}"); }

// 4) valid login (userid+websid) -> account, then GetMyPlaylists throws clear error (no browser data)
try {
    kw.SetBrowserLoginResult("酷我测试", new List<RemotePlaylist> { new RemotePlaylist { Platform="kw", RemoteId="123", Name="测试歌单", TrackCount=5 } });
    var acc = await kw.LoginByCookieAsync("userid=641794944; websid=122194817; username=13298607960");
    Console.WriteLine($"[kw] 有效Cookie登录={(acc == null ? "失败" : "成功: " + acc.DisplayName)}  Uid={acc?.Uid}");
    if (acc != null) {
        var pls = await kw.GetMyPlaylistsAsync();
        Console.WriteLine($"[kw] 浏览器缓存歌单数={pls.Count} 首={pls.FirstOrDefault()?.Name}");
        // LoadAccountAsync restore check
        var kw2 = new KuwoPlaylistProvider(new MusicHttpClient(loggerFactory.CreateLogger<MusicHttpClient>()), loggerFactory.CreateLogger<KuwoPlaylistProvider>());
        await kw2.LoadAccountAsync(acc);
        var restored = await kw2.GetMyPlaylistsAsync();
        Console.WriteLine($"[kw] 恢复后歌单数={restored.Count} 首={restored.FirstOrDefault()?.Name}");
    }
} catch (Exception ex) { Console.WriteLine($"[kw] 有效登录异常: {ex.Message}"); }

// 5) GetMyPlaylists without browser data -> clear error
try {
    var kw3 = new KuwoPlaylistProvider(new MusicHttpClient(loggerFactory.CreateLogger<MusicHttpClient>()), loggerFactory.CreateLogger<KuwoPlaylistProvider>());
    await kw3.LoginByCookieAsync("userid=641794944; websid=122194817");
    var _ = await kw3.GetMyPlaylistsAsync();
    Console.WriteLine("[kw] 无浏览器数据时意外返回");
} catch (Exception ex) { Console.WriteLine($"[kw] 无浏览器数据提示(预期): {ex.Message.Substring(0, 40)}..."); }
