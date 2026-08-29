using System.Windows;
using System.Windows.Input;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>Creates runtime-dependent secondary windows from the Host-owned dependency graph.</summary>
public sealed class ApplicationWindowFactory(
    IAccessibleAnnouncer announcer,
    LyricsWorkflow lyrics,
    IMusicSourceService sources,
    SettingsWorkflow settings,
    ITtsService tts,
    IDownloadWorkflow downloads)
{
    public UserGuideWindow CreateUserGuide(Window? owner) =>
        WithOwner(new UserGuideWindow(announcer), owner);

    public SponsorQrWindow CreateSponsor(Window? owner) =>
        WithOwner(new SponsorQrWindow(announcer), owner);

    public LyricsWindow CreateLyrics(Func<KeyEventArgs, bool> handleHotkey)
    {
        ArgumentNullException.ThrowIfNull(handleHotkey);
        return new LyricsWindow(lyrics, handleHotkey);
    }

    public SourceManagerWindow CreateSourceManager(
        Window owner,
        Func<KeyEventArgs, bool> handleHotkey)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(handleHotkey);
        return WithOwner(new SourceManagerWindow(announcer, sources, handleHotkey), owner);
    }

    public DownloadManagerWindow CreateDownloadManager(
        Window owner,
        Func<KeyEventArgs, bool> handleHotkey)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(handleHotkey);
        return WithOwner(new DownloadManagerWindow(
            announcer,
            settings,
            downloads,
            handleHotkey), owner);
    }

    public SettingsWindow CreateSettings(
        Window owner,
        Func<KeyEventArgs, bool> handleHotkey,
        Action<bool> setInputSuppressed,
        Hotkey? reconfigure = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(handleHotkey);
        ArgumentNullException.ThrowIfNull(setInputSuppressed);
        return WithOwner(new SettingsWindow(
            announcer,
            settings,
            tts,
            downloads,
            handleHotkey,
            setInputSuppressed,
            reconfigure), owner);
    }

    private static T WithOwner<T>(T window, Window? owner) where T : Window
    {
        if (owner is not null) window.Owner = owner;
        return window;
    }
}
