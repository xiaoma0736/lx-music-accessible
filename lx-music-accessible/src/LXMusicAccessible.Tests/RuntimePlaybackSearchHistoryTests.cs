using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>Regression coverage for playback mode routing and keyboard-only search history ownership.</summary>
public sealed class RuntimePlaybackSearchHistoryTests
{
    private const string ClearAllText = "删除全部记录";

    [Fact]
    public void Startup_applies_saved_play_mode_to_the_playback_coordinator()
    {
        WinFormsTestRunner.Run(() =>
        {
            var playback = new ModeRecordingPlayback();
            var settings = CreateSettings(new UserSettings { DefaultPlayMode = PlayMode.Shuffle });
            settings.LoadAsync(IntPtr.Zero).GetAwaiter().GetResult();
            using var shell = CreateShell(playback, settings);

            Assert.Equal(PlayMode.Shuffle, playback.Mode);
        });
    }

    [Fact]
    public void Toggling_play_mode_updates_the_playback_coordinator()
    {
        WinFormsTestRunner.Run(() =>
        {
            var playback = new ModeRecordingPlayback();
            using var shell = CreateShell(playback);

            InvokeTogglePlayMode(shell);

            Assert.Equal(PlayMode.Loop, playback.Mode);
        });
    }

    [Fact]
    public async Task Search_history_keeps_twenty_records_then_offers_enter_to_clear_all()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new LocalSearchHistoryStore(directory);
            await store.SaveAsync(Enumerable.Range(1, 20).Select(index => new SearchHistory
            {
                Keyword = $"keyword-{index:00}",
                SearchTime = DateTime.Now.AddSeconds(-index)
            }).ToArray());
            var workflow = CreateBrowseWorkflow(store);
            await workflow.LoadSearchHistoryAsync();

            for (var index = 0; index < 20; index++)
            {
                var record = workflow.NavigateSearchHistory("draft", 1);
                Assert.True(record.Handled);
                Assert.Equal($"keyword-{index + 1:00}", record.Text);
            }

