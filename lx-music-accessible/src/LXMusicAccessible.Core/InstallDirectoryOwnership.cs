using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace LXMusicAccessible.Core;

public enum InstallDirectoryRefusal
{
    None,
    MissingOwnership,
    InvalidOwnership,
    IdentifierMismatch,
    ProtectedPath,
    ReparsePoint
}

public sealed record InstalledFile(string RelativePath, long Length, string Sha256);

public sealed record OwnedInstallDirectory(
    string Path,
    Guid OwnershipId,
    IReadOnlyList<InstalledFile> Files);

public sealed record InstallCleanupResult(
    bool Accepted,
    InstallDirectoryRefusal Refusal,
    IReadOnlyList<string> Failures,
    bool PreservedUnknownContent)
{
    public bool Succeeded => Accepted && Failures.Count == 0;
}

/// <summary>
/// Owns the complete install workflow: a selected parent becomes a dedicated application
/// directory, and uninstall removes only files recorded by a matching ownership manifest.
/// </summary>
public static class InstallDirectoryOwnership
{
    public const string ApplicationDirectoryName = "LXMusicAccessible";
    public const string MarkerFileName = ".lxmusic-install-owner.json";
    public const string ManifestFileName = ".lxmusic-install-manifest.json";
    public const string ApplicationId = "LXMusicAccessible";
    public const int CurrentSchemaVersion = 1;

    public static string DefaultParentDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs");

    public static string ResolveTargetDirectory(string parentDirectory)
    {
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(parentDirectory);
        if (!FrameworkCompatibility.IsPathFullyQualified(parentDirectory))
            throw new InvalidOperationException("The install parent must be an absolute path.");

        var parent = Normalize(parentDirectory);
        var refusal = ValidateParent(parent);
        if (refusal != InstallDirectoryRefusal.None)
            throw new InvalidOperationException($"Refusing unsafe install parent ({refusal}).");

        var target = Normalize(Path.Combine(parent, ApplicationDirectoryName));
        refusal = ValidateTarget(target);
        if (refusal != InstallDirectoryRefusal.None)
            throw new InvalidOperationException($"Refusing unsafe install directory ({refusal}).");
        return target;
    }

