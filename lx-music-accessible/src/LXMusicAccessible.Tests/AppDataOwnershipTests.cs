using System.IO;
using System.Text.Json;
using LXMusicAccessible.Data;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class AppDataOwnershipTests
{
    [Fact]
    public void Cleanup_deletes_only_allowlisted_artifacts_and_is_idempotent()
    {
        using var fixture = new TemporaryDirectory();
        var owned = Path.Combine(fixture.Path, "owned");
        var outsideSentinel = Path.Combine(fixture.Path, "outside-sentinel.txt");
        var unknownRoot = Path.Combine(owned, "keep-me.txt");
        var unknownLog = Path.Combine(owned, "logs", "keep-me.log");
        var ownershipPointer = Path.Combine(owned, "data_dir.json");
        var ownershipId = AppDataOwnership.Create(owned);

        Directory.CreateDirectory(Path.Combine(owned, "logs"));
        File.WriteAllText(Path.Combine(owned, "lxmusic.db"), "database");
        File.WriteAllText(Path.Combine(owned, "lxmusic.db-wal"), "wal");
        File.WriteAllText(Path.Combine(owned, "logs", "lxmusic-20260823.txt"), "log");
        File.WriteAllText(Path.Combine(owned, "logs", "login-diagnostic.txt"), "diagnostic");
        File.WriteAllText(unknownRoot, "unknown");
        File.WriteAllText(unknownLog, "unknown");
        File.WriteAllText(ownershipPointer, "pointer owned by the path coordinator");
        File.WriteAllText(outsideSentinel, "outside");

        var first = AppDataOwnership.Clean(owned, ownershipId);
        var second = AppDataOwnership.Clean(owned, ownershipId);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.True(first.PreservedUnknownContent);
        Assert.True(second.PreservedUnknownContent);
        Assert.False(File.Exists(Path.Combine(owned, "lxmusic.db")));
        Assert.False(File.Exists(Path.Combine(owned, "lxmusic.db-wal")));
        Assert.False(File.Exists(Path.Combine(owned, "logs", "lxmusic-20260823.txt")));
        Assert.False(File.Exists(Path.Combine(owned, "logs", "login-diagnostic.txt")));
        Assert.True(File.Exists(unknownRoot));
        Assert.True(File.Exists(unknownLog));
        Assert.True(File.Exists(ownershipPointer));
        Assert.True(File.Exists(outsideSentinel));
    }

    [Fact]
    public void Cleanup_refuses_missing_forged_and_mismatched_ownership()
    {
        using var fixture = new TemporaryDirectory();
        var unowned = Directory.CreateDirectory(Path.Combine(fixture.Path, "unowned")).FullName;
        var unownedDatabase = Path.Combine(unowned, "lxmusic.db");
        File.WriteAllText(unownedDatabase, "keep");

        var missing = AppDataOwnership.Clean(unowned, Guid.NewGuid());
        Assert.Equal(AppDataCleanupRefusal.MissingOwnership, missing.Refusal);
        Assert.True(File.Exists(unownedDatabase));

        File.WriteAllText(
            Path.Combine(unowned, AppDataOwnership.MarkerFileName),
            JsonSerializer.Serialize(new
            {
                ApplicationId = "not-lx-music-accessible",
                SchemaVersion = AppDataOwnership.CurrentSchemaVersion,
                OwnershipId = Guid.NewGuid(),
                DirectoryPath = Path.GetFullPath(unowned)
            }));
        var forged = AppDataOwnership.Clean(unowned, Guid.NewGuid());
        Assert.Equal(AppDataCleanupRefusal.InvalidOwnership, forged.Refusal);
        Assert.True(File.Exists(unownedDatabase));

        File.WriteAllText(
            Path.Combine(unowned, AppDataOwnership.MarkerFileName),
            JsonSerializer.Serialize(new
            {
                ApplicationId = AppDataOwnership.ApplicationId,
                SchemaVersion = AppDataOwnership.CurrentSchemaVersion + 1,
                OwnershipId = Guid.NewGuid(),
                DirectoryPath = Path.GetFullPath(unowned)
            }));
        var unsupportedSchema = AppDataOwnership.Clean(unowned, Guid.NewGuid());
        Assert.Equal(AppDataCleanupRefusal.InvalidOwnership, unsupportedSchema.Refusal);
        Assert.True(File.Exists(unownedDatabase));

        File.Delete(Path.Combine(unowned, AppDataOwnership.MarkerFileName));
        File.Delete(unownedDatabase);
        var ownershipId = AppDataOwnership.Create(unowned);
        File.WriteAllText(unownedDatabase, "keep");
        var mismatch = AppDataOwnership.Clean(unowned, Guid.NewGuid());
        Assert.Equal(AppDataCleanupRefusal.IdentifierMismatch, mismatch.Refusal);
        Assert.NotEqual(ownershipId, Guid.Empty);
        Assert.True(File.Exists(unownedDatabase));
    }

    [Fact]
    public void Cleanup_normalizes_case_and_traversal_without_touching_siblings()
    {
        using var fixture = new TemporaryDirectory();
        var owned = Path.Combine(fixture.Path, "OwnedData");
        var ownershipId = AppDataOwnership.Create(owned);
        var database = Path.Combine(owned, "lxmusic.db");
        var sibling = Path.Combine(fixture.Path, "sibling.txt");
        File.WriteAllText(database, "database");
        File.WriteAllText(sibling, "sibling");

        var traversedPath = Path.Combine(owned, "unused", "..");
        var pathWithDifferentCase = OperatingSystem.IsWindows()
            ? traversedPath.ToUpperInvariant()
            : traversedPath;
        var result = AppDataOwnership.Clean(pathWithDifferentCase, ownershipId);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(database));
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public void Cleanup_refuses_protected_and_overlapping_directories()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var protectedResult = AppDataOwnership.Clean(root, Guid.NewGuid());
        Assert.Equal(AppDataCleanupRefusal.ProtectedPath, protectedResult.Refusal);
        Assert.Equal(
            AppDataCleanupRefusal.ProtectedPath,
            AppDataOwnership.Validate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.Equal(
            AppDataCleanupRefusal.ProtectedPath,
            AppDataOwnership.Validate(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));

        using var fixture = new TemporaryDirectory();
        var parent = Path.Combine(fixture.Path, "parent");
        var child = Path.Combine(parent, "child");
        var parentId = AppDataOwnership.Create(parent);
        var childId = AppDataOwnership.Create(child);
        File.WriteAllText(Path.Combine(parent, "lxmusic.db"), "parent");
        File.WriteAllText(Path.Combine(child, "lxmusic.db"), "child");

        var conflictingIdentityResults = AppDataOwnership.CleanMany(
            new[]
            {
                new OwnedAppDataDirectory(parent, parentId),
                new OwnedAppDataDirectory(parent.ToUpperInvariant(), Guid.NewGuid())
            });
        Assert.All(conflictingIdentityResults,
            result => Assert.Equal(AppDataCleanupRefusal.IdentifierMismatch, result.Refusal));
        Assert.True(File.Exists(Path.Combine(parent, "lxmusic.db")));

        var results = AppDataOwnership.CleanMany(
            new[] { new OwnedAppDataDirectory(parent, parentId), new OwnedAppDataDirectory(child, childId) });

        Assert.All(results, result => Assert.Equal(AppDataCleanupRefusal.OverlappingPaths, result.Refusal));
        Assert.True(File.Exists(Path.Combine(parent, "lxmusic.db")));
        Assert.True(File.Exists(Path.Combine(child, "lxmusic.db")));
    }

    [Fact]
    public void Cleanup_refuses_directory_links_before_deleting_target_files()
    {
        using var fixture = new TemporaryDirectory();
        var target = Path.Combine(fixture.Path, "target");
        var link = Path.Combine(fixture.Path, "link");
        var ownershipId = AppDataOwnership.Create(target);
        var database = Path.Combine(target, "lxmusic.db");
        File.WriteAllText(database, "database");

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = AppDataOwnership.Clean(link, ownershipId);

        Assert.Equal(AppDataCleanupRefusal.ReparsePoint, result.Refusal);
        Assert.True(File.Exists(database));
    }

    [Fact]
    public void Legacy_nonempty_custom_directory_is_not_claimed()
    {
        using var fixture = new TemporaryDirectory();
        var legacy = Directory.CreateDirectory(Path.Combine(fixture.Path, "legacy-custom")).FullName;
        var sentinel = Path.Combine(legacy, "personal-file.txt");
        File.WriteAllText(sentinel, "personal");

        Assert.Throws<InvalidOperationException>(() => AppDataOwnership.Create(legacy));
        Assert.False(File.Exists(Path.Combine(legacy, AppDataOwnership.MarkerFileName)));
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void Delete_failures_are_structured_and_do_not_expose_absolute_paths()
    {
        using var fixture = new TemporaryDirectory();
        var owned = Path.Combine(fixture.Path, "owned");
        var ownershipId = AppDataOwnership.Create(owned);
        var database = Path.Combine(owned, "lxmusic.db");
        File.WriteAllText(database, "database");
        var failures = new List<AppDataCleanupFailure>();

        using (File.Open(database, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = AppDataOwnership.Clean(owned, ownershipId, failures.Add);

            Assert.True(result.Accepted);
            Assert.False(result.Succeeded);
            var failure = Assert.Single(failures);
            Assert.Equal("delete-file", failure.Operation);
            Assert.Equal("lxmusic.db", failure.RelativePath);
            Assert.Equal(nameof(IOException), failure.ErrorType);
            Assert.DoesNotContain(fixture.Path, JsonSerializer.Serialize(failure), StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(AppDataOwnership.Clean(owned, ownershipId).Succeeded);
        Assert.False(Directory.Exists(owned));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() =>
            Path = Path.Combine(Path.GetTempPath(), "LXMusicAccessible.Ownership-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
