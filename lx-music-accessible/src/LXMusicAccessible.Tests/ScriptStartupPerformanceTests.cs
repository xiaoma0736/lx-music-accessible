using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ScriptStartupPerformanceTests
{
    [Theory]
    [InlineData("lx.request('https://fixture.invalid/startup', { timeout: 30000 }, function (error) { globalThis.__requestError = String(error && error.message || error); });")]
    [InlineData("globalThis.fetch('https://fixture.invalid/startup').catch(function (error) { globalThis.__requestError = String(error && error.message || error); });")]
    [InlineData("try { define(function (require, module, httpFetch) { httpFetch('https://fixture.invalid/startup').catch(function (error) { globalThis.__requestError = String(error && error.message || error); }); }); } catch (error) { globalThis.__requestError = String(error && error.message || error); }")]
    public async Task Source_validation_skips_optional_top_level_http_requests(string networkCall)
    {
        using var handler = new RejectingHttpMessageHandler();
        var engine = new LxScriptEngine(
            NullLogger<LxScriptEngine>.Instance,
            new ScriptNetworkClient(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })));
        var script = new LxScriptInfo
        {
            Id = "startup-background",
            Name = "Startup background",
            RawScript = """
                globalThis.__requestError = '';
                lx.on(lx.EVENT_NAMES.request, function () {
                  return Promise.resolve('https://fixture.invalid/audio.mp3');
                });
                """
                + networkCall
                + """
                lx.send(lx.EVENT_NAMES.inited, {
                  status: true,
                  sources: {
                    tx: { type: 'music', actions: ['musicUrl'], qualitys: ['128k'] }
                  }
                });
                """
        };

        var loaded = await engine.TestLoadAsync(script);

        Assert.True(loaded, engine.LastError ?? "script load failed");
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Jint_fallback_validation_also_skips_top_level_http_requests()
    {
        using var handler = new RejectingHttpMessageHandler();
        var engine = new LxScriptEngine(
            NullLogger<LxScriptEngine>.Instance,
            new ScriptNetworkClient(handler, (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })));
        var script = new LxScriptInfo
        {
            Id = "jint-startup-background",
            Name = "Jint startup background",
            RawScript = """
                lx.on(lx.EVENT_NAMES.request, function () {
                  return Promise.resolve('https://fixture.invalid/audio.mp3');
                });
                lx.request('https://fixture.invalid/startup', { timeout: 30000 }, function (error) {
                  globalThis.__requestError = String(error && error.message || error);
                });
                lx.send(lx.EVENT_NAMES.inited, {
                  status: true,
                  sources: {
                    tx: { type: 'music', actions: ['musicUrl'], qualitys: ['128k'] }
                  }
                });
                """
        };
        var method = typeof(LxScriptEngine).GetMethod(
            "ExecuteJintLoad",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var loaded = Assert.IsType<bool>(method!.Invoke(
            engine,
            [script, CancellationToken.None]));

        Assert.True(loaded, engine.LastError ?? "Jint script load failed");
        Assert.Empty(handler.Requests);
    }
}
