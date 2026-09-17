namespace LXMusicAccessible.Models;

/// <summary>
/// Translates the persisted legacy <see cref="PlatformKey"/> numbering into Win32 virtual-key codes.
/// Settings store the menu-era legacy key numbers, where <c>Home</c> is 22 and
/// <c>F1</c> is 90, while <c>RegisterHotKey</c> and WM_KEYDOWN need virtual-key codes
/// (<c>VK_HOME</c> is 0x24). Sending the raw number silently binds the wrong key or fails, which is
/// why every native registration and every keyboard route goes through this table.
/// </summary>
public static class KeyboardKeys
{
    private static readonly Dictionary<uint, PlatformKey> Reverse = BuildReverse();

    /// <summary>
    /// Returns the Win32 virtual-key code for a configured key, or 0 when the key has no
    /// registerable virtual-key code.
    /// </summary>
    public static uint ToVirtualKey(PlatformKey key) => (uint)(key switch
    {
        PlatformKey.Cancel => 0x03,
        PlatformKey.Back => 0x08,
        PlatformKey.Tab => 0x09,
        PlatformKey.Clear => 0x0C,
        PlatformKey.Return => 0x0D,
        PlatformKey.Pause => 0x13,
        PlatformKey.Capital => 0x14,
        PlatformKey.KanaMode => 0x15,
        PlatformKey.JunjaMode => 0x17,
        PlatformKey.FinalMode => 0x18,
        PlatformKey.HanjaMode => 0x19,
        PlatformKey.Escape => 0x1B,
        PlatformKey.ImeConvert => 0x1C,
        PlatformKey.ImeNonConvert => 0x1D,
        PlatformKey.ImeAccept => 0x1E,
        PlatformKey.ImeModeChange => 0x1F,
        PlatformKey.Space => 0x20,
        PlatformKey.Prior => 0x21,
        PlatformKey.Next => 0x22,
        PlatformKey.End => 0x23,
        PlatformKey.Home => 0x24,
        PlatformKey.Left => 0x25,
        PlatformKey.Up => 0x26,
        PlatformKey.Right => 0x27,
        PlatformKey.Down => 0x28,
        PlatformKey.Select => 0x29,
        PlatformKey.Print => 0x2A,
        PlatformKey.Execute => 0x2B,
        PlatformKey.Snapshot => 0x2C,
        PlatformKey.Insert => 0x2D,
        PlatformKey.Delete => 0x2E,
        PlatformKey.Help => 0x2F,
        >= PlatformKey.D0 and <= PlatformKey.D9 => 0x30 + (int)key - (int)PlatformKey.D0,
        >= PlatformKey.A and <= PlatformKey.Z => 0x41 + (int)key - (int)PlatformKey.A,
        PlatformKey.LWin => 0x5B,
        PlatformKey.RWin => 0x5C,
        PlatformKey.Apps => 0x5D,
        PlatformKey.Sleep => 0x5F,
        >= PlatformKey.NumPad0 and <= PlatformKey.NumPad9 => 0x60 + (int)key - (int)PlatformKey.NumPad0,
        PlatformKey.Multiply => 0x6A,
        PlatformKey.Add => 0x6B,
        PlatformKey.Separator => 0x6C,
        PlatformKey.Subtract => 0x6D,
        PlatformKey.Decimal => 0x6E,
        PlatformKey.Divide => 0x6F,
        >= PlatformKey.F1 and <= PlatformKey.F24 => 0x70 + (int)key - (int)PlatformKey.F1,
        PlatformKey.NumLock => 0x90,
        PlatformKey.Scroll => 0x91,
        PlatformKey.LeftShift => 0xA0,
        PlatformKey.RightShift => 0xA1,
        PlatformKey.LeftCtrl => 0xA2,
        PlatformKey.RightCtrl => 0xA3,
        PlatformKey.LeftAlt => 0xA4,
        PlatformKey.RightAlt => 0xA5,
        PlatformKey.BrowserBack => 0xA6,
        PlatformKey.BrowserForward => 0xA7,
        PlatformKey.BrowserRefresh => 0xA8,
        PlatformKey.BrowserStop => 0xA9,
        PlatformKey.BrowserSearch => 0xAA,
        PlatformKey.BrowserFavorites => 0xAB,
        PlatformKey.BrowserHome => 0xAC,
        PlatformKey.VolumeMute => 0xAD,
        PlatformKey.VolumeDown => 0xAE,
        PlatformKey.VolumeUp => 0xAF,
        PlatformKey.MediaNextTrack => 0xB0,
        PlatformKey.MediaPreviousTrack => 0xB1,
        PlatformKey.MediaStop => 0xB2,
        PlatformKey.MediaPlayPause => 0xB3,
        PlatformKey.LaunchMail => 0xB4,
        PlatformKey.SelectMedia => 0xB5,
        PlatformKey.LaunchApplication1 => 0xB6,
        PlatformKey.LaunchApplication2 => 0xB7,
        PlatformKey.Oem1 => 0xBA,
        PlatformKey.OemPlus => 0xBB,
        PlatformKey.OemComma => 0xBC,
        PlatformKey.OemMinus => 0xBD,
        PlatformKey.OemPeriod => 0xBE,
        PlatformKey.Oem2 => 0xBF,
        PlatformKey.Oem3 => 0xC0,
        PlatformKey.AbntC1 => 0xC1,
        PlatformKey.AbntC2 => 0xC2,
        PlatformKey.Oem4 => 0xDB,
        PlatformKey.Oem5 => 0xDC,
        PlatformKey.Oem6 => 0xDD,
        PlatformKey.Oem7 => 0xDE,
        PlatformKey.Oem8 => 0xDF,
        PlatformKey.Oem102 => 0xE2,
        PlatformKey.ImeProcessed => 0xE5,
        PlatformKey.OemAttn => 0x9D,
        PlatformKey.OemFinish => 0x9E,
        PlatformKey.OemCopy => 0x9F,
        PlatformKey.OemAuto => 0xA0,
        PlatformKey.OemEnlw => 0xA1,
        PlatformKey.OemBackTab => 0xA2,
        PlatformKey.Attn => 0xF6,
        PlatformKey.CrSel => 0xF7,
        PlatformKey.ExSel => 0xF8,
        PlatformKey.EraseEof => 0xF9,
        PlatformKey.Play => 0xFA,
        PlatformKey.Zoom => 0xFB,
        PlatformKey.NoName => 0xFC,
        PlatformKey.Pa1 => 0xFD,
        PlatformKey.OemClear => 0xFE,
        _ => 0
    });

    /// <summary>
    /// Returns the configured key for a Win32 virtual-key code, or <see cref="PlatformKey.None"/>
    /// when the code has no configured key. Aliased names resolve to the first declared name.
    /// </summary>
    public static PlatformKey FromVirtualKey(uint virtualKey) =>
        Reverse.TryGetValue(virtualKey, out var key) ? key : PlatformKey.None;

    private static Dictionary<uint, PlatformKey> BuildReverse()
    {
        var map = new Dictionary<uint, PlatformKey>();
        foreach (PlatformKey key in Enum.GetValues(typeof(PlatformKey)))
        {
            var virtualKey = ToVirtualKey(key);
            if (virtualKey != 0 && !map.ContainsKey(virtualKey)) map[virtualKey] = key;
        }
        return map;
    }
}