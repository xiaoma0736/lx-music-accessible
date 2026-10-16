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
    public void Source_changed_before_handle_is_created_does_not_throw_from_import_event()
    {
        var announcer = new RecordingSourceAnnouncer();
        var sources = new RouteRecordingSourceService();
        using var form = new SourceManagerForm(announcer, sources, _ => false);

        Assert.Null(Record.Exception(() => sources.Replace(new MusicSource
        {
            Id = "background_imported",
            Name = "Background source",
            IsDefault = true,
            IsEnabled = true
        })));
        Assert.Empty(announcer.Messages);
    }

    [Fact]
    public void Script_update_alert_is_announced_without_a_modal_dialog()
    {
        var announcer = new RecordingSourceAnnouncer();
        var sources = new RouteRecordingSourceService();
        using var form = new SourceManagerForm(announcer, sources, _ => false);

        sources.RaiseScriptUpdateAlert(new LxScriptUpdateAlertEventArgs
        {
            Log = "fixed source endpoint",
            UpdateUrl = "https://fixture.invalid/source.js"
        });

        var message = Assert.Single(announcer.Messages);
        Assert.Contains("音源更新：fixed source endpoint", message);
        Assert.Contains("https://fixture.invalid/source.js", message);
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

    [Fact]
    public async Task Delete_key_removes_the_selected_source_after_keyboard_confirmation_flow()
    {
        var announcer = new RecordingSourceAnnouncer();
        var target = new MusicSource
        {
            Id = "delete_target",
            Name = "Deletable source",
            IsEnabled = true,
            IsDefault = true
        };
        var sources = new RouteRecordingSourceService();
        using var form = new SourceManagerForm(announcer, sources, _ => false);
        var list = (ListBox)typeof(SourceManagerForm).GetField(
            "_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
        list.Items.Clear();
        list.Items.Add(new ObjectBox<MusicSource>(target.AccessibleName, target));
        list.SelectedIndex = 0;
        var handled = await form.HandleSourceListKeyAsync(
            new KeyEventArgs(Keys.Delete), null, requireConfirmation: false);

        Assert.True(handled);
        Assert.Equal(["delete_target"], sources.Removed);
        Assert.Contains("已删除音源：Deletable source", announcer.Messages);
    }

    [Theory]
    [InlineData(MusicSourceImportStatus.Imported, "已导入并设为默认音源：Imported source")]
    [InlineData(MusicSourceImportStatus.Merged, "已合并并设为默认音源：Imported source")]
    [InlineData(MusicSourceImportStatus.Replaced, "已用高版本替换并设为默认音源：Imported source")]
    [InlineData(MusicSourceImportStatus.VersionConflict, "导入音源失败：已存在相同或更高版本的 Imported source，保留当前音源。")]
    public async Task Detailed_import_results_have_explicit_announcements(
        MusicSourceImportStatus status,
        string expectedMessage)
    {
        var announcer = new RecordingSourceAnnouncer();
        var sources = new RouteRecordingSourceService
        {
            DetailedStatus = status,
            Imported = new MusicSource
            {
                Id = "imported_status",
                Name = "Imported source",
                IsEnabled = true,
                IsDefault = status != MusicSourceImportStatus.VersionConflict
            }
        };
        using var form = new SourceManagerForm(announcer, sources, _ => false);
        var method = typeof(SourceManagerForm).GetMethod(
            "ImportSourceAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        await RunImportAsync(method!, form, @"C:\fixture\molan-source.js");

        Assert.Contains(expectedMessage, announcer.Messages);
    }

    [Fact]
    public async Task Physical_delete_key_uses_the_destructive_confirmation_workflow()
    {
        var announcer = new RecordingSourceAnnouncer();
        var target = new MusicSource { Id = "delete_target", Name = "Deletable source" };
        var sources = new RouteRecordingSourceService();
        var confirmationMessages = new List<string>();
        using var form = new SourceManagerForm(
            announcer,
            sources,
            _ => false,
            message =>
            {
                confirmationMessages.Add(message);
                return true;
            });
        var list = (ListBox)typeof(SourceManagerForm).GetField(
            "_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
        list.Items.Clear();
        list.Items.Add(new ObjectBox<MusicSource>(target.AccessibleName, target));
        list.SelectedIndex = 0;

        var handled = await form.HandleSourceListKeyAsync(new KeyEventArgs(Keys.Delete));

        Assert.True(handled);
        var confirmation = Assert.Single(confirmationMessages);
        Assert.Contains("Deletable source", confirmation);
        Assert.Equal(["delete_target"], sources.Removed);
        Assert.Contains("已删除音源：Deletable source", announcer.Messages);
    }

    [Fact]
    public async Task Merged_or_replaced_source_that_is_not_default_does_not_claim_default()
    {
        var announcer = new RecordingSourceAnnouncer();
        var sources = new RouteRecordingSourceService
        {
            DetailedStatus = MusicSourceImportStatus.Replaced,
            Imported = new MusicSource
            {
                Id = "non_default_replacement",
                Name = "Imported source",
                IsEnabled = false,
                IsDefault = false
            }
        };
        using var form = new SourceManagerForm(announcer, sources, _ => false);
        var method = typeof(SourceManagerForm).GetMethod(
            "ImportSourceAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        await RunImportAsync(method!, form, @"C:\fixture\molan-source.js");

        Assert.Contains("已用高版本替换音源：Imported source", announcer.Messages);
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

        private EventHandler<LxScriptUpdateAlertEventArgs>? _scriptUpdateAlert;

        public event EventHandler<LxScriptUpdateAlertEventArgs>? ScriptUpdateAlert
        {
            add => _scriptUpdateAlert += value;
            remove => _scriptUpdateAlert -= value;
        }

        public List<string> UrlImports { get; } = new();
        public List<string> FileImports { get; } = new();
        public List<string> Removed { get; } = new();
        public MusicSource? Imported { get; set; }
        public MusicSourceImportStatus DetailedStatus { get; set; } = MusicSourceImportStatus.Imported;

        public async Task<MusicSource?> ImportFromUrlAsync(string url, CancellationToken cancellationToken = default) =>
            (await ImportFromUrlDetailedAsync(url, cancellationToken))?.Source;

        public Task<MusicSourceImportResult?> ImportFromUrlDetailedAsync(string url, CancellationToken cancellationToken = default)
        {
            UrlImports.Add(url);
            if (Imported is null) return Task.FromResult<MusicSourceImportResult?>(null);
            if (!_sources.Any(source => source.Id == Imported.Id)) _sources.Add(Imported);
            return Task.FromResult<MusicSourceImportResult?>(new(Imported, DetailedStatus));
        }

        public async Task<MusicSource?> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default) =>
            (await ImportFromFileDetailedAsync(filePath, cancellationToken))?.Source;

        public Task<MusicSourceImportResult?> ImportFromFileDetailedAsync(string filePath, CancellationToken cancellationToken = default)
        {
            FileImports.Add(filePath);
            Imported ??= new MusicSource { Id = "script_imported", Name = "Imported source" };
            if (!_sources.Any(source => source.Id == Imported.Id)) _sources.Add(Imported);
            return Task.FromResult<MusicSourceImportResult?>(new(Imported, DetailedStatus));
        }

        public List<MusicSource> GetAllSources() => _sources.ToList();
        public List<MusicSource> GetEnabledSources() => _sources.Where(source => source.IsEnabled).ToList();
        public MusicSource? GetDefaultSource() => _sources.FirstOrDefault(source => source.IsDefault);

        public void RaiseScriptUpdateAlert(LxScriptUpdateAlertEventArgs args) =>
            _scriptUpdateAlert?.Invoke(this, args);

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
        public Task<bool> RemoveSourceAsync(MusicSource source, CancellationToken cancellationToken = default)
        {
            Removed.Add(source.Id);
            _sources.Remove(source);
            return Task.FromResult(true);
        }
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
