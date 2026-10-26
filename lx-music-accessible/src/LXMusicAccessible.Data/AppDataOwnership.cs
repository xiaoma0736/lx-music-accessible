using System.Security;
using System.Text.Json;

namespace LXMusicAccessible.Data;

public enum AppDataCleanupRefusal
{
    None,
    MissingOwnership,
    InvalidOwnership,
    IdentifierMismatch,
    ProtectedPath,
    ReparsePoint,
    OverlappingPaths
}

public sealed record OwnedAppDataDirectory(string Path, Guid OwnershipId);

public sealed record AppDataCleanupFailure(string Operation, string RelativePath, string ErrorType);

public sealed record AppDataCleanupResult(
    bool Accepted,
    AppDataCleanupRefusal Refusal,
    IReadOnlyList<AppDataCleanupFailure> Failures,
    bool PreservedUnknownContent)
{
    public bool Succeeded => Accepted && Failures.Count == 0;
}

/// <summary>
/// Creates verifiable app-data directories and removes only known application artifacts.
/// Unknown content is never traversed or deleted.
/// </summary>
public static class AppDataOwnership
{
    public const string MarkerFileName = ".lxmusic-data-owner.json";
    public const int CurrentSchemaVersion = 1;
    public const string ApplicationId = "LXMusicAccessible";

    private static readonly string[] KnownRootFiles =
    {
        "lxmusic.db",
        "lxmusic.db-shm",
        "lxmusic.db-wal"
    };

    private static readonly string[] KnownAdoptionRootFiles =
        KnownRootFiles.Concat(new[] { "data_dir.json", "data_dir.txt" }).ToArray();

    public static Guid Create(string directory) => Create(directory, adoptKnownArtifacts: false);

    internal static Guid AdoptKnownDefaultDirectory(string directory) =>
        Create(directory, adoptKnownArtifacts: true);

