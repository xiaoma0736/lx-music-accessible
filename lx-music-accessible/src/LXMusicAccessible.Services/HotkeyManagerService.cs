using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Windows.Input;
using LXMusicAccessible.Models;

namespace LXMusicAccessible.Services;

/// <summary>
/// 快捷键管理器服务实现
/// </summary>
public class HotkeyManagerService : IHotkeyManager
{
    // 全局热键修饰符常量（Win32）
    private const uint MOD_ALT = 0x1;
    private const uint MOD_CONTROL = 0x2;
    private const uint MOD_SHIFT = 0x4;
    private const uint MOD_WIN = 0x8;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly ILogger<HotkeyManagerService> _logger;
    private readonly List<Hotkey> _hotkeys = new();
    private readonly List<Hotkey> _globalHotkeys = new();
    private readonly HashSet<string> _registeredCombos = new();
    private IntPtr _windowHandle;

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;
    public event EventHandler<HotkeysRegistrationFailedEventArgs>? HotkeysRegistrationFailed;

    public HotkeyManagerService(ILogger<HotkeyManagerService> logger)
    {
        _logger = logger;
    }

    // ============ 默认快捷键 ============

    /// <summary>默认快捷键定义（动作标识 → 名称/按键/修饰键）</summary>
    private static (string ActionId, string Name, Key Key, ModifierKeys Modifiers)[] DefaultHotkeyDefs => new[]
    {
        (HotkeyActions.PlayPause, "播放/暂停", Key.Space, ModifierKeys.Control),
        (HotkeyActions.Stop, "停止", Key.Space, ModifierKeys.Control | ModifierKeys.Shift),
        (HotkeyActions.Previous, "上一首", Key.Left, ModifierKeys.Control),
        (HotkeyActions.Next, "下一首", Key.Right, ModifierKeys.Control),
        (HotkeyActions.Rewind, "快退", Key.Left, ModifierKeys.Control | ModifierKeys.Alt),
        (HotkeyActions.Forward, "快进", Key.Right, ModifierKeys.Control | ModifierKeys.Alt),
        (HotkeyActions.TogglePlayMode, "播放模式", Key.M, ModifierKeys.Control),
        (HotkeyActions.Download, "下载", Key.D, ModifierKeys.Control),
        (HotkeyActions.VolumeUp, "音量加", Key.Up, ModifierKeys.Control),
        (HotkeyActions.VolumeDown, "音量减", Key.Down, ModifierKeys.Control),
        (HotkeyActions.SwitchNextOutputDevice, "下一个播放设备", Key.Up, ModifierKeys.Control | ModifierKeys.Alt),
        (HotkeyActions.SwitchPreviousOutputDevice, "上一个播放设备", Key.Down, ModifierKeys.Control | ModifierKeys.Alt),
        (HotkeyActions.LyricDelay, "歌词延后", Key.J, ModifierKeys.Control | ModifierKeys.Alt),
        (HotkeyActions.LyricAdvance, "歌词提前", Key.K, ModifierKeys.Control | ModifierKeys.Alt),
        (HotkeyActions.ToggleWindow, "隐藏/显示窗口", Key.H, ModifierKeys.Control | ModifierKeys.Shift)
    };

    /// <summary>局部快捷键定义（IsGlobal=false，仅程序内生效）：F1~F12（F1 播放模式/F2 下载/F3 上一首/F4 下一首/F5 播放暂停/F6 停止/F7 快退/F8 快进/F9 音量减/F10 音量加/F11 上一设备/F12 下一设备）、Shift+F7/F8（歌词延后/提前）</summary>
    private static (string ActionId, string Name, Key Key, ModifierKeys Modifiers)[] DefaultLocalHotkeyDefs => new[]
    {
        (HotkeyActions.PlayPause, "播放/暂停", Key.F5, ModifierKeys.None),
        (HotkeyActions.Stop, "停止", Key.F6, ModifierKeys.None),
        (HotkeyActions.Previous, "上一首", Key.F3, ModifierKeys.None),
        (HotkeyActions.Next, "下一首", Key.F4, ModifierKeys.None),
        (HotkeyActions.Rewind, "快退", Key.F7, ModifierKeys.None),
        (HotkeyActions.Forward, "快进", Key.F8, ModifierKeys.None),
        (HotkeyActions.VolumeDown, "音量减", Key.F9, ModifierKeys.None),
        (HotkeyActions.VolumeUp, "音量加", Key.F10, ModifierKeys.None),
        (HotkeyActions.SwitchPreviousOutputDevice, "上一个播放设备", Key.F11, ModifierKeys.None),
        (HotkeyActions.SwitchNextOutputDevice, "下一个播放设备", Key.F12, ModifierKeys.None),
        (HotkeyActions.LyricDelay, "歌词延后", Key.F7, ModifierKeys.Shift),
        (HotkeyActions.LyricAdvance, "歌词提前", Key.F8, ModifierKeys.Shift),
        (HotkeyActions.TogglePlayMode, "播放模式", Key.F1, ModifierKeys.None),
        (HotkeyActions.Download, "下载", Key.F2, ModifierKeys.None)
    };

