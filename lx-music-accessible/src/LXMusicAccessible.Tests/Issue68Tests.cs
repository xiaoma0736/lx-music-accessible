using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LXMusicAccessible.App;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>
/// Issue #68: personal music sources, favourites, saved-playlist snapshots, settings and hotkeys
/// must be durable at the moment of the action and after shutdown/restart; a failed write must be
/// announced and leave the previous data intact; and the resolved data directory must never
/// silently nest itself under a directory the next run does not read.
/// </summary>
public sealed class Issue68Tests
{
    private const string ScriptUrl = "https://fixture.invalid/issue68-source.js";

    private const string ValidScript = """
        /**
         * @name Issue 68 test source
         * @version 1.0.0
         */
        lx.on(lx.EVENT_NAMES.request, function () {
          return Promise.resolve('https://fixture.invalid/issue68.mp3');
        });
        """;

    // ================================================================
    //  数据目录：不再静默嵌套到下次启动读不到的位置
    // ================================================================

    [Fact]
    public void Resolved_data_directory_is_not_nested_inside_itself()
    {
        using var temp = new TemporaryDirectory();

        var resolved = AppDataPaths.ResolveDataDir(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, AppDataPaths.DefaultDirName), resolved);
        Assert.DoesNotContain(
            AppDataPaths.DefaultDirName + Path.DirectorySeparatorChar + AppDataPaths.DefaultDirName,
            resolved,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_data_directory_sits_directly_under_local_app_data()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(localAppData, AppDataPaths.DefaultDirName), AppDataPaths.DefaultDataDir);
        Assert.DoesNotContain(
            AppDataPaths.DefaultDirName + Path.DirectorySeparatorChar + AppDataPaths.DefaultDirName,
            AppDataPaths.DefaultDataDir,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_nested_data_directory_is_adopted_once_without_data_loss()
    {
        using var temp = new TemporaryDirectory();
        var conventional = Path.Combine(temp.Path, AppDataPaths.DefaultDirName);
        var nested = Path.Combine(conventional, AppDataPaths.DefaultDirName);
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "lxmusic.db"), "nested-database");
        File.WriteAllText(Path.Combine(nested, "lxmusic.db-wal"), "nested-write-ahead-log");
        File.WriteAllText(Path.Combine(nested, "notes.txt"), "nested-notes");

        var moved = AppDataPaths.MigrateLegacyNestedDataDirectory(temp.Path);

