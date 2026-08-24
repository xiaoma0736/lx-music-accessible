using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using LXMusicAccessible.App;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ApplicationHostTests
{
    [Fact]
    public async Task Host_validates_singletons_and_creates_accessible_secondary_windows()
    {
        await using var app = await TestApplicationHost.StartAsync();

        Assert.Same(
            app.Services.GetRequiredService<IDataService>(),
            app.Services.GetRequiredService<IDataService>());
        Assert.Same(
            app.Services.GetRequiredService<IMusicSourceService>(),
            app.Services.GetRequiredService<IMusicSourceService>());
        Assert.Same(
            app.Services.GetRequiredService<IMusicSourceService>(),
            app.Services.GetRequiredService<IMusicBrowseSource>());
        Assert.Same(
            app.Services.GetRequiredService<MusicHttpClient>(),
            app.Services.GetRequiredService<MusicHttpClient>());
        Assert.Same(
            app.Announcer,
            app.Services.GetRequiredService<IAccessibleAnnouncer>());

        var windows = app.Services.GetRequiredService<ApplicationWindowFactory>();
        Assert.Same(windows, app.Services.GetRequiredService<ApplicationWindowFactory>());

        await RunOnStaThreadAsync(() =>
        {
            var guide = windows.CreateUserGuide(owner: null);
            var sponsor = windows.CreateSponsor(owner: null);
            var lyrics = windows.CreateLyrics(_ => false);
            try
            {
                Assert.Empty(AutomationProperties.GetName(guide));
                Assert.Equal("使用说明", guide.Title);
                Assert.Equal("使用说明正文", AutomationProperties.GetName(guide.GuideText));
                Assert.Empty(AutomationProperties.GetName(sponsor));
                Assert.Equal("赞助二维码", sponsor.Title);
                Assert.Empty(AutomationProperties.GetName(sponsor.CloseBtn));
                Assert.Equal("关闭", sponsor.CloseBtn.Content);
                Assert.Empty(AutomationProperties.GetName(lyrics));
                Assert.Equal("歌词", lyrics.Title);
                Assert.Equal("歌词", AutomationProperties.GetName(lyrics.LyricsList));

                guide.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, guide));
                Assert.False(string.IsNullOrWhiteSpace(guide.GuideText.Text));
                Assert.NotEmpty(guide.ChapterCombo.Items);
                guide.ChapterCombo.SelectedIndex = 0;

                sponsor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, sponsor));
            }
            finally
            {
                lyrics.PrepareForAppShutdown();
                lyrics.Close();
                sponsor.Close();
                guide.Close();
            }
        });

        Assert.Contains(app.Announcer.Messages, message => message.StartsWith("已跳转到章节：", StringComparison.Ordinal));
        Assert.Contains(app.Announcer.Messages, message => message.StartsWith("赞助二维码已显示", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Host_disposes_sync_and_async_dependencies_exactly_once()
    {
        var syncProbe = new SyncDisposeProbe();
        var asyncProbe = new AsyncDisposeProbe();
        var app = await TestApplicationHost.StartAsync(services =>
        {
            services.AddSingleton(_ => syncProbe);
            services.AddSingleton(_ => asyncProbe);
        });

        _ = app.Services.GetRequiredService<SyncDisposeProbe>();
        _ = app.Services.GetRequiredService<AsyncDisposeProbe>();
        await app.DisposeAsync();
        await app.DisposeAsync();

        Assert.Equal(1, syncProbe.DisposeCount);
        Assert.Equal(1, asyncProbe.DisposeCount);
    }

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

            var history = app.Services.GetRequiredService<LocalSearchHistoryStore>();
            await history.SaveAsync([new SearchHistory { Keyword = "fixture" }]);
            Assert.Equal("fixture", Assert.Single(await history.LoadAsync()).Keyword);
            Assert.True(File.Exists(Path.Combine(
                dataDirectory,
                "file-storage",
                "search_history.json")));

            var accessibility = app.Services.GetRequiredService<IAccessibleAnnouncer>();
            var speech = app.Services.GetRequiredService<ITtsService>();
            Assert.Same(app.Announcer, accessibility);
            Assert.Same(app.Speech, speech);
            accessibility.Announce("host ready");
            await speech.SpeakAsync("speech recorded");
            Assert.Equal(new[] { "host ready" }, app.Announcer.Messages);
            Assert.Equal(new[] { "speech recorded" }, app.Speech.Messages);

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

            var playback = app.Services.GetRequiredService<IPlaybackCoordinator>();
            var player = app.Services.GetRequiredService<IAudioPlaybackControl>();
            Assert.Same(playback, player);
            playback.Stop();
            Assert.False(playback.IsPlaying);
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

    private static Task RunOnStaThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class SyncDisposeProbe : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class AsyncDisposeProbe : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