    /// <summary>载入快捷键配置，缺失的动作自动补默认值。全局/局部按 ActionId+IsGlobal 作用域分别匹配。</summary>
    public void LoadHotkeys(IEnumerable<Hotkey> hotkeys)
    {
        _hotkeys.Clear();
        var provided = (hotkeys ?? Enumerable.Empty<Hotkey>())
            .Where(h => !string.IsNullOrEmpty(h.ActionId))
            .ToList();

        LoadHotkeyDefs(DefaultHotkeyDefs, isLocal: false, provided);
        LoadHotkeyDefs(DefaultLocalHotkeyDefs, isLocal: true, provided);

        // 迁移（仅一次生效）：旧版局部默认 快进=F7/快退=F8 调整为 快退=F7/快进=F8。
        // 仅当保存值仍是旧默认时才改写；用户自定义过的保持不变。
        foreach (var h in _hotkeys.Where(h => !h.IsGlobal))
        {
            if (h.ActionId == HotkeyActions.Forward && h.Key == Key.F7 && h.Modifiers == ModifierKeys.None)
                h.Key = Key.F8;
            else if (h.ActionId == HotkeyActions.Rewind && h.Key == Key.F8 && h.Modifiers == ModifierKeys.None)
                h.Key = Key.F7;
        }

        _logger.LogInformation("载入快捷键配置，共 {Count} 个动作", _hotkeys.Count);
    }

    /// <summary>按一组定义载入快捷键；provided 按 ActionId+IsGlobal 作用域匹配（同一动作可同时有全局与局部各一项）</summary>
    private void LoadHotkeyDefs(
        (string ActionId, string Name, Key Key, ModifierKeys Modifiers)[] defs,
        bool isLocal,
        List<Hotkey> provided)
    {
        foreach (var def in defs)
        {
            var existing = provided.FirstOrDefault(h => h.ActionId == def.ActionId && h.IsGlobal == !isLocal);
            if (existing != null)
            {
                existing.Name = def.Name;
                _hotkeys.Add(existing);
            }
            else
            {
                _hotkeys.Add(new Hotkey
                {
                    Id = def.ActionId,
                    ActionId = def.ActionId,
                    Name = def.Name,
                    Key = def.Key,
                    Modifiers = def.Modifiers,
                    IsGlobal = !isLocal,
                    IsEnabled = true
                });
            }
        }
    }

    // ============ 兼容旧接口 ============

    public void RegisterHotkey(Hotkey hotkey)
    {
        if (CheckConflict(hotkey))
        {
            _logger.LogWarning("快捷键冲突: {Name}", hotkey.Name);
            return;
        }
        _hotkeys.Add(hotkey);
    }

    public void UnregisterHotkey(Hotkey hotkey) => _hotkeys.Remove(hotkey);

    public void RegisterGlobalHotkey(Hotkey hotkey)
    {
        if (CheckConflict(hotkey))
        {
            _logger.LogWarning("全局快捷键冲突: {Name}", hotkey.Name);
            return;
        }
        _globalHotkeys.Add(hotkey);
    }

    public void UnregisterGlobalHotkey(Hotkey hotkey) => _globalHotkeys.Remove(hotkey);

    public bool CheckConflict(Hotkey hotkey)
    {
        var allHotkeys = _hotkeys.Concat(_globalHotkeys);
        return allHotkeys.Any(h =>
            h.Key == hotkey.Key &&
            h.Modifiers == hotkey.Modifiers &&
            h.IsEnabled &&
            !string.Equals(h.ActionId, hotkey.ActionId, StringComparison.Ordinal));
    }

    public List<Hotkey> GetAllHotkeys() => new(_hotkeys);

    public List<Hotkey> GetAllGlobalHotkeys() => new(_globalHotkeys);

    // ============ 全局注册 ============

    /// <summary>初始化全局快捷键注册（失败触发 HotkeysRegistrationFailed）</summary>
    public void InitializeGlobalHotkeys(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        RefreshGlobalHotkeys();
    }

