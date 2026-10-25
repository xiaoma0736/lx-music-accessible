using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class LxScriptRuntimeCompatibilityTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");

    [Theory]
    [InlineData("musicUrl")]
    [InlineData("pic")]
    [InlineData("lyric")]
    public async Task Request_handler_receives_the_original_payload_and_valid_actions_return_contract_results(string action)
    {
        var engine = CreateEngine();
        const string body = """
            lx.on(lx.EVENT_NAMES.request, function (request) {
              if (request.action !== globalThis.__act || request.source !== 'tx') return Promise.reject(new Error('wrong dispatch'));
              if (request.action === 'musicUrl' && request.info.type !== '320k') return Promise.reject(new Error('bad type'));
              if (request.action !== 'musicUrl' && request.info.musicInfo.id !== 'song') return Promise.reject(new Error('bad music info'));
              if (request.action === 'musicUrl') return Promise.resolve('https://fixture.invalid/audio.mp3');
              if (request.action === 'pic') return Promise.resolve('https://fixture.invalid/cover.jpg');
              return Promise.resolve({ lyric: 'line', tlyric: 'translation', rlyric: 'romanization', lxlyric: 'lx' });
            });
            """;
        var script = CreateScript(body.Replace("globalThis.__act", "'" + action + "'", StringComparison.Ordinal));
        var music = new MusicInfo { Id = "song", Title = "Title", Artist = "Artist" };

        object? actual = action switch
        {
            "musicUrl" => await engine.GetPlayUrlAsync(script, music, "qq", "320k"),
            "pic" => await engine.GetPicAsync(script, music, "qq"),
            _ => await engine.GetLyricAsync(script, music, "qq")
        };

        Assert.Null(engine.LastFailure);
        if (action == "lyric")
        {
            var lyric = Assert.IsType<LxLyricRaw>(actual);
            Assert.Equal("line", lyric.Lyric);
            Assert.Equal("translation", lyric.Tlyric);
            Assert.Equal("romanization", lyric.Rlyric);
            Assert.Equal("lx", lyric.Lxlyric);
        }
        else
        {
            Assert.Equal("https://fixture.invalid/" + (action == "musicUrl" ? "audio.mp3" : "cover.jpg"), actual);
        }
    }

    [Theory]
    [InlineData("musicUrl", "unsafe-url")]
    [InlineData("musicUrl", "oversize")]
    [InlineData("pic", "unsafe-url")]
    [InlineData("pic", "oversize")]
    [InlineData("lyric", "empty")]
    [InlineData("lyric", "oversize-tlyric")]
    [InlineData("lyric", "oversize-rlyric")]
    [InlineData("lyric", "oversize-lxlyric")]
    public async Task Action_results_outside_the_original_contract_map_to_a_safe_failure(
        string action,
        string caseName)
    {
        var engine = CreateEngine();
        var response = ResponseFor(caseName);
        var script = CreateScript(
            $"lx.on(lx.EVENT_NAMES.request, function () {{ return Promise.resolve({response}); }});");
        var music = new MusicInfo { Id = "song", Title = "Title" };

        object? actual = action switch
        {
            "musicUrl" => await engine.GetPlayUrlAsync(script, music, "qq", "128k"),
            "pic" => await engine.GetPicAsync(script, music, "qq"),
            _ => await engine.GetLyricAsync(script, music, "qq")
        };

        Assert.Null(actual);
        Assert.Equal("script_result_invalid", engine.LastError);
    }

    private static string ResponseFor(string caseName) => caseName switch
    {
        "unsafe-url" => "'ftp://fixture.invalid/file'",
        "oversize" => "'" + new string('x', 2049) + "'",
        "empty" => "{}",
        "oversize-tlyric" => "{ lyric: 'ok', tlyric: '" + new string('x', 5121) + "' }",
        "oversize-rlyric" => "{ lyric: 'ok', rlyric: '" + new string('x', 5121) + "' }",
        "oversize-lxlyric" => "{ lyric: 'ok', lxlyric: '" + new string('x', 8193) + "' }",
        _ => throw new ArgumentOutOfRangeException(nameof(caseName))
    };

    [Fact]
    public async Task Runtime_action_and_quality_declarations_restrict_playback_dispatch()
    {
        var engine = CreateEngine();
        var script = CreateScript("""
            lx.on(lx.EVENT_NAMES.request, function (request) {
              return Promise.resolve(request.action === 'lyric'
                ? { lyric: 'ok' }
                : 'https://fixture.invalid/audio.mp3');
            });
            lx.send(lx.EVENT_NAMES.inited, {
              sources: {
                tx: { type: 'music', actions: ['lyric'], qualitys: ['128k'] },
                wy: { type: 'music', actions: ['musicUrl'], qualitys: ['128k'] }
              }
            });
            """);
        var music = new MusicInfo { Id = "song", Title = "Title" };

        Assert.True(await engine.TestLoadAsync(script), engine.LastError);
        Assert.Equal(["qq", "netease"], script.SupportedSources);
        Assert.Equal(["lyric"], script.ActionMap["qq"].ToArray());
        Assert.Equal(["musicUrl"], script.ActionMap["netease"].ToArray());
        Assert.Null(await engine.GetPlayUrlAsync(script, music, "qq", "128k"));
        Assert.Equal("action_not_declared", engine.LastError);
        Assert.NotNull(await engine.GetLyricAsync(script, music, "qq"));

        Assert.Null(await engine.GetLyricAsync(script, music, "netease"));
        Assert.Equal("action_not_declared", engine.LastError);
        Assert.Equal("https://fixture.invalid/audio.mp3", await engine.GetPlayUrlAsync(script, music, "netease", "128k"));
    }

    [Fact]
    public async Task Update_alert_requires_a_log_and_limits_its_size_and_update_url()
    {
        var engine = CreateEngine();
        var events = new List<LxScriptUpdateAlertEventArgs>();
        engine.UpdateAlert += (_, e) => events.Add(e);
        var script = CreateScript("""
            lx.on(lx.EVENT_NAMES.request, function () { return Promise.resolve(null); });
            lx.send(lx.EVENT_NAMES.updateAlert, {
              log: 'released'.padEnd(1100, '!'),
              updateUrl: 'file:///unsafe/update.exe'
            });
            lx.send(lx.EVENT_NAMES.updateAlert, { log: 'safe', updateUrl: 'https://fixture.invalid/update' });
            """);

        Assert.True(await engine.TestLoadAsync(script), engine.LastError);

        var alert = Assert.Single(events);
        Assert.True(alert.Log.Length == 1027, alert.Log.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.StartsWith("released", alert.Log, StringComparison.Ordinal);
        Assert.EndsWith("...", alert.Log);
        Assert.Null(alert.UpdateUrl);
    }

    [Theory]
    [InlineData("body", "{\"id\":\"song\",\"quality\":\"320k\"}", "application/json")]
    [InlineData("form", "id=song&quality=320k&note=a%20b", "application/x-www-form-urlencoded")]
    public async Task Request_bridge_sends_headers_and_object_or_form_bodies(
        string bodyKind,
        string expectedBody,
        string expectedContentType)
    {
        var uri = new Uri("https://public.test/request");
        using var handler = new RecordingHttpHandler();
        handler.RegisterJson(HttpMethod.Post, uri, """{"value":"ok"}""");
        var engine = CreateEngine(handler);
        var payload = bodyKind == "body"
            ? "body: { id: 'song', quality: '320k' }"
            : "form: { id: 'song', quality: '320k', note: 'a b' }";
        var body = $$"""
            lx.on(lx.EVENT_NAMES.request, function () {
            return new Promise(function (resolve, reject) {
              lx.request('{{uri}}', {
                method: 'POST',
                headers: { 'X-Fixture': 'present', 'Content-Type': '{{expectedContentType}}' },
                {{payload}}
              }, function (error, response) {
                if (error) reject(error); else resolve(response.body.value);
              });
            });
            });
            """;
        var scriptInfo = new LxScriptInfo { Id = "request", Name = "request", RawScript = body };

        await engine.GetPlayUrlAsync(scriptInfo, new MusicInfo(), "qq", "128k");

        Assert.True(handler.Snapshots.Count == 1, engine.LastError ?? "no request");
        var request = handler.Snapshots.Single();
        Assert.Equal("present", request.Headers["X-Fixture"]);
        Assert.StartsWith(expectedContentType, request.ContentType, StringComparison.Ordinal);
        Assert.Equal(expectedBody, request.Content);
    }

    [Fact]
    public async Task FormData_bridge_sends_a_safe_multipart_payload()
    {
        var uri = new Uri("https://public.test/upload");
        using var handler = new RecordingHttpHandler();
        handler.RegisterJson(HttpMethod.Post, uri, """{"value":"ok"}""");
        var engine = CreateEngine(handler);
        var body = $$"""
            lx.on(lx.EVENT_NAMES.request, function () {
            return new Promise(function (resolve, reject) {
              lx.request('{{uri}}', {
                method: 'POST',
                formData: { id: 'song', quality: '320k' }
              }, function (error, response) {
                if (error) reject(error); else resolve(response.body.value);
              });
            });
            });
            """;
        var scriptInfo = new LxScriptInfo { Id = "form-data", Name = "form-data", RawScript = body };

        await engine.GetPlayUrlAsync(scriptInfo, new MusicInfo(), "qq", "128k");

        Assert.True(handler.Snapshots.Count == 1, engine.LastError ?? "no request");
        var request = handler.Snapshots.Single();
        Assert.StartsWith("multipart/form-data; boundary=", request.ContentType, StringComparison.Ordinal);
        Assert.Contains("Content-Disposition: form-data; name=\"id\"\r\n\r\nsong", request.Content, StringComparison.Ordinal);
        Assert.EndsWith("\r\n--" + request.ContentType.Split('=')[1] + "--", request.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirected_binary_response_shapes_are_preserved()
    {
        var start = new Uri("https://public.test/start");
        var binary = new Uri("https://public.test/binary");
        var bytes = Encoding.UTF8.GetBytes("fixture-bytes");
        using var handler = new RejectingHttpMessageHandler();
        handler.Register(HttpMethod.Get, start, () => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = binary }
        });
        handler.Register(HttpMethod.Get, binary, () =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
            content.Headers.TryAddWithoutValidation("X-Fixture", "binary");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var engine = CreateEngine(handler);
        var body = """
            lx.on(lx.EVENT_NAMES.request, function () {
            return new Promise(function (resolve, reject) {
              lx.request('https://public.test/start', {}, function (error, response) {
                if (error) { reject(error); return; }
                resolve({
                  lyric: lx.utils.bufToString(response.raw, 'hex'),
                  tlyric: response.statusCode + '|' + response.headers['X-Fixture']
                });
              });
            });
            });
            """;
        var scriptInfo = new LxScriptInfo { Id = "binary", Name = "binary", RawScript = body };

        var lyric = await engine.GetLyricAsync(scriptInfo, new MusicInfo(), "qq");

        Assert.True(lyric is not null, engine.LastError ?? "no lyric");
        Assert.Equal("200|binary", lyric.Tlyric);
    }

    [Fact]
    public async Task Runtime_crypto_buffer_and_zlib_apis_use_real_boundaries()
    {
        using var rsa = RSA.Create(1024);
        var pem = CreateSpkiPem(rsa.ExportParameters(false));
        var engine = CreateEngine();
        var jsPem = pem.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("'", "\\'");
        var body = $$"""
            lx.on(lx.EVENT_NAMES.request, async function () {
            var crypto = lx.utils.crypto;
            var encrypted = crypto.rsaEncrypt(Buffer.from('abc'), '{{jsPem}}');
            var key = Buffer.from('0123456789abcdef');
            var iv = Buffer.from('0123456789abcdef');
            var aes = crypto.aesEncrypt(Buffer.from('abc'), 'aes-128-cbc', key, iv);
            var roundTrip = crypto.aesDecrypt(aes, 'aes-128-cbc', key, iv);
            var compressed = await lx.utils.zlib.deflate(Buffer.from('abc'));
            var expanded = await lx.utils.zlib.inflate(compressed);
            return Promise.resolve({
              lyric: [
                crypto.md5('abc'),
                crypto.sha1('abc'),
                crypto.sha256('abc'),
                crypto.sha512('abc')
              ].join('|'),
              tlyric: encrypted.length + '|' + encrypted[127],
              rlyric: lx.utils.bufToString(roundTrip, 'utf8') + '|' + lx.utils.bufToString(expanded, 'utf8'),
              lxlyric: crypto.randomBytes(32).length + '|' + Buffer.from('aGk=', 'base64').toString('utf8')
            });
            });
            """;
        var script = CreateScript(body);

        var lyric = await engine.GetLyricAsync(script, new MusicInfo(), "qq");

        Assert.True(lyric is not null, engine.LastError ?? "no lyric");
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72|a9993e364706816aba3e25717850c26c9cd0d89d|ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad|ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f", lyric.Lyric);
        Assert.StartsWith("128|", lyric.Tlyric);
        Assert.Equal("abc|abc", lyric.Rlyric);
        Assert.Equal("32|hi", lyric.Lxlyric);
    }

    [Fact]
    public async Task Unsupported_crypto_algorithm_maps_to_safe_failure()
    {
        var engine = CreateEngine();
        var script = CreateScript("""
            lx.on(lx.EVENT_NAMES.request, function () {
              return cryptoOperation();
            });
            function cryptoOperation() {
              try {
                lx.utils.crypto.aesEncrypt(Buffer.from('abc'), 'aes-128-gcm', Buffer.alloc(16), Buffer.alloc(16));
                return Promise.resolve({ lyric: 'unexpected' });
              } catch (error) {
                return Promise.resolve({ lyric: error.message });
              }
            }
            """);
        var lyric = await engine.GetLyricAsync(script, new MusicInfo(), "qq");
        Assert.NotNull(lyric);
        Assert.Equal("crypto_operation_unsupported", lyric!.Lyric);
    }

    [Fact]
    public async Task Script_metadata_and_commonjs_amd_shims_are_exposed()
    {
        const string body = """
            /**
             * @name Metadata fixture
             * @description A compatibility source
             * @version 3.4.5
             * @author Fixture Author
             * @homepage https://fixture.invalid/author
             */
            lx.on(lx.EVENT_NAMES.request, function () {
              var moduleValue = require('lx-music-source') === lx;
              var amdValue = define('compat', ['require', 'httpFetch'], function (require, httpFetch) {
                return typeof httpFetch;
              });
              return Promise.resolve({
                lyric: [
                  lx.currentScriptInfo.name,
                  lx.currentScriptInfo.description,
                  lx.currentScriptInfo.version,
                  lx.currentScriptInfo.author,
                  lx.currentScriptInfo.homepage,
                  typeof window,
                  typeof navigator.userAgent,
                  moduleValue,
                  amdValue,
                  lx.currentScriptInfo.rawScript.indexOf('@author') >= 0
                ].join('|')
              });
            });
            """;
        var source = new ScriptMusicSource(NullLogger<ScriptMusicSource>.Instance)
            .StageFromContent(body, "compat", "fixture", isLocal: true);
        Assert.NotNull(source);
        var engine = new LxScriptEngine(NullLogger<LxScriptEngine>.Instance);

        var lyric = await engine.GetLyricAsync(source, new MusicInfo(), "qq");

        Assert.True(lyric is not null, engine.LastError ?? "no lyric");
        Assert.Equal("Metadata fixture|A compatibility source|3.4.5|Fixture Author|https://fixture.invalid/author|object|string|true|function|true", lyric!.Lyric);
    }

    [Fact]
    public async Task Failed_inited_status_is_rejected_by_the_original_runtime_contract()
    {
        var engine = CreateEngine();
        var script = CreateScript("""
            lx.on(lx.EVENT_NAMES.request, function () {
              return Promise.reject(new Error('not usable'));
            });
            lx.send(lx.EVENT_NAMES.inited, {
              status: false,
              sources: {
                tx: { type: 'music', actions: ['musicUrl'], qualitys: ['128k'] }
              }
            });
            """);

        var loaded = await engine.TestLoadAsync(script);

        Assert.False(loaded);
        Assert.NotNull(engine.LastError);
    }

    private static LxScriptEngine CreateEngine(HttpMessageHandler? handler = null)
    {
        var actualHandler = handler ?? new RejectingHttpMessageHandler();
        return new LxScriptEngine(
            NullLogger<LxScriptEngine>.Instance,
            new ScriptNetworkClient(actualHandler, (_, _) => Task.FromResult(new[] { PublicAddress })));
    }

    private static LxScriptInfo CreateScript(string body) => new()
    {
        Id = "runtime-compatibility",
        Name = "Runtime compatibility",
        RawScript = body
    };

    private sealed record HttpRequestSnapshot(
        string Method,
        string Uri,
        string ContentType,
        string Content,
        IReadOnlyDictionary<string, string> Headers);

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly List<HttpRequestSnapshot> _snapshots = new();
        private readonly Dictionary<(string Method, string Uri), HttpResponseMessage> _responses = new();

        public IReadOnlyList<HttpRequestSnapshot> Snapshots => _snapshots;

        public void RegisterJson(HttpMethod method, Uri uri, string json)
        {
            _responses[(method.Method, uri.AbsoluteUri)] = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri?.AbsoluteUri ?? "";
            var content = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var headers = request.Headers.ToDictionary(
                pair => pair.Key,
                pair => string.Join(",", pair.Value),
                StringComparer.OrdinalIgnoreCase);
            _snapshots.Add(new HttpRequestSnapshot(request.Method.Method, uri, request.Content?.Headers.ContentType?.ToString() ?? "", content, headers));
            if (!_responses.TryGetValue((request.Method.Method, uri), out var response))
                throw new InvalidOperationException("Unregistered fixture request: " + uri);
            return Task.FromResult(response);
        }
    }

    private static string CreateSpkiPem(RSAParameters parameters)
    {
        Span<byte> der = stackalloc byte[163];
        der[0] = 0x30; der[1] = 0x81; der[2] = 0x9f;
        der[3] = 0x30; der[4] = 0x0d; der[5] = 0x06; der[6] = 0x09; der[7] = 0x2a; der[8] = 0x86; der[9] = 0x48; der[10] = 0x86; der[11] = 0xf7; der[12] = 0x0d; der[13] = 0x01; der[14] = 0x01; der[15] = 0x01; der[16] = 0x05; der[17] = 0x00;
        der[18] = 0x03; der[19] = 0x81; der[20] = 0x8d; der[21] = 0x00; der[22] = 0x30; der[23] = 0x81; der[24] = 0x89; der[25] = 0x02; der[26] = 0x81; der[27] = 0x81;
        parameters.Modulus!.AsSpan().CopyTo(der.Slice(29, 128));
        der[156] = 0x00; der[157] = 0x02; der[158] = 0x03; der[159] = 0x01; der[160] = 0x00; der[161] = 0x01;
        return "-----BEGIN PUBLIC KEY-----\\n" + Convert.ToBase64String(der.ToArray()) + "\\n-----END PUBLIC KEY-----";
    }

}