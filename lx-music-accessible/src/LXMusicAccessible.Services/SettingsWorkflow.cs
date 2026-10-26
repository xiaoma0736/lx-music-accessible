using System.Text.Json;
using System.Threading;
using LXMusicAccessible.Data;
using Microsoft.Data.Sqlite;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Services;

internal interface ISettingsStore
{
    Task<UserSettings?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken);
}

internal sealed class DataSettingsStore(IDataService data) : ISettingsStore
{
    public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) =>
        data.GetSettingsAsync(cancellationToken);

    public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) =>
        data.SaveSettingsAsync(settings, cancellationToken);
}

/// <summary>
/// Immutable, versioned settings state. The mutable persistence model is cloned at the seam.
/// </summary>
public sealed class SettingsEditState
{
    private readonly string _json;

    internal SettingsEditState(long version, UserSettings settings)
    {
        Version = version;
        _json = JsonSerializer.Serialize(settings);
    }

    public long Version { get; }
    public UserSettings Settings => CloneSettings();

    internal UserSettings CloneSettings() =>
        JsonSerializer.Deserialize<UserSettings>(_json)
        ?? throw new InvalidOperationException("Settings state could not be restored.");

    internal bool HasSameContent(SettingsEditState other) =>
        string.Equals(_json, other._json, StringComparison.Ordinal);
}

public sealed record SettingsChangedEventArgs(
    SettingsEditState? Previous,
    SettingsEditState Current);

public enum SettingsSaveStatus
{
    Saved,
    Unchanged,
    Stale,
    Failed
}

public readonly record struct SettingsSaveResult(
    SettingsSaveStatus Status,
    long Version,
    string ErrorType = "")
{
    public bool Succeeded => Status is SettingsSaveStatus.Saved
        or SettingsSaveStatus.Unchanged
        or SettingsSaveStatus.Stale;
}

public sealed record SettingsEditResult(
    bool Accepted,
    SettingsEditState State,
    IReadOnlyList<string> Errors);

public enum HotkeyEditStatus
{
    Applied,
    Unchanged,
    Invalid,
    Conflict,
    RegistrationFailed,
    RollbackFailed,
    PersistenceFailed
}

public sealed record HotkeyEditResult(
    HotkeyEditStatus Status,
    SettingsEditState State,
    IReadOnlyList<Hotkey> Conflicts,
    SettingsSaveResult Save)
{
    public bool Applied => Status is HotkeyEditStatus.Applied or HotkeyEditStatus.Unchanged;
}

public sealed record DataDirectoryChangeResult(bool Succeeded, string Path, string Message, bool CleanupSucceeded = true, IReadOnlyList<string>? CleanupFailures = null);

/// <summary>
/// Deep settings module: defaults, immutable editing, validation, versioned persistence,
/// hotkey conflicts and compensated native registration all live behind this interface.
/// </summary>
public sealed class SettingsWorkflow : IDisposable
{
    private static readonly HashSet<string> Qualities = new(StringComparer.OrdinalIgnoreCase)
    {
        "128k", "192k", "320k", "flac", "flac24bit", "hires", "atmos", "atmos_plus",
        "master", "jyeffect", "sky", "dolby", "jymaster"
    };

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _dataDirectoryGate = new(1, 1);
    private readonly ISettingsStore _store;
    private readonly HotkeyManagerService _hotkeys;
    private readonly ILogger<SettingsWorkflow> _logger;
    private SettingsEditState _current;
    private long _version;
    private long _persistedVersion = -1;
    private int _disposed;

    public SettingsWorkflow(IDataService data, ILoggerFactory loggerFactory)
        : this(
            new DataSettingsStore(data),
            new HotkeyManagerService(
                loggerFactory.CreateLogger<HotkeyManagerService>(),
                new Win32GlobalHotkeyAdapter()),
            loggerFactory.CreateLogger<SettingsWorkflow>())
    {
    }

    internal SettingsWorkflow(
        ISettingsStore store,
        HotkeyManagerService hotkeys,
        ILogger<SettingsWorkflow> logger)
    {
        _store = store;
        _hotkeys = hotkeys;
        _logger = logger;
        _hotkeys.RegistrationFailed += (_, e) => LastRegistrationFailures = e.FailedHotkeys;
        var settings = Normalize(new UserSettings());
        settings.CustomHotkeys = _hotkeys.Snapshot().ToList();
        _current = new SettingsEditState(0, settings);
    }