            var clearAll = workflow.NavigateSearchHistory("keyword-20", 1);
            Assert.True(clearAll.Handled);
            Assert.Equal(ClearAllText, clearAll.Text);
            Assert.Equal("keyword-20", workflow.NavigateSearchHistory(ClearAllText, -1).Text);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Enter_on_history_clear_entry_clears_all_without_a_search()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var directory = CreateDirectory();
            try
            {
                var store = new LocalSearchHistoryStore(directory);
                await store.SaveAsync([
                    new() { Keyword = "first", SearchTime = DateTime.Now.AddSeconds(2) },
                    new() { Keyword = "second", SearchTime = DateTime.Now }
                ]);
                var workflow = CreateBrowseWorkflow(store);
                await workflow.LoadSearchHistoryAsync();
                var source = new InteractionBrowseSource();
                using var shell = CreateShell(workflow, source);
                shell.Show();
                await WaitForHistoryLoaded(workflow, 2);
                var searchBox = Assert.IsType<TextBox>(shell.Controls.Find("搜索关键词", true).Single());

                Navigate(searchBox, 1);
                Navigate(searchBox, 1);
                Navigate(searchBox, 1);
                Assert.Equal(ClearAllText, searchBox.Text);
                RaiseKeyDown(searchBox, Keys.Enter);
                await WaitFor(async () => (await store.LoadAsync()).Count == 0);
                Assert.Equal(0, source.SearchCalls);
                Assert.Empty(searchBox.Text);
            }
            finally { Directory.Delete(directory, recursive: true); }
        });
    }

    [Fact]
    public void Ctrl_delete_removes_the_current_history_record_without_touching_editing_keys()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var directory = CreateDirectory();
            try
            {
                var store = new LocalSearchHistoryStore(directory);
                await store.SaveAsync([new() { Keyword = "current", SearchTime = DateTime.Now }]);
                var workflow = CreateBrowseWorkflow(store);
                await workflow.LoadSearchHistoryAsync();
                using var shell = CreateShell(workflow);
                shell.Show();
                await WaitForHistoryLoaded(workflow, 1);
                var searchBox = Assert.IsType<TextBox>(shell.Controls.Find("搜索关键词", true).Single());

                Navigate(searchBox, 1);
                RaiseKeyDown(searchBox, Keys.Control | Keys.Delete);
                await WaitFor(async () => (await store.LoadAsync()).Count == 0);

                searchBox.Text = "draft text";
                RaiseKeyDown(searchBox, Keys.Left);
                RaiseKeyDown(searchBox, Keys.Right);
                Assert.Equal("draft text", searchBox.Text);
            }
            finally { Directory.Delete(directory, recursive: true); }
        });
    }

    private static string CreateDirectory() => Path.Combine(
        Path.GetTempPath(), "lx-playback-history-" + Guid.NewGuid().ToString("N"));

    private static SettingsWorkflow CreateSettings(UserSettings settings) => new(
        new RecordingSettingsStore(settings),
        new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, new RecordingHotkeyAdapter()),
        NullLogger<SettingsWorkflow>.Instance);

    private static MusicBrowseWorkflow CreateBrowseWorkflow(LocalSearchHistoryStore store) => new(
        new InteractionBrowseSource(),
        new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance),
        new RecordingAnnouncer(),
        new ModeRecordingPlayback(),
        store,
        NullLogger<MusicBrowseWorkflow>.Instance);

    internal static WinFormsShell CreateShellForLifecycle() => new(
        new WinFormsShellServices
        {
            Announcer = new RecordingAnnouncer(),
            BrowseSource = new InteractionBrowseSource(),
            Playback = new ModeRecordingPlayback(),
            TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-lifecycle-shell-" + Guid.NewGuid().ToString("N"))
        });

    private static WinFormsShell CreateShell(ModeRecordingPlayback playback, SettingsWorkflow? settings = null) => new(
        new WinFormsShellServices
        {
            Announcer = new RecordingAnnouncer(),
            BrowseSource = new InteractionBrowseSource(),
            Playback = playback,
            Settings = settings,
            TemporaryDirectory = CreateDirectory()
        });

    private static TestShell CreateShell(MusicBrowseWorkflow browse, InteractionBrowseSource? source = null) => new(
        new WinFormsShellServices
        {
            Announcer = new RecordingAnnouncer(),
            BrowseSource = source ?? new InteractionBrowseSource(),
            Playback = new ModeRecordingPlayback(),
            BrowseWorkflow = browse,
            TemporaryDirectory = CreateDirectory()
        });

    private sealed class TestShell(WinFormsShellServices services) : WinFormsShell(services)
    {
        protected override bool ConfirmClearSearchHistory() => true;
    }

    private static void InvokeTogglePlayMode(Form shell)
    {
        var method = shell.GetType().GetMethod(
            "TogglePlayMode",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(shell, Array.Empty<object?>());
    }

    private static void Navigate(Control control, int step) => RaiseKeyDown(control, step > 0 ? Keys.Down : Keys.Up);

    private static void RaiseKeyDown(Control control, Keys key)
    {
        control.Focus();
        Application.DoEvents();
        var field = typeof(Control).GetField("EventKeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var keyObject = field?.GetValue(control);
        if (keyObject is null) return;
        var eventsProperty = typeof(Control).GetProperty("Events", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var events = (System.ComponentModel.EventHandlerList?)eventsProperty?.GetValue(control);
        var handler = events?[keyObject] as KeyEventHandler;
        handler?.Invoke(control, new KeyEventArgs(key));
    }

    private static async Task WaitForHistoryLoaded(MusicBrowseWorkflow workflow, int count)
    {
        var field = typeof(MusicBrowseWorkflow).GetField(
            "_history", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        await WaitFor(async () =>
        {
            var history = Assert.IsType<List<SearchHistory>>(field!.GetValue(workflow));
            if (history.Count != count) return false;
            // Shell construction starts its own asynchronous reload. The list can already contain the
            // preloaded records, so give that reload enough time to finish before keyboard navigation.
            await Task.Delay(75);
            return history.Count == count;
        });
    }

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (await condition()) return;
            await Task.Delay(25);
        }
        Assert.True(await condition(), "condition was not reached");
    }

    private sealed class ModeRecordingPlayback : IPlaybackCoordinator
    {
        public PlayMode Mode { get; set; }
        public bool IsPlaying => false;
        public bool IsPaused => false;
        public MusicInfo? CurrentMusic => null;
        public string DefaultQuality { get; set; } = "128k";
#pragma warning disable 67
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted;
#pragma warning restore 67

        public Task<PlaybackResult> PlayAsync(PlaybackRequest request) => Task.FromResult(
            new PlaybackResult(PlaybackResultStatus.Playing, request.Selection, request.Origin, "playing"));
        public Task<PlaybackResult> ReplayAsync() => Task.FromResult(
            new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
        public void Pause() { }
        public void Stop() { }
        public Task<PlaybackResult> NextAsync() => Task.FromResult(
            new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
        public Task<PlaybackResult> PreviousAsync() => Task.FromResult(
            new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Static, "no change"));
        public Task ResumeAsync() => Task.CompletedTask;
    }

    private sealed class RecordingSettingsStore(UserSettings? loaded = null) : ISettingsStore
    {
        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(loaded);
        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingHotkeyAdapter : IGlobalHotkeyAdapter
    {
        public int LastRegisterError { get; private set; }
        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers) => true;
        public bool Unregister(IntPtr windowHandle, int id) => true;
    }
}
