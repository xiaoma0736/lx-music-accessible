using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>Regressions for playlist audit remediation and local-music playlist entry.</summary>
public sealed class PlaylistRemediationTests
{
    [Fact]
    public async Task Kugou_personal_playlists_load_every_page()
    {
        using var http = new MusicHttpClient(NullLogger<MusicHttpClient>.Instance, new KugouPagingHandler());
        var provider = new KugouPlaylistProvider(http, NullLogger<KugouPlaylistProvider>.Instance);
        SetPrivate(provider, "_uid", "123");
        SetPrivate(provider, "_token", "token");

        var playlists = await provider.GetMyPlaylistsAsync();

        // Created playlists: page 1 has 100 items and page 2 has 2. Collected playlists add one.
        Assert.Equal(103, playlists.Count);
        Assert.Equal(101, playlists.Count(item => !item.IsCollected));
        Assert.Equal(2, playlists.Count(item => item.IsCollected));
    }

    [Fact]
    public async Task Snapshot_import_rejects_duplicate_remote_keys()
    {
        using var database = await TemporaryDatabase.CreateAsync();
        var playlist = new Playlist
        {
            Name = "Remote",
            Type = PlaylistType.User,
            Source = "netease",
            RemoteId = "netease_1",
            IsSnapshot = true
        };
        await database.Data.SavePlaylistAsync(playlist);

        playlist.Id = Guid.NewGuid().ToString();
        playlist.Name = "Remote copy";
        await Assert.ThrowsAsync<SqliteException>(() => database.Data.SavePlaylistAsync(playlist));
    }

    [Fact]
    public async Task Remote_favorite_rejects_duplicate_source_and_remote_id()
    {
        using var database = await TemporaryDatabase.CreateAsync();
        var playlist = new Playlist
        {
            Name = "Favorite",
            Type = PlaylistType.Recommend,
            Source = "qq",
            RemoteId = "qq_1"
        };
        await database.Data.SavePlaylistAsync(playlist);

        playlist.Id = Guid.NewGuid().ToString();
        playlist.Name = "Favorite copy";
        await Assert.ThrowsAsync<SqliteException>(() => database.Data.SavePlaylistAsync(playlist));
    }

