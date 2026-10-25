using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>
/// Regression tests for issue #71: lyric announcements were delivered through both UI Automation
/// and optional TTS, voice preference was not connected to the announcer, the unbound hide hotkey
/// gave users no working global default, and hiding the main window left owned tool windows exposed.
/// </summary>
public sealed class Issue71AccessibleWindowAndSpeechTests
{
    [Fact]
    public void Hide_hotkey_has_a_registered_system_wide_default()
    {
        WinFormsTestRunner.Run(() =>
        {
            var adapter = new RecordingAdapter();
            var workflow = CreateWorkflow(new Store(), adapter);

            _ = workflow.LoadAsync(new IntPtr(1234)).GetAwaiter().GetResult();
            var hide = Single(workflow.GetHotkeys(), HotkeyActions.ToggleWindow, isGlobal: true);

            Assert.True(hide.IsEnabled);
            Assert.Equal(PlatformKey.H, hide.Key);
            Assert.Equal(PlatformModifiers.Control | PlatformModifiers.Alt, hide.Modifiers);
            Assert.True(workflow.IsGlobalHotkeyRegistered(hide));
            Assert.Contains(14, adapter.RegisteredIds);
        });
    }

    [Fact]
    public void Legacy_unbound_hide_hotkey_is_migrated_to_the_new_default()
    {
        WinFormsTestRunner.Run(() =>
        {
            var adapter = new RecordingAdapter();
            var settings = new UserSettings { HotkeySchemaVersion = 2 };
            settings.CustomHotkeys.Add(new Hotkey
            {
                Id = HotkeyActions.ToggleWindow,
                ActionId = HotkeyActions.ToggleWindow,
                IsGlobal = true,
                IsEnabled = false
            });
            var workflow = CreateWorkflow(new Store { Loaded = settings }, adapter);

            var state = workflow.LoadAsync(new IntPtr(1234)).GetAwaiter().GetResult();
            var hide = Single(state.Settings.CustomHotkeys, HotkeyActions.ToggleWindow, isGlobal: true);

            Assert.Equal(3, state.Settings.HotkeySchemaVersion);
            Assert.Equal(PlatformKey.H, hide.Key);
            Assert.True(workflow.IsGlobalHotkeyRegistered(hide));
        });
    }

    [Fact]
    public void Hiding_the_window_group_hides_owned_tools_and_restore_brings_them_back()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell();
            var first = new Form { AccessibleName = "Tool one" };
            var second = new Form { AccessibleName = "Tool two" };
            var integration = new WinFormsDesktopIntegration(new RecordingAnnouncer());
            try
            {
                integration.Attach(shell);
                first.Owner = shell;
                second.Owner = shell;
                shell.Show();
                first.Show();
                second.Show();
                Application.DoEvents();

                Assert.True(integration.HideToTray());
                Application.DoEvents();
                Assert.False(shell.Visible);
                Assert.False(first.Visible);
                Assert.False(second.Visible);

                integration.RestoreWindow();
                Application.DoEvents();
                Assert.True(shell.Visible);
                Assert.True(first.Visible);
                Assert.True(second.Visible);
            }
            finally
            {
                first.Dispose();
                second.Dispose();
                integration.Dispose();
            }
        });
    }

    [Fact]
    public void All_announcements_use_tts_without_uia_notifications()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var live = new Label();
            var tts = new CountingTts();
            var announcer = new WinFormsAccessibleAnnouncer(
                new ITtsService[] { tts },
                NullLogger<WinFormsAccessibleAnnouncer>.Instance);
            announcer.Attach(live);

            announcer.Announce(new AccessibleAnnouncement(
                "第一句歌词",
                AnnouncementKind.Progress,
                "lyrics",
                AnnouncementSpeech.Always));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && tts.SpeakCount == 0)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }

            Assert.Equal(1, tts.SpeakCount);
            Assert.Equal("第一句歌词", live.AccessibleName);

            announcer.Announce(new AccessibleAnnouncement("搜索完成", AnnouncementKind.Status, "search-results"));
            Assert.Equal(1, tts.SpeakCount);
        });
    }

    [Fact]
    public void Voice_preference_is_applied_to_the_shell_announcer()
    {
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var settings = CreateWorkflow(new Store(), new RecordingAdapter());
            _ = await settings.LoadAsync(IntPtr.Zero);
            settings.Edit(current => current.EnableVoiceAnnouncement = false);
            var announcer = new RecordingAnnouncer();
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = announcer,
                BrowseSource = new FakeBrowseSource(),
                Playback = new RecordingPlayback(),
                Settings = settings,
                TemporaryDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
            });

            var apply = typeof(WinFormsShell).GetMethod(
                "ApplySettings",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            Assert.NotNull(apply);
            apply!.Invoke(shell, Array.Empty<object?>());

            Assert.False(announcer.SpeechEnabled);
        });
    }

    private static SettingsWorkflow CreateWorkflow(ISettingsStore store, IGlobalHotkeyAdapter adapter) => new(
        store,
        new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, adapter),
        NullLogger<SettingsWorkflow>.Instance);

    private static Hotkey Single(IEnumerable<Hotkey> hotkeys, string actionId, bool isGlobal) =>
        Assert.Single(hotkeys, item => item.ActionId == actionId && item.IsGlobal == isGlobal);

    private static WinFormsShell CreateShell() => new(new WinFormsShellServices
    {
        Announcer = new RecordingAnnouncer(),
        BrowseSource = new FakeBrowseSource(),
        Playback = new RecordingPlayback(),
        TemporaryDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    });

    private sealed class Store : ISettingsStore
    {
        public UserSettings? Loaded { get; init; }
        public Task SaveAsync(UserSettings settings, System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<UserSettings?> LoadAsync(System.Threading.CancellationToken cancellationToken) => Task.FromResult(Loaded);
    }

    private sealed class RecordingAdapter : IGlobalHotkeyAdapter
    {
        public List<int> RegisteredIds { get; } = new();
        public int LastRegisterError { get; }

        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers)
        {
            RegisteredIds.Add(id);
            return true;
        }

        public bool Unregister(IntPtr windowHandle, int id)
        {
            RegisteredIds.Remove(id);
            return true;
        }
    }

    private sealed class CountingTts : ITtsService
    {
        public int SpeakCount { get; private set; }
        public bool IsAvailable => true;
        public string ActiveBackendName => "Test";

        public Task SpeakAsync(string text)
        {
            SpeakCount++;
            return Task.CompletedTask;
        }

        public Task SpeakQueuedAsync(string text) => Task.CompletedTask;
        public void Stop() { }
        public void SetRate(double rate) { }
        public void SetVoice(string voiceId) { }
        public List<VoiceInfo> GetAvailableVoices() => new();
    }

}