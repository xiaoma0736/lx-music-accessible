using System.Windows;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.App;

internal readonly record struct PlaylistLoginDialogResult(bool Succeeded, string? AccountName = null);

/// <summary>The single seam used by the playlist view to open Host-owned login dialogs.</summary>
internal interface IPlaylistLoginWindowFactory
{
    PlaylistLoginDialogResult ShowWebLogin(string platform, Window? owner);
    bool ShowKugouQrLogin(Window? owner);
}

internal sealed class PlaylistLoginWindowFactory(
    IPlaylistImportService service,
    IAccessibleAnnouncer announcer,
    KugouQrLoginService kugouQrLogin,
    ILogger<WebLoginWindow> webLoginLogger) : IPlaylistLoginWindowFactory
{
    public PlaylistLoginDialogResult ShowWebLogin(string platform, Window? owner)
    {
        var dialog = new WebLoginWindow(platform, service, announcer, webLoginLogger)
        {
            Owner = owner
        };
        return new PlaylistLoginDialogResult(dialog.ShowDialog() == true, dialog.LastAccountName);
    }

    public bool ShowKugouQrLogin(Window? owner)
    {
        var dialog = new KugouQrLoginWindow(service, announcer, kugouQrLogin)
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true;
    }
}