    [Fact]
    public async Task Saved_playlist_load_includes_local_playlists_after_online_playlists()
    {
        using var database = await TemporaryDatabase.CreateAsync();
        foreach (var defaultPlaylist in await database.Data.GetPlaylistsAsync().WaitAsync(TimeSpan.FromSeconds(5)))
            await database.Data.DeletePlaylistAsync(defaultPlaylist.Id);
        var online = new Playlist { Name = "Online", Type = PlaylistType.User, IsLocal = false };
        var local = new Playlist { Name = "My local", Type = PlaylistType.System, IsLocal = true };
        await database.Data.SavePlaylistAsync(online);
        await database.Data.SavePlaylistAsync(local);

        var playlists = new List<Playlist>();
        playlists.AddRange(await database.Data.GetFavoritePlaylistsAsync(false));
        playlists.AddRange(await database.Data.GetFavoritePlaylistsAsync(true));

        Assert.Equal(new[] { online.Id, local.Id }, playlists.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Local_only_add_songs_selects_and_activates_local_playlist()
    {
        using var database = await TemporaryDatabase.CreateAsync();
        var browse = CreateBrowse();
        var saved = new SavedPlaylistWorkflow(database.Data, browse);
        var song = new MusicInfo { Id = @"Local|C:\music\a.mp3", Title = "A", Source = "Local", PlayUrl = @"C:\music\a.mp3" };

        var result = await saved.AddSongsAsync(new[] { song });

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(result.Snapshot.Selected);
        Assert.True(result.Snapshot.Selected!.IsLocal);
        Assert.Equal("我的本地歌曲", result.Snapshot.Selected.Name);
        Assert.Single(result.Snapshot.Songs);
        Assert.Equal("A", result.Snapshot.Songs[0].Title);
    }

    [Fact]
    public async Task Snapshot_import_filters_songs_without_stable_identity_or_title()
    {
        using var database = await TemporaryDatabase.CreateAsync();
        var provider = new StaticProvider(new[]
        {
            new MusicInfo { Id = "valid", Title = "Valid", Source = "netease" },
            new MusicInfo { Id = "", Title = "No id", Source = "netease" },
            new MusicInfo { Id = "no-title", Title = "", Source = "netease" }
        });
        var service = new PlaylistImportService(new[] { provider }, database.Data, NullLogger<PlaylistImportService>.Instance);
        var messages = new List<string>();
        var remote = new RemotePlaylist { Platform = "netease", RemoteId = "1", Name = "Remote" };

        var result = await service.ImportPlaylistAsync("netease", remote, messages.Add);

        var playlist = (await database.Data.GetPlaylistsAsync()).Single(item => item.IsSnapshot);
        var songs = await database.Data.GetPlaylistItemsAsync(playlist.Id);
        Assert.Equal(new[] { "valid" }, songs.Select(item => item.Id).ToArray());
        Assert.Contains("已跳过 2 首无效歌曲。", messages);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void Import_form_escape_returns_from_preview_before_closing()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var service = new FakeImportService();
            using var form = new PlaylistImportForm(service, new RecordingAnnouncer(), new NoLoginFactory(), _ => { });
            form.Show();
            Application.DoEvents();
            SetPrivate(form, "_platform", "netease");
            SetPrivate(form, "_remotePlaylists", new List<RemotePlaylist> { service.Playlists[0] });
            PlaylistList(form).Items.Add(new ObjectBox<RemotePlaylist>(service.Playlists[0].Name, service.Playlists[0]));
            PlaylistList(form).SelectedIndex = 0;

            var preview = InvokePrivateAsync(form, "PreviewAsync");
            service.PreviewStarted.TrySetResult(null);
            service.ReleasePreview.SetResult(null);
            await service.ReleasePreview.Task;
            await preview;
            Application.DoEvents();

            InvokeKeyDown(form, Keys.Escape);
            Application.DoEvents();
            Assert.False(form.IsDisposed, "First Escape in song preview closed the import form.");
            Assert.True(form.Controls.Find("个人歌单", true)[0].Focused, "First Escape did not return focus to playlists.");

            InvokeKeyDown(form, Keys.Escape);
            Application.DoEvents();
            Assert.True(form.IsDisposed, "Second Escape at playlist level did not close the import form.");
        });
    }

    [Fact]
    public void Import_command_ignores_reentrant_activation()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var service = new FakeImportService();
            using var form = new PlaylistImportForm(service, new RecordingAnnouncer(), new NoLoginFactory(), _ => { });
            form.Show();
            Application.DoEvents();
            SetPrivate(form, "_platform", "netease");
            PlaylistList(form).Items.Add(new ObjectBox<RemotePlaylist>(service.Playlists[0].Name, service.Playlists[0]));
            PlaylistList(form).SelectedIndex = 0;

            var first = InvokePrivateAsync(form, "ImportAsync");
            var second = InvokePrivateAsync(form, "ImportAsync");
            service.ImportStarted.TrySetResult(null);
            service.ReleaseImport.SetResult(null);
            await first;
            await second;