        Assert.Equal(3, moved);
        Assert.Equal("nested-database", File.ReadAllText(Path.Combine(conventional, "lxmusic.db")));
        Assert.Equal("nested-write-ahead-log", File.ReadAllText(Path.Combine(conventional, "lxmusic.db-wal")));
        Assert.Equal("nested-notes", File.ReadAllText(Path.Combine(conventional, "notes.txt")));
        Assert.False(Directory.Exists(nested));
        Assert.Equal(0, AppDataPaths.MigrateLegacyNestedDataDirectory(temp.Path));
    }

    [Fact]
    public void Legacy_nested_migration_keeps_the_newer_conventional_database()
    {
        using var temp = new TemporaryDirectory();
        var conventional = Path.Combine(temp.Path, AppDataPaths.DefaultDirName);
        var nested = Path.Combine(conventional, AppDataPaths.DefaultDirName);
        Directory.CreateDirectory(nested);
        var nestedDatabase = Path.Combine(nested, "lxmusic.db");
        File.WriteAllText(nestedDatabase, "older-nested-database");
        File.SetLastWriteTimeUtc(nestedDatabase, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var conventionalDatabase = Path.Combine(conventional, "lxmusic.db");
        File.WriteAllText(conventionalDatabase, "newer-conventional-database");
        File.SetLastWriteTimeUtc(conventionalDatabase, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var moved = AppDataPaths.MigrateLegacyNestedDataDirectory(temp.Path);

        Assert.Equal(0, moved);
        Assert.Equal("newer-conventional-database", File.ReadAllText(conventionalDatabase));
    }

    [Fact]
    public void Legacy_nested_migration_is_a_no_op_without_a_nested_database()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(
            temp.Path,
            AppDataPaths.DefaultDirName,
            AppDataPaths.DefaultDirName,
            "empty-child"));

        Assert.Equal(0, AppDataPaths.MigrateLegacyNestedDataDirectory(temp.Path));
    }
    // ================================================================
    //  个人音源：变更即时持久化，重启后恢复
    // ================================================================

    [Fact]
    public async Task Personal_source_mutations_persist_and_restore_across_a_restart()
    {
        using var app = await TestApplicationHost.StartAsync();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), ValidScript);
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        await sources.InitializeScriptSourcesAsync();
        var imported = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));
        Assert.True(imported.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, imported.TrustState);

        Assert.True(await sources.SetDefaultSourceAsync(imported.Id));
        Assert.True(await sources.DisableSourceAsync(imported));
        imported.Priority = 4;
        Assert.True(await sources.UpdateSourceAsync(imported));

        // 立即落盘：不需要等到退出流程。
        var data = app.Services.GetRequiredService<IDataService>();
        var stored = Assert.Single(await data.GetMusicSourcesAsync(), source => source.Id == imported.Id);
        Assert.True(stored.IsDefault);
        Assert.False(stored.IsEnabled);
        Assert.Equal(4, stored.Priority);
        Assert.Equal(ScriptTrustState.UserApproved, stored.TrustState);
        Assert.False(string.IsNullOrWhiteSpace(stored.ScriptContent));

        // 重启：全新的服务实例从 SQLite 恢复。
        var restarted = ActivatorUtilities.CreateInstance<MusicSourceService>(app.Services);
        await restarted.InitializeScriptSourcesAsync();

        var restored = Assert.Single(restarted.GetAllSources(), source => source.Id == imported.Id);
        Assert.True(restored.IsDefault);
        Assert.False(restored.IsEnabled);
        Assert.Equal(4, restored.Priority);
        Assert.Equal(ScriptTrustState.UserApproved, restored.TrustState);
        Assert.Equal(imported.Id, restarted.GetDefaultSource()!.Id);
    }

    [Fact]
    public async Task Failed_source_write_rolls_back_and_keeps_the_previous_data_intact()
    {
        using var app = await TestApplicationHost.StartAsync();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), ValidScript);
        var data = app.Services.GetRequiredService<IDataService>();
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        await sources.InitializeScriptSourcesAsync();
        var imported = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));
        Assert.True(await sources.SetDefaultSourceAsync("netease"));
        var priorDefaultId = sources.GetDefaultSource()!.Id;
        var priorLastUpdate = imported.LastUpdateDate;

        var databasePath = Path.Combine(app.DataDirectory, "lxmusic.db");
        var backupPath = databasePath + ".issue68-rollback-backup";
        SqliteConnection.ClearAllPools();
        File.Move(databasePath, backupPath);
        Directory.CreateDirectory(databasePath);

        bool setDefault, disabled, updated, removed;
        try
        {
            setDefault = await sources.SetDefaultSourceAsync(imported.Id);
            disabled = await sources.DisableSourceAsync(imported);
            updated = await sources.UpdateSourceAsync(imported);
            removed = await sources.RemoveSourceAsync(imported);
        }
        finally
        {
            Directory.Delete(databasePath);
            File.Move(backupPath, databasePath);
            SqliteConnection.ClearAllPools();
        }

        Assert.False(setDefault);
        Assert.False(disabled);
        Assert.False(updated);
        Assert.False(removed);

        // 内存状态回滚到失败之前
        Assert.False(imported.IsDefault);
        Assert.True(imported.IsEnabled);
        Assert.Equal(ScriptTrustState.UserApproved, imported.TrustState);
        Assert.Equal(priorLastUpdate, imported.LastUpdateDate);
        Assert.Contains(sources.GetAllSources(), source => source.Id == imported.Id);
        Assert.NotNull(sources.GetScriptSource().GetScript(imported.Id));
        Assert.Equal(priorDefaultId, sources.GetDefaultSource()!.Id);

        // 既有持久化数据保持原样
        var persisted = await data.GetMusicSourcesAsync();
        var persistedImported = Assert.Single(persisted, source => source.Id == imported.Id);
        Assert.True(persistedImported.IsEnabled);
        Assert.False(persistedImported.IsDefault);
        Assert.Equal("netease", Assert.Single(persisted, source => source.IsDefault).Id);
    }

    // ================================================================
    //  收藏：本地与网络歌曲在动作发生时就已持久化
    // ================================================================

    [Fact]
    public async Task Local_and_remote_favorites_are_durable_at_the_moment_of_the_action()
    {
        using var app = await TestApplicationHost.StartAsync();
        var workflow = app.Services.GetRequiredService<SavedPlaylistWorkflow>();
        await workflow.LoadAsync();
        var remoteSong = new MusicInfo
        {
            Id = "issue68-remote-song",
            Title = "网络收藏",
            Artist = "Remote artist",
            Source = "netease",
            PlayUrl = "https://fixture.invalid/issue68-remote.mp3"
        };
        var localSong = new MusicInfo
        {
            Id = "issue68-local-song",
            Title = "本地收藏",
            Artist = "Local artist",
            Source = "Local",
            PlayUrl = @"C:\issue68\local-song.mp3"
        };

        var result = await workflow.AddSongsAsync(new[] { remoteSong, localSong });

        Assert.True(result.Succeeded);
        Assert.Contains("已添加 1 首歌曲到在线收藏", result.Message, StringComparison.Ordinal);
        Assert.Contains("已添加 1 首歌曲到本地歌曲", result.Message, StringComparison.Ordinal);

        // 立即落盘：用全新的数据服务读回，不经过任何退出流程。
        SqliteConnection.ClearAllPools();
        var restarted = new DataService(NullLogger<DataService>.Instance, app.DataDirectory);
        var onlineDefault = await restarted.GetOrCreateDefaultPlaylistAsync(local: false);
        var localDefault = await restarted.GetOrCreateDefaultPlaylistAsync(local: true);
        Assert.NotNull(onlineDefault);
        Assert.NotNull(localDefault);
        var onlineSongs = await restarted.GetPlaylistItemsAsync(onlineDefault!.Id);
        var localSongs = await restarted.GetPlaylistItemsAsync(localDefault!.Id);
        Assert.Equal("issue68-remote-song", Assert.Single(onlineSongs, song => song.Id == remoteSong.Id).Id);
        Assert.Equal("issue68-local-song", Assert.Single(localSongs, song => song.Id == localSong.Id).Id);

        // 重复收藏只播报，不产生重复行。
        var repeated = await workflow.AddSongsAsync(new[] { remoteSong });

        Assert.True(repeated.Succeeded);
        Assert.Contains("已在在线收藏中", repeated.Message, StringComparison.Ordinal);
        Assert.Single(await restarted.GetPlaylistItemsAsync(onlineDefault.Id));
    }

    // ================================================================
    //  歌单快照：替换后完整保留最新集合
    // ================================================================

    [Fact]
    public async Task Playlist_snapshot_replacement_keeps_only_the_newest_complete_set()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        var playlist = new Playlist
        {
            Id = "issue68-snapshot",
            Name = "快照歌单",
            Description = "issue 68",
            Type = PlaylistType.User,
            IsSnapshot = true
        };

        var first = await data.ReplacePlaylistSnapshotAsync(
            playlist,
            Enumerable.Range(1, 5).Select(index => SnapshotSong("first-" + index)));

        Assert.Equal(5, first.ImportedCount);
        Assert.Equal(0, first.SkippedCount);

        var second = await data.ReplacePlaylistSnapshotAsync(
            playlist,
            new[] { SnapshotSong("second-1"), SnapshotSong("second-2") });

        Assert.Equal(2, second.ImportedCount);
        var replaced = await data.GetPlaylistItemsAsync(playlist.Id);
        Assert.Equal(new[] { "second-1", "second-2" }, replaced.Select(item => item.Id));

        var duplicates = await data.ReplacePlaylistSnapshotAsync(
            playlist,
            new[] { SnapshotSong("third-1"), SnapshotSong("third-1") });

        Assert.Equal(1, duplicates.ImportedCount);
        Assert.Equal(1, duplicates.SkippedCount);
        var final = await data.GetPlaylistItemsAsync(playlist.Id);
        Assert.Equal("third-1", Assert.Single(final).Id);
    }
    // ================================================================
    //  设置与快捷键：即时持久化，重启后恢复
    // ================================================================

    [Fact]
    public async Task Settings_and_hotkey_changes_persist_immediately_and_survive_a_restart()
    {
        using var app = await TestApplicationHost.StartAsync();
        var settings = app.Services.GetRequiredService<SettingsWorkflow>();
        await settings.LoadAsync(IntPtr.Zero);

        var edit = settings.Edit(state =>
        {
            state.DefaultVolume = 0.42;
            state.DefaultPlayMode = PlayMode.SingleLoop;
            state.DefaultPlayQuality = "flac24bit";
            state.DefaultDownloadQuality = "flac24bit";
            state.DownloadPath = string.Empty;
        });
        Assert.True(edit.Accepted);
        Assert.True((await settings.SaveAsync(edit.State)).Succeeded);

        var hotkey = await settings.ReplaceHotkeyAsync(
            HotkeyActions.Next,
            isGlobal: false,
            PlatformKey.F12,
            PlatformModifiers.Shift);
        Assert.True(hotkey.Applied);

        SqliteConnection.ClearAllPools();
        var restarted = new SettingsWorkflow(
            new DataService(NullLogger<DataService>.Instance, app.DataDirectory),
            NullLoggerFactory.Instance);
        var restored = await restarted.LoadAsync(IntPtr.Zero);

        Assert.Equal(0.42, restored.Settings.DefaultVolume);
        Assert.Equal(PlayMode.SingleLoop, restored.Settings.DefaultPlayMode);
        Assert.Equal("flac24bit", restored.Settings.DefaultPlayQuality);
        Assert.Equal("flac24bit", restored.Settings.DefaultDownloadQuality);
        var restoredHotkey = Assert.Single(
            restored.Settings.CustomHotkeys,
            item => item.ActionId == HotkeyActions.Next && !item.IsGlobal);
        Assert.Equal(PlatformKey.F12, restoredHotkey.Key);
        Assert.Equal(PlatformModifiers.Shift, restoredHotkey.Modifiers);
    }

    [Fact]
    public async Task Announcement_toggles_write_settings_at_the_moment_of_the_action()
    {
        using var app = await TestApplicationHost.StartAsync();
        var settings = app.Services.GetRequiredService<SettingsWorkflow>();
        await settings.LoadAsync(IntPtr.Zero);
        var before = settings.Current.Settings.EnableVoiceAnnouncement;
        var toggles = new AnnouncementToggleService(app.Announcer, settings);
        var reopened = new DataService(NullLogger<DataService>.Instance, app.DataDirectory);

        toggles.ToggleVoiceAnnouncement();

        var persisted = await WaitForSettingAsync(
            reopened,
            candidate => candidate.EnableVoiceAnnouncement != before);

        Assert.NotNull(persisted);
        Assert.Equal(!before, persisted!.EnableVoiceAnnouncement);
    }

    // ================================================================
    //  退出保存：只跑一次，失败/超时会播报且保留既有数据
    // ================================================================

    [Fact]
    public async Task Shutdown_coordinator_persists_once_and_releases_the_host()
    {
        var persisted = 0;
        var released = 0;
        var feedback = new RecordingShutdownFeedback();
        var participant = new RecordingShutdownParticipant(
            _ => { persisted++; return Task.CompletedTask; });
        var coordinator = CreateCoordinator(
            participant,
            feedback,
            _ => { released++; return Task.CompletedTask; });

        var firstTask = coordinator.RequestAsync();
        var secondTask = coordinator.RequestAsync();
        var first = await firstTask;
        var second = await secondTask;

        Assert.Same(firstTask, secondTask);
        Assert.True(first.Succeeded);
        Assert.Equal(ShutdownStatus.Completed, first.Status);
        Assert.Equal(ApplicationShutdownCoordinator.ReleasePhase, first.Phase);
        Assert.Equal(first, second);
        Assert.Equal(1, participant.CaptureCount);
        Assert.Equal(1, persisted);
        Assert.Equal(1, released);
        Assert.Empty(feedback.Results);
    }

    [Fact]
    public async Task Shutdown_failure_is_announced_and_leaves_the_previous_data_intact()
    {
        using var app = await TestApplicationHost.StartAsync();
        var data = app.Services.GetRequiredService<IDataService>();
        var source = new MusicSource
        {
            Id = "issue68-prior-source",
            Name = "既有音源",
            Version = "1.0.0",
            IsEnabled = true,
            Priority = 2,
            TrustState = ScriptTrustState.UserApproved
        };
        await data.SaveMusicSourcesAsync(new List<MusicSource> { source });
        await data.SaveSettingsAsync(new UserSettings { DefaultVolume = 0.11 });

        var released = false;
        var feedback = new RecordingShutdownFeedback();
        var participant = new RecordingShutdownParticipant(
            _ => Task.FromException(new InvalidOperationException("persistence exploded")));
        var coordinator = CreateCoordinator(
            participant,
            feedback,
            _ => { released = true; return Task.CompletedTask; });

        var result = await coordinator.RequestAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownStatus.Failed, result.Status);
        Assert.Equal(ApplicationShutdownCoordinator.PersistencePhase, result.Phase);
        Assert.Equal(ShutdownStatus.Failed, Assert.Single(feedback.Results).Status);
        // 宿主仍然被安全释放，用户不会卡在无法退出的状态。
        Assert.True(released);

        SqliteConnection.ClearAllPools();
        var reopened = new DataService(NullLogger<DataService>.Instance, app.DataDirectory);
        Assert.Equal("issue68-prior-source", Assert.Single(await reopened.GetMusicSourcesAsync()).Id);
        Assert.Equal(0.11, (await reopened.GetSettingsAsync())!.DefaultVolume);
    }

    [Fact]
    public async Task Shutdown_timeout_is_announced_without_hanging()
    {
        var feedback = new RecordingShutdownFeedback();
        var participant = new RecordingShutdownParticipant(
            token => Task.Delay(Timeout.Infinite, token));
        var coordinator = new ApplicationShutdownCoordinator(
            () => participant,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            feedback,
            NullLogger<ApplicationShutdownCoordinator>.Instance,
            TimeSpan.FromMilliseconds(200));

        var result = await coordinator.RequestAsync();

        Assert.Equal(ShutdownStatus.TimedOut, result.Status);
        Assert.Equal(ApplicationShutdownCoordinator.PersistencePhase, result.Phase);
        Assert.Equal(ShutdownStatus.TimedOut, Assert.Single(feedback.Results).Status);
    }

    [Fact]
    public async Task Shell_shutdown_participant_persists_settings_and_sources_together()
    {
        using var app = await TestApplicationHost.StartAsync();
        app.HttpHandler.RegisterText(HttpMethod.Get, new Uri(ScriptUrl), ValidScript);
        var settings = app.Services.GetRequiredService<SettingsWorkflow>();
        await settings.LoadAsync(IntPtr.Zero);
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        await sources.InitializeScriptSourcesAsync();
        var imported = Assert.IsType<MusicSource>(await sources.ImportFromUrlAsync(ScriptUrl));
        var edit = settings.Edit(state => state.DefaultVolume = 0.17);
        Assert.True(edit.Accepted);

        var participant = new ShellShutdownParticipant(settings, sources);
        await participant.PersistAsync(participant.CaptureAndBlock(), CancellationToken.None);

        SqliteConnection.ClearAllPools();
        var reopened = new DataService(NullLogger<DataService>.Instance, app.DataDirectory);
        Assert.Equal(0.17, (await reopened.GetSettingsAsync())!.DefaultVolume);
        Assert.Contains(await reopened.GetMusicSourcesAsync(), stored => stored.Id == imported.Id);
    }

    [Fact]
    public async Task Shell_shutdown_participant_reports_a_failed_write_instead_of_silently_succeeding()
    {
        using var app = await TestApplicationHost.StartAsync();
        var settings = app.Services.GetRequiredService<SettingsWorkflow>();
        await settings.LoadAsync(IntPtr.Zero);
        var sources = (MusicSourceService)app.Services.GetRequiredService<IMusicSourceService>();
        await sources.InitializeScriptSourcesAsync();
        var participant = new ShellShutdownParticipant(settings, sources);
        var snapshot = participant.CaptureAndBlock();

        var databasePath = Path.Combine(app.DataDirectory, "lxmusic.db");
        var backupPath = databasePath + ".issue68-shutdown-backup";
        SqliteConnection.ClearAllPools();
        File.Move(databasePath, backupPath);
        Directory.CreateDirectory(databasePath);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => participant.PersistAsync(snapshot, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(databasePath);
            File.Move(backupPath, databasePath);
            SqliteConnection.ClearAllPools();
        }
    }
    // ================================================================
    //  旧版数据：升级后仍可读，旧收藏迁入默认歌单
    // ================================================================

    [Fact]
    public async Task Legacy_database_is_upgraded_and_old_rows_stay_readable()
    {
        using var temp = new TemporaryDirectory();
        var legacySong = new MusicInfo
        {
            Id = "issue68-legacy-favorite",
            Title = "旧版收藏",
            Artist = "Legacy artist",
            Source = "netease"
        };
        await CreateLegacyDatabaseAsync(temp.Path, legacySong);

        var migration = await new DatabaseInitializer(
            NullLogger<DatabaseInitializer>.Instance,
            temp.Path).InitializeAsync();

        Assert.True(migration.Succeeded);
        Assert.Equal(0, migration.PreviousVersion);

        var data = new DataService(NullLogger<DataService>.Instance, temp.Path);
        var defaultPlaylist = await data.GetOrCreateDefaultPlaylistAsync(local: false);
        Assert.NotNull(defaultPlaylist);
        var migrated = Assert.Single(await data.GetPlaylistItemsAsync(defaultPlaylist!.Id));
        Assert.Equal(legacySong.Id, migrated.Id);
        Assert.Equal(legacySong.Title, migrated.Title);

        // 旧版设置 JSON 缺少新字段时仍可读取，并完成快捷键架构迁移。
        await data.SetSettingValueAsync("UserSettings", "{\"DefaultVolume\":0.33,\"LoopMode\":true}");
        var workflow = new SettingsWorkflow(data, NullLoggerFactory.Instance);
        var loaded = await workflow.LoadAsync(IntPtr.Zero);

        Assert.Equal(0.33, loaded.Settings.DefaultVolume);
        Assert.Equal(PlayMode.Loop, loaded.Settings.DefaultPlayMode);
        Assert.Equal(3, loaded.Settings.HotkeySchemaVersion);
        Assert.Contains(loaded.Settings.CustomHotkeys, hotkey => hotkey.IsGlobal);
    }

    // ================================================================
    //  生产组合：启动初始化音源，退出走已绑定的保存管线
    // ================================================================

    [Fact]
    public void Production_startup_initializes_persisted_music_sources()
    {
        var source = ReadProductionFile("WinFormsAppHost.cs");

        Assert.Contains("await sources.InitializeScriptSourcesAsync(cancellationToken)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_exit_path_binds_and_persists_through_the_shutdown_coordinator()
    {
        var host = ReadProductionFile("WinFormsAppHost.cs");
        var program = ReadProductionFile("Program.cs");

        Assert.Contains("BindShutdown(_host)", host, StringComparison.Ordinal);
        Assert.Contains("IApplicationShutdownParticipant, ShellShutdownParticipant", host, StringComparison.Ordinal);
        Assert.Contains("sources.PersistSourcesOrThrowAsync(cancellationToken)", host, StringComparison.Ordinal);
        Assert.Contains("AppDataPaths.MigrateLegacyNestedDataDirectory()", program, StringComparison.Ordinal);
    }

    // ================================================================
    //  辅助
    // ================================================================

    // ================================================================
    //  退出通路：真实的退出命令走已绑定的保存管线
    // ================================================================

    [Fact]
    public async Task Exit_command_persists_through_the_bound_pipeline_exactly_once()
    {
        using var app = await TestApplicationHost.StartAsync();
        var persisted = 0;
        var released = 0;
        var participant = new RecordingShutdownParticipant(
            _ => { persisted++; return Task.CompletedTask; });
        var request = new ApplicationShutdownRequest();
        var coordinator = new ApplicationShutdownCoordinator(
            () => participant,
            _ => Task.CompletedTask,
            _ => { released++; return Task.CompletedTask; },
            new RecordingShutdownFeedback(),
            NullLogger<ApplicationShutdownCoordinator>.Instance,
            TimeSpan.FromSeconds(15));
        request.Bind(coordinator.RequestAsync);
        using var lifecycle = new DesktopLifecycleWorkflow(
            new WinFormsDesktopIntegration(app.Announcer),
            request,
            app.Services.GetRequiredService<IPlaybackCoordinator>(),
            app.Announcer);

        var first = lifecycle.ExecuteAsync(DesktopCommand.Exit);
        var second = lifecycle.ExecuteAsync(DesktopCommand.Exit);

        Assert.Same(first, second);
        Assert.Equal(DesktopLifecycleState.Closed, (await first).State);
        Assert.Equal(1, participant.CaptureCount);
        Assert.Equal(1, persisted);
        Assert.Equal(1, released);
        // 保存完成后窗口不再被隐藏到托盘的偏好拦住，消息循环才能结束。
        Assert.True(lifecycle.AllowWindowClose);
        Assert.True(lifecycle.LastShutdownResult?.Succeeded);
    }

    [Fact]
    public async Task Unbound_shutdown_request_reports_an_error_instead_of_skipping_the_save()
    {
        var request = new ApplicationShutdownRequest();

        // 旧实现直接返回已完成的任务，退出时静默跳过保存；未绑定必须显式报错。
        await Assert.ThrowsAsync<InvalidOperationException>(() => request.RequestAsync());

        var calls = 0;
        request.Bind(() =>
        {
            calls++;
            return Task.FromResult(new ShutdownResult(
                ShutdownStatus.Completed,
                ApplicationShutdownCoordinator.ReleasePhase));
        });

        Assert.True((await request.RequestAsync()).Succeeded);
        Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(
            () => request.Bind(() => Task.FromResult(default(ShutdownResult))));
    }

    private static ApplicationShutdownCoordinator CreateCoordinator(
        RecordingShutdownParticipant participant,
        RecordingShutdownFeedback feedback,
        Func<CancellationToken, Task> release) => new(
            () => participant,
            _ => Task.CompletedTask,
            release,
            feedback,
            NullLogger<ApplicationShutdownCoordinator>.Instance,
            TimeSpan.FromSeconds(15));

    private static MusicInfo SnapshotSong(string id) => new()
    {
        Id = id,
        Title = id,
        Artist = "issue68",
        Source = "netease"
    };

    private static async Task<UserSettings?> WaitForSettingAsync(
        DataService data,
        Func<UserSettings, bool> predicate)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var candidate = await data.GetSettingsAsync();
            if (candidate is not null && predicate(candidate)) return candidate;
            await Task.Delay(25);
        }
        return null;
    }

    private static string ReadProductionFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "verify.ps1")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
        return File.ReadAllText(Path.Combine(root, "src", "LXMusicAccessible.App", fileName));
    }

    private static async Task CreateLegacyDatabaseAsync(string directory, MusicInfo favorite)
    {
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "lxmusic.db")
        }.ToString());
        await connection.OpenAsync();
        using (connection)
        {
            await connection.ExecuteAsync(@"
                CREATE TABLE FavoriteSongs (
                    MusicId TEXT NOT NULL,
                    MusicSource TEXT NOT NULL,
                    MusicData TEXT NOT NULL,
                    OrderNum INTEGER,
                    AddedDate TEXT,
                    PRIMARY KEY (MusicId, MusicSource)
                )");
            await connection.ExecuteAsync(
                @"INSERT INTO FavoriteSongs (MusicId, MusicSource, MusicData, OrderNum, AddedDate)
                  VALUES (@MusicId, @MusicSource, @MusicData, 0, @AddedDate)",
                new
                {
                    MusicId = favorite.Id,
                    MusicSource = favorite.Source,
                    MusicData = System.Text.Json.JsonSerializer.Serialize(favorite),
                    AddedDate = DateTime.Now.ToString("o")
                });
        }
        SqliteConnection.ClearAllPools();
    }

    private sealed class RecordingShutdownParticipant(Func<CancellationToken, Task> persist)
        : IApplicationShutdownParticipant
    {
        public int CaptureCount { get; private set; }

        public ApplicationShutdownSnapshot CaptureAndBlock()
        {
            CaptureCount++;
            return new ApplicationShutdownSnapshot(new SettingsEditState(0, new UserSettings()));
        }

        public Task PersistAsync(ApplicationShutdownSnapshot snapshot, CancellationToken cancellationToken)
            => persist(cancellationToken);
    }

    private sealed class RecordingShutdownFeedback : IShutdownFeedback
    {
        public List<ShutdownResult> Results { get; } = new();

        public void Report(ShutdownResult result) => Results.Add(result);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "LXMusicAccessible.Issue68-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
