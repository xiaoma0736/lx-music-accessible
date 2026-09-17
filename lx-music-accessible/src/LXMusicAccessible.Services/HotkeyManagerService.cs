using System.Runtime.InteropServices;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Services;

internal interface IGlobalHotkeyAdapter
{
    int LastRegisterError { get; }

    bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers);
    bool Unregister(IntPtr windowHandle, int id);
}

internal sealed class Win32GlobalHotkeyAdapter : IGlobalHotkeyAdapter
{
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModWindows = 0x8;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public int LastRegisterError { get; private set; }

    public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers)
    {
        LastRegisterError = 0;
        var virtualKey = ToVirtualKey(key);
        if (virtualKey == 0)
        {
            LastRegisterError = 1400;
            return false;
        }
        var registered = RegisterHotKey(
            windowHandle,
            id,
            ToFlags(modifiers) | ModNoRepeat,
            virtualKey);
        if (!registered)
            LastRegisterError = Marshal.GetLastWin32Error();
        return registered;
    }

    public bool Unregister(IntPtr windowHandle, int id) => UnregisterHotKey(windowHandle, id);

    /// <summary>
    /// Persisted hotkeys keep the legacy menu-era numbering (<c>Home</c> is 22), while
    /// <c>RegisterHotKey</c> and WM_KEYDOWN need Win32 virtual-key codes (<c>VK_HOME</c> is 0x24).
    /// Sending the raw number silently binds the wrong key or fails, so native registration and
    /// keyboard dispatch share one translation table. A zero result means the key cannot be
    /// registered natively; the caller reports that instead of registering a wrong binding.
    /// </summary>
    internal static uint ToVirtualKey(PlatformKey key) => KeyboardKeys.ToVirtualKey(key);

    private static uint ToFlags(PlatformModifiers modifiers)
    {
        uint flags = 0;
        if ((modifiers & PlatformModifiers.Alt) != 0) flags |= ModAlt;
        if ((modifiers & PlatformModifiers.Control) != 0) flags |= ModControl;
        if ((modifiers & PlatformModifiers.Shift) != 0) flags |= ModShift;
        if ((modifiers & PlatformModifiers.Windows) != 0) flags |= ModWindows;
        return flags;
    }
}

internal enum HotkeyApplyStatus
{
    Applied,
    Unchanged,
    Invalid,
    Conflict,
    RegistrationFailed,
    RollbackFailed
}

internal sealed record HotkeyApplyResult(
    HotkeyApplyStatus Status,
    IReadOnlyList<Hotkey> Hotkeys,
    IReadOnlyList<Hotkey> Conflicts)
{
    public bool Applied => Status is HotkeyApplyStatus.Applied or HotkeyApplyStatus.Unchanged;
}

/// <summary>
/// Owns hotkey defaults, matching, conflict validation and compensated Win32 registration.
/// Only <see cref="SettingsWorkflow"/> exposes this implementation to application callers.
/// </summary>
internal sealed class HotkeyManagerService
{
    private readonly ILogger<HotkeyManagerService> _logger;
    private readonly IGlobalHotkeyAdapter _native;
    private readonly List<Hotkey> _hotkeys = new();
    private readonly Dictionary<int, Hotkey> _registered = new();
    private IntPtr _windowHandle;

    internal event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;
    internal event EventHandler<HotkeysRegistrationFailedEventArgs>? RegistrationFailed;

    internal HotkeyManagerService(
        ILogger<HotkeyManagerService> logger,
        IGlobalHotkeyAdapter native)
    {
        _logger = logger;
        _native = native;
        _hotkeys.AddRange(BuildDefaults());
    }

    internal IReadOnlyList<Hotkey> Load(IEnumerable<Hotkey>? saved)
    {
        _hotkeys.Clear();
        _hotkeys.AddRange(MergeWithDefaults(saved));
        return Snapshot();
    }

    internal IReadOnlyList<Hotkey> AttachWindow(IntPtr windowHandle)
    {
        UnregisterAllGlobal();
        _windowHandle = windowHandle;
        if (_windowHandle == IntPtr.Zero) return Array.Empty<Hotkey>();

        var failed = new List<Hotkey>();
        var failureDetails = new List<string>();
        foreach (var (hotkey, id) in EnabledGlobals(_hotkeys))
        {
            if (_native.Register(_windowHandle, id, hotkey.Key, hotkey.Modifiers))
            {
                _registered[id] = Clone(hotkey);
            }
            else
            {
                failureDetails.Add($"{hotkey.ActionId}:{_native.LastRegisterError}");
                failed.Add(Clone(hotkey));
            }
        }

        if (failed.Count > 0)
        {
            _logger.LogWarning(
                "Global hotkey initialization failed for {Count} bindings: {Details}",
                failed.Count,
                string.Join(", ", failureDetails));
            RegistrationFailed?.Invoke(
                this,
                new HotkeysRegistrationFailedEventArgs { FailedHotkeys = failed });
        }
        return failed;
    }