    /// <summary>检测组合键是否已被其他程序占用：临时注册一次（成功立即注销），注册失败视为被占用。</summary>
    public bool IsComboOccupied(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None || IsModifierKey(key)) return false;
        if (_windowHandle == IntPtr.Zero) return false; // 无窗口句柄时跳过预检
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var flags = ToModifierFlags(modifiers) | MOD_NOREPEAT;
        const int trialId = 0x4E1E; // 专用探测 id，避开正式注册使用的索引 id
        bool ok;
        try
        {
            ok = RegisterHotKey(_windowHandle, trialId, flags, vk);
        }
        catch
        {
            return false;
        }
        if (ok)
        {
            try { UnregisterHotKey(_windowHandle, trialId); } catch { }
            return false;
        }
        return true;
    }

    /// <summary>判断指定全局快捷键当前是否已在系统注册成功（用于设置列表“冲突”标记）</summary>
    public bool IsGlobalHotkeyRegistered(Hotkey hotkey)
    {
        if (hotkey == null || !hotkey.IsGlobal || hotkey.Key == Key.None) return true;
        if (_windowHandle == IntPtr.Zero) return true; // 未初始化注册环境时不标记冲突
        return _registeredCombos.Contains(ComboKey(hotkey));
    }

    /// <summary>重新注册全部全局快捷键</summary>
    public void RefreshGlobalHotkeys()
    {
        UnregisterAllGlobal();
        if (_windowHandle == IntPtr.Zero) return;

        var failed = new List<Hotkey>();
        for (int i = 0; i < _hotkeys.Count; i++)
        {
            var h = _hotkeys[i];
            if (!h.IsEnabled || !h.IsGlobal || h.Key == Key.None) continue;
            if (IsModifierKey(h.Key)) continue;

            var vk = (uint)KeyInterop.VirtualKeyFromKey(h.Key);
            var flags = ToModifierFlags(h.Modifiers) | MOD_NOREPEAT;

            if (RegisterHotKey(_windowHandle, i, flags, vk))
            {
                _registeredCombos.Add(ComboKey(h));
                _logger.LogInformation("全局快捷键注册成功: {Name} ({Key})", h.Name, h.Key);
            }
            else
            {
                _logger.LogWarning("全局快捷键注册失败: {Name} ({Key})", h.Name, h.Key);
                failed.Add(h);
            }
        }

        if (failed.Count > 0)
            HotkeysRegistrationFailed?.Invoke(this, new HotkeysRegistrationFailedEventArgs { FailedHotkeys = failed });
    }

    /// <summary>注销全部全局快捷键</summary>
    public void UnregisterAllGlobal()
    {
        _registeredCombos.Clear();
        if (_windowHandle == IntPtr.Zero) return;
        for (int i = 0; i < _hotkeys.Count; i++)
            UnregisterHotKey(_windowHandle, i);
    }

    /// <summary>按全局注册 id 触发快捷键（由 WM_HOTKEY 调用）</summary>
    public void RaiseGlobalHotkey(int id)
    {
        if (id < 0 || id >= _hotkeys.Count) return;
        var h = _hotkeys[id];
        if (h.IsEnabled && h.Key != Key.None)
            OnHotkeyPressed(h);
    }

    /// <summary>程序内兜底匹配（仅匹配全局注册失败的组合，避免与 WM_HOTKEY 重复触发）</summary>
    public Hotkey? MatchFallback(Key key, ModifierKeys modifiers)
    {
        foreach (var h in _hotkeys)
        {
            if (!h.IsEnabled || !h.IsGlobal || h.Key == Key.None) continue;
            if (h.Key != key || h.Modifiers != modifiers) continue;
            if (_registeredCombos.Contains(ComboKey(h))) continue;
            return h;
        }
        return null;
    }
    /// <summary>程序内局部快捷键匹配（仅匹配 IsGlobal=false 的局部快捷键，主窗口聚焦时由 PreviewKeyDown 调用）</summary>
    public Hotkey? MatchLocalHotkey(Key key, ModifierKeys modifiers)
    {
        foreach (var h in _hotkeys)
        {
            if (!h.IsEnabled || h.IsGlobal || h.Key == Key.None) continue;
            if (h.Key != key || h.Modifiers != modifiers) continue;
            return h;
        }
        return null;
    }

    // ============ 辅助 ============

    private void OnHotkeyPressed(Hotkey hotkey)
    {
        HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs
        {
            Hotkey = hotkey,
            PressedTime = DateTime.Now,
            Key = hotkey.Key,
            Modifiers = hotkey.Modifiers
        });
    }

    private static string ComboKey(Hotkey h) => $"{h.Key}|{(int)h.Modifiers}";

    private static uint ToModifierFlags(ModifierKeys modifiers)
    {
        uint flags = 0;
        if ((modifiers & ModifierKeys.Alt) != 0) flags |= MOD_ALT;
        if ((modifiers & ModifierKeys.Control) != 0) flags |= MOD_CONTROL;
        if ((modifiers & ModifierKeys.Shift) != 0) flags |= MOD_SHIFT;
        if ((modifiers & ModifierKeys.Windows) != 0) flags |= MOD_WIN;
        return flags;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
