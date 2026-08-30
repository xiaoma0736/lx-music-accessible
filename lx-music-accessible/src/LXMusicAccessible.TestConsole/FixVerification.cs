using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LXMusicAccessible.Models;
using LXMusicAccessible.Data;
using LXMusicAccessible.Services;
using LXMusicAccessible.Core.MusicApi;

namespace LXMusicAccessible.TestConsole;

/// <summary>Focused regression checks for the Kugou search and LX V8 script fixes.</summary>
internal static class FixVerification
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        Console.WriteLine("=== 修复回归验证 ===");
        int pass = 0, fail = 0;
        void Check(bool ok, string name, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "✅" : "❌")} {name}{(string.IsNullOrEmpty(detail) ? "" : $" -> {detail}")}");
            if (ok) pass++; else fail++;
        }

        var dataDir = Path.Combine(Path.GetTempPath(), "lx-fixverify-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<string>(dataDir);
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

        var imported = new List<MusicSource>();
        using var provider = services.BuildServiceProvider();
        try
        {
            var sourceService = provider.GetRequiredService<IMusicSourceService>();
            var engine = provider.GetRequiredService<LxScriptEngine>();
            var scriptSource = provider.GetRequiredService<ScriptMusicSource>();

            Console.WriteLine("--- 酷狗搜索 ---");
            PagedResult<MusicInfo>? kgPage = null;
            foreach (var pageSize in new[] { 9, 20, 40 })
            {
                kgPage = await sourceService.SearchAsync("晴天", 1, pageSize, "song", "kg");
                Check(kgPage.Items.Count > 0 && kgPage.TotalItems > 0,
                    $"酷狗搜索 pageSize={pageSize}", $"count={kgPage.Items.Count}, total={kgPage.TotalItems}");
            }

            var kgSong = kgPage?.Items.FirstOrDefault(x => !string.IsNullOrEmpty(x.Id));
            Check(kgSong != null, "获得酷狗播放测试样本", kgSong?.Title);

            Console.WriteLine("--- 本地 LX 脚本导入与 V8 执行 ---");
            var defaultScripts = new[]
            {
                @"D:\000\音源接口\墨澜音乐源v2.3.3.js",
                @"D:\000\音源接口\lx-玉宁熙V1.2.2.js"
            };
            var scriptPaths = args.Skip(1).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            if (scriptPaths.Length == 0) scriptPaths = defaultScripts;

            foreach (var path in scriptPaths)
            {
                var label = Path.GetFileName(path);
                if (!File.Exists(path))
                {
                    Check(false, $"{label} 存在", path);
                    continue;
                }

                try
                {
                    var source = await sourceService.ImportFromFileAsync(path);
                    Check(source != null, $"{label} 本地导入");
                    if (source == null)
                    {
                        Check(false, $"{label} 导入错误", engine.LastError);
                        continue;
                    }
                    imported.Add(source);

                    var testOk = await sourceService.TestSourceAsync(source);
                    Check(testOk, $"{label} TestSource", testOk ? null : engine.LastError);

                    var info = scriptSource.GetScript(source.Id);
                    Check(info != null, $"{label} 脚本信息");

                    string? playUrl = null;
                    if (info != null)
                    {
                        foreach (var (apiSource, lxSource) in new[]
                        {
                            ("kg", "kg"), ("qq", "tx"), ("netease", "wy"), ("kw", "kw"), ("migu", "mg")
                        })
                        {
                            var sample = await sourceService.SearchAsync("晴天", 1, 1, "song", apiSource);
                            var song = sample.Items.FirstOrDefault();
                            if (song == null) continue;
                            playUrl = await engine.GetPlayUrlAsync(info, song, lxSource, "128k");
                            if (!string.IsNullOrEmpty(playUrl)) break;
                        }
                    }
                    Check(!string.IsNullOrEmpty(playUrl), $"{label} 播放URL", string.IsNullOrEmpty(playUrl) ? engine.LastError : playUrl);
                }
                catch (Exception ex)
                {
                    Check(false, $"{label} 验证", ex.Message);
                }
            }

            Console.WriteLine("--- 合成脚本动作与运行时桥 ---");
            try
            {
                var actions = new LxScriptInfo
                {
                    Id = "verify_actions",
                    Name = "Verify Actions",
                    RawScript = """
                        const { EVENT_NAMES, on, send } = globalThis.lx;
                        on(EVENT_NAMES.request, ({ action }) => {
                          if (action === 'musicUrl') return Promise.resolve('https://example.com/audio.mp3');
                          if (action === 'lyric') return Promise.resolve({ lyric: '[00:01.00]hello' });
                          if (action === 'pic') return Promise.resolve('https://example.com/cover.jpg');
                          return Promise.reject('unsupported');
                        });
                        send(EVENT_NAMES.inited, {});
                        """
                };
                var song = new MusicInfo { Id = "id", Title = "title", Artist = "artist", Album = "album", Source = "qq", Duration = "03:00" };
                Check(await engine.TestLoadAsync(actions), "合成脚本加载");
                Check(await engine.GetPlayUrlAsync(actions, song, "tx", "128k") == "https://example.com/audio.mp3", "合成 musicUrl");
                var lyric = await engine.GetLyricAsync(actions, song, "tx");
                Check(lyric?.Lyric.Contains("hello") == true, "合成 lyric");
                Check(await engine.GetPicAsync(actions, song, "tx") == "https://example.com/cover.jpg", "合成 pic");
            }
            catch (Exception ex)
            {
                Check(false, "合成脚本动作", ex.Message);
            }

            try
            {
                var runtime = new LxScriptInfo
                {
                    Id = "verify_runtime",
                    Name = "Verify Runtime",
                    RawScript = """
                        const { EVENT_NAMES, on, send, utils } = globalThis.lx;
                        on(EVENT_NAMES.request, async ({ action }) => {
                          if (action !== 'musicUrl') return Promise.reject('unsupported');
                          const key = Buffer.from('0123456789abcdef');
                          const iv = Buffer.from('0000000000000000');
                          const enc = utils.crypto.aesEncrypt(Buffer.from('hello-aes'), 'aes-128-cbc', key, iv);
                          const dec = utils.crypto.aesDecrypt(enc, 'aes-128-cbc', key, iv);
                          const text = utils.buffer.bufToString(dec, 'utf8');
                          const md5 = utils.crypto.md5('abc');
                          const random = utils.crypto.randomBytes(16);
                          const packed = await utils.zlib.deflate(Buffer.from('hello-zlib'));
                          const unpacked = await utils.zlib.inflate(packed);
                          return Promise.resolve('ok:' + text + '|' + md5 + '|' + random.length + '|' + utils.buffer.bufToString(unpacked, 'utf8'));
                        });
                        send(EVENT_NAMES.inited, {});
                        """
                };
                var song = new MusicInfo { Id = "id", Title = "title", Artist = "artist", Album = "album", Source = "qq", Duration = "03:00" };
                var result = await engine.GetPlayUrlAsync(runtime, song, "tx", "128k");
                Check(string.Equals(result, "ok:hello-aes|900150983cd24fb0d6963f7d28e17f72|16|hello-zlib", StringComparison.Ordinal),
                    "合成运行时桥", result);
            }
            catch (Exception ex)
            {
                Check(false, "合成运行时桥", ex.Message);
            }
        }
        finally
        {
            try { if (provider is IDisposable disposable) disposable.Dispose(); } catch { }
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }

        Console.WriteLine($"=== 修复回归验证完成：通过 {pass}，失败 {fail} ===");
        return fail == 0 ? 0 : 1;
    }
}

