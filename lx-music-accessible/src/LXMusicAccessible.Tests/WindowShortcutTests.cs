using System;
using System.IO;
using System.Windows.Input;
using LXMusicAccessible.App;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class WindowShortcutTests
{
    [Theory]
    [InlineData(Key.D1, WindowShortcutTarget.Search)]
    [InlineData(Key.NumPad1, WindowShortcutTarget.Search)]
    [InlineData(Key.D2, WindowShortcutTarget.Playlist)]
    [InlineData(Key.NumPad2, WindowShortcutTarget.Playlist)]
    [InlineData(Key.D3, WindowShortcutTarget.CategoryPlaylists)]
    [InlineData(Key.NumPad3, WindowShortcutTarget.CategoryPlaylists)]
    [InlineData(Key.D4, WindowShortcutTarget.Rankings)]
    [InlineData(Key.NumPad4, WindowShortcutTarget.Rankings)]
    [InlineData(Key.D5, WindowShortcutTarget.DownloadManager)]
    [InlineData(Key.NumPad5, WindowShortcutTarget.DownloadManager)]
    [InlineData(Key.D6, WindowShortcutTarget.PlaylistImport)]
    [InlineData(Key.NumPad6, WindowShortcutTarget.PlaylistImport)]
    [InlineData(Key.D7, WindowShortcutTarget.SourceManager)]
    [InlineData(Key.NumPad7, WindowShortcutTarget.SourceManager)]
    [InlineData(Key.D8, WindowShortcutTarget.Settings)]
    [InlineData(Key.NumPad8, WindowShortcutTarget.Settings)]
    internal void Ctrl_number_keys_map_to_browse_and_tool_windows(Key key, WindowShortcutTarget expected)
    {
        Assert.True(WindowShortcuts.TryGetTarget(key, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(Key.None, ModifierKeys.Control)]
    [InlineData(Key.D9, ModifierKeys.Control)]
    [InlineData(Key.D1, ModifierKeys.Alt)]
    [InlineData(Key.D1, ModifierKeys.Control | ModifierKeys.Shift)]
    public void Unsupported_combinations_do_not_switch_windows(Key key, ModifierKeys modifiers)
    {
        Assert.False(WindowShortcuts.Matches(modifiers)
            && WindowShortcuts.TryGetTarget(key, out _));
    }

    [Fact]
    public void Main_and_tool_windows_route_reserved_window_shortcuts_without_lyrics()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "LXMusicAccessible.App", "MainWindow.xaml.cs"));
        var lyrics = File.ReadAllText(Path.Combine(root, "src", "LXMusicAccessible.App", "LyricsWindow.xaml.cs"));

        Assert.Contains("TryHandleWindowShortcut(e.Key == Key.System ? e.SystemKey : e.Key, mods)", main, StringComparison.Ordinal);
        Assert.Contains("TryHandleWindowShortcut(key, modifiers)", main, StringComparison.Ordinal);
        Assert.Contains("OpenWindowShortcutTarget", main, StringComparison.Ordinal);
        Assert.DoesNotContain("TryHandleWindowShortcut", lyrics, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "verify.ps1")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
