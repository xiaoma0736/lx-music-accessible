using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class MolanSourceImportUsabilityTests
{
    private const string LocalScript = """
        /**
         * @name Molan compatibility fixture
         * @version 2.3.4
         */
        lx.on(lx.EVENT_NAMES.request, function () {
          return Promise.resolve('https://fixture.invalid/imported-audio.mp3');
        });
        lx.send(lx.EVENT_NAMES.inited, {
          sources: {
            kw: { type: 'music', actions: ['musicUrl'], qualitys: ['128k'] }
          }
        });
        """;

    [Fact]
    public async Task Imported_source_is_activated_as_default_and_persisted()
    {
        using var host = await TestApplicationHost.StartAsync();
        var sources = host.Services.GetRequiredService<MusicSourceService>();
        var scriptPath = Path.Combine(host.DataDirectory, "molan-source.js");
        await Task.Run(() => File.WriteAllText(scriptPath, LocalScript));

        await sources.InitializeScriptSourcesAsync();
        var imported = Assert.IsType<MusicSource>(await sources.ImportFromFileAsync(scriptPath));

        Assert.True(imported.IsEnabled);
        Assert.True(imported.IsDefault);
        Assert.Equal(imported.Id, sources.GetDefaultSource()?.Id);
        var persisted = Assert.Single(
            await host.Services.GetRequiredService<IDataService>().GetMusicSourcesAsync(),
            source => source.Id == imported.Id);
        Assert.True(persisted.IsEnabled);
        Assert.True(persisted.IsDefault);
    }

    [Fact]
    public async Task Local_import_uses_the_file_path_and_announces_the_usable_default_source()
    {
        var announcer = new RecordingSourceAnnouncer();
        var sources = new RouteRecordingSourceService();
        using var form = new SourceManagerForm(announcer, sources, _ => false);
        const string path = @"C:\fixture\molan-source.js";
        var method = typeof(SourceManagerForm).GetMethod(
            "ImportSourceAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        await RunImportAsync(method!, form, path);

        Assert.Empty(sources.UrlImports);
        Assert.Equal([path], sources.FileImports);
        Assert.Equal(["已导入并设为默认音源：Imported source"], announcer.Messages);
    }

    private static async Task RunImportAsync(MethodInfo method, Form form, string path)
    {
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(form, [path]));
        await task;
    }

    private sealed class RecordingSourceAnnouncer : IAccessibleAnnouncer
    {
        public List<AccessibleAnnouncement> Announcements { get; } = new();
        public List<string> Messages { get; } = new();
        public bool SpeechEnabled { get; set; } = true;
        public void Announce(AccessibleAnnouncement announcement)
        {
            Announcements.Add(announcement);
            Messages.Add(announcement.Text);
        }
    }

    private sealed class RouteRecordingSourceService : IMusicSourceService
    {
        private readonly List<MusicSource> _sources = new();
        private EventHandler? _changed;

        public event EventHandler? SourcesChanged
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public event EventHandler<LxScriptUpdateAlertEventArgs>? ScriptUpdateAlert
        {
            add { }
            remove { }
        }

        public List<string> UrlImports { get; } = new();
        public List<string> FileImports { get; } = new();

        public Task<MusicSource?> ImportFromUrlAsync(string url, CancellationToken cancellationToken = default)
        {
            UrlImports.Add(url);
            return Task.FromResult<MusicSource?>(null);
        }

        public Task<MusicSource?> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            FileImports.Add(filePath);
            var source = new MusicSource { Id = "script_imported", Name = "Imported source" };
            _sources.Add(source);
            return Task.FromResult<MusicSource?>(source);
        }

        public List<MusicSource> GetAllSources() => _sources.ToList();
        public List<MusicSource> GetEnabledSources() => _sources.Where(source => source.IsEnabled).ToList();
        public MusicSource? GetDefaultSource() => _sources.FirstOrDefault(source => source.IsDefault);

        public void Replace(params MusicSource[] source)
        {
            _sources.Clear();
            _sources.AddRange(source);
            _changed?.Invoke(this, EventArgs.Empty);
        }

        public Task<ScriptActivationResult> EnableSourceAsync(MusicSource source, CancellationToken cancellationToken = default) => Task.FromResult(new ScriptActivationResult(true, ScriptActivationStatus.Activated, "已启用"));
        public void DisableSource(MusicSource source) { }
        public Task<bool> DisableSourceAsync(MusicSource source, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void RemoveSource(MusicSource source) { }
        public Task<bool> RemoveSourceAsync(MusicSource source, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> UpdateSourceAsync(MusicSource source) => Task.FromResult(true);
        public Task<bool> RefreshSourceAsync(MusicSource source, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TestSourceAsync(MusicSource source) => Task.FromResult(true);
        public void SetDefaultSource(string id) { }
        public Task<bool> SetDefaultSourceAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<LxLyricRaw?> GetLyricFromScriptsAsync(MusicInfo music, CancellationToken cancellationToken = default) => Task.FromResult<LxLyricRaw?>(null);
        public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page = 1, int pageSize = 20, string searchType = "song", string sourceId = "all", CancellationToken cancellationToken = default) => Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public Task<string?> GetPlayUrlAsync(string musicId, string source, string quality = "128k", CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> GetPlayUrlAsync(MusicInfo music, string source, string quality = "128k", CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId = "netease", CancellationToken cancellationToken = default) => Task.FromResult(new List<PlaylistCategory>());
        public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page = 1, int pageSize = 30, string sourceId = "netease", CancellationToken cancellationToken = default) => Task.FromResult(PagedResult<Playlist>.Empty(page, pageSize));
        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public Task<List<Ranking>> GetRankingsAsync(string sourceId = "netease", CancellationToken cancellationToken = default) => Task.FromResult(new List<Ranking>());
        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
        public IMusicApi? GetApi(string id) => null;
        public Task<List<string>> GetQualitiesForSourceAsync(string sourceId) => Task.FromResult(new List<string>());
        public Task InitializeScriptSourcesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PersistSourcesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PersistSourcesOrThrowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