            Assert.Single(service.ImportCalls);
        });
    }

    [Fact]
    public async Task Account_restore_waits_for_one_inflight_restore()
    {
        using var database = await TemporaryDatabase.CreateAsync();
        var provider = new BlockingRestoreProvider();
        await database.Data.SaveAccountAsync(new AccountInfo { Platform = "netease", Uid = "1", DisplayName = "Restored", AuthData = "{}" });
        var service = new PlaylistImportService(new[] { provider }, database.Data, NullLogger<PlaylistImportService>.Instance);

        var first = service.EnsureAccountsLoadedAsync();
        var second = service.EnsureAccountsLoadedAsync();
        provider.RestoreStarted.TrySetResult(null);
        provider.ReleaseRestore.SetResult(null);
        await first;
        await second;

        Assert.Equal(1, provider.LoadAccountCalls);
    }

    [Fact]
    public void Playlist_page_exposes_local_music_commands()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = new RecordingAnnouncer(),
                BrowseSource = new EmptyBrowseSource(),
                Playback = new NoPlayback(),
                TemporaryDirectory = Path.Combine(Path.GetTempPath(), "playlist-local-ui-" + Guid.NewGuid().ToString("N"))
            });

            var fileButton = shell.Controls.Find("打开本地文件", true).SingleOrDefault();
            var folderButton = shell.Controls.Find("打开本地文件夹", true).SingleOrDefault();
            Assert.NotNull(fileButton);
            Assert.NotNull(folderButton);
            Assert.Equal("打开本地文件", fileButton!.AccessibleName);
            Assert.Equal("打开本地文件夹", folderButton!.AccessibleName);
            Assert.Equal(10, fileButton.TabIndex);
            Assert.Equal(11, folderButton.TabIndex);
        });
    }

    [Fact]
    public void Local_playlist_paths_become_stable_songs_and_skip_unsupported_files()
    {
        var songs = WinFormsShell.CreateLocalMusicInfosForTests(new[]
        {
            @"C:\music\one.MP3",
            @"C:\music\readme.txt",
            @"C:\music\two.flac"
        }, out var skipped);

        Assert.Equal(2, songs.Count);
        Assert.Equal(1, skipped);
        Assert.Equal(@"Local|C:\music\one.MP3", songs[0].Id);
        Assert.Equal("one", songs[0].Title);
        Assert.Equal("Local", songs[0].Source);
        Assert.Equal(@"C:\music\one.MP3", songs[0].PlayUrl);
        Assert.Equal(@"Local|C:\music\two.flac", songs[1].Id);
    }
    [Fact]
    public async Task Activation_messages_identify_where_playlist_data_came_from()
    {
        using var database = await TemporaryDatabase.CreateAsync();
        foreach (var defaultPlaylist in await database.Data.GetPlaylistsAsync())
            await database.Data.DeletePlaylistAsync(defaultPlaylist.Id);

        var local = new Playlist { Name = "Local songs", Type = PlaylistType.System, IsLocal = true };
        var snapshot = new Playlist
        {
            Name = "Imported", Type = PlaylistType.User, Source = "netease", RemoteId = "netease_1", IsSnapshot = true
        };
        var cachedFavorite = new Playlist
        {
            Name = "Cached", Type = PlaylistType.User, Source = "qq", RemoteId = "qq_1"
        };
        var remoteFavorite = new Playlist
        {
            Name = "Remote", Type = PlaylistType.User, Source = "kw", RemoteId = "kw_1"
        };
        foreach (var playlist in new[] { local, snapshot, cachedFavorite, remoteFavorite })
            await database.Data.SavePlaylistAsync(playlist);

        await database.Data.AddSongsToPlaylistAsync(local.Id, new[] { LocalSong(@"C:\music\a.mp3") });
        await database.Data.AddSongsToPlaylistAsync(snapshot.Id, new[] { Song("snapshot-song") });
        await database.Data.AddSongsToPlaylistAsync(cachedFavorite.Id, new[] { Song("cached-song") });

        var saved = new SavedPlaylistWorkflow(database.Data, CreateBrowse());

        saved.Select(local);
        var localResult = await saved.ActivateAsync();
        Assert.True(localResult.Succeeded, localResult.Message);
        Assert.Contains("已从本地歌曲列表读取", localResult.Message);
        Assert.Contains("（本地歌曲）", localResult.Message);

        saved.Select(snapshot);
        var snapshotResult = await saved.ActivateAsync();
        Assert.True(snapshotResult.Succeeded, snapshotResult.Message);
        Assert.Contains("已从本地快照读取", snapshotResult.Message);
        Assert.Contains("（本地快照）", snapshotResult.Message);

        saved.Select(cachedFavorite);
        var cachedResult = await saved.ActivateAsync();
        Assert.True(cachedResult.Succeeded, cachedResult.Message);
        Assert.Contains("已从本地缓存读取", cachedResult.Message);
        Assert.Contains("（远程收藏）", cachedResult.Message);

        saved.Select(remoteFavorite);
        var remoteResult = await saved.ActivateAsync();
        Assert.True(remoteResult.Succeeded, remoteResult.Message);
        Assert.Contains("已从远程加载", remoteResult.Message);
        Assert.Contains("（远程收藏）", remoteResult.Message);
    }

    [Fact]
    public async Task Legacy_recommended_network_favorite_falls_back_to_playlist_detail()
    {
        using var database = await TemporaryDatabase.CreateAsync();
        foreach (var defaultPlaylist in await database.Data.GetPlaylistsAsync())
            await database.Data.DeletePlaylistAsync(defaultPlaylist.Id);
        var favorite = new Playlist
        {
            Name = "QQ network playlist", Type = PlaylistType.Recommend, Source = "qq", RemoteId = "qq_1"
        };
        await database.Data.SavePlaylistAsync(favorite);
        var source = new LegacyFavoriteBrowseSource();
        var saved = new SavedPlaylistWorkflow(database.Data, CreateBrowse(source));
        saved.Select(favorite);

        var result = await saved.ActivateAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("playlist-song", Assert.Single(result.Snapshot.Songs).Id);
        Assert.True(source.PlaylistDetailCalled);
    }

    [Fact]
    public async Task Default_local_playlist_is_protected_and_action_messages_name_the_playlist_kind()
    {
        using var database = await TemporaryDatabase.CreateAsync();
        var local = await database.Data.GetOrCreateDefaultPlaylistAsync(local: true);
        Assert.NotNull(local);
        var snapshot = new Playlist
        {
            Name = "Imported", Type = PlaylistType.User, Source = "netease", RemoteId = "netease_1", IsSnapshot = true
        };
        var favorite = new Playlist
        {
            Name = "Favorite", Type = PlaylistType.User, Source = "qq", RemoteId = "qq_1"
        };
        await database.Data.SavePlaylistAsync(snapshot);
        await database.Data.SavePlaylistAsync(favorite);

        var saved = new SavedPlaylistWorkflow(database.Data, CreateBrowse());
        saved.Select(local);
        var deleteResult = await saved.DeleteSelectedAsync();
        Assert.False(deleteResult.Succeeded);
        Assert.Equal("默认本地歌曲歌单不能删除", deleteResult.Message);

        saved.Select(snapshot);
        var renameResult = await saved.RenameSelectedAsync("Renamed snapshot");
        Assert.True(renameResult.Succeeded, renameResult.Message);
        Assert.Contains("本地快照歌单已重命名", renameResult.Message);

        var clearSnapshotResult = await saved.ClearAsync();
        Assert.True(clearSnapshotResult.Succeeded, clearSnapshotResult.Message);
        Assert.Contains("本地快照歌单已清空", clearSnapshotResult.Message);

        saved.Select(favorite);
        var clearFavoriteResult = await saved.ClearAsync();
        Assert.True(clearFavoriteResult.Succeeded, clearFavoriteResult.Message);
        Assert.Contains("远程收藏已清空", clearFavoriteResult.Message);
        Assert.Contains("重新进入时可能从远程重新加载", clearFavoriteResult.Message);
    }

    [Fact]
    public void Playlist_confirmations_and_rename_prompts_explain_scope()
    {
        var local = new Playlist { Name = "Local songs", Type = PlaylistType.System, IsLocal = true };
        var snapshot = new Playlist
        {
            Name = "Imported", Type = PlaylistType.User, Source = "netease", RemoteId = "netease_1", IsSnapshot = true
        };
        var favorite = new Playlist
        {
            Name = "Favorite", Type = PlaylistType.User, Source = "qq", RemoteId = "qq_1"
        };

        Assert.False(SavedPlaylistWorkflow.CanDelete(local));
        Assert.Contains("不会删除磁盘上的音频文件", SavedPlaylistWorkflow.ClearConfirmation(local));
        Assert.Contains("重新导入时可能被远程名称覆盖", SavedPlaylistWorkflow.RenamePrompt(snapshot));
        Assert.Contains("将删除本地快照和歌曲列表", SavedPlaylistWorkflow.DeleteConfirmation(snapshot));
        Assert.Contains("只删除本地收藏", SavedPlaylistWorkflow.DeleteConfirmation(favorite));
        Assert.Contains("下次进入可能从远程重新加载", SavedPlaylistWorkflow.ClearConfirmation(favorite));
    }

    private static MusicInfo Song(string id) => new() { Id = id, Title = id, Source = "test" };
    private static MusicInfo LocalSong(string path) => new()
    {
        Id = "Local|" + path,
        Title = System.IO.Path.GetFileNameWithoutExtension(path),
        Source = "Local",
        PlayUrl = path
    };
    private static MusicBrowseWorkflow CreateBrowse(IMusicBrowseSource? source = null) => new(
        source ?? new EmptyBrowseSource(),
        new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance),
        new RecordingAnnouncer(),
        new NoPlayback(),
        new LocalSearchHistoryStore(Path.Combine(Path.GetTempPath(), "playlist-remediation-" + Guid.NewGuid().ToString("N"))),
        NullLogger<MusicBrowseWorkflow>.Instance);

    private static ListBox PlaylistList(Form form) => (ListBox)form.Controls.Find("个人歌单", true)[0];

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?')
            .Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => { var separator = part.IndexOf('='); return separator < 0 ? new[] { part } : new[] { part[..separator], part[(separator + 1)..] }; })
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));
    }
    private static void SetPrivate(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static Task InvokePrivateAsync(object target, string name)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (Task)method!.Invoke(target, Array.Empty<object>())!;
    }

    private static void InvokeKeyDown(Form form, Keys keyData)
    {
        var method = form.GetType().GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(form, new object[] { new KeyEventArgs(keyData) });
    }

    private sealed class EmptyBrowseSource : IMusicBrowseSource
    {
        public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page, int pageSize, string searchType, string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));

        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));

        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(new List<PlaylistCategory>());

        public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<Playlist>.Empty(page, pageSize));

        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));

        public Task<List<Ranking>> GetRankingsAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(new List<Ranking>());

        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
    }
    private sealed class LegacyFavoriteBrowseSource : IMusicBrowseSource
    {
        public bool PlaylistDetailCalled { get; private set; }

        public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page, int pageSize, string searchType, string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));

        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));

        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(new List<PlaylistCategory>());

        public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<Playlist>.Empty(page, pageSize));

        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page, int pageSize, CancellationToken cancellationToken)
        {
            PlaylistDetailCalled = true;
            return Task.FromResult(new PagedResult<MusicInfo>
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = 1,
                Items = [new MusicInfo { Id = "playlist-song", Title = "Network song", Source = "qq" }]
            });
        }

        public Task<List<Ranking>> GetRankingsAsync(string sourceId, CancellationToken cancellationToken) =>
            Task.FromResult(new List<Ranking>());

        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(PagedResult<MusicInfo>.Empty(page, pageSize));
    }

    private sealed class NoPlayback : IPlaybackCoordinator
    {
        public bool IsPlaying => false;
        public bool IsPaused => false;
        public MusicInfo? CurrentMusic => null;
        public PlayMode Mode { get; set; }
        public string DefaultQuality { get; set; } = "128k";
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted { add { } remove { } }
        public Task<PlaybackResult> PlayAsync(PlaybackRequest request) => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, null, PlaybackQueueOrigin.Static, string.Empty));
        public Task<PlaybackResult> ReplayAsync() => PlayAsync(default);
        public Task<PlaybackResult> NextAsync() => PlayAsync(default);
        public Task<PlaybackResult> PreviousAsync() => PlayAsync(default);
        public Task ResumeAsync() => Task.CompletedTask;
        public void Pause() { }
        public void Stop() { }
    }

    private sealed class KugouPagingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = ParseQuery(request.RequestUri!.Query);
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var page = document.RootElement.GetProperty("page").GetInt32();
            var type = document.RootElement.GetProperty("type").GetInt32();
            var count = type == 0 ? (page == 1 ? 100 : 1) : 2;
            var items = string.Join(",", Enumerable.Range(1, count).Select(index =>
                "{\"listid\":" + ((type * 10000) + ((page - 1) * 100) + index) + ",\"name\":\"P" + type + "-" + page + "-" + index + "\",\"count\":1}"));
            var json = "{\"error_code\":0,\"data\":{\"list\":[" + items + "],\"total\":" + (type == 0 ? 101 : 2) + "}}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StaticProvider(IReadOnlyList<MusicInfo> songs) : IPlaylistImportProvider
    {
        public string PlatformId => "netease";
        public string PlatformName => "网易云音乐";
        public Task<AccountInfo?> LoginByCookieAsync(string cookieText) => Task.FromResult<AccountInfo?>(null);
        public Task LoadAccountAsync(AccountInfo account) => Task.CompletedTask;
        public Task LogoutAsync() => Task.CompletedTask;
        public Task<List<RemotePlaylist>> GetMyPlaylistsAsync() => Task.FromResult(new List<RemotePlaylist>());
        public Task<List<MusicInfo>> GetPlaylistSongsAsync(RemotePlaylist playlist) => Task.FromResult(songs.ToList());
    }

    private sealed class BlockingRestoreProvider : IPlaylistImportProvider
    {
        public int LoadAccountCalls { get; private set; }
        public TaskCompletionSource<object?> RestoreStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object?> ReleaseRestore { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string PlatformId => "netease";
        public string PlatformName => "网易云音乐";
        public Task<AccountInfo?> LoginByCookieAsync(string cookieText) => Task.FromResult<AccountInfo?>(null);
        public Task LogoutAsync() => Task.CompletedTask;
        public Task<List<RemotePlaylist>> GetMyPlaylistsAsync() => Task.FromResult(new List<RemotePlaylist>());
        public Task<List<MusicInfo>> GetPlaylistSongsAsync(RemotePlaylist playlist) => Task.FromResult(new List<MusicInfo>());

        public async Task LoadAccountAsync(AccountInfo account)
        {
            LoadAccountCalls++;
            RestoreStarted.TrySetResult(null);
            await ReleaseRestore.Task;
        }
    }

    private sealed class NoLoginFactory : IPlaylistLoginWindowFactory
    {
        public PlaylistLoginMethod? SelectLoginMethod(string platform, Form? owner) => PlaylistLoginMethod.Cookie;
        public PlaylistLoginDialogResult ShowWebLogin(string platform, Form? owner) => new(false);
        public bool ShowKugouQrLogin(Form? owner) => false;
    }

    private sealed class FakeImportService : IPlaylistImportService
    {
        public List<RemotePlaylist> Playlists { get; } = new() { new() { Platform = "netease", RemoteId = "1", Name = "Remote" } };
        public List<string> ImportCalls { get; } = new();
        public TaskCompletionSource<object?> PreviewStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object?> ReleasePreview { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object?> ImportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object?> ReleaseImport { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<IPlaylistImportProvider> Providers => Array.Empty<IPlaylistImportProvider>();
        public IPlaylistImportProvider? GetProvider(string platformId) => null;
        public Task EnsureAccountsLoadedAsync() => Task.CompletedTask;
        public AccountInfo? GetAccount(string platformId) => null;
        public Task<AccountInfo?> LoginWithCookieAsync(string platformId, string cookieText) => Task.FromResult<AccountInfo?>(null);
        public Task<AccountInfo?> LoginWithBrowserResultAsync(string platformId, string cookieText, string? displayName, List<RemotePlaylist>? playlists) => Task.FromResult<AccountInfo?>(null);
        public Task LogoutAsync(string platformId) => Task.CompletedTask;
        public Task<List<RemotePlaylist>> GetMyPlaylistsAsync(string platformId) => Task.FromResult(Playlists);
        public async Task<List<MusicInfo>> GetPlaylistSongsPreviewAsync(string platformId, RemotePlaylist remote)
        {
            PreviewStarted.TrySetResult(null);
            await ReleasePreview.Task;
            return new List<MusicInfo> { new() { Id = "song", Title = "Song", Source = "netease" } };
        }

        public async Task<PlaylistImportResult> ImportPlaylistAsync(string platformId, RemotePlaylist remote, Action<string>? announce = null, CancellationToken cancellationToken = default)
        {
            ImportCalls.Add(remote.RemoteId);
            ImportStarted.TrySetResult(null);
            await ReleaseImport.Task;
            return new PlaylistImportResult { PlaylistId = remote.RemoteId, PlaylistName = remote.Name, ImportedCount = 1 };
        }

        public Task<IReadOnlyList<PlaylistImportResult>> ImportPlaylistsAsync(string platformId, IReadOnlyList<RemotePlaylist> remotes, Action<string>? announce = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private const string Prefix = "LXMusicAccessible.PlaylistRemediation-";
        private TemporaryDatabase(string path)
        {
            Path = path;
            Data = new DataService(NullLogger<DataService>.Instance, path);
        }
        public string Path { get; }
        public IDataService Data { get; }

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var database = new TemporaryDatabase(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
            var initialized = await new DatabaseInitializer(NullLogger<DatabaseInitializer>.Instance, database.Path).InitializeAsync();
            Assert.True(initialized.Succeeded, initialized.Message);
            return database;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            var fullPath = System.IO.Path.GetFullPath(Path);
            if (!System.IO.Path.GetFileName(fullPath).StartsWith(Prefix, StringComparison.Ordinal)) return;
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        }
    }
}
