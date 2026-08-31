namespace LXMusicAccessible.App;

/// <summary>Browse pages and tool windows exposed by the fixed Ctrl+number window switcher.</summary>
internal enum WindowShortcutTarget
{
    Search,
    Playlist,
    CategoryPlaylists,
    Rankings,
    DownloadManager,
    PlaylistImport,
    SourceManager,
    Settings
}

/// <summary>Maps the fixed window-switching chords; lyrics deliberately has no number target.</summary>
internal static class WindowShortcuts
{
    public static bool Matches(System.Windows.Input.ModifierKeys modifiers) =>
        modifiers == System.Windows.Input.ModifierKeys.Control;

    private static readonly HashSet<System.Windows.Input.Key> ShortcutKeys = new()
    {
        System.Windows.Input.Key.D1, System.Windows.Input.Key.NumPad1,
        System.Windows.Input.Key.D2, System.Windows.Input.Key.NumPad2,
        System.Windows.Input.Key.D3, System.Windows.Input.Key.NumPad3,
        System.Windows.Input.Key.D4, System.Windows.Input.Key.NumPad4,
        System.Windows.Input.Key.D5, System.Windows.Input.Key.NumPad5,
        System.Windows.Input.Key.D6, System.Windows.Input.Key.NumPad6,
        System.Windows.Input.Key.D7, System.Windows.Input.Key.NumPad7,
        System.Windows.Input.Key.D8, System.Windows.Input.Key.NumPad8
    };

    public static bool TryGetTarget(System.Windows.Input.Key key, out WindowShortcutTarget target)
    {
        if (!ShortcutKeys.Contains(key))
        {
            target = default;
            return false;
        }

        target = key switch
        {
            System.Windows.Input.Key.D1 or System.Windows.Input.Key.NumPad1 => WindowShortcutTarget.Search,
            System.Windows.Input.Key.D2 or System.Windows.Input.Key.NumPad2 => WindowShortcutTarget.Playlist,
            System.Windows.Input.Key.D3 or System.Windows.Input.Key.NumPad3 => WindowShortcutTarget.CategoryPlaylists,
            System.Windows.Input.Key.D4 or System.Windows.Input.Key.NumPad4 => WindowShortcutTarget.Rankings,
            System.Windows.Input.Key.D5 or System.Windows.Input.Key.NumPad5 => WindowShortcutTarget.DownloadManager,
            System.Windows.Input.Key.D6 or System.Windows.Input.Key.NumPad6 => WindowShortcutTarget.PlaylistImport,
            System.Windows.Input.Key.D7 or System.Windows.Input.Key.NumPad7 => WindowShortcutTarget.SourceManager,
            _ => WindowShortcutTarget.Settings
        };
        return true;
    }

}
