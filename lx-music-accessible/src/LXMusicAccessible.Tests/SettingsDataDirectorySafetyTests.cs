using System.Reflection;
using LXMusicAccessible.Data;
using LXMusicAccessible.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class SettingsDataDirectorySafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LXSettingsSafety-" + Guid.NewGuid().ToString("N"));

    public SettingsDataDirectorySafetyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Partial_copy_cleanup_reports_locked_files_and_can_retry()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var copied = Path.Combine(target, "lxmusic.db");
        File.WriteAllText(copied, "database");
        var failures = new List<string>();
        var cleanup = typeof(SettingsWorkflow).GetMethod("TryCleanupPartialCopy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(cleanup);

        using (new FileStream(copied, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var lockedResult = (bool)cleanup!.Invoke(null, [target, new[] { copied }, failures])!;
            Assert.False(lockedResult);
            Assert.NotEmpty(failures);
            Assert.True(File.Exists(copied));
        }

        var retryResult = (bool)cleanup!.Invoke(null, [target, new[] { copied }, failures])!;
        Assert.True(retryResult);
        Assert.False(File.Exists(copied));
    }

    [Fact]
    public async Task Concurrent_data_directory_changes_are_serialized_without_a_second_partial_copy()
    {
        var fixture = new ConcurrentFixture();
        try
        {
            fixture.CreateCurrentContent();
            var workflow = fixture.CreateWorkflow();
            var first = workflow.ChangeDataDirectoryAsync(fixture.SelectedParent);
            var second = workflow.ChangeDataDirectoryAsync(fixture.SelectedParent);
            var results = await Task.WhenAll(first, second);

            Assert.Equal(1, results.Count(result => result.Succeeded));
            Assert.Equal(1, results.Count(result => !result.Succeeded && result.Message.Contains("already running", StringComparison.Ordinal)));
            Assert.True(File.Exists(Path.Combine(fixture.SelectedParent, AppDataPaths.DefaultDirName, "lxmusic.db")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            fixture.Dispose();
        }
    }

    [Fact]
    public void Database_copy_uses_narrow_sqlite_file_names()
    {
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "lxmusic.db-old"), "old");
        var database = Path.Combine(source, "lxmusic.db");
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=false"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Settings ([Key] TEXT PRIMARY KEY, Value TEXT NOT NULL);";
            command.ExecuteNonQuery();
        }

        var copy = typeof(SettingsWorkflow).GetMethod("CopyDataDirectoryContents", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(copy);
        var copied = new List<string>();
        copy!.Invoke(null, [source, target, copied]);

        Assert.Equal([Path.Combine(target, "lxmusic.db")], copied);
        Assert.False(File.Exists(Path.Combine(target, "lxmusic.db-old")));
        File.Delete(Path.Combine(target, "lxmusic.db"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private sealed class ConcurrentFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LXConcurrentData-" + Guid.NewGuid().ToString("N"));
        public string LocalAppData { get; }
        public string SelectedParent { get; }
        public string Current { get; }

        public ConcurrentFixture()
        {
            LocalAppData = Path.Combine(Root, "localappdata");
            SelectedParent = Path.Combine(Root, "selected");
            Directory.CreateDirectory(LocalAppData);
            Directory.CreateDirectory(SelectedParent);
            Current = AppDataPaths.PrepareDataDir(LocalAppData);
        }

        public void CreateCurrentContent()
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(Current, "lxmusic.db")};Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Settings ([Key] TEXT PRIMARY KEY, Value TEXT NOT NULL); INSERT INTO Settings VALUES (@Key, @Value);";
            command.Parameters.AddWithValue("@Key", "ready");
            command.Parameters.AddWithValue("@Value", "yes");
            command.ExecuteNonQuery();
        }

        public SettingsWorkflow CreateWorkflow() => new(
            new MemorySettingsStore(),
            new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, new NoHotkeyAdapter()),
            NullLogger<SettingsWorkflow>.Instance);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<UserSettings?>(new());
        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoHotkeyAdapter : IGlobalHotkeyAdapter
    {
        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers) => true;
        public bool Unregister(IntPtr windowHandle, int id) => true;
        public int LastRegisterError => 0;
    }
}