    internal HotkeyApplyResult Replace(
        string actionId,
        bool isGlobal,
        PlatformKey key,
        PlatformModifiers modifiers)
    {
        var candidate = Snapshot().Select(Clone).ToList();
        var target = candidate.FirstOrDefault(item =>
            item.ActionId == actionId && item.IsGlobal == isGlobal);
        if (target is null)
            return Result(HotkeyApplyStatus.Invalid, candidate);

        if (target.Key == key
            && target.Modifiers == modifiers
            && target.IsEnabled == (key != PlatformKey.None))
        {
            return Result(HotkeyApplyStatus.Unchanged, candidate);
        }

        target.Key = key;
        target.Modifiers = key == PlatformKey.None ? PlatformModifiers.None : modifiers;
        target.IsEnabled = key != PlatformKey.None;
        return TryApply(candidate);
    }

    internal HotkeyApplyResult Reset() => TryApply(BuildDefaults());

    internal IReadOnlyList<Hotkey> Snapshot() => _hotkeys.Select(Clone).ToArray();

    /// <summary>
    /// True only while the native registration for an enabled global binding is live, so a missing
    /// message window or a failed RegisterHotKey reports "not registered" instead of claiming the
    /// binding works.
    /// </summary>
    internal bool IsGlobalRegistered(string actionId) =>
        _windowHandle != IntPtr.Zero
        && _registered.Values.Any(item => item.ActionId == actionId && item.IsGlobal);

    internal Hotkey? MatchFallback(PlatformKey key, PlatformModifiers modifiers)
    {
        foreach (var hotkey in _hotkeys)
        {
            if (!hotkey.IsEnabled || !hotkey.IsGlobal || hotkey.Key == PlatformKey.None) continue;
            if (hotkey.Key != key || hotkey.Modifiers != modifiers) continue;
            if (_registered.Values.Any(item => SameIdentity(item, hotkey))) continue;
            return Clone(hotkey);
        }
        return null;
    }

    internal Hotkey? MatchLocal(PlatformKey key, PlatformModifiers modifiers)
    {
        var match = _hotkeys.FirstOrDefault(item =>
            item.IsEnabled
            && !item.IsGlobal
            && item.Key == key
            && item.Modifiers == modifiers);
        return match is null ? null : Clone(match);
    }

