using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>
/// Issue #67 (歌单、排行榜不能正常加载网易音乐内容) 的回归测试。
/// 真实根因有四个，全部用离线 fixture 固定：
/// 1. NeteaseCrypto 取的是 SubjectPublicKeyInfo DER 的前 128 字节而不是 RSA 模数，
///    导致 encSecKey 非法，网易云对 weapi 请求返回 HTTP 200 空响应（歌单、排行榜、分类全部加载失败）；
/// 2. linuxapi v6 playlist/detail 忽略 n/p/offset，只内嵌少量 tracks，完整顺序在 playlist.trackIds，
///    因此按 playlist.tracks 分页时第二页起为空、大歌单被截断；
/// 3. playlist/catalogue 的 sub[].category 是 JSON 数字，旧的字符串访问器会抛异常；
/// 4. 排行榜失败时回退到硬编码列表，MusicSourceService 又吞掉异常，界面只看到空列表而不是错误提示。
/// </summary>
public sealed class Issue67Tests
{
    private const int PageSize = 20;
    private const long PlaylistId = 5395389058L;
    private const long RankingId = 3778678L;
    private const int PlaylistTrackCount = 136;
    private const int RankingTrackCount = 200;
    private const int EmbeddedPlaylistTrackCount = 10;

    // 128 字节输入 0x01..0x80 用网易云公开模数做 RSA_NO_PADDING 加密的固定结果。
    private const string RsaVectorCipher =
        "551de1af96472ca98c41241e5251d67fae99d5542b77df02ae07adcf56e38a397cf29eb903a806cf83052bb671ec43eeb" +
        "419587040d469f197f87c79cf537e705393934e4908639b796caff50c9d6ee9c26e870e6c4a954b53dce06c53064b8bd" +
        "ce57897929a84daa19cb38683178ecf453dfaecb628c2ce396dfa55d8590a85";

    private const string V6PlaylistDetailUrl = "https://music.163.com/api/v6/playlist/detail";

    // NeteaseMusicService.DefaultHeaders: the official weapi/linuxapi request headers.
    private const string NeteaseUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/60.0.3112.90 Safari/537.36";

    private static readonly long[] PlaylistTrackIds = Enumerable
        .Range(0, PlaylistTrackCount).Select(index => 1_901_371_647L + index).ToArray();

    private static readonly long[] RankingTrackIds = Enumerable
        .Range(0, RankingTrackCount).Select(index => 2_600_000_000L + index).ToArray();

    private static readonly Uri LinuxForwardUri = new("https://music.163.com/api/linux/forward");
    private static readonly Uri SongDetailUri = new("https://music.163.com/weapi/v3/song/detail");
    private static readonly Uri PlaylistListUri = new("https://music.163.com/weapi/playlist/list");
    private static readonly Uri TopListUri = new("https://music.163.com/weapi/toplist");
    private static readonly Uri CatalogueUri = new("https://music.163.com/weapi/playlist/catalogue");
    private static readonly Uri HotTagsUri = new("https://music.163.com/weapi/playlist/hottags");

    [Fact]
    public void Netease_crypto_signs_weapi_payloads_with_the_published_rsa_modulus()
    {
        var input = new byte[128];
        for (var index = 0; index < input.Length; index++) input[index] = (byte)(index + 1);
        Assert.Equal(RsaVectorCipher, InvokeRsaEncryptRaw(input));

        var first = NeteaseCrypto.Weapi(new { cat = "华语", order = "hot", limit = PageSize, offset = 0 });
        var second = NeteaseCrypto.Weapi(new { cat = "华语", order = "hot", limit = PageSize, offset = 0 });
        Assert.NotEmpty(first["params"]);
        Assert.Matches("^[0-9a-f]{256}$", first["encSecKey"]);
        Assert.NotEqual(first["params"], second["params"]);
        Assert.NotEqual(first["encSecKey"], second["encSecKey"]);
    }

