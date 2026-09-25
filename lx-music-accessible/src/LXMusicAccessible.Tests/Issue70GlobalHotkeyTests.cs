using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>
/// Issue #70: configured global hotkeys did nothing. The persisted bindings keep the legacy
/// menu-era key numbering (Home is 22) while RegisterHotKey and keyboard dispatch need Win32
/// virtual-key codes (VK_HOME is 0x24), the "is registered" predicate was inverted, local routing
/// hardcoded F1-F12 instead of consulting the configured bindings, and startup awaited off the
/// message-loop thread so the message window existed but never received WM_HOTKEY. These tests pin
/// each behaviour on the same seams the production composition uses: the settings workflow, the
/// native adapter contract, the real hidden message window and the shell's key routing.
/// </summary>
public sealed class Issue70GlobalHotkeyTests
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyAlreadyRegistered = 1409;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);

    // ------------------------------------------------------------------ key translation (RC1)

    [Fact]
    public void Platform_keys_map_to_win32_virtual_key_codes_not_legacy_menu_numbers()
    {
        // The persisted numbering is menu-era: Home is 22. RegisterHotKey needs VK_HOME (0x24).
        Assert.Equal(22, (int)PlatformKey.Home);
        Assert.Equal(36u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.Home));
        Assert.Equal(35u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.End));
        Assert.Equal(33u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.Prior));
        Assert.Equal(34u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.Next));
        Assert.Equal(0x25u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.Left));
        Assert.Equal(0x27u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.Right));
        Assert.Equal(0x26u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.Up));
        Assert.Equal(0x28u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.Down));
        Assert.Equal(0x70u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.F1));
        Assert.Equal(0x87u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.F24));
        Assert.Equal(0x41u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.A));
        Assert.Equal(0x30u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.D0));
        Assert.Equal(0u, Win32GlobalHotkeyAdapter.ToVirtualKey(PlatformKey.None));

        // Native dispatch reads a virtual key and has to map it back onto the configured binding,
        // or a correctly registered global would resolve to no action at all.
        Assert.Equal(PlatformKey.Home, KeyboardKeys.FromVirtualKey(0x24));
        Assert.Equal(PlatformKey.End, KeyboardKeys.FromVirtualKey(0x23));
        Assert.Equal(PlatformKey.F1, KeyboardKeys.FromVirtualKey(0x70));
        Assert.Equal(PlatformKey.A, KeyboardKeys.FromVirtualKey(0x41));
        Assert.Equal(PlatformKey.None, KeyboardKeys.FromVirtualKey(0));
        Assert.Equal(0x21u, KeyboardKeys.ToVirtualKey(KeyboardKeys.FromVirtualKey(0x21)));
        Assert.Equal(0x22u, KeyboardKeys.ToVirtualKey(KeyboardKeys.FromVirtualKey(0x22)));

        // A WinForms key code such as Keys.F10 is a virtual key too, so the shell route and the
        // native route resolve the same configured binding.
        Assert.Equal(PlatformKey.F10, KeyboardKeys.FromVirtualKey((uint)Keys.F10));
        Assert.Equal(PlatformKey.Down, KeyboardKeys.FromVirtualKey((uint)Keys.Down));
    }

    [Fact]
    public void Keys_without_a_virtual_key_code_are_rejected_before_native_registration()
    {
        var adapter = new Win32GlobalHotkeyAdapter();

        Assert.False(adapter.Register(new IntPtr(1), 0, PlatformKey.None, PlatformModifiers.None));
        Assert.Equal(1400, adapter.LastRegisterError);
    }
    // ----------------------------------------------- native registration of the translated key

    [Fact]
    public void Translated_virtual_key_is_accepted_by_native_registration()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var window = new GlobalHotkeyMessageWindow();
            var native = new Win32GlobalHotkeyAdapter();
            var modifiers = PlatformModifiers.Control | PlatformModifiers.Alt | PlatformModifiers.Shift;
            try
            {
                Assert.True(
                    native.Register(window.Handle, 0x7070, PlatformKey.F24, modifiers),
                    "RegisterHotKey rejected the translated virtual key with " + native.LastRegisterError);
            }
            finally
            {
                native.Unregister(window.Handle, 0x7070);
            }
        });
    }

    // --------------------------------------------------------- startup registration on the window

    [Fact]
    public async Task Startup_registration_binds_every_enabled_global_on_the_message_window()
    {
        var adapter = new ProbeHotkeyAdapter();
        var workflow = CreateWorkflow(new ProbeSettingsStore(), adapter);

        var state = await workflow.LoadAsync(new IntPtr(1234));

        Assert.Equal(3, state.Settings.HotkeySchemaVersion);
        Assert.Empty(workflow.LastRegistrationFailures);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 8, 9, 14 }, adapter.IdsOn(new IntPtr(1234)));
        AssertBinding(adapter, new IntPtr(1234), 0, PlatformKey.Home);
        AssertBinding(adapter, new IntPtr(1234), 1, PlatformKey.End);
        AssertBinding(adapter, new IntPtr(1234), 2, PlatformKey.Prior);
        AssertBinding(adapter, new IntPtr(1234), 3, PlatformKey.Next);
        AssertBinding(adapter, new IntPtr(1234), 4, PlatformKey.Left);
        AssertBinding(adapter, new IntPtr(1234), 5, PlatformKey.Right);
        AssertBinding(adapter, new IntPtr(1234), 8, PlatformKey.Up);
        AssertBinding(adapter, new IntPtr(1234), 9, PlatformKey.Down);

        foreach (var action in new[]
                 {
                     HotkeyActions.PlayPause, HotkeyActions.Stop, HotkeyActions.Previous, HotkeyActions.Next,
                     HotkeyActions.Rewind, HotkeyActions.Forward, HotkeyActions.VolumeUp, HotkeyActions.VolumeDown
                 })
        {
            Assert.True(workflow.IsGlobalHotkeyRegistered(Global(workflow, action)), action);
        }

        // A binding the user never enabled must not be registered natively.
        Assert.DoesNotContain(6, adapter.IdsOn(new IntPtr(1234)));
        Assert.DoesNotContain(7, adapter.IdsOn(new IntPtr(1234)));
    }

    [Fact]
    public async Task Enabled_global_is_not_reported_registered_without_a_message_window()
    {
        var adapter = new ProbeHotkeyAdapter();
        var workflow = CreateWorkflow(new ProbeSettingsStore(), adapter);
        var raised = new List<string>();
        workflow.HotkeyPressed += (_, e) => raised.Add(e.Hotkey.ActionId);

        await workflow.LoadAsync(IntPtr.Zero);

        Assert.False(workflow.IsGlobalHotkeyRegistered(Global(workflow, HotkeyActions.PlayPause)));
        Assert.Empty(workflow.LastRegistrationFailures);
        Assert.DoesNotContain(
            adapter.Operations,
            operation => operation.StartsWith("register:", StringComparison.Ordinal));
        workflow.RaiseGlobalHotkey(0);
        Assert.Empty(raised);
    }

    [Fact]
    public async Task Reattaching_moves_native_registrations_to_the_new_window_handle()
    {
        var adapter = new ProbeHotkeyAdapter();
        var workflow = CreateWorkflow(new ProbeSettingsStore(), adapter);

        await workflow.LoadAsync(new IntPtr(100));
        adapter.Operations.Clear();
        await workflow.LoadAsync(new IntPtr(200));

        Assert.Empty(adapter.IdsOn(new IntPtr(100)));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 8, 9, 14 }, adapter.IdsOn(new IntPtr(200)));
        Assert.Contains("unregister:100:0", adapter.Operations);
        Assert.Contains("unregister:100:9", adapter.Operations);
        Assert.Contains("register:200:0:" + PlatformKey.Home, adapter.Operations);
        Assert.DoesNotContain(
            adapter.Operations,
            operation => operation.StartsWith("register:100:", StringComparison.Ordinal));
        Assert.True(workflow.IsGlobalHotkeyRegistered(Global(workflow, HotkeyActions.Stop)));
        Assert.Empty(workflow.LastRegistrationFailures);
    }

    // --------------------------------------------------------- replacement and rollback (RC5, RC6)

    [Fact]
    public async Task Failed_replacement_restores_the_previous_binding_and_keeps_settings()
    {
        var adapter = new ProbeHotkeyAdapter();
        var store = new ProbeSettingsStore();
        var workflow = CreateWorkflow(store, adapter);
        await workflow.LoadAsync(new IntPtr(600));

        adapter.Fail((PlatformKey.F24, PlatformModifiers.Control | PlatformModifiers.Alt));
        var result = await workflow.ReplaceHotkeyAsync(
            HotkeyActions.Rewind,
            isGlobal: true,
            PlatformKey.F24,
            PlatformModifiers.Control | PlatformModifiers.Alt);

        Assert.Equal(HotkeyEditStatus.RegistrationFailed, result.Status);
        Assert.False(result.Applied);
        Assert.Empty(store.Saved);
        // The replacement was attempted, then compensated: the old binding is live again and no
        // partial F24 binding was left behind.
        Assert.Contains("register:600:4:" + PlatformKey.F24, adapter.Operations);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 8, 9, 14 }, adapter.IdsOn(new IntPtr(600)));
        AssertBinding(adapter, new IntPtr(600), 4, PlatformKey.Left);
        Assert.Contains(adapter.Operations, operation => operation == "unregister:600:4");
        var rewind = Global(workflow, HotkeyActions.Rewind);
        Assert.Equal(PlatformKey.Left, rewind.Key);
        Assert.True(rewind.IsEnabled);
        Assert.True(workflow.IsGlobalHotkeyRegistered(rewind));
        Assert.Equal(
            PlatformKey.Left,
            result.State.Settings.CustomHotkeys.Single(item => item.ActionId == HotkeyActions.Rewind && item.IsGlobal).Key);
    }

    [Fact]
    public async Task Failed_multi_binding_rollback_preserves_every_previous_binding()
    {
        var adapter = new ProbeHotkeyAdapter();
        var store = new ProbeSettingsStore
        {
            Loaded = SavedGlobals(
                (HotkeyActions.Rewind, PlatformKey.F23, PlatformModifiers.Control | PlatformModifiers.Alt),
                (HotkeyActions.Forward, PlatformKey.F24, PlatformModifiers.Control | PlatformModifiers.Alt))
        };
        var workflow = CreateWorkflow(store, adapter);
        await workflow.LoadAsync(new IntPtr(700));
        AssertBinding(adapter, new IntPtr(700), 4, PlatformKey.F23);
        AssertBinding(adapter, new IntPtr(700), 5, PlatformKey.F24);

        // Resetting changes two globals at once, so the manager re-registers the whole table; the
        // second registration fails and every original binding has to come back untouched.
        adapter.Fail((PlatformKey.Right, PlatformModifiers.Control | PlatformModifiers.Alt));
        var result = await workflow.ResetHotkeysAsync();

        Assert.Equal(HotkeyEditStatus.RegistrationFailed, result.Status);
        Assert.False(result.Applied);
        Assert.Empty(store.Saved);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 8, 9, 14 }, adapter.IdsOn(new IntPtr(700)));
        AssertBinding(adapter, new IntPtr(700), 0, PlatformKey.Home);
        AssertBinding(adapter, new IntPtr(700), 4, PlatformKey.F23);
        AssertBinding(adapter, new IntPtr(700), 5, PlatformKey.F24);
        AssertBinding(adapter, new IntPtr(700), 9, PlatformKey.Down);
        Assert.Equal(PlatformKey.F23, Global(workflow, HotkeyActions.Rewind).Key);
        Assert.Equal(PlatformKey.F24, Global(workflow, HotkeyActions.Forward).Key);
        Assert.Equal(
            PlatformKey.F23,
            result.State.Settings.CustomHotkeys.Single(item => item.ActionId == HotkeyActions.Rewind && item.IsGlobal).Key);
        Assert.True(workflow.IsGlobalHotkeyRegistered(Global(workflow, HotkeyActions.Forward)));
    }

    [Fact]
    public async Task Conflicting_or_invalid_edit_is_reported_and_changes_nothing()
    {
        var adapter = new ProbeHotkeyAdapter();
        var store = new ProbeSettingsStore();
        var workflow = CreateWorkflow(store, adapter);
        await workflow.LoadAsync(new IntPtr(800));

        var conflict = await workflow.ReplaceHotkeyAsync(
            HotkeyActions.PlayPause,
            isGlobal: true,
            PlatformKey.End,
            PlatformModifiers.Control | PlatformModifiers.Alt);

        Assert.Equal(HotkeyEditStatus.Conflict, conflict.Status);
        Assert.False(conflict.Applied);
        Assert.Equal(
            new[] { HotkeyActions.PlayPause, HotkeyActions.Stop },
            conflict.Conflicts.Select(hotkey => hotkey.ActionId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Empty(store.Saved);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 8, 9, 14 }, adapter.IdsOn(new IntPtr(800)));
        AssertBinding(adapter, new IntPtr(800), 0, PlatformKey.Home);
        AssertBinding(adapter, new IntPtr(800), 1, PlatformKey.End);
        Assert.Equal(PlatformKey.Home, Global(workflow, HotkeyActions.PlayPause).Key);

        var invalid = await workflow.ReplaceHotkeyAsync(
            HotkeyActions.Stop, isGlobal: true, PlatformKey.LeftCtrl, PlatformModifiers.Control);

        Assert.Equal(HotkeyEditStatus.Invalid, invalid.Status);
        Assert.False(invalid.Applied);
        // An invalid key is reported as the rejected binding so the settings form can name it.
        Assert.Equal(new[] { HotkeyActions.Stop }, invalid.Conflicts.Select(hotkey => hotkey.ActionId));
        Assert.Empty(store.Saved);
        AssertBinding(adapter, new IntPtr(800), 1, PlatformKey.End);
        Assert.Equal(PlatformKey.End, Global(workflow, HotkeyActions.Stop).Key);
    }

    [Fact]
    public async Task Clearing_and_resetting_globals_update_native_registration_and_persist()
    {
        var adapter = new ProbeHotkeyAdapter();
        var store = new ProbeSettingsStore();
        var workflow = CreateWorkflow(store, adapter);
        await workflow.LoadAsync(new IntPtr(810));
        var pressed = new List<string>();
        workflow.HotkeyPressed += (_, e) => pressed.Add(e.Hotkey.ActionId);

        var cleared = await workflow.ClearHotkeyAsync(HotkeyActions.Stop, isGlobal: true);

        Assert.Equal(HotkeyEditStatus.Applied, cleared.Status);
        Assert.Single(store.Saved);
        Assert.Equal(new[] { 0, 2, 3, 4, 5, 8, 9, 14 }, adapter.IdsOn(new IntPtr(810)));
        var clearedStop = Global(workflow, HotkeyActions.Stop);
        Assert.Equal(PlatformKey.None, clearedStop.Key);
        Assert.False(clearedStop.IsEnabled);
        Assert.Null(workflow.MatchFallback(PlatformKey.End, PlatformModifiers.Control | PlatformModifiers.Alt));
        workflow.RaiseGlobalHotkey(1);
        Assert.Empty(pressed);
        Assert.Equal(
            PlatformKey.None,
            store.Saved[0].CustomHotkeys.Single(item => item.ActionId == HotkeyActions.Stop && item.IsGlobal).Key);

        var reset = await workflow.ResetHotkeysAsync();

        Assert.Equal(HotkeyEditStatus.Applied, reset.Status);
        Assert.Equal(2, store.Saved.Count);
        AssertBinding(adapter, new IntPtr(810), 1, PlatformKey.End);
        var resetStop = Global(workflow, HotkeyActions.Stop);
        Assert.Equal(PlatformKey.End, resetStop.Key);
        Assert.True(resetStop.IsEnabled);
        Assert.True(workflow.IsGlobalHotkeyRegistered(resetStop));
        workflow.RaiseGlobalHotkey(1);
        Assert.Equal(new[] { HotkeyActions.Stop }, pressed);
        Assert.Equal(
            PlatformKey.End,
            store.Saved[1].CustomHotkeys.Single(item => item.ActionId == HotkeyActions.Stop && item.IsGlobal).Key);
    }

    // --------------------------------------------------------------- message window dispatch (RC8)

    [Fact]
    public void Message_window_dispatches_a_registered_hotkey_exactly_once() =>
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            using var window = new GlobalHotkeyMessageWindow();
            var adapter = new ProbeHotkeyAdapter();
            var workflow = CreateWorkflow(new ProbeSettingsStore(), adapter);
            var pressed = new List<string>();
            await workflow.LoadAsync(window.Handle);
            window.HotkeyReceived += (_, id) => workflow.RaiseGlobalHotkey(id);
            workflow.HotkeyPressed += (_, e) => pressed.Add(e.Hotkey.ActionId);

            Assert.True(PostMessage(window.Handle, WmHotkey, IntPtr.Zero, IntPtr.Zero));
            Application.DoEvents();
            Assert.Equal(new[] { HotkeyActions.PlayPause }, pressed);

            // A hotkey id that Windows never registered must not run an action.
            Assert.True(PostMessage(window.Handle, WmHotkey, new IntPtr(6), IntPtr.Zero));
            Application.DoEvents();
            Assert.Equal(new[] { HotkeyActions.PlayPause }, pressed);

            // A second press runs exactly once more; the two routes must not both fire.
            Assert.True(PostMessage(window.Handle, WmHotkey, IntPtr.Zero, IntPtr.Zero));
            Application.DoEvents();
            Assert.Equal(new[] { HotkeyActions.PlayPause, HotkeyActions.PlayPause }, pressed);
        });
    // ------------------------------------------------------------------ shell key routing (RC3, RC4)

    [Fact]
    public void Local_routing_follows_configuration_and_does_not_replay_registered_globals() =>
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var adapter = new ProbeHotkeyAdapter();
            var workflow = CreateWorkflow(new ProbeSettingsStore(), adapter);
            var playback = new ProbePlayback();
            var audio = new ProbeAudio();
            using var shell = CreateShell(new RecordingAnnouncer(), workflow, playback, audio);
            await workflow.LoadAsync(new IntPtr(500));

            // The default local volume-up binding is F10 and it runs through the same action table a
            // configured global uses, so the two routes cannot drift apart.
            Assert.True(InvokeProcessCmdKey(shell, Keys.F10, shell.Handle));
            Assert.Equal(1, audio.VolumeChanges);

            // Ctrl+Alt+End is registered natively, so the local route must leave it to the message
            // window instead of running the same action a second time.
            Assert.False(InvokeProcessCmdKey(shell, Keys.Control | Keys.Alt | Keys.End, shell.Handle));
            Assert.Null(workflow.MatchLocalHotkey(PlatformKey.End, PlatformModifiers.Control | PlatformModifiers.Alt));
            Assert.Null(workflow.MatchFallback(PlatformKey.End, PlatformModifiers.Control | PlatformModifiers.Alt));
            Assert.Equal(0, playback.StopCalls);

            workflow.RaiseGlobalHotkey(1);
            Assert.Equal(1, playback.StopCalls);

            // Rebinding a local shortcut must take effect for the shell route immediately.
            var rebound = await workflow.ReplaceHotkeyAsync(
                HotkeyActions.VolumeUp, isGlobal: false, PlatformKey.Home, PlatformModifiers.Shift);
            Assert.Equal(HotkeyEditStatus.Applied, rebound.Status);
            Assert.Null(workflow.MatchLocalHotkey(PlatformKey.F10, PlatformModifiers.None));

            Assert.True(InvokeProcessCmdKey(shell, Keys.Shift | Keys.Home, shell.Handle));
            Assert.Equal(2, audio.VolumeChanges);
            Assert.True(InvokeProcessCmdKey(shell, Keys.F9, shell.Handle));
            Assert.Equal(3, audio.VolumeChanges);
        });

    [Fact]
    public void Global_binding_that_could_not_register_still_runs_as_a_local_fallback() =>
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var adapter = new ProbeHotkeyAdapter();
            adapter.Fail((PlatformKey.End, PlatformModifiers.Control | PlatformModifiers.Alt));
            var workflow = CreateWorkflow(new ProbeSettingsStore(), adapter);
            var playback = new ProbePlayback();
            await workflow.LoadAsync(new IntPtr(501));
            using var shell = CreateShell(new RecordingAnnouncer(), workflow, playback, new ProbeAudio());

            Assert.Equal(
                new[] { HotkeyActions.Stop },
                workflow.LastRegistrationFailures.Select(hotkey => hotkey.ActionId));
            Assert.False(workflow.IsGlobalHotkeyRegistered(Global(workflow, HotkeyActions.Stop)));

            // Windows never received the binding, so the in-window route has to keep the action
            // reachable instead of reporting a hotkey that silently does nothing.
            Assert.True(InvokeProcessCmdKey(shell, Keys.Control | Keys.Alt | Keys.End, shell.Handle));
            Assert.Equal(1, playback.StopCalls);
        });
    [Fact]
    public void Writable_text_box_suppresses_hotkeys_while_readers_keep_them() =>
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var adapter = new ProbeHotkeyAdapter();
            var workflow = CreateWorkflow(new ProbeSettingsStore(), adapter);
            var audio = new ProbeAudio();
            using var shell = CreateShell(new RecordingAnnouncer(), workflow, new ProbePlayback(), audio);
            await workflow.LoadAsync(new IntPtr(502));

            var editor = new TextBox { Name = "editor", AccessibleName = "editor" };
            var reader = new TextBox { Name = "reader", AccessibleName = "reader", ReadOnly = true };
            shell.Controls.Add(editor);
            shell.Controls.Add(reader);
            _ = editor.Handle;
            _ = reader.Handle;

            var rebound = await workflow.ReplaceHotkeyAsync(
                HotkeyActions.VolumeUp, isGlobal: false, PlatformKey.Home, PlatformModifiers.Shift);
            Assert.Equal(HotkeyEditStatus.Applied, rebound.Status);

            // Typing in a writable box must never fire a playback action.
            InvokeProcessCmdKey(shell, Keys.Shift | Keys.Home, editor.Handle);
            Assert.Equal(0, audio.VolumeChanges);

            // A read-only lyrics reader keeps the playback keys.
            Assert.True(InvokeProcessCmdKey(shell, Keys.Shift | Keys.Home, reader.Handle));
            Assert.Equal(1, audio.VolumeChanges);

            Assert.True(InvokeProcessCmdKey(shell, Keys.Shift | Keys.Home, shell.Handle));
            Assert.Equal(2, audio.VolumeChanges);
        });

    [Fact]
    public void Global_bindings_survive_hide_show_focus_changes_and_later_settings_changes() =>
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var adapter = new ProbeHotkeyAdapter();
            var workflow = CreateWorkflow(new ProbeSettingsStore(), adapter);
            var playback = new ProbePlayback();
            using var shell = CreateShell(new RecordingAnnouncer(), workflow, playback, new ProbeAudio());
            var handle = shell.Handle;
            await workflow.LoadAsync(handle);

            shell.Show();
            Application.DoEvents();
            var editor = new TextBox { Name = "editor", AccessibleName = "editor" };
            shell.Controls.Add(editor);
            editor.Focus();
            Application.DoEvents();

            // A global binding does not depend on the shell having focus.
            workflow.RaiseGlobalHotkey(1);
            Assert.Equal(1, playback.StopCalls);

            shell.Hide();
            Application.DoEvents();
            workflow.RaiseGlobalHotkey(1);
            Assert.Equal(2, playback.StopCalls);

            shell.Show();
            Application.DoEvents();
            Assert.Equal(handle, shell.Handle);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 8, 9, 14 }, adapter.IdsOn(handle));
            workflow.RaiseGlobalHotkey(1);
            Assert.Equal(3, playback.StopCalls);

            // A later settings change must not drop the bindings that were already working.
            var rebound = await workflow.ReplaceHotkeyAsync(
                HotkeyActions.Download,
                isGlobal: true,
                PlatformKey.F24,
                PlatformModifiers.Control | PlatformModifiers.Alt);
            Assert.Equal(HotkeyEditStatus.Applied, rebound.Status);
            AssertBinding(adapter, handle, 7, PlatformKey.F24);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 7, 8, 9, 14 }, adapter.IdsOn(handle));
            AssertBinding(adapter, handle, 0, PlatformKey.Home);
            AssertBinding(adapter, handle, 1, PlatformKey.End);
            Assert.True(workflow.IsGlobalHotkeyRegistered(Global(workflow, HotkeyActions.Stop)));
            Assert.Empty(workflow.LastRegistrationFailures);
            workflow.RaiseGlobalHotkey(1);
            Assert.Equal(4, playback.StopCalls);

            shell.Hide();
        });
    // ------------------------------------------------------- accessible failure reporting (RC6, RC7)

    [Fact]
    public void Startup_registration_failures_are_announced_as_accessible_errors() =>
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var adapter = new ProbeHotkeyAdapter();
            adapter.Fail(
                (PlatformKey.Home, PlatformModifiers.Control | PlatformModifiers.Alt),
                (PlatformKey.End, PlatformModifiers.Control | PlatformModifiers.Alt));
            var workflow = CreateWorkflow(new ProbeSettingsStore(), adapter);
            await workflow.LoadAsync(new IntPtr(901));
            Assert.Equal(2, workflow.LastRegistrationFailures.Count);

            // Production resolves the shell after startup registration already ran, so the shell has
            // to report the recorded failures instead of waiting for an event nobody listened to.
            var announcer = new RecordingAnnouncer();
            using var shell = CreateShell(announcer, workflow, new ProbePlayback(), new ProbeAudio());

            var failure = Assert.Single(
                announcer.Announcements,
                item => item.ActivityId == "hotkey-registration");
            Assert.Equal(AnnouncementKind.Error, failure.Kind);
            Assert.False(string.IsNullOrWhiteSpace(failure.Text));
        });

    // ------------------------------------------------------------------- settings UI recorder (RC5)

    [Fact]
    public void Settings_recorder_persists_the_win32_key_and_escape_cancels() =>
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var adapter = new ProbeHotkeyAdapter();
            var workflow = CreateWorkflow(new ProbeSettingsStore(), adapter);
            await workflow.LoadAsync(new IntPtr(902));
            using var form = new SettingsForm(
                new RecordingAnnouncer(),
                workflow,
                new ProbeTts(),
                new FakeDownloadWorkflow(),
                _ => false,
                _ => { });
            form.Show();
            Application.DoEvents();

            var item = new HotkeySettingItem
            {
                ActionId = HotkeyActions.VolumeUp,
                ActionName = "volume up",
                IsGlobal = true
            };
            StartRecording(form, item);
            Assert.True(form.IsRecordingHotkey);

            InvokeKeyDown(form, Keys.Shift | Keys.Home);
            await WaitUntil(() => !form.IsRecordingHotkey);

            var volumeUp = Global(workflow, HotkeyActions.VolumeUp);
            Assert.Equal(PlatformKey.Home, volumeUp.Key);
            Assert.Equal(PlatformModifiers.Shift, volumeUp.Modifiers);
            AssertBinding(adapter, new IntPtr(902), 8, PlatformKey.Home, PlatformModifiers.Shift);

            StartRecording(form, item);
            Assert.True(form.IsRecordingHotkey);
            InvokeKeyDown(form, Keys.Escape);
            await WaitUntil(() => !form.IsRecordingHotkey);

            Assert.False(form.IsDisposed);
            Assert.Equal(PlatformKey.Home, Global(workflow, HotkeyActions.VolumeUp).Key);
        });
    // --------------------------------------------------------------------------------- helpers

    private static SettingsWorkflow CreateWorkflow(ISettingsStore store, IGlobalHotkeyAdapter adapter) =>
        new(
            store,
            new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, adapter),
            NullLogger<SettingsWorkflow>.Instance);

    private static WinFormsShell CreateShell(
        RecordingAnnouncer announcer,
        SettingsWorkflow workflow,
        IPlaybackCoordinator playback,
        IAudioPlaybackControl audio) =>
        new(new WinFormsShellServices
        {
            Announcer = announcer,
            BrowseSource = new InteractionBrowseSource(),
            Playback = playback,
            Audio = audio,
            Settings = workflow,
            TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-issue70-" + Guid.NewGuid().ToString("N"))
        });

    private static Hotkey Global(SettingsWorkflow workflow, string actionId) =>
        workflow.GetHotkeys().Single(item => item.ActionId == actionId && item.IsGlobal);

    private static void AssertBinding(
        ProbeHotkeyAdapter adapter,
        IntPtr handle,
        int id,
        PlatformKey key,
        PlatformModifiers modifiers = PlatformModifiers.Control | PlatformModifiers.Alt)
    {
        Assert.Equal(key, adapter.KeyOn(handle, id));
        Assert.Equal(modifiers, adapter.ModifiersOn(handle, id));
    }

    private static bool InvokeProcessCmdKey(Form form, Keys keyData, IntPtr messageTarget)
    {
        var method = typeof(Form).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var arguments = new object?[] { new Message { HWnd = messageTarget }, keyData };
        return (bool)method.Invoke(form, arguments)!;
    }

    private static void InvokeKeyDown(Form form, Keys keyData)
    {
        var method = form.GetType().GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(form, new object[] { new KeyEventArgs(keyData) });
    }

    private static void StartRecording(SettingsForm form, HotkeySettingItem item)
    {
        var method = typeof(SettingsForm).GetMethod("StartRecording", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(form, new object[] { item });
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition was not reached");
    }

    private static UserSettings SavedGlobals(
        params (string ActionId, PlatformKey Key, PlatformModifiers Modifiers)[] bindings) =>
        new()
        {
            HotkeySchemaVersion = 2,
            CustomHotkeys = bindings.Select(binding => new Hotkey
            {
                Id = binding.ActionId,
                ActionId = binding.ActionId,
                Name = binding.ActionId,
                Key = binding.Key,
                Modifiers = binding.Modifiers,
                IsGlobal = true,
                IsEnabled = true
            }).ToList()
        };
    // --------------------------------------------------------------------------------- test doubles

    private sealed class ProbeSettingsStore : ISettingsStore
    {
        public UserSettings? Loaded { get; set; }
        public List<UserSettings> Saved { get; } = new();

        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Loaded);

        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken)
        {
            Saved.Add(settings);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records native registration per window handle so a test can prove registrations move with
    /// re-attachment, that a failed replacement is rolled back, and that a conflict or an invalid key
    /// never touches the native table at all.
    /// </summary>
    private sealed class ProbeHotkeyAdapter : IGlobalHotkeyAdapter
    {
        private readonly Dictionary<(IntPtr Handle, int Id), (PlatformKey Key, PlatformModifiers Modifiers)> _registered = new();
        private readonly HashSet<(PlatformKey Key, PlatformModifiers Modifiers)> _fail = new();

        public int LastRegisterError { get; private set; }
        public List<string> Operations { get; } = new();

        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers)
        {
            Operations.Add($"register:{windowHandle}:{id}:{key}");
            if (_fail.Contains((key, modifiers)))
            {
                LastRegisterError = HotkeyAlreadyRegistered;
                return false;
            }
            _registered[(windowHandle, id)] = (key, modifiers);
            return true;
        }

        public bool Unregister(IntPtr windowHandle, int id)
        {
            Operations.Add($"unregister:{windowHandle}:{id}");
            _registered.Remove((windowHandle, id));
            return true;
        }

        public void Fail(params (PlatformKey Key, PlatformModifiers Modifiers)[] bindings) =>
            _fail.UnionWith(bindings);

        public IReadOnlyList<int> IdsOn(IntPtr windowHandle) =>
            _registered.Keys
                .Where(entry => entry.Handle == windowHandle)
                .Select(entry => entry.Id)
                .OrderBy(id => id)
                .ToArray();

        public PlatformKey? KeyOn(IntPtr windowHandle, int id) =>
            _registered.TryGetValue((windowHandle, id), out var entry) ? entry.Key : null;

        public PlatformModifiers? ModifiersOn(IntPtr windowHandle, int id) =>
            _registered.TryGetValue((windowHandle, id), out var entry) ? entry.Modifiers : null;
    }
    private sealed class ProbePlayback : IPlaybackCoordinator
    {
        public int StopCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public bool IsPlaying => true;
        public bool IsPaused => false;
        public MusicInfo? CurrentMusic => null;
        public PlayMode Mode { get; set; }
        public string DefaultQuality { get; set; } = "128k";
#pragma warning disable 67
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted;
#pragma warning restore 67

        public Task<PlaybackResult> PlayAsync(PlaybackRequest request) =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, request.Selection, request.Origin, "playing"));

        public Task<PlaybackResult> ReplayAsync() =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, null, PlaybackQueueOrigin.Static, "no change"));

        public void Pause() => PauseCalls++;

        public void Stop() => StopCalls++;

        public Task<PlaybackResult> NextAsync() =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, null, PlaybackQueueOrigin.Static, "no change"));

        public Task<PlaybackResult> PreviousAsync() =>
            Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, null, PlaybackQueueOrigin.Static, "no change"));

        public Task ResumeAsync() => Task.CompletedTask;
    }

    private sealed class ProbeAudio : IAudioPlaybackControl
    {
        public int VolumeChanges { get; private set; }
        public int SeekCalls { get; private set; }
        public double Volume { get; private set; } = 0.5;
        public TimeSpan Position => TimeSpan.FromSeconds(30);
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
        public string? ActualQuality => null;

        public void SetVolume(double volume)
        {
            Volume = volume;
            VolumeChanges++;
        }

        public void SyncVolumeFromDevice() { }
        public void SetPosition(TimeSpan position) { }
        public void SeekTo(TimeSpan position) => SeekCalls++;
        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => Array.Empty<OutputDeviceInfo>();
        public int CurrentOutputDeviceIndex => -1;
        public string CurrentOutputDeviceName => "default";
        public string? SwitchToNextOutputDevice() => null;
        public string? SwitchToPreviousOutputDevice() => null;
        public string? SwitchOutputDevice(int index) => null;
    }

    private sealed class ProbeTts : ITtsService
    {
        public bool IsAvailable => false;
        public string ActiveBackendName => "none";
        public Task SpeakAsync(string text) => Task.CompletedTask;
        public Task SpeakQueuedAsync(string text) => Task.CompletedTask;
        public void Stop() { }
        public void SetRate(double rate) { }
        public void SetVoice(string voiceId) { }
        public List<VoiceInfo> GetAvailableVoices() => new();
    }
}