    /// <summary>
    /// The enabled globals whose native registration failed in the most recent attach. Production
    /// resolves WinFormsShell after startup registration has already run, so the shell reads this
    /// instead of the event, which fires before any shell subscriber exists.
    /// </summary>
    public IReadOnlyList<Hotkey> LastRegistrationFailures { get; private set; } = Array.Empty<Hotkey>();

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed
    {
        add => _hotkeys.HotkeyPressed += value;
        remove => _hotkeys.HotkeyPressed -= value;
    }

    public event EventHandler<HotkeysRegistrationFailedEventArgs>? HotkeysRegistrationFailed
    {
        add => _hotkeys.RegistrationFailed += value;
        remove => _hotkeys.RegistrationFailed -= value;
    }

    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    public SettingsEditState Current
    {
        get { lock (_stateGate) return _current; }
    }

    public string DataDirectory => AppDataPaths.ResolveDataDir();

    public async Task<DataDirectoryChangeResult> ChangeDataDirectoryAsync(string selectedParent, CancellationToken cancellationToken = default)
    {
        if (!await _dataDirectoryGate.WaitAsync(0, cancellationToken))
            return new(false, DataDirectory, "Data directory change is already running.");
        try
        {
            return await Task.Run(() => ChangeDataDirectoryInRoot(selectedParent, AppDataPaths.LocalAppDataRoot), cancellationToken);
        }
        finally
        {
            _dataDirectoryGate.Release();
        }
    }

    public DataDirectoryChangeResult ChangeDataDirectory(string selectedParent) =>
        ChangeDataDirectoryInRoot(selectedParent, AppDataPaths.LocalAppDataRoot);

    internal DataDirectoryChangeResult ChangeDataDirectoryInRoot(
        string selectedParent,
        string localApplicationDataDirectory)
    {
        string? createdTarget = null;
        List<string> cleanupFailures = new();
        bool cleanupSucceeded = true;
        var copiedFiles = new List<string>();
        try
        {
            FrameworkCompatibility.ThrowIfNullOrWhiteSpace(selectedParent);
            var current = Path.GetFullPath(AppDataPaths.ResolveDataDir(localApplicationDataDirectory));
            var parent = Path.GetFullPath(selectedParent.Trim());
            var candidate = Path.GetFullPath(Path.Combine(parent, AppDataPaths.DefaultDirName));
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
            {
                return new(true, current, "已选择当前数据目录");
            }

            var target = AppDataPaths.CreateOwnedDataDirectory(parent, localApplicationDataDirectory).Path;
            createdTarget = target;
            CopyDataDirectoryContents(current, target, copiedFiles);
            AppDataPaths.WriteDataDirPointer(target, localApplicationDataDirectory);
            return new(true, target, "数据目录已设置，请重启应用后完全生效");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or SqliteException)
        {
            if (createdTarget is not null)
            {
                cleanupSucceeded = TryCleanupPartialCopy(createdTarget, copiedFiles, cleanupFailures);
                if (cleanupSucceeded)
                {
                    try
                    {
                        var marker = Path.Combine(createdTarget, AppDataOwnership.MarkerFileName);
                        if (File.Exists(marker)) File.Delete(marker);
                        if (Directory.Exists(createdTarget)
                            && !Directory.EnumerateFileSystemEntries(createdTarget).Any())
                        {
                            Directory.Delete(createdTarget, recursive: false);
                        }
                    }
                    catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                    {
                        cleanupSucceeded = false;
                        cleanupFailures.Add("ownership-marker: " + cleanupException.GetType().Name);
                    }
                }
            }
            _logger.LogWarning("Data directory change failed: {ErrorType}", exception.GetType().Name);
            var message = cleanupSucceeded
                ? "设置数据目录失败，未写入任何更改"
                : "设置数据目录失败；请关闭占用程序后手动删除目标目录再重试";
            return new(false, DataDirectory, message, cleanupSucceeded, cleanupFailures);
        }
    }