    public static OwnedInstallDirectory InstallFromZip(Stream payload, string parentDirectory)
    {
        FrameworkCompatibility.ThrowIfNull(payload);
        var target = ResolveTargetDirectory(parentDirectory);
        var targetExisted = Directory.Exists(target);
        OwnedInstallDirectory? previous = null;

        if (targetExisted)
        {
            var entries = Directory.EnumerateFileSystemEntries(target).ToArray();
            if (entries.Length > 0 && !TryRead(target, out previous))
                throw new InvalidOperationException("An existing non-empty directory cannot be claimed as an installation.");
        }

        var parent = Directory.GetParent(target)?.FullName
            ?? throw new InvalidOperationException("The install directory has no safe parent.");
        Directory.CreateDirectory(parent);
        if (ValidateParent(parent) != InstallDirectoryRefusal.None)
            throw new InvalidOperationException("The install parent became unsafe before staging.");
        var staging = Path.Combine(parent, $".{ApplicationDirectoryName}-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        if (HasReparsePointInExistingPath(staging))
            throw new InvalidOperationException("The installer staging directory is a reparse point.");
        var stagedFiles = new List<string>();

        try
        {
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var relativePath = NormalizeRelativePath(entry.FullName);
                    var destination = GetContainedPath(staging, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = entry.Open();
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output);
                    stagedFiles.Add(relativePath);
                }
            }

            if (stagedFiles.Count == 0)
                throw new InvalidOperationException("The installer payload is empty.");

            Directory.CreateDirectory(target);
            if (ValidateTarget(target) != InstallDirectoryRefusal.None)
                throw new InvalidOperationException("The install directory became unsafe before commit.");
            var previousPaths = previous?.Files
                .Select(file => file.RelativePath)
                .ToHashSet(PathComparer)
                ?? new HashSet<string>(PathComparer);

            foreach (var relativePath in stagedFiles)
            {
                var source = GetContainedPath(staging, relativePath);
                var destination = GetContainedPath(target, relativePath);
                if (HasReparsePointInExistingPath(destination))
                    throw new InvalidOperationException($"The install path contains a reparse point: {relativePath}");
                if (File.Exists(destination) && !previousPaths.Contains(relativePath))
                    throw new InvalidOperationException($"The install directory contains an unowned conflicting file: {relativePath}");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }

            var installedFiles = stagedFiles
                .OrderBy(path => path, PathComparer)
                .Select(path => DescribeFile(path, GetContainedPath(target, path)))
                .ToArray();
            var ownershipId = previous?.OwnershipId ?? Guid.NewGuid();
            WriteJsonAtomically(
                Path.Combine(target, ManifestFileName),
                new InstallManifest(ApplicationId, CurrentSchemaVersion, ownershipId, target, installedFiles));

            if (previous is null)
            {
                // The marker is the commit point: an interrupted first install is never considered owned.
                WriteJsonNew(
                    Path.Combine(target, MarkerFileName),
                    new OwnershipMarker(ApplicationId, CurrentSchemaVersion, ownershipId, target));
            }
            else
            {
                DeleteObsoleteKnownFiles(target, previous.Files, installedFiles);
            }

            return new OwnedInstallDirectory(target, ownershipId, installedFiles);
        }
        catch
        {
            if (!targetExisted && !File.Exists(Path.Combine(target, MarkerFileName)))
                DeleteKnownFilesAndEmptyDirectories(target, stagedFiles);
            throw;
        }
        finally
        {
            DeleteKnownFilesAndEmptyDirectories(staging, stagedFiles);
        }
    }

    public static bool TryRead(string directory, out OwnedInstallDirectory ownedDirectory)
    {
        ownedDirectory = default!;
        try
        {
            if (!FrameworkCompatibility.IsPathFullyQualified(directory)) return false;
            var target = Normalize(directory);
            if (ValidateTarget(target) != InstallDirectoryRefusal.None) return false;

            var markerPath = Path.Combine(target, MarkerFileName);
            var manifestPath = Path.Combine(target, ManifestFileName);
            if (!File.Exists(markerPath) || !File.Exists(manifestPath)) return false;

            var marker = ReadJson<OwnershipMarker>(markerPath);
            var manifest = ReadJson<InstallManifest>(manifestPath);
            if (marker is null || manifest is null
                || marker.ApplicationId != ApplicationId
                || manifest.ApplicationId != ApplicationId
                || marker.SchemaVersion != CurrentSchemaVersion
                || manifest.SchemaVersion != CurrentSchemaVersion
                || marker.OwnershipId == Guid.Empty
                || marker.OwnershipId != manifest.OwnershipId
                || !string.Equals(Normalize(marker.DirectoryPath), target, PathComparison)
                || !string.Equals(Normalize(manifest.DirectoryPath), target, PathComparison)
                || manifest.Files.Count == 0
                || manifest.Files.Any(file => !IsValidFileRecord(target, file)))
            {
                return false;
            }

            var duplicate = manifest.Files
                .GroupBy(file => file.RelativePath, PathComparer)
                .Any(group => group.Count() > 1);
            if (duplicate) return false;

            ownedDirectory = new OwnedInstallDirectory(target, marker.OwnershipId, manifest.Files);
            return true;
        }
        catch (Exception ex) when (IsFileSystemOrFormatException(ex))
        {
            return false;
        }
    }

    public static bool MatchesManifest(string directory, InstalledFile file)
    {
        try
        {
            var target = Normalize(directory);
            return IsValidFileRecord(target, file)
                && FileMatches(GetContainedPath(target, file.RelativePath), file);
        }
        catch (Exception ex) when (IsFileSystemOrFormatException(ex))
        {
            return false;
        }
    }

    public static InstallCleanupResult Clean(string directory, Guid expectedOwnershipId)
    {
        string target;
        try
        {
            if (!FrameworkCompatibility.IsPathFullyQualified(directory))
                return Refused(InstallDirectoryRefusal.ProtectedPath);
            target = Normalize(directory);
        }
        catch (Exception ex) when (IsFileSystemOrFormatException(ex))
        {
            return Refused(InstallDirectoryRefusal.ProtectedPath);
        }

        var pathRefusal = ValidateTarget(target);
        if (pathRefusal != InstallDirectoryRefusal.None) return Refused(pathRefusal);
        if (!Directory.Exists(target)) return Accepted();

        var markerPath = Path.Combine(target, MarkerFileName);
        var manifestPath = Path.Combine(target, ManifestFileName);
        if (!File.Exists(markerPath) || !File.Exists(manifestPath))
            return Refused(InstallDirectoryRefusal.MissingOwnership);
        if (!TryRead(target, out var owned)) return Refused(InstallDirectoryRefusal.InvalidOwnership);
        if (owned.OwnershipId != expectedOwnershipId)
            return Refused(InstallDirectoryRefusal.IdentifierMismatch);

        var failures = new List<string>();
        var preservedUnknownContent = false;
        foreach (var file in owned.Files)
        {
            var path = GetContainedPath(target, file.RelativePath);
            try
            {
                if (!File.Exists(path)) continue;
                if (HasReparsePointInExistingPath(path) || !FileMatches(path, file))
                {
                    preservedUnknownContent = true;
                    continue;
                }
                File.Delete(path);
            }
            catch (Exception ex) when (IsFileSystemOrFormatException(ex))
            {
                failures.Add($"{file.RelativePath}: {ex.GetType().Name}");
            }
        }

        DeleteEmptyManifestDirectories(target, owned.Files, failures);
        if (failures.Count == 0)
        {
            try
            {
                var remaining = Directory.EnumerateFileSystemEntries(target).ToArray();
                var onlyMetadata = remaining.All(path =>
                    string.Equals(path, markerPath, PathComparison)
                    || string.Equals(path, manifestPath, PathComparison));
                if (onlyMetadata)
                {
                    File.Delete(manifestPath);
                    File.Delete(markerPath);
                    Directory.Delete(target, recursive: false);
                }
                else
                {
                    preservedUnknownContent = true;
                }
            }
            catch (Exception ex) when (IsFileSystemOrFormatException(ex))
            {
                failures.Add($".: {ex.GetType().Name}");
            }
        }

        return new InstallCleanupResult(true, InstallDirectoryRefusal.None, failures, preservedUnknownContent);
    }

    public static InstallDirectoryRefusal Validate(string directory)
    {
        try
        {
            if (!FrameworkCompatibility.IsPathFullyQualified(directory)) return InstallDirectoryRefusal.ProtectedPath;
            return ValidateTarget(Normalize(directory));
        }
        catch (Exception ex) when (IsFileSystemOrFormatException(ex))
        {
            return InstallDirectoryRefusal.ProtectedPath;
        }
    }

    private static InstallDirectoryRefusal ValidateParent(string parent)
    {
        var root = Path.GetPathRoot(parent);
        if (string.IsNullOrEmpty(root) || string.Equals(TrimEnd(parent), TrimEnd(root), PathComparison))
            return InstallDirectoryRefusal.ProtectedPath;

        if (ProtectedExactPaths().Any(path => string.Equals(TrimEnd(parent), TrimEnd(path), PathComparison)))
            return InstallDirectoryRefusal.ProtectedPath;
        return HasReparsePointInExistingPath(parent)
            ? InstallDirectoryRefusal.ReparsePoint
            : InstallDirectoryRefusal.None;
    }

    private static InstallDirectoryRefusal ValidateTarget(string target)
    {
        var root = Path.GetPathRoot(target);
        if (string.IsNullOrEmpty(root) || string.Equals(TrimEnd(target), TrimEnd(root), PathComparison))
            return InstallDirectoryRefusal.ProtectedPath;

        if (ProtectedExactPaths().Any(path => string.Equals(TrimEnd(target), TrimEnd(path), PathComparison)))
            return InstallDirectoryRefusal.ProtectedPath;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (new[] { windows, system }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .Any(path => IsAncestor(path, target)))
        {
            return InstallDirectoryRefusal.ProtectedPath;
        }

        return HasReparsePointInExistingPath(target)
            ? InstallDirectoryRefusal.ReparsePoint
            : InstallDirectoryRefusal.None;
    }

    private static IEnumerable<string> ProtectedExactPaths() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
    }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Normalize);

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized)
            || FrameworkCompatibility.IsPathFullyQualified(normalized)
            || normalized.Split(Path.DirectorySeparatorChar).Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe payload path: {path}");
        }
        if (string.Equals(normalized, MarkerFileName, PathComparison)
            || string.Equals(normalized, ManifestFileName, PathComparison))
        {
            throw new InvalidDataException($"Payload path is reserved for ownership metadata: {path}");
        }
        return normalized;
    }

    private static string GetContainedPath(string directory, string relativePath)
    {
        var root = Normalize(directory);
        var path = Path.GetFullPath(Path.Combine(root, NormalizeRelativePath(relativePath)));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, PathComparison))
            throw new InvalidDataException($"Path escapes the installation directory: {relativePath}");
        return path;
    }

    private static InstalledFile DescribeFile(string relativePath, string path) =>
        new(relativePath, new FileInfo(path).Length, Sha256(path));

    private static bool IsValidFileRecord(string target, InstalledFile file)
    {
        if (file.Length < 0 || file.Sha256.Length != 64
            || file.Sha256.Any(character => !Uri.IsHexDigit(character))) return false;
        _ = GetContainedPath(target, file.RelativePath);
        return !string.Equals(file.RelativePath, MarkerFileName, PathComparison)
            && !string.Equals(file.RelativePath, ManifestFileName, PathComparison);
    }

    private static bool FileMatches(string path, InstalledFile file) =>
        File.Exists(path)
        && new FileInfo(path).Length == file.Length
        && string.Equals(Sha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase);

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return FrameworkCompatibility.ToHexString(FrameworkCompatibility.HashData(stream)).ToLowerInvariant();
    }

    private static void DeleteObsoleteKnownFiles(
        string target,
        IEnumerable<InstalledFile> previous,
        IEnumerable<InstalledFile> current)
    {
        var previousFiles = previous.ToArray();
        var currentPaths = current.Select(file => file.RelativePath).ToHashSet(PathComparer);
        foreach (var file in previousFiles.Where(file => !currentPaths.Contains(file.RelativePath)))
        {
            var path = GetContainedPath(target, file.RelativePath);
            try
            {
                if (FileMatches(path, file) && !HasReparsePointInExistingPath(path)) File.Delete(path);
            }
            catch (Exception ex) when (IsFileSystemOrFormatException(ex))
            {
                // A stale file is safer to retain than to turn a successful upgrade into ambiguous ownership.
                System.Diagnostics.Debug.WriteLine($"Obsolete install file was retained: {ex.GetType().Name}");
            }
        }
        DeleteEmptyManifestDirectories(target, previousFiles, failures: null);
    }

    private static void DeleteKnownFilesAndEmptyDirectories(string directory, IEnumerable<string> relativePaths)
    {
        if (!Directory.Exists(directory) || HasReparsePointInExistingPath(directory)) return;
        var files = relativePaths.Distinct(PathComparer).ToArray();
        foreach (var relativePath in files)
        {
            var path = GetContainedPath(directory, relativePath);
            if (File.Exists(path) && !HasReparsePointInExistingPath(path)) File.Delete(path);
        }
        DeleteEmptyManifestDirectories(
            directory,
            files.Select(path => new InstalledFile(path, 0, new string('0', 64))),
            failures: null);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, recursive: false);
    }

    private static void DeleteEmptyManifestDirectories(
        string target,
        IEnumerable<InstalledFile> files,
        ICollection<string>? failures)
    {
        var directories = files
            .SelectMany(file => ManifestParentDirectories(target, file.RelativePath))
            .Distinct(PathComparer)
            .OrderByDescending(path => path!.Length)
            .ToArray();
        foreach (var directory in directories)
        {
            try
            {
                if (Directory.Exists(directory)
                    && !HasReparsePointInExistingPath(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch (Exception ex) when (IsFileSystemOrFormatException(ex))
            {
                failures?.Add($"{FrameworkCompatibility.GetRelativePath(target, directory!)}: {ex.GetType().Name}");
            }
        }
    }

    private static IEnumerable<string> ManifestParentDirectories(string target, string relativePath)
    {
        for (var directory = Path.GetDirectoryName(GetContainedPath(target, relativePath));
             directory is not null && !string.Equals(directory, target, PathComparison);
             directory = Path.GetDirectoryName(directory))
        {
            yield return directory;
        }
    }

    private static void WriteJsonNew<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value);
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            WriteJsonNew(temporary, value);
            FrameworkCompatibility.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static T? ReadJson<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream);
    }

    private static bool HasReparsePointInExistingPath(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
        var directory = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path).Directory;
        for (var current = directory; current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
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

    private static InstallCleanupResult Accepted() =>
        new(true, InstallDirectoryRefusal.None, Array.Empty<string>(), false);

    private static InstallCleanupResult Refused(InstallDirectoryRefusal refusal) =>
        new(false, refusal, Array.Empty<string>(), false);

    private sealed record OwnershipMarker(
        string ApplicationId,
        int SchemaVersion,
        Guid OwnershipId,
        string DirectoryPath);

    private sealed record InstallManifest(
        string ApplicationId,
        int SchemaVersion,
        Guid OwnershipId,
        string DirectoryPath,
        IReadOnlyList<InstalledFile> Files);
}