    [Fact]
    public void Linuxapi_forward_payload_is_aes_ecb_encrypted_with_the_linux_api_key()
    {
        var eparams = NeteaseCrypto.Linuxapi(new
        {
            method = "POST",
            url = V6PlaylistDetailUrl,
            @params = new { id = RankingId, n = PageSize, s = 8 }
        })["eparams"];

        using var payload = DecodeLinuxForwardPayload(eparams);
        Assert.Equal("POST", payload.RootElement.GetProperty("method").GetString());
        Assert.Equal(V6PlaylistDetailUrl, payload.RootElement.GetProperty("url").GetString());
        var parameters = payload.RootElement.GetProperty("params");
        Assert.Equal(RankingId, parameters.GetProperty("id").GetInt64());
        Assert.Equal(PageSize, parameters.GetProperty("n").GetInt32());
        Assert.Equal(8, parameters.GetProperty("s").GetInt32());
    }

    [Fact]
    public async Task Netease_categories_group_numeric_category_codes_and_keep_hot_tags()
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(http);
        handler.RegisterText(HttpMethod.Post, CatalogueUri, """
        {
          "code": 200,
          "categories": { "0": "语种", "1": "风格", "4": "主题" },
          "sub": [
            { "name": "综艺", "category": 4, "resourceCount": 5000, "hot": true, "type": 0 },
            { "name": "流行", "category": 1, "resourceCount": 22000, "hot": true, "type": 0 },
            { "name": "清晨", "category": 0, "resourceCount": 3200, "hot": false, "type": 0 }
          ]
        }
        """);
        handler.RegisterText(HttpMethod.Post, HotTagsUri, """
        { "code": 200, "tags": [ { "playlistTag": { "id": 100, "name": "华语" } }, { "playlistTag": { "id": 101, "name": "流行" } } ] }
        """);

        var categories = await service.GetPlaylistCategoriesAsync();

