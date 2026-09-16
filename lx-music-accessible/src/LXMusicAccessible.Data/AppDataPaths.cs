using System.Diagnostics;
using System.Text.Json;

namespace LXMusicAccessible.Data;

/// <summary>
/// Resolves the app's data directory and records the ownership identity used for safe cleanup.
/// </summary>
public static class AppDataPaths
{
    public const string DefaultDirName = "lxmusic";
    private const string PointerFileName = "data_dir.json";
    private const string LegacyPointerFileName = "data_dir.txt";
    private const string DefaultOwnershipFileName = ".lxmusic-default-owner.json";
    private const int PointerSchemaVersion = 1;

    /// <summary>Default data directory under %LocalAppData%.</summary>
    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultDirName);

    /// <summary>Returns a verified owned directory, a safe legacy custom directory, or the default.</summary>
    public static string ResolveDataDir()
    {
        try
        {
            var pointerPath = Path.Combine(DefaultDataDir, PointerFileName);
            if (File.Exists(pointerPath))
            {
                return TryReadPointer(pointerPath, out var owned) ? owned.Path : DefaultDataDir;
            }

            var legacyPointerPath = Path.Combine(DefaultDataDir, LegacyPointerFileName);
            if (File.Exists(legacyPointerPath))
            {
                var legacyPath = File.ReadAllText(legacyPointerPath).Trim();
                if (!string.IsNullOrEmpty(legacyPath)
                    && Path.IsPathRooted(legacyPath)
                    && AppDataOwnership.Validate(legacyPath) == AppDataCleanupRefusal.None)
                {
                    return Path.GetFullPath(legacyPath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Debug.WriteLine($"Data directory resolution failed: {ex.GetType().Name}");
        }

        return DefaultDataDir;
    }

    /// <summary>
    /// Establishes ownership only for the conventional default directory. Legacy custom directories
    /// remain usable but unowned, so cleanup will preserve them.
    /// </summary>
    public static string PrepareDataDir()
    {
        var directory = ResolveDataDir();
        if (!string.Equals(Path.GetFullPath(directory), Path.GetFullPath(DefaultDataDir), PathComparison))
            return directory;

        try
        {
            var markerExisted = File.Exists(Path.Combine(directory, AppDataOwnership.MarkerFileName));
            var ownershipId = AppDataOwnership.AdoptKnownDefaultDirectory(directory);
            if (!markerExisted)
            {
                try
                {
                    WriteDefaultOwnership(ownershipId);
                }
                catch
                {
                    try
                    {
                        File.Delete(Path.Combine(directory, AppDataOwnership.MarkerFileName));
                    }
                    catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                    {
                        Debug.WriteLine($"Default ownership rollback failed: {rollbackException.GetType().Name}");
                    }

                    throw;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine($"Default data ownership was not established: {ex.GetType().Name}");
        }

        return directory;
    }

    /// <summary>Creates a dedicated owned child beneath a user-selected parent directory.</summary>
    public static OwnedAppDataDirectory CreateOwnedDataDirectory(string selectedParent)
    {
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(selectedParent);
        var target = Path.GetFullPath(Path.Combine(selectedParent, DefaultDirName));
        var current = ResolveDataDir();

        foreach (var existing in new[] { current, DefaultDataDir }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(Path.GetFullPath(existing), target, PathComparison)
                && AppDataOwnership.PathsOverlap(existing, target))
            {
                throw new InvalidOperationException(
                    "The new data directory must not contain or be contained by an existing data directory.");
            }
        }

        var ownershipId = AppDataOwnership.Create(target);
        return new OwnedAppDataDirectory(target, ownershipId);
    }

    /// <summary>Activates an already-owned data directory.</summary>
    public static void WriteDataDirPointer(string directory)
    {
        if (!AppDataOwnership.TryRead(directory, out var owned))
            throw new InvalidOperationException("The selected data directory is not owned by this application.");

        Directory.CreateDirectory(DefaultDataDir);
        var pointerPath = Path.Combine(DefaultDataDir, PointerFileName);
        var temporaryPath = pointerPath + ".tmp";
        var pointer = new DataDirectoryPointer(PointerSchemaVersion, owned.Path, owned.OwnershipId);
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(pointer));
        FrameworkCompatibility.Move(temporaryPath, pointerPath, overwrite: true);
    }

    /// <summary>Safely cleans every currently known owned data directory.</summary>
    public static IReadOnlyList<AppDataCleanupResult> CleanupOwnedData(
        Action<AppDataCleanupFailure>? logFailure = null)
    {
        OwnedAppDataDirectory? currentTarget = null;
        var pointerPath = Path.Combine(DefaultDataDir, PointerFileName);
        if (TryReadPointer(pointerPath, out var current)
            && !string.Equals(Path.GetFullPath(current.Path), Path.GetFullPath(DefaultDataDir), PathComparison))
        {
            currentTarget = current;
        }
        else
        {
            var legacyDirectory = ResolveDataDir();
            if (!string.Equals(Path.GetFullPath(legacyDirectory), Path.GetFullPath(DefaultDataDir), PathComparison))
                currentTarget = new OwnedAppDataDirectory(legacyDirectory, Guid.Empty);
        }

        OwnedAppDataDirectory? defaultTarget = null;
        if (TryReadDefaultOwnership(out var defaultDirectory))
            defaultTarget = defaultDirectory;
        else if (Directory.Exists(DefaultDataDir))
            defaultTarget = new OwnedAppDataDirectory(DefaultDataDir, Guid.Empty);

        if (currentTarget is not null
            && defaultTarget is not null
            && AppDataOwnership.PathsOverlap(currentTarget.Path, defaultTarget.Path))
        {
            return new[]
            {
                RefusedOverlap(),
                RefusedOverlap()
            };
        }

        var results = new List<AppDataCleanupResult>();
        if (currentTarget is not null)
        {
            var currentResult = AppDataOwnership.Clean(
                currentTarget.Path,
                currentTarget.OwnershipId,
                logFailure);
            results.Add(currentResult);
            if (!currentResult.Succeeded) return results;
        }

        if (defaultTarget is not null && defaultTarget.OwnershipId != Guid.Empty)
        {
            var pointerFailures = DeleteDefaultPointerFiles(logFailure);
            if (pointerFailures.Count > 0)
            {
                results.Add(new AppDataCleanupResult(
                    true,
                    AppDataCleanupRefusal.None,
                    pointerFailures,
                    false));
            }
        }

        if (defaultTarget is not null)
        {
            results.Add(AppDataOwnership.Clean(
                defaultTarget.Path,
                defaultTarget.OwnershipId,
                logFailure));
        }

        if (!Directory.Exists(DefaultDataDir) && File.Exists(DefaultOwnershipFilePath))
        {
            try
            {
                File.Delete(DefaultOwnershipFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var failure = new AppDataCleanupFailure(
                    "delete-file",
                    "default-ownership-record",
                    ex.GetType().Name);
                logFailure?.Invoke(failure);
                results.Add(new AppDataCleanupResult(
                    true,
                    AppDataCleanupRefusal.None,
                    new[] { failure },
                    false));
            }
        }

        return results;
    }

    private static IReadOnlyList<AppDataCleanupFailure> DeleteDefaultPointerFiles(
        Action<AppDataCleanupFailure>? logFailure)
    {
        var failures = new List<AppDataCleanupFailure>();
        foreach (var fileName in new[] { PointerFileName, LegacyPointerFileName })
        {
            try
            {
                var path = Path.Combine(DefaultDataDir, fileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var failure = new AppDataCleanupFailure("delete-file", fileName, ex.GetType().Name);
                failures.Add(failure);
                logFailure?.Invoke(failure);
            }
        }

        return failures;
    }

    private static AppDataCleanupResult RefusedOverlap() => new(
        false,
        AppDataCleanupRefusal.OverlappingPaths,
        Array.Empty<AppDataCleanupFailure>(),
        false);

    private static string DefaultOwnershipFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DefaultOwnershipFileName);

    private static void WriteDefaultOwnership(Guid ownershipId)
    {
        var record = new DataDirectoryPointer(PointerSchemaVersion, Path.GetFullPath(DefaultDataDir), ownershipId);
        var temporaryPath = DefaultOwnershipFilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record));
        FrameworkCompatibility.Move(temporaryPath, DefaultOwnershipFilePath, true);
    }

    private static bool TryReadDefaultOwnership(out OwnedAppDataDirectory ownedDirectory)
    {
        ownedDirectory = default!;
        try
        {
            if (!File.Exists(DefaultOwnershipFilePath)) return false;
            var record = JsonSerializer.Deserialize<DataDirectoryPointer>(
                File.ReadAllText(DefaultOwnershipFilePath));
            if (record is null
                || record.SchemaVersion != PointerSchemaVersion
                || record.OwnershipId == Guid.Empty
                || !string.Equals(Path.GetFullPath(record.Path), Path.GetFullPath(DefaultDataDir), PathComparison)
                || !AppDataOwnership.TryRead(DefaultDataDir, out var marker)
                || marker.OwnershipId != record.OwnershipId)
            {
                return false;
            }

            ownedDirectory = marker;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Debug.WriteLine($"Default ownership record was rejected: {ex.GetType().Name}");
            return false;
        }
    }

    private static bool TryReadPointer(string pointerPath, out OwnedAppDataDirectory ownedDirectory)
    {
        ownedDirectory = default!;
        try
        {
            if (!File.Exists(pointerPath)) return false;
            var pointer = JsonSerializer.Deserialize<DataDirectoryPointer>(File.ReadAllText(pointerPath));
            if (pointer is null
                || pointer.SchemaVersion != PointerSchemaVersion
                || pointer.OwnershipId == Guid.Empty
                || !Path.IsPathRooted(pointer.Path)
                || AppDataOwnership.Validate(pointer.Path) != AppDataCleanupRefusal.None
                || !AppDataOwnership.TryRead(pointer.Path, out var marker)
                || marker.OwnershipId != pointer.OwnershipId)
            {
                return false;
            }

            ownedDirectory = marker;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Debug.WriteLine($"Data directory pointer was rejected: {ex.GetType().Name}");
            return false;
        }
    }

    private static StringComparison PathComparison =>
        FrameworkCompatibility.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record DataDirectoryPointer(int SchemaVersion, string Path, Guid OwnershipId);
}
