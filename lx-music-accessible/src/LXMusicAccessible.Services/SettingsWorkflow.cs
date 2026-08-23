using System.Text.Json;
using System.Windows.Input;
using LXMusicAccessible.Data;
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
}

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

/// <summary>
/// Deep settings module: defaults, immutable editing, validation, versioned persistence,
/// hotkey conflicts and compensated native registration all live behind this interface.
/// </summary>
public sealed class SettingsWorkflow
{
    private static readonly HashSet<string> Qualities = new(StringComparer.OrdinalIgnoreCase)
    {
        "128k", "192k", "320k", "flac", "hires", "atmos", "atmos_plus",
        "master", "jyeffect", "sky", "dolby", "jymaster"
    };

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ISettingsStore _store;
    private readonly HotkeyManagerService _hotkeys;
    private readonly ILogger<SettingsWorkflow> _logger;
    private SettingsEditState _current;
    private long _version;
    private long _persistedVersion = -1;

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
        var settings = Normalize(new UserSettings());
        settings.CustomHotkeys = _hotkeys.Snapshot().ToList();
        _current = new SettingsEditState(0, settings);
    }

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

    public SettingsEditState Current
    {
        get { lock (_stateGate) return _current; }
    }

    public async Task<SettingsEditState> LoadAsync(
        IntPtr windowHandle,
        CancellationToken cancellationToken = default)
    {
        var settings = Normalize(await _store.LoadAsync(cancellationToken) ?? new UserSettings());
        lock (_stateGate)
        {
            settings.CustomHotkeys = _hotkeys.Load(settings.CustomHotkeys).ToList();
            _current = new SettingsEditState(++_version, settings);
            _persistedVersion = _version;
        }
        _hotkeys.AttachWindow(windowHandle);
        return Current;
    }

    public SettingsEditResult Edit(Action<UserSettings> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        SettingsEditState basis;
        lock (_stateGate) basis = _current;
        var candidate = basis.CloneSettings();
        edit(candidate);
        candidate = Normalize(candidate);
        candidate.CustomHotkeys = _hotkeys.Snapshot().ToList();
        var errors = Validate(candidate);
        if (errors.Count > 0)
            return new(false, basis, errors);

        lock (_stateGate)
        {
            _current = new SettingsEditState(++_version, candidate);
            return new(true, _current, Array.Empty<string>());
        }
    }

    public Task<HotkeyEditResult> ReplaceHotkeyAsync(
        string actionId,
        bool isGlobal,
        Key key,
        ModifierKeys modifiers,
        CancellationToken cancellationToken = default) =>
        ApplyHotkeyAsync(
            () => _hotkeys.Replace(actionId, isGlobal, key, modifiers),
            cancellationToken);

    public Task<HotkeyEditResult> ClearHotkeyAsync(
        string actionId,
        bool isGlobal,
        CancellationToken cancellationToken = default) =>
        ReplaceHotkeyAsync(actionId, isGlobal, Key.None, ModifierKeys.None, cancellationToken);

    public Task<HotkeyEditResult> ResetHotkeysAsync(
        CancellationToken cancellationToken = default) =>
        ApplyHotkeyAsync(_hotkeys.Reset, cancellationToken);

    public IReadOnlyList<Hotkey> GetHotkeys() => _hotkeys.Snapshot();

    public bool IsGlobalHotkeyRegistered(Hotkey hotkey) =>
        hotkey is null || !hotkey.IsGlobal || !hotkey.IsEnabled || hotkey.Key == Key.None
        || _hotkeys.IsGlobalRegistered(hotkey.ActionId);

    public Hotkey? MatchFallback(Key key, ModifierKeys modifiers) =>
        _hotkeys.MatchFallback(key, modifiers);

    public Hotkey? MatchLocalHotkey(Key key, ModifierKeys modifiers) =>
        _hotkeys.MatchLocal(key, modifiers);

    public void RaiseGlobalHotkey(int id) => _hotkeys.RaiseGlobal(id);

    public void UnregisterAllGlobal() => _hotkeys.UnregisterAllGlobal();

    public async Task<SettingsSaveResult> SaveAsync(
        SettingsEditState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
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
        lock (_stateGate)
        {
            native = apply();
            if (native.Status == HotkeyApplyStatus.Applied)
            {
                var settings = _current.CloneSettings();
                settings.CustomHotkeys = native.Hotkeys.ToList();
                _current = new SettingsEditState(++_version, settings);
            }
            state = _current;
        }

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
            && !Path.IsPathFullyQualified(settings.DownloadPath))
            errors.Add("下载目录必须是绝对路径。");
        if (settings.AccessibilitySettings.AnnouncementRate is < 0.5 or > 2)
            errors.Add("朗读语速必须在 0.5 到 2 之间。");
        return errors;
    }
}
