using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class WinFormsShellAdditionalTests
{
    [Fact]
    public void Repeated_f5_dispatch_is_debounced_so_pause_is_not_toggled_twice()
    {
        var playback = new StatefulPlayback();
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(playback);
            shell.Show();
            Application.DoEvents();
            InvokeToggle(shell);
            InvokeToggle(shell);
            Assert.True(playback.IsPaused);
            Assert.Equal(1, playback.PauseCalls);
        });
    }

    [Fact]
    public void Search_results_use_load_more_button()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new StatefulPlayback());
            var button = Assert.IsType<Button>(shell.Controls.Find("SearchLoadMore", true).Single());
            Assert.Equal("加载更多歌曲", button.Text);
            Assert.False(button.TabStop);
        });
    }

    [Fact]
    public async Task Category_songs_append_when_load_more_is_used()
    {
        var source = new CountingBrowseSource();
        await StaAsync(async () =>
        {
            using var shell = CreateShell(source, new StatefulPlayback());
            shell.Show();
            Application.DoEvents();
            AddCategory(shell);
            shell.LoadCategoryPlaylistsForTests();
            await WaitFor(() => source.CategoryCalls == 1);
            var playlists = shell.CategoryPlaylistsForTests;
            playlists.SelectedIndex = 0;
            await shell.LoadCategorySongsForTests();
            await shell.LoadMoreCategorySongsForTests();
            var songs = shell.CategorySongsForTests;
            Assert.Equal(2, songs.Items.Count);
            Assert.Equal(2, source.SongCalls);
            Assert.False(((Button)shell.Controls.Find("CategorySongsLoadMore", true).Single()).Enabled);
        });
    }

    private static WinFormsShell CreateShell(IMusicBrowseSource source, IPlaybackCoordinator playback) => new(
        new WinFormsShellServices
        {
            Announcer = new RecordingAnnouncer(),
            BrowseSource = source ?? new InteractionBrowseSource(),
            Playback = playback,
            TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-shell-additional-" + Guid.NewGuid().ToString("N"))
        });

    private static WinFormsShell CreateShell(StatefulPlayback playback) => CreateShell(new InteractionBrowseSource(), playback);

    private static void AddCategory(WinFormsShell shell)
    {
        var list = shell.CategoryListForTests;
        list.Items.Add(new ObjectBox<PlaylistCategory>("tag", new PlaylistCategory
        {
            Id = "tag",
            Name = "tag",
            Tags = new System.Collections.Generic.List<PlaylistTag> { new() { Id = "tag", Name = "tag" } }
        }));
        list.SelectedIndex = 0;
    }

    private static void InvokeToggle(Form shell)
    {
        var method = shell.GetType().GetMethod("TogglePlayback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(shell, Array.Empty<object?>());
        Application.DoEvents();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "condition was not reached");
    }

    private static Task StaAsync(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(async () =>
        {
            try { await test(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        return Task.CompletedTask;
    }
}

public sealed class StatefulPlayback : IPlaybackCoordinator
{
    public bool IsPlaying { get; private set; } = true;
    public bool IsPaused { get; private set; }
    public int PauseCalls { get; private set; }
    public MusicInfo? CurrentMusic { get; private set; } = new() { Id = "current", Title = "Current", Source = "netease" };
    public PlayMode Mode { get; set; }
    public string DefaultQuality { get; set; } = "128k";
#pragma warning disable 67
    public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
    public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted;
#pragma warning restore 67

    public Task<PlaybackResult> PlayAsync(PlaybackRequest request)
    {
        CurrentMusic = request.Selection;
        IsPlaying = true;
        IsPaused = false;
        return Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, request.Selection, request.Origin, "playing"));
    }

    public Task<PlaybackResult> ReplayAsync()
    {
        IsPlaying = true;
        IsPaused = false;
        return Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, CurrentMusic, PlaybackQueueOrigin.Static, "resumed"));
    }

    public void Pause()
    {
        PauseCalls++;
        IsPlaying = false;
        IsPaused = true;
    }

    public void Stop()
    {
        IsPlaying = false;
        IsPaused = false;
    }

    public Task<PlaybackResult> NextAsync() => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
    public Task<PlaybackResult> PreviousAsync() => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
    public Task ResumeAsync() => ReplayAsync();
}