    private static Guid Create(string directory, bool adoptKnownArtifacts)
    {
        var fullDirectory = Normalize(directory);
        var refusal = ValidateNormalizedPath(fullDirectory);
        if (refusal != AppDataCleanupRefusal.None)
            throw new InvalidOperationException($"Refusing to own an unsafe data directory ({refusal}).");

        if (TryRead(fullDirectory, out var existing)) return existing.OwnershipId;
        if (File.Exists(Path.Combine(fullDirectory, MarkerFileName)))
            throw new InvalidOperationException("The data directory contains an invalid ownership marker.");

        if (Directory.Exists(fullDirectory))
        {
            var entries = Directory.EnumerateFileSystemEntries(fullDirectory).ToArray();
            if (entries.Length > 0 && (!adoptKnownArtifacts || !ContainsOnlyKnownArtifacts(fullDirectory, entries)))
                throw new InvalidOperationException("An existing non-empty directory cannot be claimed as application data.");
        }
        else
        {
            Directory.CreateDirectory(fullDirectory);
        }

        var marker = new OwnershipMarker(
            ApplicationId,
            CurrentSchemaVersion,
            Guid.NewGuid(),
            fullDirectory);
        using var stream = new FileStream(
            Path.Combine(fullDirectory, MarkerFileName),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        JsonSerializer.Serialize(stream, marker);
        return marker.OwnershipId;
    }

    public static bool TryRead(string directory, out OwnedAppDataDirectory ownedDirectory)
    {
        ownedDirectory = default!;
        string fullDirectory;
        try
        {
            fullDirectory = Normalize(directory);
            var markerPath = Path.Combine(fullDirectory, MarkerFileName);
            if (!File.Exists(markerPath)) return false;

            using var stream = File.OpenRead(markerPath);
            var marker = JsonSerializer.Deserialize<OwnershipMarker>(stream);
            if (marker is null
                || marker.ApplicationId != ApplicationId
                || marker.SchemaVersion != CurrentSchemaVersion
                || marker.OwnershipId == Guid.Empty
                || !string.Equals(Normalize(marker.DirectoryPath), fullDirectory, PathComparison))
            {
                return false;
            }

            ownedDirectory = new OwnedAppDataDirectory(fullDirectory, marker.OwnershipId);
            return true;
        }
        catch (Exception ex) when (IsFileSystemOrFormatException(ex))
        {
            return false;
        }
    }

    public static AppDataCleanupResult Clean(
        string directory,
        Guid expectedOwnershipId,
        Action<AppDataCleanupFailure>? logFailure = null)
    {
        string fullDirectory;
        try
        {
            fullDirectory = Normalize(directory);
        }
        catch (Exception ex) when (IsFileSystemOrFormatException(ex))
        {
            return Refused(AppDataCleanupRefusal.ProtectedPath);
        }

        var pathRefusal = ValidateNormalizedPath(fullDirectory);
        if (pathRefusal != AppDataCleanupRefusal.None) return Refused(pathRefusal);
        if (!Directory.Exists(fullDirectory)) return Accepted();

        var markerPath = Path.Combine(fullDirectory, MarkerFileName);
        if (!File.Exists(markerPath)) return Refused(AppDataCleanupRefusal.MissingOwnership);
        if (!TryRead(fullDirectory, out var owned)) return Refused(AppDataCleanupRefusal.InvalidOwnership);
        if (owned.OwnershipId != expectedOwnershipId)
            return Refused(AppDataCleanupRefusal.IdentifierMismatch);

        var logsDirectory = Path.Combine(fullDirectory, "logs");
        if (Directory.Exists(logsDirectory) && IsReparsePoint(logsDirectory))
            return Refused(AppDataCleanupRefusal.ReparsePoint);

        var failures = new List<AppDataCleanupFailure>();
        var files = KnownRootFiles.Select(name => Path.Combine(fullDirectory, name)).ToList();
        try
        {
            // Migration backups are application-owned copies of the user's database, not unknown
            // user documents; deleting all data must not leave a second copy of credentials behind.
            files.AddRange(Directory.EnumerateFiles(fullDirectory, "lxmusic.db.pre-v*.bak", SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex) when (IsFileSystemOrFormatException(ex))
        {
            AddFailure(failures, logFailure, "enumerate", "migration-backups", ex);
        }
        if (Directory.Exists(logsDirectory))
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(logsDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsKnownLogFile));
            }
            catch (Exception ex) when (IsFileSystemOrFormatException(ex))
            {
                AddFailure(failures, logFailure, "enumerate", "logs", ex);
                return new AppDataCleanupResult(true, AppDataCleanupRefusal.None, failures, false);
            }
        }

        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex) when (IsFileSystemOrFormatException(ex))
            {
                AddFailure(failures, logFailure, "delete-file", FrameworkCompatibility.GetRelativePath(fullDirectory, file), ex);
            }
        }

        TryDeleteIfEmpty(logsDirectory, "logs", failures, logFailure);

        var preservedUnknownContent = false;
        if (failures.Count == 0)
        {
            try
            {
                var remaining = Directory.EnumerateFileSystemEntries(fullDirectory).ToArray();
                if (remaining.Length == 1
                    && string.Equals(remaining[0], markerPath, PathComparison))
                {
                    File.Delete(markerPath);
                    Directory.Delete(fullDirectory, recursive: false);
                }
                else
                {
                    preservedUnknownContent = remaining.Any(path =>
                        !string.Equals(path, markerPath, PathComparison));
                }
            }
            catch (Exception ex) when (IsFileSystemOrFormatException(ex))
            {
                AddFailure(failures, logFailure, "delete-empty-directory", ".", ex);
            }
        }

        return new AppDataCleanupResult(
            true,
            AppDataCleanupRefusal.None,
            failures,
            preservedUnknownContent);
    }

    public static IReadOnlyList<AppDataCleanupResult> CleanMany(
        IReadOnlyCollection<OwnedAppDataDirectory> directories,
        Action<AppDataCleanupFailure>? logFailure = null)
    {
        OwnedAppDataDirectory[] requested;
        try
        {
            requested = directories
                .Select(item => new OwnedAppDataDirectory(Normalize(item.Path), item.OwnershipId))
                .ToArray();
        }
        catch (Exception ex) when (IsFileSystemOrFormatException(ex))
        {
            return directories.Select(_ => Refused(AppDataCleanupRefusal.ProtectedPath)).ToArray();
        }

        var groups = requested.GroupBy(item => item.Path, PathComparer).ToArray();
        if (groups.Any(group => group.Select(item => item.OwnershipId).Distinct().Count() > 1))
            return requested.Select(_ => Refused(AppDataCleanupRefusal.IdentifierMismatch)).ToArray();

        var normalized = groups.Select(group => group.First()).ToArray();

        // ponytail: uninstall has at most two targets; pairwise validation keeps the safety rule obvious.
        for (var i = 0; i < normalized.Length; i++)
        {
            for (var j = i + 1; j < normalized.Length; j++)
            {
                if (IsAncestor(normalized[i].Path, normalized[j].Path)
                    || IsAncestor(normalized[j].Path, normalized[i].Path))
                {
                    return normalized.Select(_ => Refused(AppDataCleanupRefusal.OverlappingPaths)).ToArray();
                }
            }
        }

        return normalized.Select(item => Clean(item.Path, item.OwnershipId, logFailure)).ToArray();
    }

    public static AppDataCleanupRefusal Validate(string directory)
    {
        try
        {
            return ValidateNormalizedPath(Normalize(directory));
        }
        catch (Exception ex) when (IsFileSystemOrFormatException(ex))
        {
            return AppDataCleanupRefusal.ProtectedPath;
        }
    }

    public static bool PathsOverlap(string left, string right)
    {
        try
        {
            return PathsOverlapNormalized(Normalize(left), Normalize(right));
        }
        catch (Exception ex) when (IsFileSystemOrFormatException(ex))
        {
            return true;
        }
    }

    private static bool ContainsOnlyKnownArtifacts(string directory, IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (File.Exists(entry) && KnownAdoptionRootFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(entry)
                && string.Equals(name, "downloads", StringComparison.OrdinalIgnoreCase)
                && !IsReparsePoint(entry))
            {
                continue;
            }

            if (!Directory.Exists(entry)
                || !string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase)
                || IsReparsePoint(entry))
            {
                return false;
            }

            if (Directory.EnumerateDirectories(entry).Any()
                || Directory.EnumerateFiles(entry).Any(file => !IsKnownLogFile(file)))
            {
                return false;
            }
        }

        return true;
    }

    private static AppDataCleanupRefusal ValidateNormalizedPath(string fullDirectory)
    {
        var root = Path.GetPathRoot(fullDirectory);
        if (string.IsNullOrEmpty(root) || string.Equals(TrimEnd(fullDirectory), TrimEnd(root), PathComparison))
            return AppDataCleanupRefusal.ProtectedPath;

        var userRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        if (userRoots.Where(path => !string.IsNullOrWhiteSpace(path))
            .Any(path => string.Equals(TrimEnd(fullDirectory), TrimEnd(Normalize(path)), PathComparison)
                || IsAncestor(fullDirectory, Normalize(path))))
        {
            return AppDataCleanupRefusal.ProtectedPath;
        }

        var systemRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        if (systemRoots.Where(path => !string.IsNullOrWhiteSpace(path))
            .Any(path => PathsOverlapNormalized(fullDirectory, Normalize(path))))
        {
            return AppDataCleanupRefusal.ProtectedPath;
        }

        return HasReparsePointInExistingPath(fullDirectory)
            ? AppDataCleanupRefusal.ReparsePoint
            : AppDataCleanupRefusal.None;
    }

    private static bool HasReparsePointInExistingPath(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists && IsReparsePoint(current.FullName)) return true;
        }

        return false;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsKnownLogFile(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(name, "login-diagnostic.txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "app.log", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("app-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            || (name.StartsWith("lxmusic-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDeleteIfEmpty(
        string directory,
        string relativePath,
        ICollection<AppDataCleanupFailure> failures,
        Action<AppDataCleanupFailure>? logFailure)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (IsFileSystemOrFormatException(ex))
        {
            AddFailure(failures, logFailure, "delete-empty-directory", relativePath, ex);
        }
    }

    private static void AddFailure(
        ICollection<AppDataCleanupFailure> failures,
        Action<AppDataCleanupFailure>? logFailure,
        string operation,
        string relativePath,
        Exception exception)
    {
        var failure = new AppDataCleanupFailure(operation, relativePath, exception.GetType().Name);
        failures.Add(failure);
        logFailure?.Invoke(failure);
    }

    private static bool IsFileSystemOrFormatException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or ArgumentException
            or JsonException;

    private static string Normalize(string path)
    {
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, PathComparison) ? fullPath : TrimEnd(fullPath);
    }

    private static string TrimEnd(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathsOverlapNormalized(string left, string right) =>
        string.Equals(TrimEnd(left), TrimEnd(right), PathComparison)
        || IsAncestor(left, right)
        || IsAncestor(right, left);

    private static bool IsAncestor(string possibleAncestor, string path)
    {
        var relative = FrameworkCompatibility.GetRelativePath(TrimEnd(possibleAncestor), TrimEnd(path));
        return relative != "."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)
            && !string.Equals(relative, "..", PathComparison)
            && !Path.IsPathRooted(relative);
    }

    private static StringComparison PathComparison =>
        FrameworkCompatibility.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        FrameworkCompatibility.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static AppDataCleanupResult Accepted() =>
        new(true, AppDataCleanupRefusal.None, Array.Empty<AppDataCleanupFailure>(), false);

    private static AppDataCleanupResult Refused(AppDataCleanupRefusal refusal) =>
        new(false, refusal, Array.Empty<AppDataCleanupFailure>(), false);

    private sealed record OwnershipMarker(
        string ApplicationId,
        int SchemaVersion,
        Guid OwnershipId,
        string DirectoryPath);
}