        Assert.Equal(4, categories.Count);
        Assert.Equal("recommend", categories[0].Id);
        Assert.Equal("推荐", categories[0].Name);
        Assert.Equal(new[] { "华语", "流行" }, categories[0].Tags.Select(tag => tag.Name));
        Assert.Equal(new[] { "4", "1", "0" }, categories.Skip(1).Select(category => category.Id));
        Assert.Equal(new[] { "主题", "风格", "语种" }, categories.Skip(1).Select(category => category.Name));
        Assert.Equal(new[] { "综艺" }, categories[1].Tags.Select(tag => tag.Name));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("""{ "code": 200, "categories": { "0": "语种" } }""")]
    [InlineData("""{ "code": 501, "message": "系统错误" }""")]
    public async Task Netease_category_failures_throw_instead_of_returning_an_empty_list(string cataloguePayload)
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(http);
        handler.RegisterText(HttpMethod.Post, CatalogueUri, cataloguePayload);
        handler.RegisterText(HttpMethod.Post, HotTagsUri, """{ "code": 200, "tags": [] }""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetPlaylistCategoriesAsync());

        Assert.Contains("playlist/catalogue", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Netease_category_playlists_are_paged_and_map_the_playlist_rows()
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(http);
        var requestCount = 0;
        handler.RegisterAsync(HttpMethod.Post, PlaylistListUri, _ =>
        {
            requestCount++;
            var payload = requestCount == 1
                ? """
                  {
                    "code": 200, "cat": "华语", "total": 260, "more": true,
                    "playlists": [
                      { "id": 5395389058, "name": "华语流行精选", "trackCount": 136, "playCount": 1452000,
                        "coverImgUrl": "https://p1.music.126.net/playlist-1.jpg", "description": "精选华语流行",
                        "tags": [ "华语", "流行" ] }
                    ]
                  }
                  """
                : """
                  {
                    "code": 200, "cat": "华语", "total": 260, "more": true,
                    "playlists": [
                      { "id": 2666429317, "name": "华语新歌速递", "trackCount": 48, "playCount": 880000,
                        "coverImgUrl": "https://p1.music.126.net/playlist-2.jpg", "description": "每日更新",
                        "tags": [ "华语" ] }
                    ]
                  }
                  """;
            return Task.FromResult(JsonResponse(payload));
        });

        var first = await service.GetPlaylistsByCategoryAsync("华语", 1, 1);
        var second = await service.GetPlaylistsByCategoryAsync("华语", 2, 1);

        var firstRow = Assert.Single(first.Items);
        Assert.Equal(260, first.TotalItems);
        Assert.Equal(260, second.TotalItems);
        Assert.Equal("netease_5395389058", firstRow.Id);
        Assert.Equal("华语流行精选", firstRow.Name);
        Assert.Equal(new[] { "华语", "流行" }, firstRow.Tags);
        Assert.Equal(PlaylistType.Recommend, firstRow.Type);
        Assert.Equal("netease_2666429317", Assert.Single(second.Items).Id);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("POST", request.Method));
    }

    [Theory]
    [InlineData("""{ "code": 200, "cat": "华语", "total": 0, "more": false }""")]
    [InlineData("""{ "code": 404, "message": "分类不存在" }""")]
    public async Task Netease_category_playlist_failures_throw_instead_of_returning_an_empty_page(string payload)
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(http);
        handler.RegisterText(HttpMethod.Post, PlaylistListUri, payload);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetPlaylistsByCategoryAsync("华语", 1, 30));

        Assert.Contains("playlist/list", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Netease_empty_category_page_stays_a_valid_empty_result()
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(http);
        handler.RegisterText(HttpMethod.Post, PlaylistListUri,
            """{ "code": 200, "cat": "清晨", "total": 0, "more": false, "playlists": [] }""");

        var result = await service.GetPlaylistsByCategoryAsync("清晨", 1, 30);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalItems);
    }

    [Fact]
    public async Task Netease_playlist_detail_pages_track_ids_and_resolves_the_rest_through_song_detail()
    {
        var handler = new RejectingHttpMessageHandler();
        var capture = new LinuxForwardCapturingHandler(handler);
        using var http = new MusicHttpClient(
            NullLogger<MusicHttpClient>.Instance, capture, new System.Net.CookieContainer());
        var service = CreateService(http);
        var embedded = PlaylistTrackIds.Take(EmbeddedPlaylistTrackCount).ToArray();
        handler.RegisterText(HttpMethod.Post, LinuxForwardUri, Json(new
        {
            code = 200,
            playlist = new
            {
                id = PlaylistId,
                name = "华语流行精选",
                trackCount = PlaylistTrackCount,
                trackIds = PlaylistTrackIds.Select(id => new { id, v = 1 }).ToArray(),
                tracks = embedded.Select((id, index) => SongRow(id, "嵌入曲目" + index, index)).ToArray()
            },
            privileges = new[]
            {
                new { id = PlaylistTrackIds[0], maxbr = 999000, maxBrLevel = "hires" },
                new { id = PlaylistTrackIds[1], maxbr = 320000, maxBrLevel = "exhigh" },
                new { id = PlaylistTrackIds[2], maxbr = 128000, maxBrLevel = "standard" }
            }
        }));
        var detailCalls = 0;
        handler.RegisterAsync(HttpMethod.Post, SongDetailUri, _ =>
        {
            // Page 1 only misses the ids the v6 payload did not embed; page 2 needs the whole slice.
            var batch = detailCalls == 0
                ? PlaylistTrackIds.Skip(EmbeddedPlaylistTrackCount).Take(PageSize - EmbeddedPlaylistTrackCount).ToArray()
                : PlaylistTrackIds.Skip(PageSize).Take(PageSize).ToArray();
            detailCalls++;
            return Task.FromResult(JsonResponse(Json(new
            {
                code = 200,
                songs = batch.Select((id, index) => SongRow(id, "详情曲目-" + id, index)).ToArray(),
                privileges = batch.Select(id => new
                {
                    id,
                    maxbr = id == PlaylistTrackIds[PageSize] ? 999000 : 128000,
                    maxBrLevel = id == PlaylistTrackIds[PageSize] ? "hires" : "standard"
                }).ToArray()
            })));
        });

        var first = await service.GetPlaylistDetailAsync(PlaylistId.ToString(), 1, PageSize);
        var second = await service.GetPlaylistDetailAsync(PlaylistId.ToString(), 2, PageSize);

        Assert.Equal(PlaylistTrackCount, first.TotalItems);
        Assert.Equal(PlaylistTrackCount, second.TotalItems);
        Assert.Equal(PageSize, first.Items.Count);
        Assert.Equal(PageSize, second.Items.Count);
        Assert.Equal(
            PlaylistTrackIds.Take(PageSize).Select(id => id.ToString()),
            first.Items.Select(item => item.Id));
        Assert.Equal(
            PlaylistTrackIds.Skip(PageSize).Take(PageSize).Select(id => id.ToString()),
            second.Items.Select(item => item.Id));

        var embeddedRow = first.Items[0];
        Assert.Equal("嵌入曲目0", embeddedRow.Title);
        Assert.Equal("歌手0", embeddedRow.Artist);
        Assert.Equal("专辑0", embeddedRow.Album);
        Assert.Equal("03:00", embeddedRow.Duration);
        Assert.Equal("https://p1.music.126.net/pic0.jpg", embeddedRow.CoverUrl);
        Assert.Equal("netease", embeddedRow.Source);
        Assert.Equal(999, embeddedRow.Quality);
        Assert.Equal(320, first.Items[1].Quality);
        Assert.Equal(128, first.Items[2].Quality);
        // Rows 1-10 come from the v6 payload; rows 11-20 are resolved through /v3/song/detail.
        Assert.Equal("嵌入曲目9", first.Items[9].Title);
        Assert.Equal("详情曲目-" + PlaylistTrackIds[10], first.Items[10].Title);
        Assert.Equal(999, second.Items[0].Quality);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(2, detailCalls);
        Assert.Equal(2, handler.Requests.Count(request => request.Uri == LinuxForwardUri.AbsoluteUri));

        Assert.Equal(2, capture.EncryptedPayloads.Count);
        using var forwarded = DecodeLinuxForwardPayload(capture.EncryptedPayloads[0]);
        Assert.Equal(V6PlaylistDetailUrl, forwarded.RootElement.GetProperty("url").GetString());
        Assert.Equal(PlaylistId, forwarded.RootElement.GetProperty("params").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Netease_ranking_detail_pages_track_ids_and_reuses_embedded_tracks()
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(http);
        handler.RegisterText(HttpMethod.Post, LinuxForwardUri, Json(new
        {
            code = 200,
            playlist = new
            {
                id = RankingId,
                name = "热歌榜",
                trackCount = RankingTrackCount,
                updateTime = 1_789_628_315_403L,
                trackIds = RankingTrackIds.Select(id => new { id, v = 0 }).ToArray(),
                tracks = RankingTrackIds.Take(PageSize)
                    .Select((id, index) => SongRow(id, "榜内曲目" + index, index)).ToArray()
            },
            privileges = new[]
            {
                new { id = RankingTrackIds[0], maxbr = 999000, maxBrLevel = "hires" },
                new { id = RankingTrackIds[1], maxbr = 320000, maxBrLevel = "exhigh" }
            }
        }));
        var detailCalls = 0;
        handler.RegisterAsync(HttpMethod.Post, SongDetailUri, _ =>
        {
            var batch = RankingTrackIds.Skip((detailCalls + 1) * PageSize).Take(PageSize).ToArray();
            detailCalls++;
            return Task.FromResult(JsonResponse(Json(new
            {
                code = 200,
                songs = batch.Select((id, index) => SongRow(id, "榜单详情-" + id, index)).ToArray(),
                privileges = batch.Select(id => new { id, maxbr = 128000, maxBrLevel = "standard" }).ToArray()
            })));
        });

        var first = await service.GetRankingDetailAsync(RankingId.ToString(), 1, PageSize);

        Assert.Equal(RankingTrackCount, first.TotalItems);
        Assert.Equal(
            RankingTrackIds.Take(PageSize).Select(id => id.ToString()),
            first.Items.Select(item => item.Id));
        Assert.Equal("榜内曲目0", first.Items[0].Title);
        Assert.Equal(999, first.Items[0].Quality);
        Assert.Equal(320, first.Items[1].Quality);
        Assert.Equal(128, first.Items[2].Quality);
        // 整页都已内嵌在 v6 详情里，不得再请求 /v3/song/detail。
        Assert.Single(handler.Requests);
        Assert.Equal(0, detailCalls);

        var second = await service.GetRankingDetailAsync(RankingId.ToString(), 2, PageSize);

        Assert.Equal(RankingTrackCount, second.TotalItems);
        Assert.Equal(
            RankingTrackIds.Skip(PageSize).Take(PageSize).Select(id => id.ToString()),
            second.Items.Select(item => item.Id));
        Assert.Equal("榜单详情-" + RankingTrackIds[PageSize], second.Items[0].Title);
        // Page 1 only needed the v6 detail; page 2 added the v6 detail plus one /v3/song/detail call.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, handler.Requests.Count(request => request.Uri == LinuxForwardUri.AbsoluteUri));
        Assert.Equal(1, detailCalls);
    }

    [Fact]
    public async Task Netease_browse_requests_use_the_official_headers_and_encrypted_forms()
    {
        var handler = new RejectingHttpMessageHandler();
        var capture = new RequestShapeCapturingHandler(handler);
        using var http = new MusicHttpClient(
            NullLogger<MusicHttpClient>.Instance, capture, new System.Net.CookieContainer());
        var service = CreateService(http);
        handler.RegisterText(HttpMethod.Post, TopListUri, """{ "code": 200, "list": [] }""");
        handler.RegisterText(HttpMethod.Post, LinuxForwardUri, Json(new
        {
            code = 200,
            playlist = new
            {
                id = RankingId,
                trackCount = 0,
                tracks = Array.Empty<object>(),
                trackIds = Array.Empty<object>()
            }
        }));

        await service.GetRankingsAsync();
        await service.GetRankingDetailAsync(RankingId.ToString(), 1, PageSize);

        Assert.Equal(2, capture.Shapes.Count);
        var weapi = capture.Shapes[0];
        Assert.Equal(TopListUri.AbsoluteUri, weapi.Uri);
        Assert.Equal("https://music.163.com/", weapi.Header("Referer"));
        Assert.Equal("https://music.163.com", weapi.Header("Origin"));
        Assert.Equal(NeteaseUserAgent, weapi.Header("User-Agent"));
        Assert.Equal("application/x-www-form-urlencoded", weapi.MediaType);
        Assert.Matches("^[0-9a-f]{256}$", weapi.Field("encSecKey"));
        Assert.NotEmpty(weapi.Field("params"));

        var linux = capture.Shapes[1];
        Assert.Equal(LinuxForwardUri.AbsoluteUri, linux.Uri);
        Assert.Equal("https://music.163.com/", linux.Header("Referer"));
        Assert.Equal(NeteaseUserAgent, linux.Header("User-Agent"));
        Assert.Equal("application/x-www-form-urlencoded", linux.MediaType);
        Assert.NotEmpty(linux.Field("eparams"));
    }

    [Fact]
    public async Task Netease_rankings_list_maps_the_real_toplists()
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(http);
        handler.RegisterText(HttpMethod.Post, TopListUri, """
        {
          "code": 200,
          "artistToplist": { "name": "歌手榜", "coverUrl": "https://p1.music.126.net/artist.jpg" },
          "list": [
            { "id": 3778678, "name": "热歌榜", "coverImgUrl": "https://p1.music.126.net/hot.jpg",
              "updateFrequency": "每周四更新", "updateTime": 1789628315403, "trackCount": 200 },
            { "id": 19723756, "name": "飙升榜", "coverImgUrl": "https://p1.music.126.net/rising.jpg",
              "updateFrequency": "刚刚更新", "updateTime": 1789628315403, "trackCount": 100 },
            { "id": 3779629, "name": "新歌榜", "coverImgUrl": "https://p1.music.126.net/new.jpg",
              "updateFrequency": "每日更新", "updateTime": 1789628315403, "trackCount": 100 },
            { "id": 2884035, "name": "原创榜", "coverImgUrl": "https://p1.music.126.net/original.jpg",
              "updateFrequency": "每周四更新", "updateTime": 1789628315403, "trackCount": 100 },
            { "id": 71384707, "name": "Netease 古典榜", "coverImgUrl": "https://p1.music.126.net/classic.jpg",
              "updateFrequency": "每周更新", "updateTime": 1789628315403, "trackCount": 100 }
          ]
        }
        """);

        var rankings = await service.GetRankingsAsync();

        Assert.Equal(5, rankings.Count);
        Assert.Equal(
            new[] { "netease_3778678", "netease_19723756", "netease_3779629", "netease_2884035", "netease_71384707" },
            rankings.Select(ranking => ranking.Id));
        Assert.Equal(
            new[] { RankingType.Hot, RankingType.Rising, RankingType.New, RankingType.Original, RankingType.Other },
            rankings.Select(ranking => ranking.Type));
        Assert.Equal("刚刚更新", rankings[1].Description);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789628315403L).DateTime, rankings[1].UpdateDate);
        Assert.All(rankings, ranking => Assert.Equal("netease", ranking.Source));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("""{ "code": 501, "message": "系统错误" }""")]
    [InlineData("""{ "code": 200, "artistToplist": { "name": "歌手榜" } }""")]
    public async Task Netease_ranking_failures_throw_instead_of_a_hardcoded_list(string payload)
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(http);
        handler.RegisterText(HttpMethod.Post, TopListUri, payload);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetRankingsAsync());

        Assert.Contains("toplist", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Netease_empty_toplist_stays_a_valid_empty_result()
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(http);
        handler.RegisterText(HttpMethod.Post, TopListUri, """{ "code": 200, "list": [] }""");

        var rankings = await service.GetRankingsAsync();

        Assert.Empty(rankings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Netease_detail_failures_surface_the_error_instead_of_an_empty_page(bool rankingDetail)
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(http);
        handler.RegisterText(HttpMethod.Post, LinuxForwardUri,
            """{ "code": 404, "message": "歌单不存在", "msg": "歌单不存在" }""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rankingDetail
                ? service.GetRankingDetailAsync("0", 1, PageSize)
                : service.GetPlaylistDetailAsync("0", 1, PageSize));

        Assert.Contains("code=404", exception.Message, StringComparison.Ordinal);
        Assert.Contains("歌单不存在", exception.Message, StringComparison.Ordinal);
        Assert.Contains(V6PlaylistDetailUrl, exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Netease_detail_without_a_playlist_throws_instead_of_an_empty_page(bool rankingDetail)
    {
        var handler = new RejectingHttpMessageHandler();
        using var http = CreateHttp(handler);
        var service = CreateService(http);
        handler.RegisterText(HttpMethod.Post, LinuxForwardUri, """{ "code": 200 }""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rankingDetail
                ? service.GetRankingDetailAsync("0", 1, PageSize)
                : service.GetPlaylistDetailAsync("0", 1, PageSize));

        Assert.Contains("code=200", exception.Message, StringComparison.Ordinal);
        Assert.Contains("id=0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("未返回歌单数据", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    private static object SongRow(long id, string name, int index) => new
    {
        id,
        name,
        ar = new[] { new { id = 1000L + index, name = "歌手" + index } },
        al = new
        {
            id = 2000L + index,
            name = "专辑" + index,
            picUrl = "https://p1.music.126.net/pic" + index + ".jpg"
        },
        dt = 180000 + index * 1000
    };

    private static MusicHttpClient CreateHttp(RejectingHttpMessageHandler handler) =>
        new(NullLogger<MusicHttpClient>.Instance, handler, new System.Net.CookieContainer());

    private static NeteaseMusicService CreateService(MusicHttpClient http) =>
        new(http, NullLogger<NeteaseMusicService>.Instance);

    private static HttpResponseMessage JsonResponse(string content) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(content) };

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static string InvokeRsaEncryptRaw(byte[] data)
    {
        var method = typeof(NeteaseCrypto)
            .GetMethod("RsaEncryptRaw", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("NeteaseCrypto.RsaEncryptRaw was not found.");
        return (string)(method.Invoke(null, new object[] { data }) ?? string.Empty);
    }

    private static JsonDocument DecodeLinuxForwardPayload(string eparams)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.UTF8.GetBytes("rFgB&h#%2?^eDg:Q");
        using var decryptor = aes.CreateDecryptor();
        var bytes = new byte[eparams.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = Convert.ToByte(eparams.Substring(index * 2, 2), 16);
        var plain = decryptor.TransformFinalBlock(bytes, 0, bytes.Length);
        return JsonDocument.Parse(plain);
    }

    /// <summary>
    /// 记录 linuxapi 请求正文的装饰器：weapi 正文无法解密（RSA 单向），但 linuxapi 正文用固定密钥
    /// AES-ECB 加密，因此可以解出真正转发给网易云的内层 URL 与 id，用来固定 v6 详情端点。
    /// </summary>
    private sealed class LinuxForwardCapturingHandler : DelegatingHandler
    {
        public LinuxForwardCapturingHandler(RejectingHttpMessageHandler inner) : base(inner)
        {
        }

        public List<string> EncryptedPayloads { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri == LinuxForwardUri && request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync();
                var match = Regex.Match(body, "eparams=([0-9A-Fa-f]+)");
                if (match.Success) EncryptedPayloads.Add(match.Groups[1].Value);
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Records the request shape (URI, headers, media type and form fields) of every outgoing call so the
    /// tests can pin the official Netease headers and encrypted form bodies.
    /// </summary>
    private sealed class RequestShapeCapturingHandler : DelegatingHandler
    {
        public RequestShapeCapturingHandler(RejectingHttpMessageHandler inner) : base(inner)
        {
        }

        public List<CapturedRequest> Shapes { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri && request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync();
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in request.Headers)
                {
                    // HTTP keeps multi-token headers such as User-Agent as several values.
                    headers[header.Key] = string.Join(" ", header.Value);
                }
                Shapes.Add(new CapturedRequest(
                    uri.AbsoluteUri,
                    headers,
                    request.Content.Headers.ContentType?.MediaType ?? string.Empty,
                    ParseForm(body)));
            }
            return await base.SendAsync(request, cancellationToken);
        }

        private static Dictionary<string, string> ParseForm(string body)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in body.Split('&'))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0) continue;
                values[pair[..separator]] = Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
            return values;
        }
    }

    private sealed record CapturedRequest(
        string Uri,
        IReadOnlyDictionary<string, string> Headers,
        string MediaType,
        IReadOnlyDictionary<string, string> Form)
    {
        public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;

        public string Field(string name) => Form.TryGetValue(name, out var value) ? value : string.Empty;
    }
}