    internal void RaiseGlobal(int id)
    {
        if (!_registered.TryGetValue(id, out var hotkey)) return;
        HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs
        {
            Hotkey = Clone(hotkey),
            PressedTime = DateTime.Now,
            Key = hotkey.Key,
            Modifiers = hotkey.Modifiers
        });
    }

    internal void UnregisterAllGlobal()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            foreach (var id in _registered.Keys.ToArray())
                _native.Unregister(_windowHandle, id);
        }
        _registered.Clear();
    }

    private HotkeyApplyResult TryApply(IReadOnlyList<Hotkey> candidate)
    {
        var validation = Validate(candidate);
        if (validation.Status != HotkeyApplyStatus.Applied)
            return validation;

        if (SameBindings(_hotkeys, candidate))
            return Result(HotkeyApplyStatus.Unchanged, candidate);

        if (_windowHandle != IntPtr.Zero)
        {
            var changedGlobals = candidate
                .Select((hotkey, id) => (hotkey, id))
                .Where(item => item.hotkey.IsGlobal
                    && !SameBinding(_hotkeys[item.id], item.hotkey))
                .ToArray();
            var nativeResult = changedGlobals.Length switch
            {
                0 => HotkeyApplyStatus.Applied,
                1 => ReplaceNativeBinding(changedGlobals[0].id, changedGlobals[0].hotkey),
                _ => ReplaceNative(candidate)
            };
            if (nativeResult != HotkeyApplyStatus.Applied)
                return Result(nativeResult, Snapshot());
        }

        _hotkeys.Clear();
        _hotkeys.AddRange(candidate.Select(Clone));
        return Result(HotkeyApplyStatus.Applied, Snapshot());
    }

    private HotkeyApplyStatus ReplaceNativeBinding(int id, Hotkey candidate)
    {
        var hadPrevious = _registered.TryGetValue(id, out var previous);
        if (hadPrevious && !_native.Unregister(_windowHandle, id))
            return HotkeyApplyStatus.RegistrationFailed;
        _registered.Remove(id);

        if (!candidate.IsEnabled || candidate.Key == PlatformKey.None)
            return HotkeyApplyStatus.Applied;

        if (_native.Register(_windowHandle, id, candidate.Key, candidate.Modifiers))
        {
            _registered[id] = Clone(candidate);
            return HotkeyApplyStatus.Applied;
        }

        if (!hadPrevious)
            return HotkeyApplyStatus.RegistrationFailed;
        if (_native.Register(_windowHandle, id, previous!.Key, previous.Modifiers))
        {
            _registered[id] = Clone(previous);
            return HotkeyApplyStatus.RegistrationFailed;
        }
        return HotkeyApplyStatus.RollbackFailed;
    }

    private HotkeyApplyStatus ReplaceNative(IReadOnlyList<Hotkey> candidate)
    {
        var previous = _registered.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
        var removed = new List<int>();

        foreach (var id in previous.Keys)
        {
            if (!_native.Unregister(_windowHandle, id))
                return RestoreAfterFailedRemoval(previous, removed);
            removed.Add(id);
        }
        _registered.Clear();

        var added = new List<int>();
        foreach (var (hotkey, id) in EnabledGlobals(candidate))
        {
            if (_native.Register(_windowHandle, id, hotkey.Key, hotkey.Modifiers))
            {
                added.Add(id);
                _registered[id] = Clone(hotkey);
                continue;
            }

            foreach (var addedId in added)
                _native.Unregister(_windowHandle, addedId);
            _registered.Clear();
            return RestorePrevious(previous)
                ? HotkeyApplyStatus.RegistrationFailed
                : HotkeyApplyStatus.RollbackFailed;
        }
        return HotkeyApplyStatus.Applied;
    }

    private HotkeyApplyStatus RestoreAfterFailedRemoval(
        IReadOnlyDictionary<int, Hotkey> previous,
        IReadOnlyCollection<int> removed)
    {
        foreach (var id in removed)
            _registered.Remove(id);
        var restored = true;
        foreach (var id in removed)
        {
            var hotkey = previous[id];
            if (_native.Register(_windowHandle, id, hotkey.Key, hotkey.Modifiers))
                _registered[id] = Clone(hotkey);
            else
                restored = false;
        }
        return restored
            ? HotkeyApplyStatus.RegistrationFailed
            : HotkeyApplyStatus.RollbackFailed;
    }

    private bool RestorePrevious(IReadOnlyDictionary<int, Hotkey> previous)
    {
        var restored = true;
        foreach (var (id, hotkey) in previous)
        {
            if (_native.Register(_windowHandle, id, hotkey.Key, hotkey.Modifiers))
                _registered[id] = Clone(hotkey);
            else
                restored = false;
        }
        return restored;
    }

    private static HotkeyApplyResult Validate(IReadOnlyList<Hotkey> candidate)
    {
        var invalid = candidate.Where(item =>
            item.IsEnabled
            && (item.Key == PlatformKey.None
                || IsModifier(item.Key)
                || (!AllowsNoModifier(item.Key) && item.Modifiers == PlatformModifiers.None)
                || IsReserved(item.Key, item.Modifiers)))
            .Select(Clone)
            .ToArray();
        if (invalid.Length > 0)
            return new(HotkeyApplyStatus.Invalid, candidate.Select(Clone).ToArray(), invalid);

        var conflicts = candidate
            .Where(item => item.IsEnabled && item.Key != PlatformKey.None)
            .GroupBy(item => (item.Key, item.Modifiers))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(Clone)
            .ToArray();
        return conflicts.Length == 0
            ? Result(HotkeyApplyStatus.Applied, candidate)
            : new(HotkeyApplyStatus.Conflict, candidate.Select(Clone).ToArray(), conflicts);
    }

    private static IReadOnlyList<Hotkey> MergeWithDefaults(IEnumerable<Hotkey>? saved)
    {
        var provided = (saved ?? Array.Empty<Hotkey>())
            .Where(item => !string.IsNullOrWhiteSpace(item.ActionId))
            .Select(Clone)
            .ToList();
        var result = new List<Hotkey>();
        foreach (var fallback in BuildDefaults())
        {
            var existing = provided.FirstOrDefault(item => SameIdentity(item, fallback));
            if (existing is null)
            {
                if (IsBindingUsed(result.Concat(provided), fallback))
                {
                    fallback.Key = PlatformKey.None;
                    fallback.Modifiers = PlatformModifiers.None;
                    fallback.IsEnabled = false;
                }
                result.Add(fallback);
                continue;
            }
            existing.Id = fallback.Id;
            existing.Name = fallback.Name;
            result.Add(existing);
        }

        // One-time compatibility fix for the historical swapped local F7/F8 defaults.
        foreach (var hotkey in result.Where(item => !item.IsGlobal))
        {
            if (hotkey.ActionId == HotkeyActions.Forward
                && hotkey.Key == PlatformKey.F7
                && hotkey.Modifiers == PlatformModifiers.None)
                hotkey.Key = PlatformKey.F8;
            else if (hotkey.ActionId == HotkeyActions.Rewind
                     && hotkey.Key == PlatformKey.F8
                     && hotkey.Modifiers == PlatformModifiers.None)
                hotkey.Key = PlatformKey.F7;
        }
        return result;
    }

    private static List<Hotkey> BuildDefaults()
    {
        var globals = new (string Id, string Name, PlatformKey Key, PlatformModifiers Modifiers)[]
        {
            (HotkeyActions.PlayPause, "播放/暂停", PlatformKey.Home, PlatformModifiers.Control | PlatformModifiers.Alt),
            (HotkeyActions.Stop, "停止", PlatformKey.End, PlatformModifiers.Control | PlatformModifiers.Alt),
            (HotkeyActions.Previous, "上一首", PlatformKey.PageUp, PlatformModifiers.Control | PlatformModifiers.Alt),
            (HotkeyActions.Next, "下一首", PlatformKey.PageDown, PlatformModifiers.Control | PlatformModifiers.Alt),
            (HotkeyActions.Rewind, "快退", PlatformKey.Left, PlatformModifiers.Control | PlatformModifiers.Alt),
            (HotkeyActions.Forward, "快进", PlatformKey.Right, PlatformModifiers.Control | PlatformModifiers.Alt),
            (HotkeyActions.TogglePlayMode, "播放模式", PlatformKey.None, PlatformModifiers.None),
            (HotkeyActions.Download, "下载", PlatformKey.None, PlatformModifiers.None),
            (HotkeyActions.VolumeUp, "音量加", PlatformKey.Up, PlatformModifiers.Control | PlatformModifiers.Alt),
            (HotkeyActions.VolumeDown, "音量减", PlatformKey.Down, PlatformModifiers.Control | PlatformModifiers.Alt),
            (HotkeyActions.SwitchNextOutputDevice, "下一个播放设备", PlatformKey.None, PlatformModifiers.None),
            (HotkeyActions.SwitchPreviousOutputDevice, "上一个播放设备", PlatformKey.None, PlatformModifiers.None),
            (HotkeyActions.LyricDelay, "歌词延后", PlatformKey.None, PlatformModifiers.None),
            (HotkeyActions.LyricAdvance, "歌词提前", PlatformKey.None, PlatformModifiers.None),
            (HotkeyActions.ToggleWindow, "隐藏/显示窗口", PlatformKey.None, PlatformModifiers.None)
        };
        var locals = new (string Id, string Name, PlatformKey Key, PlatformModifiers Modifiers)[]
        {
            (HotkeyActions.PlayPause, "播放/暂停", PlatformKey.F5, PlatformModifiers.None),
            (HotkeyActions.Stop, "停止", PlatformKey.F6, PlatformModifiers.None),
            (HotkeyActions.Previous, "上一首", PlatformKey.F3, PlatformModifiers.None),
            (HotkeyActions.Next, "下一首", PlatformKey.F4, PlatformModifiers.None),
            (HotkeyActions.Rewind, "快退", PlatformKey.F7, PlatformModifiers.None),
            (HotkeyActions.Forward, "快进", PlatformKey.F8, PlatformModifiers.None),
            (HotkeyActions.VolumeDown, "音量减", PlatformKey.F9, PlatformModifiers.None),
            (HotkeyActions.VolumeUp, "音量加", PlatformKey.F10, PlatformModifiers.None),
            (HotkeyActions.SwitchPreviousOutputDevice, "上一个播放设备", PlatformKey.F11, PlatformModifiers.None),
            (HotkeyActions.SwitchNextOutputDevice, "下一个播放设备", PlatformKey.F12, PlatformModifiers.None),
            (HotkeyActions.LyricDelay, "歌词延后", PlatformKey.F7, PlatformModifiers.Shift),
            (HotkeyActions.LyricAdvance, "歌词提前", PlatformKey.F8, PlatformModifiers.Shift),
            (HotkeyActions.TogglePlayMode, "播放模式", PlatformKey.F1, PlatformModifiers.None),
            (HotkeyActions.Download, "下载", PlatformKey.F2, PlatformModifiers.None),
            (HotkeyActions.ToggleVoiceAnnouncement, "语音朗读提示开关", PlatformKey.F5, PlatformModifiers.Shift),
            (HotkeyActions.ToggleLyricAnnouncement, "语音朗读歌词开关", PlatformKey.F6, PlatformModifiers.Shift)
        };
        return globals.Select(item => New(item, true))
            .Concat(locals.Select(item => New(item, false)))
            .ToList();
    }

    private static bool IsBindingUsed(IEnumerable<Hotkey> hotkeys, Hotkey candidate) =>
        hotkeys.Any(item => item.IsEnabled
            && item.Key != PlatformKey.None
            && item.Key == candidate.Key
            && item.Modifiers == candidate.Modifiers);

    private static Hotkey New(
        (string Id, string Name, PlatformKey Key, PlatformModifiers Modifiers) item,
        bool global) => new()
    {
        Id = item.Id,
        ActionId = item.Id,
        Name = item.Name,
        Key = item.Key,
        Modifiers = item.Modifiers,
        IsGlobal = global,
        IsEnabled = item.Key != PlatformKey.None
    };

    private static IEnumerable<(Hotkey Hotkey, int Id)> EnabledGlobals(
        IReadOnlyList<Hotkey> hotkeys) =>
        hotkeys.Select((hotkey, id) => (hotkey, id))
            .Where(item => item.hotkey.IsGlobal
                           && item.hotkey.IsEnabled
                           && item.hotkey.Key != PlatformKey.None);

    private static bool SameBindings(IReadOnlyList<Hotkey> left, IReadOnlyList<Hotkey> right) =>
        left.Count == right.Count
        && left.Zip(right, (first, second) => (first, second)).All(pair => SameBinding(pair.first, pair.second));

    private static bool SameBinding(Hotkey left, Hotkey right) =>
        SameIdentity(left, right)
        && left.Key == right.Key
        && left.Modifiers == right.Modifiers
        && left.IsEnabled == right.IsEnabled;

    private static bool SameIdentity(Hotkey left, Hotkey right) =>
        left.ActionId == right.ActionId && left.IsGlobal == right.IsGlobal;

    private static bool AllowsNoModifier(PlatformKey key) =>
        key is >= PlatformKey.F1 and <= PlatformKey.F12
            or PlatformKey.MediaPlayPause
            or PlatformKey.MediaStop
            or PlatformKey.MediaNextTrack
            or PlatformKey.MediaPreviousTrack;

    private static bool IsModifier(PlatformKey key) =>
        key is PlatformKey.LeftShift or PlatformKey.RightShift or PlatformKey.LeftCtrl or PlatformKey.RightCtrl
            or PlatformKey.LeftAlt or PlatformKey.RightAlt or PlatformKey.LWin or PlatformKey.RWin;

    private static bool IsReserved(PlatformKey key, PlatformModifiers modifiers) =>
        modifiers == PlatformModifiers.Control && key is >= PlatformKey.D1 and <= PlatformKey.D8
        || modifiers == PlatformModifiers.Control && key is PlatformKey.F or PlatformKey.R
        || modifiers == PlatformModifiers.Alt && key is PlatformKey.M or PlatformKey.C or PlatformKey.L or PlatformKey.S or PlatformKey.R;

    private static Hotkey Clone(Hotkey hotkey) => new()
    {
        Id = hotkey.Id,
        Name = hotkey.Name,
        ActionId = hotkey.ActionId,
        Key = hotkey.Key,
        Modifiers = hotkey.Modifiers,
        IsGlobal = hotkey.IsGlobal,
        Action = hotkey.Action,
        Command = hotkey.Command,
        IsEnabled = hotkey.IsEnabled
    };

    private static HotkeyApplyResult Result(
        HotkeyApplyStatus status,
        IReadOnlyList<Hotkey> hotkeys) =>
        new(status, hotkeys.Select(Clone).ToArray(), Array.Empty<Hotkey>());
}