    private static void CopyDataDirectoryContents(
        string source,
        string target,
        ICollection<string> copiedFiles)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (name is AppDataPaths.PointerFileName or AppDataPaths.LegacyPointerFileName
                or AppDataOwnership.MarkerFileName)
                continue;

            var destination = Path.Combine(target, name);
            if (IsDatabaseFile(name))
            {
                // SQLite sidecars are never copied; a checkpointed backup API copy is the consistent
                // database image. This makes a failed attempt retryable after explicit cleanup.
                if (IsMainDatabase(name))
                {
                    SqliteDatabaseBackup.Copy(entry, destination);
                    copiedFiles.Add(destination);
                }
                continue;
            }

            if (File.Exists(entry))
            {
                var info = new FileInfo(entry);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (File.Exists(destination)) throw new IOException("Data directory destination already exists.");
                File.Copy(entry, destination, overwrite: false);
                copiedFiles.Add(destination);
            }
            else if (Directory.Exists(entry))
            {
                var info = new DirectoryInfo(entry);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                Directory.CreateDirectory(destination);
                CopyDataDirectoryContents(entry, destination, copiedFiles);
            }
        }
    }

    private static bool IsDatabaseFile(string name) =>
        name.Equals("lxmusic.db", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("lxmusic.db-", StringComparison.OrdinalIgnoreCase);

    private static bool IsMainDatabase(string name) =>
        string.Equals(name, "lxmusic.db", StringComparison.OrdinalIgnoreCase);
    private static bool TryCleanupPartialCopy(string target, IReadOnlyCollection<string> copiedFiles, ICollection<string> failures)
    {
        if (!Directory.Exists(target)) return true;
        var succeeded = true;
        foreach (var file in copiedFiles)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                succeeded = false;
                failures.Add(Path.GetFileName(file) + ": " + exception.GetType().Name);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                succeeded = false;
                failures.Add(Path.GetFileName(directory) + ": " + exception.GetType().Name);
            }
        }

        return succeeded;
    }


    public async Task<SettingsEditState> LoadAsync(
        IntPtr windowHandle,
        CancellationToken cancellationToken = default)
    {
        var settings = Normalize(await _store.LoadAsync(cancellationToken) ?? new UserSettings());
        var requiresHotkeyMigration = settings.HotkeySchemaVersion < 3;

        // Settings below schema 2 contain the legacy global defaults. Remove only the
        // stored globals so they are rebuilt from schema 2; local customizations stay.
        if (settings.HotkeySchemaVersion < 2)
            settings.CustomHotkeys = settings.CustomHotkeys
                .Where(item => !item.IsGlobal)
                .ToList();

        // Schema 3 gives the previously unbound hide command a usable global default. Remove only an
        // old default "unbound" record; a user-defined hide binding and an unbound key that collides
        // with an existing action remain untouched.
        if (settings.HotkeySchemaVersion < 3)
            settings.CustomHotkeys = settings.CustomHotkeys
                .Where(item => !(item.ActionId == HotkeyActions.ToggleWindow
                    && item.IsGlobal
                    && (!item.IsEnabled || item.Key == PlatformKey.None)))
                .ToList();

        SettingsEditState current;
        lock (_stateGate)
        {
            settings.CustomHotkeys = _hotkeys.Load(settings.CustomHotkeys).ToList();
            settings.HotkeySchemaVersion = 3;
            current = _current = new SettingsEditState(++_version, settings);
            // A migrated state is deliberately dirty so the next explicit save stores
            // schema 3 without an automatic write during startup.
            _persistedVersion = requiresHotkeyMigration ? _version - 1 : _version;
        }
        LastRegistrationFailures = Array.Empty<Hotkey>();
        _hotkeys.AttachWindow(windowHandle);
        PublishSettingsChanged(previous: null, current);
        return current;
    }

    public SettingsEditResult Edit(Action<UserSettings> edit)
    {
        FrameworkCompatibility.ThrowIfNull(edit);
        SettingsEditState basis;
        SettingsEditState current;
        lock (_stateGate)
        {
            basis = _current;
            var candidate = basis.CloneSettings();
            edit(candidate);
            candidate = Normalize(candidate);
            candidate.CustomHotkeys = _hotkeys.Snapshot().ToList();
            var errors = Validate(candidate);
            if (errors.Count > 0)
                return new(false, basis, errors);

            current = new SettingsEditState(_version + 1, candidate);
            if (basis.HasSameContent(current))
                return new(true, basis, Array.Empty<string>());
            _version++;
            _current = current;
        }
        PublishSettingsChanged(basis, current);
        return new(true, current, Array.Empty<string>());
    }

    public Task<HotkeyEditResult> ReplaceHotkeyAsync(
        string actionId,
        bool isGlobal,
        PlatformKey key,
        PlatformModifiers modifiers,
        CancellationToken cancellationToken = default) =>
        ApplyHotkeyAsync(
            () => _hotkeys.Replace(actionId, isGlobal, key, modifiers),
            cancellationToken);

    public Task<HotkeyEditResult> ClearHotkeyAsync(
        string actionId,
        bool isGlobal,
        CancellationToken cancellationToken = default) =>
        ReplaceHotkeyAsync(actionId, isGlobal, PlatformKey.None, PlatformModifiers.None, cancellationToken);

    public Task<HotkeyEditResult> ResetHotkeysAsync(
        CancellationToken cancellationToken = default) =>
        ApplyHotkeyAsync(_hotkeys.Reset, cancellationToken);

    public IReadOnlyList<Hotkey> GetHotkeys() => _hotkeys.Snapshot();

    public bool IsGlobalHotkeyRegistered(Hotkey hotkey) =>
        hotkey is null || !hotkey.IsGlobal || !hotkey.IsEnabled || hotkey.Key == PlatformKey.None
        || _hotkeys.IsGlobalRegistered(hotkey.ActionId);

    public Hotkey? MatchFallback(PlatformKey key, PlatformModifiers modifiers) =>
        _hotkeys.MatchFallback(key, modifiers);

    public Hotkey? MatchLocalHotkey(PlatformKey key, PlatformModifiers modifiers) =>
        _hotkeys.MatchLocal(key, modifiers);

    public void RaiseGlobalHotkey(int id) => _hotkeys.RaiseGlobal(id);

    public void UnregisterAllGlobal() => _hotkeys.UnregisterAllGlobal();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        UnregisterAllGlobal();
        _saveGate.Dispose();
        _dataDirectoryGate.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<SettingsSaveResult> SaveAsync(
        SettingsEditState state,
        CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(state);
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            if (state.Version < Volatile.Read(ref _persistedVersion))
                return new(SettingsSaveStatus.Stale, state.Version);

            var target = state;
            var wrote = false;
            while (true)
            {
                lock (_stateGate)
                {
                    if (_current.Version > target.Version)
                        target = _current;
                    if (target.Version <= _persistedVersion)
                    {
                        return new(
                            wrote ? SettingsSaveStatus.Saved : SettingsSaveStatus.Unchanged,
                            target.Version);
                    }
                }

                try
                {
                    await _store.SaveAsync(target.CloneSettings(), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        "Settings persistence failed for version {Version}: {ErrorType}",
                        target.Version,
                        exception.GetType().Name);
                    return new(
                        SettingsSaveStatus.Failed,
                        target.Version,
                        exception.GetType().Name);
                }

                wrote = true;
                lock (_stateGate)
                {
                    _persistedVersion = Math.Max(_persistedVersion, target.Version);
                    if (_current.Version <= _persistedVersion)
                        return new(SettingsSaveStatus.Saved, target.Version);
                    target = _current;
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<HotkeyEditResult> ApplyHotkeyAsync(
        Func<HotkeyApplyResult> apply,
        CancellationToken cancellationToken)
    {
        HotkeyApplyResult native;
        SettingsEditState state;
        SettingsEditState? previous = null;
        lock (_stateGate)
        {
            native = apply();
            if (native.Status == HotkeyApplyStatus.Applied)
            {
                previous = _current;
                var settings = _current.CloneSettings();
                settings.CustomHotkeys = native.Hotkeys.ToList();
                _current = new SettingsEditState(++_version, settings);
            }
            state = _current;
        }

        if (previous is not null) PublishSettingsChanged(previous, state);

        var status = Map(native.Status);
        if (!native.Applied || native.Status == HotkeyApplyStatus.Unchanged)
            return new(status, state, native.Conflicts, new(SettingsSaveStatus.Unchanged, state.Version));

        var save = await SaveAsync(state, cancellationToken);
        return new(
            save.Succeeded ? status : HotkeyEditStatus.PersistenceFailed,
            state,
            native.Conflicts,
            save);
    }

    private static HotkeyEditStatus Map(HotkeyApplyStatus status) => status switch
    {
        HotkeyApplyStatus.Applied => HotkeyEditStatus.Applied,
        HotkeyApplyStatus.Unchanged => HotkeyEditStatus.Unchanged,
        HotkeyApplyStatus.Invalid => HotkeyEditStatus.Invalid,
        HotkeyApplyStatus.Conflict => HotkeyEditStatus.Conflict,
        HotkeyApplyStatus.RegistrationFailed => HotkeyEditStatus.RegistrationFailed,
        HotkeyApplyStatus.RollbackFailed => HotkeyEditStatus.RollbackFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static UserSettings Normalize(UserSettings settings)
    {
        settings.AccessibilitySettings ??= new AccessibilitySettings();
        settings.CustomHotkeys ??= new List<Hotkey>();
        settings.Playback ??= new PlaybackSessionState();
        settings.LyricOffsets ??= new Dictionary<string, int>();
        var mode = settings.DefaultPlayMode ?? (settings.ShuffleMode
            ? PlayMode.Shuffle
            : settings.LoopMode ? PlayMode.Loop : PlayMode.Sequential);
        if (!Enum.IsDefined(typeof(PlayMode), mode)) mode = PlayMode.Sequential;
        settings.DefaultPlayMode = mode;
        settings.ShuffleMode = mode == PlayMode.Shuffle;
        settings.LoopMode = mode is PlayMode.Loop or PlayMode.SingleLoop;
        settings.BatchDownloadDelaySeconds = settings.BatchDownloadDelaySeconds switch
        {
            < 0 => 0,
            > 60 => 60,
            _ => settings.BatchDownloadDelaySeconds
        };
        settings.DownloadSpeedLimitKbps = settings.DownloadSpeedLimitKbps switch
        {
            <= 0 => 0,
            < 32 => 32,
            > 100000 => 100000,
            _ => settings.DownloadSpeedLimitKbps
        };
        if (!Qualities.Contains(settings.DefaultPlayQuality)) settings.DefaultPlayQuality = "128k";
        if (!Qualities.Contains(settings.DefaultDownloadQuality)) settings.DefaultDownloadQuality = "128k";
        if (string.IsNullOrWhiteSpace(settings.DefaultSearchSource)) settings.DefaultSearchSource = "all";
        if (string.IsNullOrWhiteSpace(settings.DefaultSourceId)) settings.DefaultSourceId = "netease";
        if (string.IsNullOrWhiteSpace(settings.FavoritePlaylistName)) settings.FavoritePlaylistName = "网络歌曲收藏";
        return settings;
    }

    private static IReadOnlyList<string> Validate(UserSettings settings)
    {
        var errors = new List<string>();
        if (settings.DefaultVolume is < 0 or > 1) errors.Add("音量必须在 0 到 1 之间。");
        if (settings.WindowWidth < 320 || settings.WindowHeight < 240)
            errors.Add("窗口尺寸无效。");
        if (!string.IsNullOrWhiteSpace(settings.DownloadPath)
            && !FrameworkCompatibility.IsPathFullyQualified(settings.DownloadPath))
            errors.Add("下载目录必须是绝对路径。");
        if (!string.IsNullOrWhiteSpace(settings.ThirdPartyPlayerPath)
            && !FrameworkCompatibility.IsPathFullyQualified(settings.ThirdPartyPlayerPath))
            errors.Add("第三方播放器路径必须是绝对路径。");
        if (settings.AccessibilitySettings.AnnouncementRate is < 0.5 or > 2)
            errors.Add("朗读语速必须在 0.5 到 2 之间。");
        return errors;
    }

    private void PublishSettingsChanged(SettingsEditState? previous, SettingsEditState current)
    {
        foreach (EventHandler<SettingsChangedEventArgs> handler in
            SettingsChanged?.GetInvocationList() ?? [])
        {
            try { handler(this, new(previous, current)); }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Settings observer failed for version {Version}: {ErrorType}",
                    current.Version,
                    exception.GetType().Name);
            }
        }
    }
}
