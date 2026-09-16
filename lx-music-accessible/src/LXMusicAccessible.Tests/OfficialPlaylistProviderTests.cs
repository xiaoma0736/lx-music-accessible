using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class OfficialPlaylistProviderTests
{
    [Theory]
    [InlineData("kw")]
    [InlineData("kg")]
    [InlineData("qq")]
    [InlineData("netease")]
    [InlineData("migu")]
    public async Task Categories_load_official_tags_and_recommendations(string sourceId)
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(sourceId, http);
        RegisterCategories(handler, sourceId);

        var categories = await service.GetPlaylistCategoriesAsync();

        Assert.NotEmpty(categories);
        Assert.Contains(categories, category => category.Name == "推荐" && category.Tags.Any(tag => tag.Name == "官方热标签"));
        Assert.Contains(categories, category => category.Tags.Any(tag => tag.Id.Length > 0 && tag.Name.Length > 0));
        Assert.All(categories.SelectMany(category => category.Tags), tag =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tag.Id));
            Assert.False(string.IsNullOrWhiteSpace(tag.Name));
        });
        Assert.Equal(sourceId is "kw" or "qq" or "netease" ? 2 : 1, handler.Requests.Count);
    }

    [Theory]
    [InlineData("kw")]
    [InlineData("kg")]
    [InlineData("qq")]
    [InlineData("netease")]
    [InlineData("migu")]
    public async Task Category_playlists_and_details_are_paged(string sourceId)
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(sourceId, http);
        RegisterPagedFixtures(handler, sourceId);
        var category = sourceId switch
        {
            "kw" => "81-10000",
            "kg" => "official-tag-id",
            "qq" => "207",
            "netease" => "official",
            "migu" => "official-tag-id",
            _ => throw new ArgumentException("unknown source")
        };

        var first = await service.GetPlaylistsByCategoryAsync(category, 1, 1);
        var last = await service.GetPlaylistsByCategoryAsync(category, 2, 1);
        Assert.Equal(2, first.TotalItems);
        if (sourceId != "netease") Assert.Equal("1", StripPlaylistPrefix(first.Items.Single().Id));
        Assert.Equal(2, last.TotalItems);
        Assert.Equal("2", StripPlaylistPrefix(last.Items.Single().Id));

        var songs = await service.GetPlaylistDetailAsync("100", 2, 1);
        Assert.Equal(2, songs.TotalItems);
        Assert.Equal(sourceId == "netease" ? "2" : "song-2", songs.Items.Single().Id);
    }

    private static void RegisterPagedFixtures(RejectingHttpMessageHandler http, string sourceId)
    {
        void PlaylistPage(int page)
        {
            var shared = new
            {
                total = 2,
                total_cnt = 2,
                @params = new { total = 2 },
                special_db = new object[] { new { specialid = page, specialname = "Playlist " + page } },
                data = new object[] { new { id = page, name = "Playlist " + page } },
                contents = new object[] { new { resType = "2021", resId = page.ToString(), txt = "Playlist " + page } },
                content = new
                {
                    total_cnt = 2,
                    v_item = new object[] { new { basic = new { tid = page, title = "Playlist " + page } } }
                }
            };
            object payload = sourceId switch
            {
                "qq" => new { code = 0, playlist = new { data = shared } },
                "kg" => new { code = 200, status = 1, special_db = shared.special_db },
                "netease" => new { code = 200, total = 2, playlists = new[] { new { id = page, name = "Playlist " + page } } },
                _ => new { code = 200, status = 1, data = shared }
            };
            http.RegisterText(sourceId == "netease" ? HttpMethod.Post : HttpMethod.Get, PlaylistUrl(sourceId, page), Json(payload));
        }


        if (sourceId == "qq")
        {
            PlaylistPage(1);
            PlaylistPage(2);
            http.RegisterText(HttpMethod.Post, new Uri("https://u.y.qq.com/cgi-bin/musicu.fcg"), Json(new
            {
                req_0 = new
                {
                    data = new
                    {
                        total_song_num = 2,
                        songlist = new[] { new { mid = "song-2", name = "Song 2", singer = new[] { new { name = "Artist" } }, interval = 121 } }
                    }
                }
            }));
            return;
        }

        PlaylistPage(1);
        PlaylistPage(2);

        if (sourceId == "kw")
        {
            http.RegisterText(HttpMethod.Get, Url("http://nplserver.kuwo.cn/pl.svc", new()
            {
                ["op"] = "getlistinfo", ["pid"] = "100", ["pn"] = "1", ["rn"] = "1", ["encode"] = "utf8",
                ["keyset"] = "pl2012", ["identity"] = "kuwo", ["pcmp4"] = "1",
                ["vipver"] = "MUSIC_9.0.5.0_W1", ["newver"] = "1"
            }), Json(new
            {
                result = "ok", total = 2,
                musiclist = new[] { new { id = "song-2", name = "Song 2", artist = "Artist", album = "Album", duration = "121" } }
            }));
        }
        else if (sourceId == "kg")
        {
            http.RegisterText(HttpMethod.Get, Url("http://www2.kugou.kugou.com/yueku/v9/special/getSpecial", new()
            {
                ["is_smarty"] = "1", ["cdn"] = "cdn", ["t"] = "5", ["c"] = "official-tag-id"
            }), Json(new { status = 1, data = new { @params = new { total = 2 } } }));
            http.RegisterText(HttpMethod.Get, Url("http://mobilecdn.kugou.com/api/v3/special/song", new()
            {
                ["specialid"] = "100", ["page"] = "2", ["pagesize"] = "1", ["format"] = "json"
            }), Json(new
            {
                status = 1,
                data = new { total = 2, info = new[] { new { hash = "song-2", filename = "Artist - Song 2", duration = 121 } } }
            }));
        }
        else if (sourceId == "netease")
        {
            http.RegisterText(HttpMethod.Post, new Uri("https://music.163.com/api/linux/forward"), Json(new
            {
                playlist = new
                {
                    trackCount = 2,
                    tracks = new[] { new { id = 1, name = "Song 1", duration = 120000 }, new { id = 2, name = "Song 2", duration = 121000 } }
                }
            }));
        }
        else
        {
            http.RegisterText(HttpMethod.Get, Url("https://app.c.nf.migu.cn/MIGUM3.0/resource/playlist/song/v2.0", new()
            {
                ["pageNo"] = "2", ["pageSize"] = "1", ["playlistId"] = "100"
            }), Json(new
            {
                code = "000000",
                data = new
                {
                    totalCount = 2,
                    songList = new[] { new { songId = "song-2", songName = "Song 2", singerList = new[] { new { name = "Artist" } }, duration = 121 } }
                }
            }));
        }
    }

    private static Uri PlaylistUrl(string sourceId, int page) => sourceId switch
    {
        "kw" => Url("http://wapi.kuwo.cn/api/pc/classify/playlist/getTagPlayList", new()
        {
            ["loginUid"] = "0", ["loginSid"] = "0", ["appUid"] = "76039576",
            ["pn"] = page.ToString(), ["rn"] = "1", ["id"] = "81"
        }),
        "kg" => Url("http://www2.kugou.kugou.com/yueku/v9/special/getSpecial", new()
        {
            ["is_ajax"] = "1", ["cdn"] = "cdn", ["t"] = "5", ["c"] = "official-tag-id", ["p"] = page.ToString()
        }),
        "qq" => QqPlaylistUrl(page),
        "netease" => new Uri("https://music.163.com/weapi/playlist/list"),
        "migu" => Url("https://app.c.nf.migu.cn/pc/v1.0/template/musiclistplaza-listbytag/release", new()
        {
            ["pageNumber"] = page.ToString(), ["templateVersion"] = "2", ["tagId"] = "official-tag-id"
        }),
        _ => throw new ArgumentException("unknown source")
    };

    private static Uri QqPlaylistUrl(int page)
    {
        var data = JsonSerializer.Serialize(new
        {
            comm = new { cv = 1602, ct = 20 },
            playlist = new
            {
                method = "get_category_content",
                @param = new { titleid = 207, caller = "0", category_id = 207, size = 1, page = page - 1, use_page = 1 },
                module = "playlist.PlayListCategoryServer"
            }
        });
        return Url("https://u.y.qq.com/cgi-bin/musicu.fcg", new()
        {
            ["loginUin"] = "0", ["hostUin"] = "0", ["format"] = "json", ["inCharset"] = "utf-8",
            ["outCharset"] = "utf-8", ["notice"] = "0", ["platform"] = "wk_v15.json", ["needNewCode"] = "0",
            ["data"] = data
        });
    }

    [Theory]
    [InlineData("kw")]
    [InlineData("kg")]
    [InlineData("qq")]
    [InlineData("netease")]
    [InlineData("migu")]
    public async Task Malformed_playlist_detail_throws_instead_of_returning_empty(string sourceId)
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(sourceId, http);
        RegisterDetailFixture(handler, sourceId, "{not-json");
        await Assert.ThrowsAnyAsync<JsonException>(() => service.GetPlaylistDetailAsync("100", 1, 1));
    }

    [Theory]
    [InlineData("kw")]
    [InlineData("kg")]
    [InlineData("qq")]
    [InlineData("netease")]
    [InlineData("migu")]
    public async Task Empty_playlist_detail_returns_an_empty_page(string sourceId)
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(sourceId, http);
        RegisterDetailFixture(handler, sourceId, EmptyDetailJson(sourceId));
        var result = await service.GetPlaylistDetailAsync("100", 1, 1);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("kw")]
    [InlineData("kg")]
    [InlineData("qq")]
    [InlineData("netease")]
    [InlineData("migu")]
    public async Task Playlist_detail_network_failure_throws(string sourceId)
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(sourceId, http);
        handler.RegisterAsync(DetailMethod(sourceId), DetailUri(sourceId), _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture")));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetPlaylistDetailAsync("100", 1, 1));
    }

    [Theory]
    [InlineData("kw")]
    [InlineData("kg")]
    [InlineData("qq")]
    [InlineData("netease")]
    [InlineData("migu")]
    public async Task Playlist_detail_cancellation_throws(string sourceId)
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(sourceId, http);
        handler.RegisterAsync(DetailMethod(sourceId), DetailUri(sourceId), async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        });
        using var source = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetPlaylistDetailAsync("100", 1, 1, source.Token));
    }

    private static void RegisterDetailFixture(RejectingHttpMessageHandler handler, string sourceId, string content) =>
        handler.RegisterText(DetailMethod(sourceId), DetailUri(sourceId), content);

    private static string EmptyDetailJson(string sourceId) => sourceId switch
    {
        "kw" => "{\"result\":\"ok\",\"total\":0,\"musiclist\":[]}",
        "kg" => "{\"status\":1,\"data\":{\"total\":0,\"info\":[]}}",
        "qq" => "{\"req_0\":{\"data\":{\"total_song_num\":0,\"songlist\":[]}}}",
        "netease" => "{\"playlist\":{\"trackCount\":0,\"tracks\":[]}}",
        "migu" => "{\"code\":\"000000\",\"data\":{\"totalCount\":0,\"songList\":[]}}",
        _ => "{}"
    };

    private static HttpMethod DetailMethod(string sourceId) =>
        sourceId is "qq" or "netease" ? HttpMethod.Post : HttpMethod.Get;

    private static Uri DetailUri(string sourceId)
    {
        if (sourceId == "kw")
            return Url("http://nplserver.kuwo.cn/pl.svc", new()
            {
                ["op"] = "getlistinfo", ["pid"] = "100", ["pn"] = "0", ["rn"] = "1", ["encode"] = "utf8",
                ["keyset"] = "pl2012", ["identity"] = "kuwo", ["pcmp4"] = "1",
                ["vipver"] = "MUSIC_9.0.5.0_W1", ["newver"] = "1"
            });
        if (sourceId == "kg")
            return Url("http://mobilecdn.kugou.com/api/v3/special/song", new()
            {
                ["specialid"] = "100", ["page"] = "1", ["pagesize"] = "1", ["format"] = "json"
            });
        if (sourceId == "qq")
            return new Uri("https://u.y.qq.com/cgi-bin/musicu.fcg");
        if (sourceId == "netease")
            return new Uri("https://music.163.com/api/linux/forward");
        return Url("https://app.c.nf.migu.cn/MIGUM3.0/resource/playlist/song/v2.0", new()
        {
            ["pageNo"] = "1", ["pageSize"] = "1", ["playlistId"] = "100"
        });
    }

    [Theory]
    [InlineData("kw")]
    [InlineData("kg")]
    [InlineData("qq")]
    [InlineData("netease")]
    [InlineData("migu")]
    public async Task Category_playlist_network_failure_throws(string sourceId)
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(sourceId, http);
        handler.RegisterAsync(ListMethod(sourceId), PlaylistUrl(sourceId, 1), _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture")));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetPlaylistsByCategoryAsync(ListCategory(sourceId), 1, 1));
    }

    private static HttpMethod ListMethod(string sourceId) => sourceId == "netease" ? HttpMethod.Post : HttpMethod.Get;

    private static string ListCategory(string sourceId) => sourceId switch
    {
        "kw" => "81-10000",
        "kg" => "official-tag-id",
        "qq" => "207",
        "netease" => "official",
        "migu" => "official-tag-id",
        _ => throw new ArgumentException("unknown source")
    };

    private static string StripPlaylistPrefix(string id) => id[(id.IndexOf('_') + 1)..];

    private static MusicHttpClient CreateHttp(RejectingHttpMessageHandler handler)
    {
        return new MusicHttpClient(NullLogger<MusicHttpClient>.Instance, handler, new System.Net.CookieContainer());
    }

    private static IMusicApi CreateService(string sourceId, MusicHttpClient http) => sourceId switch
    {
        "kw" => new KuwoMusicService(NullLogger<KuwoMusicService>.Instance, http),
        "kg" => new KugouMusicService(NullLogger<KugouMusicService>.Instance, http),
        "qq" => new QqMusicService(NullLogger<QqMusicService>.Instance, http),
        "netease" => new NeteaseMusicService(http, NullLogger<NeteaseMusicService>.Instance),
        "migu" => new MiguMusicService(NullLogger<MiguMusicService>.Instance, http),
        _ => throw new ArgumentException("unknown source")
    };

    private static void RegisterCategories(RejectingHttpMessageHandler http, string sourceId)
    {
        switch (sourceId)
        {
            case "kw":
                http.RegisterText(HttpMethod.Get, Url("http://wapi.kuwo.cn/api/pc/classify/playlist/getTagList", new()
                {
                    ["cmd"] = "rcm_keyword_playlist", ["user"] = "0", ["prod"] = "kwplayer_pc_9.0.5.0",
                    ["vipver"] = "9.0.5.0", ["source"] = "kwplayer_pc_9.0.5.0", ["loginUid"] = "0",
                    ["loginSid"] = "0", ["appUid"] = "76039576"
                }), Json(new
                {
                    code = 200,
                    data = new object[] { new { id = 7, name = "官方分类", data = new[] { new { id = 81, digest = 10000, name = "官方标签" } } } }
                }));
                http.RegisterText(HttpMethod.Get, Url("http://wapi.kuwo.cn/api/pc/classify/playlist/getRcmTagList", new()
                {
                    ["loginUid"] = "0", ["loginSid"] = "0", ["appUid"] = "76039576"
                }), Json(new { code = 200, data = new[] { new { data = new[] { new { id = 9, digest = 10000, name = "官方热标签" } } } } }));
                break;

            case "kg":
                http.RegisterText(HttpMethod.Get, Url("http://www2.kugou.kugou.com/yueku/v9/special/getSpecial", new()
                {
                    ["is_smarty"] = "1", ["cdn"] = "cdn", ["t"] = "5"
                }), Json(new
                {
                    status = 1,
                    data = new
                    {
                        hotTag = new { data = new { special_id = "kg-hot", special_name = "官方热标签" } },
                        tagids = new { official = new { data = new[] { new { id = "official-tag-id", name = "官方标签" } } } }
                    }
                }));
                break;

            case "qq":
                http.RegisterText(HttpMethod.Get, QqCategoryUrl(), Json(new
                {
                    code = 0,
                    tags = new { data = new { v_group = new[] { new { group_id = 1, group_name = "官方分类", v_item = new[] { new { id = "official-tag-id", name = "官方标签" } } } } } }
                }));
                http.RegisterText(HttpMethod.Get, new Uri("https://c.y.qq.com/node/pc/wk_v15/category_playlist.html"), @"<a class=""c_bg_link js_tag_item"" data-id=""kg-hot"">官方热标签</a>");
                break;

            case "netease":
                http.RegisterText(HttpMethod.Post, new Uri("https://music.163.com/weapi/playlist/catalogue"), Json(new
                {
                    code = 200,
                    categories = new Dictionary<string, string> { ["1"] = "官方分类" },
                    sub = new[] { new { category = "1", name = "官方标签" } }
                }));
                http.RegisterText(HttpMethod.Post, new Uri("https://music.163.com/weapi/playlist/hottags"), Json(new
                {
                    code = 200,
                    tags = new[] { new { playlistTag = new { name = "官方热标签" } } }
                }));
                break;

            case "migu":
                http.RegisterText(HttpMethod.Get, new Uri("https://app.c.nf.migu.cn/pc/v1.0/template/musiclistplaza-taglist/release"), Json(new
                {
                    code = "000000",
                    data = new object[]
                    {
                        new { content = new[] { new { texts = new[] { "官方热标签", "kg-hot" } } } },
                        new { header = new { title = "官方分类" }, content = new[] { new { texts = new[] { "官方标签", "official-tag-id" } } } }
                    }
                }));
                break;
        }
    }

    private static Uri Url(string url, Dictionary<string, string> parameters) =>
        new(url + "?" + string.Join("&", parameters.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}")));

    private static Uri QqCategoryUrl() => Url("https://u.y.qq.com/cgi-bin/musicu.fcg", new()
    {
        ["loginUin"] = "0", ["hostUin"] = "0", ["format"] = "json", ["inCharset"] = "utf-8",
        ["outCharset"] = "utf-8", ["notice"] = "0", ["platform"] = "wk_v15.json", ["needNewCode"] = "0",
        ["data"] = "{\"tags\":{\"method\":\"get_all_categories\",\"param\":{\"qq\":\"\"},\"module\":\"playlist.PlaylistAllCategoriesServer\"}}"
    });

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
}
