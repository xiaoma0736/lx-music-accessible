using System.Diagnostics;
using System.IO;
using System.Net.Http;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ApplicationHostTests
{
    [Fact]
    public async Task Host_uses_isolated_data_and_recording_external_adapters()
    {
        string dataDirectory;

        await using (var app = await TestApplicationHost.StartAsync())
        {
            dataDirectory = app.DataDirectory;
            var relativeToTemp = Path.GetRelativePath(Path.GetTempPath(), dataDirectory);
            Assert.False(relativeToTemp.StartsWith("..", StringComparison.Ordinal));
            Assert.False(Path.IsPathRooted(relativeToTemp));
            Assert.True(File.Exists(Path.Combine(dataDirectory, "lxmusic.db")));

            var data = app.Services.GetRequiredService<IDataService>();
            await data.SetSettingValueAsync("test.host-smoke", "ready");
            Assert.Equal("ready", await data.GetSettingValueAsync("test.host-smoke"));
            Assert.NotEmpty(await data.GetPlaylistsAsync());

            var fileStorage = app.Services.GetRequiredService<IDataStorageService>();
            await fileStorage.SaveUserSettingsAsync("fixture", new { Ready = true });
            Assert.True(File.Exists(Path.Combine(
                dataDirectory,
                "file-storage",
                "user_settings.json")));

            var accessibility = app.Services.GetRequiredService<IAccessibilityService>();
            var speech = app.Services.GetRequiredService<ITtsService>();
            Assert.Same(app.Announcer, accessibility);
            Assert.Same(app.Announcer, speech);
            accessibility.Announce("host ready");
            await speech.SpeakAsync("speech recorded");
            Assert.Equal(new[] { "host ready", "speech recorded" }, app.Announcer.Messages);

            var launcher = app.Services.GetRequiredService<IProcessLauncher>();
            Assert.Same(app.ProcessLauncher, launcher);
            launcher.Start(new ProcessStartInfo("browser.exe", "https://example.invalid")
            {
                UseShellExecute = true
            });
            var invocation = Assert.Single(app.ProcessLauncher.Invocations);
            Assert.Equal("browser.exe", invocation.FileName);

            var audio = app.Services.GetRequiredService<IAudioPlayer>();
            Assert.Same(app.AudioPlayer, audio);
            await audio.PlayAsync();
            Assert.Equal(AudioPlayerState.Playing, audio.State);

            var player = app.Services.GetRequiredService<IMusicPlayerService>();
            player.Stop();
            Assert.False(player.IsPlaying);
        }

        Assert.False(Directory.Exists(dataDirectory));
    }

    [Fact]
    public async Task Unknown_http_requests_are_blocked()
    {
        await using var app = await TestApplicationHost.StartAsync();
        var client = app.Services.GetRequiredService<MusicHttpClient>();
        var registeredUri = new Uri("https://fixture.invalid/registered");
        app.HttpHandler.RegisterText(HttpMethod.Get, registeredUri, "fixture response");

        Assert.Equal("fixture response", await client.GetAsync(registeredUri.AbsoluteUri));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetAsync("https://example.com/not-registered"));
        Assert.Contains("Unregistered HTTP request was blocked", exception.Message);
        Assert.Equal(2, app.HttpHandler.Requests.Count);
    }
}
