using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class QualitySelectionRegressionTests
{
    private static readonly string[] ProductionQualities =
    [
        "128k", "192k", "320k", "flac", "flac24bit", "hires", "atmos",
        "atmos_plus", "master", "jyeffect", "sky", "dolby", "jymaster"
    ];
    private static readonly Uri LowResolve = new(
        "https://low-resolver.test/url?source=tx&songId=quality-song&quality=320k");
    private static readonly Uri HighResolve = new(
        "https://high-resolver.test/url?source=tx&songId=quality-song&quality=320k");
    private static readonly Uri LowAudio = new("https://audio.test/quality-song-128k.mp3");
    private static readonly Uri HighAudio = new("https://audio.test/quality-song-320k.mp3");

    [Fact]
    public async Task Download_skips_an_earlier_script_that_does_not_support_the_requested_quality()
    {
        using var host = await TestApplicationHost.StartAsync();
        host.HttpHandler.RegisterText(HttpMethod.Get, LowResolve, ResolverBody(LowAudio));
        host.HttpHandler.RegisterText(HttpMethod.Get, HighResolve, ResolverBody(HighAudio));
        host.HttpHandler.Register(HttpMethod.Get, LowAudio, () => AudioResponse("128k"));
        host.HttpHandler.Register(HttpMethod.Get, HighAudio, () => AudioResponse("320k"));
        var data = host.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync([
            TrustedScript("low", "https://low-resolver.test", ["128k"], 1),
            TrustedScript("high", "https://high-resolver.test", ["128k", "320k"], 2)
        ]);
        var sources = host.Services.GetRequiredService<MusicSourceService>();
        await sources.InitializeScriptSourcesAsync();
        var target = Directory.CreateDirectory(Path.Combine(host.DataDirectory, "downloads")).FullName;

        var task = await host.Services.GetRequiredService<IDownloadWorkflow>().DownloadAsync(new(
            new MusicInfo
            {
                Id = "quality-song",
                Songmid = "quality-song",
                Source = "qq",
                Title = "quality song",
                Artist = "fixture"
            },
            "320k",
            target));

        Assert.Equal(DownloadTaskStatus.Completed, task.Status);
        Assert.Contains(
            host.HttpHandler.Requests,
            request => request == (HttpMethod.Get.Method, HighResolve.AbsoluteUri));
        Assert.DoesNotContain(
            host.HttpHandler.Requests,
            request => request == (HttpMethod.Get.Method, LowResolve.AbsoluteUri));
        Assert.Contains("fixture-quality=320k", await Task.Run(() => File.ReadAllText(task.SavePath!)));
    }

    [Fact]
    public async Task Playback_rejects_a_stale_low_quality_direct_url_and_resolves_the_selected_quality()
    {
        var audio = new QualityAwareAudioPlayer();
        using var host = await TestApplicationHost.StartAsync(services =>
            services.Replace(ServiceDescriptor.Singleton<IAudioPlayer>(audio)));
        host.HttpHandler.RegisterText(HttpMethod.Get, LowResolve, ResolverBody(LowAudio));
        host.HttpHandler.RegisterText(HttpMethod.Get, HighResolve, ResolverBody(HighAudio));
        host.HttpHandler.Register(HttpMethod.Get, LowAudio, () => AudioResponse("128k"));
        host.HttpHandler.Register(HttpMethod.Get, HighAudio, () => AudioResponse("320k"));
        var data = host.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync([
            TrustedScript("low", "https://low-resolver.test", ["128k"], 1),
            TrustedScript("high", "https://high-resolver.test", ["128k", "320k"], 2)
        ]);
        await host.Services.GetRequiredService<MusicSourceService>().InitializeScriptSourcesAsync();
        var song = new MusicInfo
        {
            Id = "quality-song",
            Songmid = "quality-song",
            Source = "qq",
            Title = "quality song",
            Artist = "fixture",
            PlayUrl = LowAudio.AbsoluteUri
        };

        var result = await host.Services.GetRequiredService<IPlaybackCoordinator>().PlayAsync(new(
            [song],
            song,
            "320k",
            PlaybackQueueOrigin.Search));

        Assert.Equal(PlaybackResultStatus.Playing, result.Status);
        Assert.Equal([LowAudio.AbsoluteUri, HighAudio.AbsoluteUri], audio.LoadedUrls);
        Assert.Equal("320k", audio.GetActualQuality());
    }

    [Fact]
    public async Task Download_preserves_every_production_quality_from_script_token_to_media_bytes()
    {
        using var host = await TestApplicationHost.StartAsync();
        RegisterQualityMatrix(host);
        var data = host.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync([
            TrustedScript("matrix", "https://matrix-resolver.test", ProductionQualities, 1)
        ]);
        var sources = host.Services.GetRequiredService<MusicSourceService>();
        await sources.InitializeScriptSourcesAsync();
        var visible = sources.GetEnabledSources().Where(source => !source.IsSystemHidden).ToArray();
        Assert.Equal("matrix", visible.First().Id);
        Assert.Equal("bundled_verified_fallback", visible.Last().Id);
        var workflow = host.Services.GetRequiredService<IDownloadWorkflow>();

        foreach (var quality in ProductionQualities)
        {
            var target = Directory.CreateDirectory(
                Path.Combine(host.DataDirectory, "matrix-downloads", quality)).FullName;
            var task = await workflow.DownloadAsync(new(
                QualitySong(quality),
                quality,
                target));

            Assert.Equal(DownloadTaskStatus.Completed, task.Status);
            Assert.Equal(quality, task.Quality);
            Assert.Null(task.QualityFallback);
            Assert.Equal(ExpectedActualQuality(quality), task.ActualQuality);
            Assert.Contains(
                host.HttpHandler.Requests,
                request => request == (HttpMethod.Get.Method, ResolverUri(quality).AbsoluteUri));
            Assert.True(File.Exists(task.SavePath));
            Assert.Contains(
                $"fixture-quality={quality}",
                Encoding.UTF8.GetString(await Task.Run(() => File.ReadAllBytes(task.SavePath!))),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Playback_preserves_every_production_quality_through_resolution_and_audio_seam()
    {
        var audio = new QualityAwareAudioPlayer();
        using var host = await TestApplicationHost.StartAsync(services =>
            services.Replace(ServiceDescriptor.Singleton<IAudioPlayer>(audio)));
        RegisterQualityMatrix(host);
        var data = host.Services.GetRequiredService<IDataService>();
        await data.SaveMusicSourcesAsync([
            TrustedScript("matrix", "https://matrix-resolver.test", ProductionQualities, 1)
        ]);
        await host.Services.GetRequiredService<MusicSourceService>().InitializeScriptSourcesAsync();
        var playback = host.Services.GetRequiredService<IPlaybackCoordinator>();

        foreach (var quality in ProductionQualities)
        {
            var song = QualitySong(quality);
            var result = await playback.PlayAsync(new(
                [song],
                song,
                quality,
                PlaybackQueueOrigin.Search,
                ForceResolve: true));

            Assert.Equal(PlaybackResultStatus.Playing, result.Status);
            Assert.Equal(AudioUri(quality).AbsoluteUri, audio.LoadedUrls.Last());
            Assert.Equal(ExpectedActualQuality(quality), audio.GetActualQuality());
        }
    }

    [Fact]
    public void Settings_expose_the_same_complete_quality_token_set_for_playback_and_download()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "LXMusicAccessible.App", "SettingsWindow.xaml"));

        Assert.Equal(ProductionQualities, ComboTokens(xaml, "DefaultQualityCombo"));
        Assert.Equal(ProductionQualities, ComboTokens(xaml, "DownloadQualityCombo"));
    }

    [Theory]
    [InlineData("flac", "128k", false)]
    [InlineData("flac", "320k", false)]
    [InlineData("flac", "flac", true)]
    [InlineData("flac24bit", "flac", false)]
    [InlineData("flac24bit", "flac24bit", true)]
    [InlineData("hires", "flac24bit", false)]
    [InlineData("hires", "hires", true)]
    public void Download_quality_validation_never_accepts_a_lower_detected_tier(
        string requested,
        string actual,
        bool accepted) =>
        Assert.Equal(accepted, MusicQuality.AcceptsDownload(requested, actual));

    private static MusicSource TrustedScript(
        string id,
        string apiUrl,
        IReadOnlyList<string> qualities,
        int priority)
    {
        var qualityList = string.Join(",", qualities.Select(quality => $"'{quality}'"));
        var script = $$"""
            /** @name {{id}} @version 1.0.0 */
            const API_URL = '{{apiUrl}}';
            const MUSIC_QUALITY = { tx: [{{qualityList}}] };
            function handleGetMusicUrl(source, musicInfo, quality) {
              return `${API_URL}/url?source=${source}&songId=${musicInfo.songmid}&quality=${quality}`;
            }
            """;
        return new MusicSource
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            ScriptUrl = $"local://{id}",
            ScriptContent = script,
            IsEnabled = true,
            Priority = priority,
            Type = SourceType.Custom,
            TrustState = ScriptTrustState.UserApproved,
            ContentSha256 = ScriptMusicSource.ComputeSha256(script)
        };
    }

    private static string ResolverBody(Uri audio) =>
        System.Text.Json.JsonSerializer.Serialize(new { code = 200, url = audio.AbsoluteUri });

    private static void RegisterQualityMatrix(TestApplicationHost host)
    {
        foreach (var quality in ProductionQualities)
        {
            var audio = AudioUri(quality);
            host.HttpHandler.RegisterText(HttpMethod.Get, ResolverUri(quality), ResolverBody(audio));
            host.HttpHandler.Register(HttpMethod.Get, audio, () => AudioResponse(quality));
        }
    }

    private static MusicInfo QualitySong(string quality) => new()
    {
        Id = $"quality-song-{quality}",
        Songmid = $"quality-song-{quality}",
        Source = "qq",
        Title = $"quality song {quality}",
        Artist = "fixture"
    };

    private static Uri ResolverUri(string quality) => new(
        $"https://matrix-resolver.test/url?source=tx&songId=quality-song-{quality}&quality={quality}");

    private static Uri AudioUri(string quality) => new(
        $"https://audio.test/quality-song-{quality}.bin");

    private static string ExpectedActualQuality(string quality) => quality switch
    {
        "128k" or "192k" or "320k" or "flac" or "flac24bit" or "hires" => quality,
        _ => "flac"
    };

    private static string[] ComboTokens(string xaml, string comboName)
    {
        var start = xaml.IndexOf($"x:Name=\"{comboName}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing combo {comboName}");
        var end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Unclosed combo {comboName}");
        return Regex.Matches(xaml[start..end], "Tag=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static HttpResponseMessage AudioResponse(string quality)
    {
        var bytes = new byte[70_000];
        if (quality is "128k" or "192k" or "320k")
        {
            bytes[0] = 0xFF;
            bytes[1] = 0xFB;
            bytes[2] = quality switch
            {
                "128k" => 0x90,
                "192k" => 0xB0,
                _ => 0xE0
            };
            bytes[3] = 0x64;
        }
        else
        {
            "fLaC"u8.CopyTo(bytes);
            bytes[4] = 0;
            bytes[7] = 34;
            var sampleRate = quality == "hires" ? 96_000 : 44_100;
            var bitsPerSample = quality is "flac24bit" or "hires" ? 24 : 16;
            var streamInfo = ((ulong)sampleRate << 44)
                | ((ulong)(2 - 1) << 41)
                | ((ulong)(bitsPerSample - 1) << 36)
                | 1000UL;
            for (var index = 25; index >= 18; index--)
            {
                bytes[index] = (byte)streamInfo;
                streamInfo >>= 8;
            }
        }
        var marker = System.Text.Encoding.UTF8.GetBytes($"fixture-quality={quality}");
        marker.CopyTo(bytes, 64);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class QualityAwareAudioPlayer : IAudioPlayer
    {
        private string? _actual;
        public List<string> LoadedUrls { get; } = [];
        public double Volume { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
        public int CurrentOutputDeviceIndex => -1;
        public AudioPlayerState State { get; set; }
        public event EventHandler<AudioPlayerState>? StateChanged { add { } remove { } }
        public event EventHandler? PlaybackCompleted { add { } remove { } }

        public Task LoadAsync(string url, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedUrls.Add(url);
            _actual = ProductionQualities.FirstOrDefault(
                quality => url.Contains($"-{quality}.", StringComparison.Ordinal)) switch
            {
                "128k" => "128k",
                "192k" => "192k",
                "320k" => "320k",
                "flac24bit" => "flac24bit",
                "hires" => "hires",
                null => "128k",
                _ => "flac"
            };
            return Task.CompletedTask;
        }

        public Task PlayAsync()
        {
            State = AudioPlayerState.Playing;
            return Task.CompletedTask;
        }

        public void Pause() => State = AudioPlayerState.Paused;
        public void Stop() => State = AudioPlayerState.Stopped;
        public string? GetActualQuality() => _actual;
        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => [];
        public bool SwitchOutputDevice(int index) => false;
        public void SeekTo(TimeSpan position) => Position = position;
        public void Dispose() { }
    }
}
