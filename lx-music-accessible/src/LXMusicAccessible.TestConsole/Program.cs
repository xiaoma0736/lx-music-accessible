using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using LXMusicAccessible.Services;
using LXMusicAccessible.Models;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Audio;

namespace LXMusicAccessible.TestConsole;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== LX Music 音源模块全链路验证 v3 ===\n");

        using var host = Host.CreateDefaultBuilder(args)
            .UseDefaultServiceProvider((_, options) =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            })
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                services.AddSingleton<string>("testdata"); // DataService 需要的 dataDir
                services.AddSingleton<MusicHttpClient>();
                services.AddSingleton<ScriptMusicSource>();
                services.AddSingleton<LxScriptEngine>();
                services.AddSingleton<NeteaseMusicService>();
                services.AddSingleton<QqMusicService>();
                services.AddSingleton<KugouMusicService>();
                services.AddSingleton<KuwoMusicService>();
                services.AddSingleton<MiguMusicService>();
                services.AddSingleton<IPlaylistImportProvider, NeteasePlaylistProvider>();
                services.AddSingleton<IPlaylistImportProvider, QqPlaylistProvider>();
                services.AddSingleton<IPlaylistImportProvider, KugouPlaylistProvider>();
                services.AddSingleton<IPlaylistImportProvider, KuwoPlaylistProvider>();
                services.AddSingleton<IPlaylistImportService, PlaylistImportService>();
                services.AddSingleton<IDataService, DataService>();
                services.AddSingleton<IMusicSourceService, MusicSourceService>();
                services.AddSingleton<ILyricService, LyricService>();
            })
            .Build();
        await host.StartAsync();
        var sp = host.Services;
        var src = (MusicSourceService)sp.GetRequiredService<IMusicSourceService>();
        var scriptSource = sp.GetRequiredService<ScriptMusicSource>();
        int pass = 0, fail = 0;

        void Check(bool ok, string name)
        {
            Console.WriteLine($"  {(ok ? "✅" : "❌")} {name}");
            if (ok) pass++; else fail++;
        }

        // ====== 1. 单源搜索 ======
        Console.WriteLine("--- 1. 单源搜索 (晴天, 每源3条) ---");
        foreach (var sid in new[] { "all", "netease", "qq", "kg", "kw", "migu" })
        {
            var r = await src.SearchAsync("晴天", 1, 3, "song", sid);
            Console.WriteLine($"  [{sid}] total={r.TotalItems} first={r.Items.FirstOrDefault()?.Title}");
            Check(r.TotalItems > 0, $"[{sid}] 搜索有结果");
        }

        // ====== 2. 脚本源加载 ======
        Console.WriteLine("--- 2. 脚本源加载 ---");
        await src.InitializeScriptSourcesAsync();
        var scripts = scriptSource.GetAllScripts();
        foreach (var s in scripts)
            Console.WriteLine($"  [{s.Id}] {(s.IsLocal ? "本地" : "在线")} {s.Name} v{s.Version} API={s.ApiUrl} 源=[{string.Join(",", s.SupportedSources)}]");
        Check(scripts.Count > 0, $"脚本源加载 {scripts.Count} 个");

        // ====== 3. 播放URL（脚本源优先） ======
        // ====== 2b. 脚本信任状态持久化验证 ======
        try
        {
            var dbSrc = sp.GetRequiredService<IDataService>();
            var after = await dbSrc.GetMusicSourcesAsync();
            Console.WriteLine($"  [持久化验证] 加载后音源数={after.Count} 未信任数={after.Count(s => s.TrustState == ScriptTrustState.Untrusted)} 内存验证脚本数={scriptSource.GetAllScripts().Count}");
        }
        catch (Exception dex) { Console.WriteLine($"  [去重验证] 异常: {dex.Message}"); }

        Console.WriteLine("--- 3. 播放URL（脚本源优先，每源取一首） ---");
        var sample = await src.SearchAsync("晴天", 1, 5);
        foreach (var sid in new[] { "netease", "qq", "kg", "kw", "migu" })
        {
            var m = sample.Items.FirstOrDefault(x => x.Source == sid) ?? sample.Items.FirstOrDefault();
            if (m == null) { Check(false, $"[{sid}] 无样本"); continue; }
            var url = await src.GetPlayUrlAsync(m, m.Source);
            Console.WriteLine($"  [{sid}] {m.Artist} - {m.Title} -> {(string.IsNullOrEmpty(url) ? "NULL" : url[..Math.Min(60, url.Length)] + "...")}");
            Check(!string.IsNullOrEmpty(url), $"[{sid}] 播放URL");
        }

        // ====== 4. 分类歌单全链路（每源） ======
        Console.WriteLine("--- 4. 分类歌单全链路（分类→歌单→详情） ---");
        foreach (var sid in new[] { "netease", "qq", "kg", "kw", "migu" })
        {
            try
            {
                var cats = await src.GetPlaylistCategoriesAsync(sid);
                Console.WriteLine($"  [{sid}] 分类数={cats.Count}");
                foreach (var c in cats.Take(3))
                {
                    var tagNames = string.Join(",", c.Tags.Take(3).Select(t => $"{t.Name}({t.Id})"));
                    Console.WriteLine($"     分类={c.Name} id={c.Id} 标签=[{tagNames}]");
                }
                if (cats.Count == 0) { Check(false, $"[{sid}] 分类"); continue; }
                bool badName = cats.Any(c => string.IsNullOrEmpty(c.Name) || c.Name.Contains("LXMusicAccessible") || c.Name.Contains("Models.Playlist"));
                Check(!badName, $"[{sid}] 分类名无类型名乱码");

                // 全部歌单入口（各源通用 "all"/"全部"）
                var allTag = cats.FirstOrDefault(c => c.Id == "all")?.Tags.FirstOrDefault()?.Id ?? "all";
                var allPls = await src.GetPlaylistsByCategoryAsync(allTag, 1, 3, sid);
                Console.WriteLine($"  [{sid}] 全部歌单数={allPls.TotalItems} 首个={allPls.Items.FirstOrDefault()?.Name}");
                Check(allPls.Items.Count > 0, $"[{sid}] 全部歌单列表");

                // 首个分类自动取第一个标签
                var tagId = cats.First().Tags.FirstOrDefault()?.Id ?? "all";
                var pls = await src.GetPlaylistsByCategoryAsync(tagId, 1, 3, sid);
                Console.WriteLine($"  [{sid}] 分类[{cats.First().Name}] 歌单数={pls.TotalItems} 首个={pls.Items.FirstOrDefault()?.Name} id={pls.Items.FirstOrDefault()?.Id}");
                if (pls.Items.Count == 0) { Check(false, $"[{sid}] 歌单列表"); continue; }
                var detail = await src.GetPlaylistDetailAsync(pls.Items[0].Id, 1, 3);
                var firstSong = detail.Items.FirstOrDefault();
                Console.WriteLine($"  [{sid}] 详情歌曲数={detail.TotalItems} 首曲={firstSong?.Title} id={firstSong?.Id} [{firstSong?.Source}]");
                bool songOk = detail.TotalItems > 0 && firstSong != null && !string.IsNullOrEmpty(firstSong.Id) && !string.IsNullOrEmpty(firstSong.Title);
                Check(songOk, $"[{sid}] 歌单详情(有歌曲且Id非空)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{sid}] 异常: {ex.Message}");
                Check(false, $"[{sid}] 分类链路");
            }
        }

        // ====== 5. 排行榜全链路（每源） ======
        Console.WriteLine("--- 5. 排行榜全链路（列表→详情） ---");
        foreach (var sid in new[] { "netease", "qq", "kg", "kw", "migu" })
        {
            try
            {
                var rks = await src.GetRankingsAsync(sid);
                Console.WriteLine($"  [{sid}] 榜单数={rks.Count} 首个={rks.FirstOrDefault()?.Name} id={rks.FirstOrDefault()?.Id}");
                if (rks.Count == 0) { Check(false, $"[{sid}] 榜单列表"); continue; }
                var det = await src.GetRankingDetailAsync(rks[0].Id, 1, 3);
                var fs = det.Items.FirstOrDefault();
                Console.WriteLine($"  [{sid}] 详情歌曲数={det.TotalItems} 首曲={fs?.Title} id={fs?.Id} [{fs?.Source}]");
                bool rkOk = det.TotalItems > 0 && fs != null && !string.IsNullOrEmpty(fs.Id) && !string.IsNullOrEmpty(fs.Title);
                Check(rkOk, $"[{sid}] 榜单详情(有歌曲且Id非空)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{sid}] 异常: {ex.Message}");
                Check(false, $"[{sid}] 榜单链路");
            }
        }

        // ====== 6. QQ 内置播放URL（修复验证） ======
        Console.WriteLine("--- 6. QQ 内置播放URL端点修复验证 ---");
        var qq = sp.GetRequiredService<QqMusicService>();
        var qqUrl = await qq.GetPlayUrlAsync("0039MnYb0qxYhV");
        Console.WriteLine($"  QQ 内置 -> {(string.IsNullOrEmpty(qqUrl) ? "NULL（可能被风控，依赖脚本源回退）" : qqUrl[..Math.Min(60, qqUrl.Length)] + "...")}");

        // ====== 7. 歌词取回 ======
        Console.WriteLine("--- 7. 歌词取回（网易云） ---");
        var lyricSong = (await src.SearchAsync("晴天", 1, 1, "song", "netease")).Items.FirstOrDefault();
        if (lyricSong != null)
        {
            var lyric = await src.GetApi("netease")!.GetLyricAsync(lyricSong);
            Console.WriteLine($"  [{lyricSong.Title}] id={lyricSong.Id} 歌词行数={(lyric?.Lines.Count ?? 0)}");
            Check(lyric is { Lines.Count: > 0 }, "歌词取回");
            try
            {
                var rawLy = await sp.GetRequiredService<MusicHttpClient>().GetAsync(
                    $"https://music.163.com/api/song/lyric?id={lyricSong.Id}&lv=-1&kv=-1&tv=-1", null,
                    new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" });
                Console.WriteLine($"  公开接口原始返回长度={rawLy.Length} 前120={rawLy[..Math.Min(120, rawLy.Length)]}");
            }
            catch (Exception rawEx) { Console.WriteLine($"  公开接口请求异常: {rawEx.Message}"); }
        }

        // ====== 8. Prism 语音朗读冒烟测试 ======
        Console.WriteLine("--- 8. Prism 语音朗读冒烟测试 ---");
        try
        {
            var prism = new PrismSpeechService(sp.GetRequiredService<ILogger<PrismSpeechService>>());
            Console.WriteLine($"  IsAvailable={prism.IsAvailable} 后端={prism.BackendName}");
            var voices = prism.GetAvailableVoices();
            Console.WriteLine($"  语音数={voices.Count} " + string.Join(" | ", voices.Take(5).Select(v => $"{v.Name} ({v.Culture})")));
            prism.SetRate(1.0);
            await prism.SpeakAsync("Prism 语音朗读测试成功");
            Check(prism.IsAvailable, "Prism 服务可用");
            prism.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Prism 测试异常: {ex.Message}");
            Check(false, "Prism 服务可用");
        }

        // ====== 9. 免费源脚本播放（正则 + Jint 双路径，tx/wy 各取一首） ======
        Console.WriteLine("--- 9. 免费源脚本播放（regex + jint，tx/wy） ---");
        try
        {
            var scriptEngine = sp.GetRequiredService<LxScriptEngine>();
            var qqSong = (await src.SearchAsync("晴天", 1, 5, "song", "qq")).Items.FirstOrDefault();
            var wySong = (await src.SearchAsync("晴天", 1, 5, "song", "netease")).Items.FirstOrDefault();
            foreach (var s in scriptSource.GetAllScripts())
            {
                var results = new List<string>();
                foreach (var (label, m) in new[] { ("tx", qqSong), ("wy", wySong) })
                {
                    if (m == null) continue;
                    var regexUrl = await scriptSource.GetPlayUrlAsync(s.Id, m.Id, m.Source, "128k");
                    var jintUrl = await scriptEngine.GetPlayUrlAsync(s, m, m.Source, "128k");
                    results.Add($"{label}(regex={(string.IsNullOrEmpty(regexUrl) ? "✗" : "✓")},jint={(string.IsNullOrEmpty(jintUrl) ? "✗" : "✓")})");
                }
                Console.WriteLine($"  [{s.Id}] {s.Name} -> {string.Join(" ", results)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  脚本播放测试异常: {ex.Message}");
            Check(false, "免费源脚本播放");
        }

        // ====== 10. 本地导入付费脚本（聆澜） ======
        Console.WriteLine("--- 10. 本地导入付费脚本（聆澜） ---");
        var paidPath = @"D:\000\音源接口\付费\lx-music-source-paid-1782976582095.js";
        if (File.Exists(paidPath))
        {
            try
            {
                var imported = await src.ImportFromFileAsync(paidPath);
                Check(imported != null, "本地导入聆澜脚本");
                if (imported != null)
                {
                    var activation = await src.EnableSourceAsync(imported);
                    Check(activation.Succeeded, "用户确认后启用聆澜脚本");
                    var info2 = scriptSource.GetScript(imported.Id);
                    var loadOk = info2 != null && await sp.GetRequiredService<LxScriptEngine>().TestLoadAsync(info2);
                    Check(loadOk, "聆澜脚本 Jint 加载校验");
                    var qqSong2 = (await src.SearchAsync("晴天", 1, 3, "song", "qq")).Items.FirstOrDefault();
                    if (info2 != null && qqSong2 != null)
                    {
                        var u = await sp.GetRequiredService<LxScriptEngine>().GetPlayUrlAsync(info2, qqSong2, qqSong2.Source, "128k");
                        Check(!string.IsNullOrEmpty(u), "聆澜 Jint 播放URL(tx)");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  导入测试异常: {ex.Message}");
                Check(false, "本地导入付费脚本");
            }
        }
        else
        {
            Console.WriteLine("  本地付费脚本不存在，跳过");
        }

        // ====== 11. 歌词全链路（同平台 + 跨平台搜索） ======
        Console.WriteLine("--- 11. 歌词全链路（LyricService 同平台 + 跨平台搜索） ---");
        try
        {
            var lyricSvc = sp.GetRequiredService<ILyricService>();

            // 11a. 网易云同平台歌词
            var wySong = (await src.SearchAsync("晴天", 1, 3, "song", "netease")).Items.FirstOrDefault();
            if (wySong != null)
            {
                var r1 = await lyricSvc.GetLyricAsync(wySong);
                Console.WriteLine($"  [netease] {wySong.Title} id={wySong.Id} -> 行数={r1?.Lyric.Lines.Count ?? 0} 来源={r1?.SourceId} 跨源={r1?.FromSearch}");
                Check(r1 is { Lyric.Lines.Count: > 0 }, "网易云歌词(同源或跨源)");
            }

            // 11b. 酷狗歌曲跨平台搜索歌词（酷狗 krc.php 修复验证 + 兜底 netease）
            var kgSong = (await src.SearchAsync("晴天", 1, 3, "song", "kg")).Items.FirstOrDefault();
            if (kgSong != null)
            {
                var kgOwn = await src.GetApi("kg")!.GetLyricAsync(kgSong);
                Console.WriteLine($"  [kg] 同源歌词行数={kgOwn?.Lines.Count ?? 0} (krc.php 修复)");
                var r2 = await lyricSvc.GetLyricAsync(kgSong);
                Console.WriteLine($"  [kg] LyricService 结果行数={r2?.Lyric.Lines.Count ?? 0} 来源={r2?.SourceId} 跨源={r2?.FromSearch}");
                Check(r2 is { Lyric.Lines.Count: > 0 }, "酷狗歌词(同源或跨源)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  歌词全链路异常: {ex.Message}");
            Check(false, "歌词全链路");
        }

        // ====== 12. 随版本验证脚本 + 隐藏内置API校验 ======
        Console.WriteLine("--- 12. 随版本验证脚本 + 隐藏内置API校验 ---");
        foreach (var s in scriptSource.GetAllScripts())
            Console.WriteLine($"  [{s.Id}] {s.Name} v{s.Version} 源[{string.Join(",", s.SupportedSources)}]");
        var bundled = src.GetAllSources().SingleOrDefault(s => s.TrustState == ScriptTrustState.Bundled);
        Check(bundled != null && bundled.IsEnabled, "随版本验证脚本已离线加载");
        var bundledInfo = bundled == null ? null : scriptSource.GetScript(bundled.Id);
        Check(bundledInfo != null && await sp.GetRequiredService<LxScriptEngine>().TestLoadAsync(bundledInfo),
            "随版本验证脚本 Jint 加载校验");

        // 隐藏内置API：管理列表不含 5 大官方源，但底层搜索仍可用
        var mgmtList = src.GetAllSources();
        var hiddenIds = mgmtList.Where(s => new[] { "netease", "qq", "kg", "kw", "migu" }.Any(id => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
        Console.WriteLine($"  管理列表数={mgmtList.Count} 泄漏官方源={hiddenIds.Count}");
        Check(hiddenIds.Count == 0, "内置官方API已从管理列表隐藏");
        var enabledList = src.GetEnabledSources();
        Console.WriteLine($"  启用源数={enabledList.Count} 内置底层源数={enabledList.Count(s => s.Type == SourceType.BuiltIn)}");
        Check(enabledList.Count(s => s.Type == SourceType.BuiltIn) >= 5, "内置官方API仍是底层搜索数据源");
        var searchCheck = await src.SearchAsync("晴天", 1, 3);
        Check(searchCheck.TotalItems > 0, "隐藏内置源后搜索仍正常");

        // ====== 13b. 默认音源即时生效 + IKUN 接口导入 ======
        Console.WriteLine("--- 13b. 默认音源即时生效 + IKUN 接口导入 ---");
        try
        {
            var firstScript = scriptSource.GetAllScripts().FirstOrDefault();
            if (firstScript != null)
            {
                src.SetDefaultSource(firstScript.Id);
                var def = src.GetDefaultSource();
                Check(def != null && string.Equals(def.Id, firstScript.Id, StringComparison.OrdinalIgnoreCase), $"设默认后立即生效[{firstScript.Id}]");
            }
            else
            {
                Console.WriteLine("  无脚本源可设默认，跳过");
            }

            const string ikunUrl = "https://c.wwwweb.top/script/lxmusic?key=IKM-P45800001-Wuyq357Mmh4Vk6fo-iZ";
            try
            {
                var ikun = await src.ImportFromUrlAsync(ikunUrl);
                if (ikun == null) { Console.WriteLine("  [ikun] 导入失败（URL不可达或脚本校验失败）"); Check(false, "IKUN导入"); }
                else
                {
                    Console.WriteLine($"  [ikun] 导入成功 {ikun.Name} v{ikun.Version} 类型={ikun.TypeText}");
                    Check(true, "IKUN导入");
                    var activation = await src.EnableSourceAsync(ikun);
                    Check(activation.Succeeded, "用户确认后启用 IKUN");
                    var ikunInfo = scriptSource.GetScript(ikun.Id);
                    if (ikunInfo != null)
                    {
                        // 校验 IKUN 音质表已正确解析（应含无损 flac）
                        var qqQ = ikunInfo.QualityMap.TryGetValue("qq", out var qqQs) ? qqQs : new List<string>();
                        Console.WriteLine($"  [ikun] 支持源=[{string.Join(",", ikunInfo.SupportedSources)}] qq音质=[{string.Join(",", qqQ)}]");
                        Check(ikunInfo.QualityMap.ContainsKey("qq") && qqQ.Contains("flac"), "IKUN 音质表含无损(flac)");
                        var txSong = (await src.SearchAsync("晴天", 1, 3, "song", "qq")).Items.FirstOrDefault();
                        if (txSong != null)
                        {
                            var u = await sp.GetRequiredService<LxScriptEngine>().GetPlayUrlAsync(ikunInfo, txSong, txSong.Source, "128k");
                            Console.WriteLine($"  [ikun] tx播放URL -> {(string.IsNullOrEmpty(u) ? "NULL" : u[..Math.Min(70, u.Length)] + "...")}");
                            Check(!string.IsNullOrEmpty(u), "IKUN Jint 播放URL(tx)");
                        }

                        // 网易/酷我：应用侧需把歌曲 id 合成到 songmid，IKUN 才能解析（修复排行榜/歌单/搜索多不能播）
                        foreach (var (sid, label) in new[] { ("netease", "wy"), ("kw", "kw") })
                        {
                            var song = (await src.SearchAsync("晴天", 1, 3, "song", sid)).Items.FirstOrDefault();
                            if (song == null) { Console.WriteLine($"  [ikun] {sid} 无搜索结果"); continue; }
                            var u2 = await sp.GetRequiredService<LxScriptEngine>().GetPlayUrlAsync(ikunInfo, song, song.Source, "128k");
                            Console.WriteLine($"  [ikun] {label}播放URL -> {(string.IsNullOrEmpty(u2) ? "NULL" : u2[..Math.Min(70, u2.Length)] + "...")}");
                            Check(!string.IsNullOrEmpty(u2), $"IKUN Jint 播放URL({label})");
                        }
                    }
                    src.RemoveSource(ikun);
                }
            }
            catch (Exception ikex) { Console.WriteLine($"  [ikun] 异常: {ikex.Message}"); Check(false, "IKUN导入"); }
        }
        catch (Exception ex) { Console.WriteLine($"  13b 异常: {ex.Message}"); }
        // ====== 14. 修复端点验证（酷狗播放URL / 咪咕 lyricUrl） ======
        Console.WriteLine("--- 14. 修复端点验证 ---");
        try
        {
            var kg2 = sp.GetRequiredService<KugouMusicService>();
            var kgSong2 = (await src.SearchAsync("晴天", 1, 3, "song", "kg")).Items.FirstOrDefault();
            if (kgSong2 != null)
            {
                var kgUrl = await kg2.GetPlayUrlAsync(kgSong2.Id, "128k");
                Console.WriteLine($"  [kg] 内置播放URL -> {(string.IsNullOrEmpty(kgUrl) ? "NULL(依赖脚本)" : kgUrl[..Math.Min(60, kgUrl.Length)] + "...")}");
            }

            var migu = sp.GetRequiredService<MiguMusicService>();
            var miguSong = (await src.SearchAsync("晴天", 1, 3, "song", "migu")).Items.FirstOrDefault();
            if (miguSong != null)
            {
                Console.WriteLine($"  [migu] lyricUrl={(string.IsNullOrEmpty(miguSong.LyricUrl) ? "空" : "有")}");
                var mLyric = await migu.GetLyricAsync(miguSong);
                Console.WriteLine($"  [migu] 歌词行数={mLyric?.Lines.Count ?? 0}");
                Check(mLyric is { Lines.Count: > 0 }, "咪咕歌词(lyricUrl)");
            }
        }
        catch (Exception ex) { Console.WriteLine($"  修复端点验证异常: {ex.Message}"); }

        // ====== 15. 网易云个人歌单导入冒烟（无需登录） ======
        Console.WriteLine("--- 15. 网易云个人歌单导入冒烟 ---");
        try
        {
            var imp = sp.GetRequiredService<IPlaylistImportService>();
            var bad = await imp.LoginWithCookieAsync("netease", "MUSIC_U=invalid_dummy_cookie_for_test;");
            Console.WriteLine($"  [netease] 无效Cookie登录结果={(bad == null ? "拒绝(预期)" : "意外成功")}");
            Check(bad == null, "网易云无效Cookie被拒绝");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  网易云导入冒烟异常: {ex.Message}");
        }

        Console.WriteLine("--- 16. QQ/酷狗/酷我 个人歌单导入冒烟（无需登录） ---");
        try
        {
            var imp = sp.GetRequiredService<IPlaylistImportService>();
            var qqProv = imp.GetProvider("qq");
            if (qqProv != null)
            {
                var qqSongs = await qqProv.GetPlaylistSongsAsync(new RemotePlaylist { RemoteId = "3285516001", Name = "smoke" });
                Console.WriteLine($"  [qq] 歌单歌曲数={qqSongs.Count} 首曲={qqSongs.FirstOrDefault()?.Title}/{qqSongs.FirstOrDefault()?.Artist}");
                Check(qqSongs.Count > 0, "QQ歌单歌曲");
                var qqBad = await imp.LoginWithCookieAsync("qq", "uin=12345; foo=bar");
                Console.WriteLine($"  [qq] 无效Cookie结果={(qqBad == null ? "拒绝(预期)" : "意外成功")}");
                Check(qqBad == null, "QQ无效Cookie被拒绝");
            }
            var kgProv = imp.GetProvider("kg");
            if (kgProv != null)
            {
                var kgSongs = await kgProv.GetPlaylistSongsAsync(new RemotePlaylist { RemoteId = "3339907", Name = "smoke" });
                Console.WriteLine($"  [kg] 歌单歌曲数={kgSongs.Count} 首曲={kgSongs.FirstOrDefault()?.Title}/{kgSongs.FirstOrDefault()?.Artist}");
                Check(kgSongs.Count > 0, "酷狗歌单歌曲");
                var kgBad = await imp.LoginWithCookieAsync("kg", "userid=123; token=");
                Console.WriteLine($"  [kg] 无效Cookie结果={(kgBad == null ? "拒绝(预期)" : "意外成功")}");
                Check(kgBad == null, "酷狗无效Cookie被拒绝");
            }
            var kwProv = imp.GetProvider("kw");
            if (kwProv != null)
            {
                var kwSongs = await kwProv.GetPlaylistSongsAsync(new RemotePlaylist { RemoteId = "3601256457", Name = "smoke" });
                Console.WriteLine($"  [kw] 歌单歌曲数={kwSongs.Count} 首曲={kwSongs.FirstOrDefault()?.Title}/{kwSongs.FirstOrDefault()?.Artist}");
                Check(kwSongs.Count > 0, "酷我歌单歌曲");
                var kwBad = await imp.LoginWithCookieAsync("kw", "foo=bar; no_token=1");
                Console.WriteLine($"  [kw] 无效Cookie结果={(kwBad == null ? "拒绝(预期)" : "意外成功")}");
                Check(kwBad == null, "酷我无效Cookie被拒绝");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  QQ/酷狗/酷我 冒烟异常: {ex.Message}");
        }
        // ====== 17. 搜索去重 / 单源过滤 / 专辑搜索 / 独家音源导入 ======
        Console.WriteLine("--- 17. 搜索去重 / 单源过滤 / 专辑搜索 / 独家音源导入 ---");
        try
        {
            // 17a. 全部音源：跨源按 专辑+歌手+时长 去重
            var all = await src.SearchAsync("晴天", 1, 20, "song", "all");
            var dup = all.Items
                .GroupBy(m => $"{m.Album?.Trim() ?? ""}||{m.Artist?.Trim() ?? ""}||{m.Duration ?? ""}")
                .Count(g => g.Count() > 1);
            Console.WriteLine($"  [all] 总条数={all.Items.Count} 去重键重复组数={dup}");
            Console.WriteLine($"  [all] 首3条: {string.Join(" | ", all.Items.Take(3).Select(m => $"{m.Artist}-{m.Title}[{m.Source}]"))}");
            Check(all.Items.Count > 0, "全部音源搜索");
            Check(dup <= 1, "全部音源按专辑+歌手+时长去重");

            // 17b. 单源过滤
            foreach (var sid in new[] { "netease", "qq", "kg", "kw", "migu" })
            {
                var single = await src.SearchAsync("晴天", 1, 10, "song", sid);
                var leaked = single.Items.Count(m => !string.Equals(m.Source, sid, StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"  [{sid}] 条数={single.Items.Count} 泄漏其他源={leaked}");
                Check(leaked == 0, $"[{sid}] 单源结果无其他源泄漏");
            }

            // 17c. 专辑搜索（全部音源）
            var albums = await src.SearchAsync("周杰伦", 1, 10, "album", "all");
            var albumRows = albums.Items.Where(m => m.IsAlbum).ToList();
            Console.WriteLine($"  [album] 专辑行={albumRows.Count} 总={albums.TotalItems}");
            foreach (var a in albumRows.Take(6))
                Console.WriteLine($"    专辑: {a.Title} / {a.Artist} / {a.Source}");
            Check(albumRows.Count > 0, "专辑搜索返回专辑行(IsAlbum)");

            // 17d. 专辑详情（取第一个有 Id 的专辑行展开）
            var firstAlbum = albumRows.FirstOrDefault(a => !string.IsNullOrEmpty(a.Id));
            if (firstAlbum != null)
            {
                var detail = await src.GetAlbumDetailAsync(firstAlbum, 1, 20);
                Console.WriteLine($"  [album detail] 《{firstAlbum.Title}》[{firstAlbum.Source}] 歌曲数={detail.Items.Count} 首曲={detail.Items.FirstOrDefault()?.Title}");
                Check(detail.Items.Count > 0, "专辑详情展开歌曲");
            }
            else
            {
                Console.WriteLine("  [album detail] 无专辑行可展开");
            }
            // 17f. 专辑详情分页收集（修复“专辑内只显示20条”）——复现 MainWindow.OpenAlbumDetailAsync 的收集循环
            if (firstAlbum != null)
            {
                try
                {
                    var collected = new List<MusicInfo>();
                    const int albumPageSize = 50;
                    int pages = 0;
                    for (int page = 1; page <= 30; page++)
                    {
                        var r = await src.GetAlbumDetailAsync(firstAlbum, page, albumPageSize);
                        var items = r.Items ?? new List<MusicInfo>();
                        int before = collected.Count;
                        foreach (var m in items)
                            if (!collected.Any(x => x.Id == m.Id && string.Equals(x.Source, m.Source, StringComparison.OrdinalIgnoreCase)))
                                collected.Add(m);
                        pages = page;
                        if (items.Count == 0) break;          // 没有更多数据
                        if (collected.Count == before) break; // 本页无新增（全量型源第二页起重复）
                    }
                    Console.WriteLine($"  [album paging] 《{firstAlbum.Title}》[{firstAlbum.Source}] 分页{pages}次 收集={collected.Count} 首");
                    Check(collected.Count > 0, "专辑详情分页收集");
                }
                catch (Exception ex) { Console.WriteLine($"  [album paging] 异常: {ex.Message}"); Check(false, "专辑详情分页收集"); }
            }


            // 17e. 独家音源导入（Jint shim 增强后应成功）
            var exclusivePath = @"D:\000\音源接口\独家音源（推荐）\[独家音源] v4.0.js";
            if (File.Exists(exclusivePath))
            {
                var exSrc = await src.ImportFromFileAsync(exclusivePath);
                Console.WriteLine($"  [exclusive] 导入结果={(exSrc == null ? "失败" : $"成功：{exSrc.Name} v{exSrc.Version} 类型={exSrc.TypeText}")}");
                Check(exSrc != null, "独家音源导入成功");
                if (exSrc != null)
                {
                    var activation = await src.EnableSourceAsync(exSrc);
                    Check(activation.Succeeded, "用户确认后启用独家音源");
                    var ok = await src.TestSourceAsync(exSrc);
                    Console.WriteLine($"  [exclusive] TestSource={ok}");
                    Check(ok, "独家音源 Jint 可执行");
                    // 清理测试导入的音源，避免污染持久化
                    src.RemoveSource(exSrc);
                }
            }
            else
            {
                Console.WriteLine($"  [exclusive] 文件不存在: {exclusivePath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  17 组验证异常: {ex.Message}");
            Check(false, "17 组验证");
        }

                // ====== 18. BASS 播放器冒烟测试（AudioPlayer） ======
        Console.WriteLine("--- 18. BASS 播放器冒烟测试 (AudioPlayer) ---");
        try
        {
            var player = new AudioPlayer();

            // 设备枚举
            var devices = player.GetOutputDevices();
            Console.WriteLine($"  输出设备: {string.Join(" | ", devices.Take(6).Select(d => d.Name))}");
            Check(devices.Count >= 1, "输出设备枚举");

            // 会话音量往返（保留合成器双向跟随的基础）
            player.Volume = 0.42;
            var v0 = player.Volume;
            Console.WriteLine($"  会话音量: set=0.42 -> read={v0:F2}");
            Check(Math.Abs(v0 - 0.42) < 0.05, "会话音量读写");
            player.Volume = 0.5;

            // 生成 3 秒 440Hz 正弦 WAV（BASS 原生支持 WAV）
            var wavPath = Path.Combine(Path.GetTempPath(), $"lxaudio_test_{Guid.NewGuid():N}.wav");
            GenerateSineWav(wavPath, 3.0, 440.0);
            Console.WriteLine($"  测试WAV: {wavPath} ({new FileInfo(wavPath).Length} bytes)");

            // 加载 + 时长
            await player.LoadAsync(wavPath);
            var dur = player.Duration;
            Console.WriteLine($"  加载WAV 时长={dur.TotalSeconds:F2}s");
            Check(dur.TotalSeconds >= 2.5, "WAV 时长解析");

            // 播放推进
            await player.PlayAsync();
            await Task.Delay(700);
            var p1 = player.Position.TotalSeconds;
            Console.WriteLine($"  播放 0.7s 后位置={p1:F2}s");
            Check(p1 >= 0.3, "播放位置推进");

            // 暂停锚定
            player.Pause();
            var p2 = player.Position.TotalSeconds;
            await Task.Delay(400);
            var p3 = player.Position.TotalSeconds;
            Console.WriteLine($"  暂停后位置不变: {p2:F2} -> {p3:F2}");
            Check(Math.Abs(p3 - p2) < 0.05, "暂停后位置锚定");

            // 恢复播放
            await player.PlayAsync();
            await Task.Delay(500);
            var p4 = player.Position.TotalSeconds;
            Console.WriteLine($"  恢复播放后位置={p4:F2}s");
            Check(p4 > p2, "恢复播放");

            // 定位
            player.SeekTo(TimeSpan.FromSeconds(1.5));
            var p5 = player.Position.TotalSeconds;
            Console.WriteLine($"  定位 1.5s -> {p5:F2}s");
            Check(Math.Abs(p5 - 1.5) < 0.25, "SeekTo 定位");

            // 在线 URL：真实解析 + BASS 流式（边下边播；建流失败自动回退下载）播放
            try
            {
                var audioSong = (await src.SearchAsync("明天会更好", 1, 5, "song", "netease")).Items.FirstOrDefault()
                    ?? (await src.SearchAsync("明天会更好", 1, 5)).Items.FirstOrDefault();
                if (audioSong != null)
                {
                    var playUrl = await src.GetPlayUrlAsync(audioSong, audioSong.Source, "128k");
                    Console.WriteLine($"  在线URL: {(string.IsNullOrEmpty(playUrl) ? "NULL" : playUrl![..Math.Min(70, playUrl.Length)] + "...")}");
                    if (!string.IsNullOrEmpty(playUrl))
                    {
                        await player.LoadAsync(playUrl);
                        var d2 = player.Duration;
                        Console.WriteLine($"  在线歌曲时长={d2.TotalSeconds:F1}s");
                        Check(d2.TotalSeconds > 5, "在线歌曲流式建流");
                        await player.PlayAsync();
                        await Task.Delay(1200);
                        Console.WriteLine($"  在线播放 1.2s 位置={player.Position.TotalSeconds:F2}s");
                        Check(player.Position.TotalSeconds > 0.5, "在线播放推进");
                        // 流式播放中热切换设备（修复“切换播放设备提示失败”：用 ChannelSetDevice 搬流，不重连/重下）
                        var devs18 = player.GetOutputDevices();
                        if (devs18.Count > 1)
                        {
                            var sw = player.SwitchOutputDevice(devs18[1].Index);
                            Console.WriteLine($"  播放中切到设备[{devs18[1].Index}] ok={sw} now={player.CurrentOutputDeviceIndex}");
                            Check(sw, "播放中热切换设备");
                            var sw2 = player.SwitchOutputDevice(-1);
                            Console.WriteLine($"  播放中切回默认 ok={sw2} now={player.CurrentOutputDeviceIndex}");
                            Check(sw2, "播放中切回默认设备");
                        }
                        player.Stop();
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"  在线播放异常: {ex.Message}"); Check(false, "在线播放"); }

            player.Dispose();
            try { File.Delete(wavPath); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  播放器冒烟异常: {ex.Message}");
            Check(false, "BASS 播放器冒烟");
        }
        // ====== 20. 第三方接口类型：musicUrl / lyric / pic 动作分派（合成脚本） ======
        Console.WriteLine("--- 20. 第三方接口类型 musicUrl/lyric/pic 动作分派（合成脚本） ---");
        try
        {
            var eng = sp.GetRequiredService<LxScriptEngine>();
            var synth = new LxScriptInfo
            {
                Id = "synth_actions",
                Name = "合成动作测试",
                RawScript = @"
const { EVENT_NAMES, on, send } = globalThis.lx;
on(EVENT_NAMES.request, ({ action, source, info }) => {
  if (action === 'musicUrl') return Promise.resolve('https://example.com/a.mp3');
  if (action === 'lyric') return Promise.resolve({ lyric: '[00:01.00]hello world', tlyric: '[00:01.00]你好世界' });
  if (action === 'pic') return Promise.resolve('https://example.com/cover.jpg');
  return Promise.reject('unsupported action');
});
send(EVENT_NAMES.inited, { sources: {} });
"
            };
            var song = new MusicInfo { Id = "x", Title = "t", Artist = "a", Source = "qq", Duration = "03:00" };
            Check(await eng.TestLoadAsync(synth), "[合成] Jint 加载");
            var mu = await eng.GetPlayUrlAsync(synth, song, song.Source, "128k");
            Check(string.Equals(mu, "https://example.com/a.mp3", StringComparison.OrdinalIgnoreCase), "[合成] musicUrl 动作");
            var lr = await eng.GetLyricAsync(synth, song, song.Source);
            Check(lr != null && lr.Lyric.Contains("hello world") && lr.Tlyric != null, "[合成] lyric 动作");
            var pc = await eng.GetPicAsync(synth, song, song.Source);
            Check(string.Equals(pc, "https://example.com/cover.jpg", StringComparison.OrdinalIgnoreCase), "[合成] pic 动作");
        }
        catch (Exception ex) { Console.WriteLine($"  20 异常: {ex.Message}"); Check(false, "接口类型动作分派"); }

        // ====== 21. 运行时兼容：AES/randomBytes/buffer/zlib（合成脚本） ======
        Console.WriteLine("--- 21. 运行时兼容 AES/md5/randomBytes/buffer/zlib ---");
        try
        {
            var eng = sp.GetRequiredService<LxScriptEngine>();
            var rt = new LxScriptInfo
            {
                Id = "synth_runtime",
                Name = "合成运行时测试",
                RawScript = @"
const { EVENT_NAMES, on, send, utils } = globalThis.lx;
on(EVENT_NAMES.request, async ({ action }) => {
  if (action !== 'musicUrl') return Promise.reject('unsupported');
  const key = Buffer.from('0123456789abcdef');
  const iv = Buffer.from('0000000000000000');
  const enc = utils.crypto.aesEncrypt(Buffer.from('hello-aes'), 'aes-128-cbc', key, iv);
  const dec = utils.crypto.aesDecrypt(enc, 'aes-128-cbc', key, iv);
  const decStr = utils.buffer.bufToString(dec, 'utf8');
  const md5 = utils.crypto.md5('abc');
  const rnd = utils.crypto.randomBytes(16);
  const comp = await utils.zlib.deflate(Buffer.from('hello-zlib'));
  const un = await utils.zlib.inflate(comp);
  const unStr = utils.buffer.bufToString(un, 'utf8');
  return Promise.resolve('ok:' + decStr + '|' + md5 + '|' + rnd.length + '|' + unStr);
});
send(EVENT_NAMES.inited, { sources: {} });
"
            };
            var song = new MusicInfo { Id = "x", Title = "t", Artist = "a", Source = "qq", Duration = "03:00" };
            var u = await eng.GetPlayUrlAsync(rt, song, song.Source, "128k");
            Console.WriteLine($"  [运行时] 结果={u}");
            Check(string.Equals(u, "ok:hello-aes|900150983cd24fb0d6963f7d28e17f72|16|hello-zlib", StringComparison.Ordinal), "AES/md5/randomBytes/buffer/zlib 全部通过");
        }
        catch (Exception ex) { Console.WriteLine($"  21 异常: {ex.Message}"); Check(false, "运行时兼容"); }

        // ====== 22. 严格匹配：换源只换同一首 ======
        Console.WriteLine("--- 22. 严格匹配 IsSameSong / FindBestMatches ---");
        try
        {
            var tgt = new MusicInfo { Title = "晴天", Artist = "周杰伦", Duration = "04:29" };
            var same = new MusicInfo { Title = "晴天", Artist = "周杰伦", Duration = "04:29" };
            var cover = new MusicInfo { Title = "晴天", Artist = "关诗敏", Duration = "04:29" };
            var diffDur = new MusicInfo { Title = "晴天", Artist = "周杰伦", Duration = "03:00" };
            var closeDiffSinger = new MusicInfo { Title = "晴天", Artist = "关诗敏", Duration = "04:27" };
            Check(MusicSearchMatcher.IsSameSong(same, tgt), "同曲命中");
            Check(!MusicSearchMatcher.IsSameSong(cover, tgt), "翻唱不同歌手不命中");
            Check(!MusicSearchMatcher.IsSameSong(diffDur, tgt), "同题异时长不命中");
            Check(!MusicSearchMatcher.IsSameSong(closeDiffSinger, tgt), "时长接近但歌手不同不命中");
            var multi1 = new MusicInfo { Title = "千里之外", Artist = "周杰伦/费玉清", Duration = "04:00" };
            var multi2 = new MusicInfo { Title = "千里之外", Artist = "费玉清、周杰伦", Duration = "04:00" };
            Check(MusicSearchMatcher.IsSameSong(multi2, multi1), "多歌手顺序不同命中");
            var ranked = MusicSearchMatcher.FindBestMatches(new[] { cover, diffDur, same }, tgt);
            Check(ranked.Count > 0 && ReferenceEquals(ranked[0], same), "严格匹配排序最优");
            var normCover = new MusicInfo { Title = "晴天(深情版)", Artist = "周杰伦", Duration = "04:28" };
            Check(MusicSearchMatcher.IsSameSong(normCover, tgt), "带后缀同曲命中");
        }
        catch (Exception ex) { Console.WriteLine($"  22 异常: {ex.Message}"); Check(false, "严格匹配"); }

        // ====== 23. 随版本验证源 + 全链路可播性校验 ======
        Console.WriteLine("--- 23. 随版本验证源 + 全链路可播性校验 ---");
        try
        {
            var bundledSource = src.GetAllSources().SingleOrDefault(s => s.TrustState == ScriptTrustState.Bundled);
            var bundledScript = bundledSource == null ? null : scriptSource.GetScript(bundledSource.Id);
            Check(bundledScript != null, "随版本验证源已加载");
            var qqSong = (await src.SearchAsync("晴天", 1, 3, "song", "qq")).Items.FirstOrDefault();
            if (qqSong != null)
            {
                // 走 src.GetPlayUrlAsync 全链路（含可播性校验）
                var url = await src.GetPlayUrlAsync(qqSong, qqSong.Source, "128k");
                Console.WriteLine($"  [随版本源链路] QQ播放URL -> {(string.IsNullOrEmpty(url) ? "NULL" : url[..Math.Min(55, url.Length)] + "...")}");
                Check(!string.IsNullOrEmpty(url), "随版本源全链路播放URL(校验后非空)");
                if (!string.IsNullOrEmpty(url))
                {
                    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    try
                    {
                        var resp = await hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        var ct = resp.Content.Headers.ContentType?.ToString() ?? "";
                        Check((int)resp.StatusCode == 200 && ct.StartsWith("audio/", StringComparison.OrdinalIgnoreCase), "随版本源全链路 URL 可播(200 audio)");
                    }
                    catch (Exception ex) { Console.WriteLine($"  [随版本源链路] 探测失败: {ex.Message}"); Check(false, "随版本源全链路 URL 可播(200 audio)"); }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"  23 异常: {ex.Message}"); }

Console.WriteLine($"\n=== 验证完成：通过 {pass}，失败 {fail} ===");
        await host.StopAsync();
    }
    /// <summary>生成指定时长/频率的正弦单声道 16bit WAV（用于播放器冒烟测试）</summary>
    static void GenerateSineWav(string path, double seconds, double freq)
    {
        int sampleRate = 44100;
        short amplitude = 12000;
        int sampleCount = (int)(sampleRate * seconds);
        int dataSize = sampleCount * 2;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                    // PCM chunk size
        w.Write((short)1);              // PCM
        w.Write((short)1);              // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);        // byte rate
        w.Write((short)2);              // block align
        w.Write((short)16);             // bits per sample
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            short s = (short)(amplitude * Math.Sin(2 * Math.PI * freq * t));
            w.Write(s);
        }
    }}
