using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class WinFormsAccessibilityAuditTests
{
    [Fact]
    public void Production_form_inventory_is_complete()
    {
        var forms = typeof(WinFormsShell).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false } && typeof(Form).IsAssignableFrom(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[]
        {
            nameof(BatchSelectionForm),
            nameof(DownloadManagerForm),
            nameof(KugouQrLoginForm),
            nameof(LyricsForm),
            nameof(ManualSourceSwitchForm),
            nameof(PlaylistImportForm),
            nameof(PlaylistLoginMethodForm),
            nameof(SettingsForm),
            nameof(SongDetailForm),
            nameof(SourceManagerForm),
            nameof(SponsorForm),
            nameof(TextInputForm),
            nameof(UserGuideForm),
            nameof(WebLoginForm),
            nameof(WinFormsShell),
        }, forms);
    }

    [Fact]
    public void Every_instantiated_production_form_has_semantic_names_and_native_focus_policy()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var scope = new FormScope();
            Audit(scope.Add(new BatchSelectionForm(new RecordingAnnouncer(), new LXMusicAccessible.Models.MusicInfo[] { new() { Id = "song", Source = "test", Title = "Song" } }, "搜索结果", _ => Task.CompletedTask)));            Audit(scope.Add(CreateShell()));
            Audit(scope.Add(CreateSettings()));
            Audit(scope.Add(new SourceManagerForm(new RecordingAnnouncer(), new AuditSourceService(), _ => false)));
            Audit(scope.Add(new DownloadManagerForm(new RecordingAnnouncer(), new MutableDownloadWorkflow())));
            Audit(scope.Add(CreatePlaylistImport()));
            Audit(scope.Add(CreateLyricsForm()));
            Audit(scope.Add(new ManualSourceSwitchForm(new RecordingAnnouncer(), new AuditManualWorkflow(), Song(), _ => false)));
            Audit(scope.Add(new UserGuideForm(new RecordingAnnouncer())));
            Audit(scope.Add(new SongDetailForm("Song", new[] { "标题：Song" })));
            Audit(scope.Add(new SponsorForm(new RecordingAnnouncer())));
            Audit(scope.Add(new TextInputForm("测试", new[] { ("名称：", "值") })));
            Audit(scope.Add(new PlaylistLoginMethodForm("netease")));
            Audit(scope.Add(new WebLoginForm("netease", new AuditImportService(), new RecordingAnnouncer(), NullLogger<WebLoginForm>.Instance)));
            Audit(scope.Add(new KugouQrLoginForm(
                new AuditImportService(),
                new RecordingAnnouncer(),
                new KugouQrLoginService(new MusicHttpClient(NullLogger<MusicHttpClient>.Instance), NullLogger<KugouQrLoginService>.Instance))));
        });
    }

    [Fact]
    public void Shell_preserves_traditional_menus_and_does_not_capture_f10_from_native_menu_behavior()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell();
            var shortcuts = EnumerateMenu(shell.Menu!).Cast<MenuItem>()
                .Select(item => item.Shortcut)
                .Where(shortcut => shortcut != Shortcut.None)
                .ToArray();

            Assert.IsType<MainMenu>(shell.Menu);
            Assert.DoesNotContain(Shortcut.F10, shortcuts);
            Assert.All(EnumerateSongLists(shell), list => Assert.IsType<ContextMenu>(list.ContextMenu));

            var audio = new RecordingAudio();
            var shellWithAudio = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = new RecordingAnnouncer(),
                BrowseSource = new FakeBrowseSource(),
                Audio = audio,
                TemporaryDirectory = TempDirectory()
            });
            try
            {
                InvokeProcessCmdKey(shellWithAudio, Keys.F10 | Keys.Alt);
                Assert.Equal(0, audio.VolumeChanges);
            }
            finally
            {
                shellWithAudio.Dispose();
            }
        });
    }

    [Fact]
    public void Shell_selection_changes_do_not_start_browse_or_playback_work()
    {
        var source = new FakeBrowseSource();
        var playback = new RecordingPlayback();
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(source, playback);
            shell.Show();
            Application.DoEvents();

            foreach (var name in new[] { "搜索音源", "搜索类型", "分类歌单音源", "排行榜音源" })
            {
                var combo = Assert.IsType<ComboBox>(Required(shell, name));
                if (combo.Items.Count > 1) combo.SelectedIndex = 1;
            }

            foreach (var list in EnumerateSongLists(shell))
            {
                if (list.Items.Count == 0) continue;
                list.SelectedIndex = 0;
            }

            Application.DoEvents();
            Assert.Equal(0, source.SearchCalls);
            Assert.Equal(0, playback.PlayCalls);
        });
    }

    [Fact]
    public void Source_manager_keeps_empty_list_focused_and_preserves_focus_through_external_changes()
    {
        WinFormsTestRunner.Run(() =>
        {
            var service = new AuditSourceService();
            using var form = new SourceManagerForm(new RecordingAnnouncer(), service, _ => false);
            form.Show();
            Application.DoEvents();
            var list = Assert.IsType<ListBox>(Required(form, "音源"));

            Assert.True(list.Visible, "An empty native list must remain focusable as the list shell.");
            Assert.True(list.Focused, "Empty source manager should start on the list shell.");

            service.Replace(new MusicSource { Id = "a", Name = "A", Version = "1", Type = SourceType.Custom, TrustState = ScriptTrustState.UserApproved },
                            new MusicSource { Id = "b", Name = "B", Version = "1", Type = SourceType.Custom, TrustState = ScriptTrustState.UserApproved });
            Application.DoEvents();
            list.SelectedIndex = 1;
            Assert.True(list.Focus());

            service.Replace(service.GetAllSources().First(), new MusicSource { Id = "c", Name = "C", Version = "1", Type = SourceType.Custom, TrustState = ScriptTrustState.UserApproved });
            Application.DoEvents();
            Assert.Equal("c", WinFormsUi.Selected<MusicSource>(list)!.Id);
            Assert.True(list.Focused, "Source refresh must not move focus away from the user.");
        });
    }

    [Fact]
    public void Source_manager_reports_changed_refresh_as_untrusted_not_plain_success()
    {
        var announcer = new RecordingAnnouncer();
        WinFormsTestRunner.Run(() =>
        {
            var service = new AuditSourceService
            {
                RefreshResult = new(true, true, ScriptTrustState.Untrusted, false)
            };
            service.Replace(new MusicSource { Id = "changed", Name = "Changed", Version = "2", Type = SourceType.Network, TrustState = ScriptTrustState.UserApproved, IsEnabled = true });
            using var form = new SourceManagerForm(announcer, service, _ => false);
            form.Show();
            Application.DoEvents();
            var list = Assert.IsType<ListBox>(Required(form, "音源"));
            list.SelectedIndex = 0;

            var update = typeof(SourceManagerForm).GetMethod("UpdateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(update);
            ((Task)update!.Invoke(form, [])!).GetAwaiter().GetResult();

            Assert.Contains(announcer.Announcements, item =>
                item.ActivityId == "source-update" &&
                item.Kind == AnnouncementKind.Error &&
                item.Text.Contains("未信任", StringComparison.Ordinal) &&
                item.Text.Contains("显式启用", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Download_manager_keeps_empty_list_focus_and_selected_focus_across_dynamic_updates()
    {
        WinFormsTestRunner.Run(() =>
        {
            var workflow = new MutableDownloadWorkflow();
            using var form = new DownloadManagerForm(new RecordingAnnouncer(), workflow);
            form.Show();
            Application.DoEvents();
            var tasks = Assert.IsType<ListView>(Required(form, "DownloadTasks"));
            Assert.True(tasks.Visible, "An empty native list must remain focusable as the list shell.");
            Assert.True(tasks.Focused, "Empty download manager should start on the list shell.");

            workflow.Add(new DownloadTask { Id = "one", Music = new MusicInfo { Artist = "A", Title = "One" }, Status = DownloadTaskStatus.Completed, Progress = 100 });
            workflow.Add(new DownloadTask { Id = "two", Music = new MusicInfo { Artist = "B", Title = "Two" }, Status = DownloadTaskStatus.Failed });
            Application.DoEvents();
            tasks.Items[1].Selected = true;
            tasks.Focus();

            workflow.Add(new DownloadTask { Id = "three", Music = new MusicInfo { Artist = "C", Title = "Three" }, Status = DownloadTaskStatus.Downloading, Progress = 25 });
            Application.DoEvents();
            Assert.Equal("two", ((DownloadTask)tasks.SelectedItems[0].Tag).Id);
            Assert.True(tasks.Focused, "Download refresh must not move focus away from the user.");
        });
    }

    [Fact]
    public void Announcer_allows_a_new_attempt_after_progress_follows_a_terminal_result()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var live = new Label();
            var announcer = new WinFormsAccessibleAnnouncer(Array.Empty<ITtsService>(), NullLogger<WinFormsAccessibleAnnouncer>.Instance);
            announcer.Attach(live);
            announcer.Announce(new AccessibleAnnouncement("first attempt failed", AnnouncementKind.Error, "retry"));
            announcer.Announce(new AccessibleAnnouncement("second attempt is starting", AnnouncementKind.Busy, "retry"));
            announcer.Announce(new AccessibleAnnouncement("second attempt finished", AnnouncementKind.Success, "retry"));

            Assert.Equal("second attempt finished", live.AccessibleName);
        });
    }

    [Fact]
    public void Shell_handles_f10_locally_instead_of_menu_activation()
    {
        WinFormsTestRunner.Run(() =>
        {
            var audio = new RecordingAudio();
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = new RecordingAnnouncer(),
                BrowseSource = new FakeBrowseSource(),
                Audio = audio,
                TemporaryDirectory = TempDirectory()
            });
            shell.Show();
            Application.DoEvents();
            var method = typeof(Form).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var arguments = new object?[] { new Message(), Keys.F10 };
            Assert.True((bool)method!.Invoke(shell, arguments)!);
            Assert.Equal(1, audio.VolumeChanges);
        });
    }

    /// <summary>
    /// A pinned AccessibleName hides a label's live Text from assistive technology, so the dynamic status,
    /// now-playing, clock and empty-state text must stay unnamed and be reported through their own value.
    /// The status bar is a native control whose accessible object only describes panels, which is why the
    /// three messages live in real child windows that a screen reader can walk into.
    /// </summary>
    [Fact]
    public void Dynamic_status_and_empty_state_text_is_reported_through_its_own_value()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var scope = new FormScope();
            WinFormsShell shell = CreateShell();
            scope.Add(shell);
            var statusBar = Descendants(shell).OfType<StatusBar>().Single();
            Assert.Equal("应用状态", statusBar.AccessibleName);

            var statusLabels = statusBar.Controls.OfType<Label>().ToArray();
            Assert.Equal(3, statusLabels.Length);
            Assert.All(statusLabels, label => Assert.Same(statusBar, label.Parent));
            Assert.Equal("就绪", shell.StatusTextForTests.Text);
            Assert.Equal("未播放", shell.PlaybackInfoForTests.Text);
            Assert.All(statusLabels, AssertReportsItsOwnText);

            var web = scope.Add(new WebLoginForm("netease", new AuditImportService(), new RecordingAnnouncer(), NullLogger<WebLoginForm>.Instance));
            var qr = scope.Add(new KugouQrLoginForm(
                new AuditImportService(),
                new RecordingAnnouncer(),
                new KugouQrLoginService(new MusicHttpClient(NullLogger<MusicHttpClient>.Instance), NullLogger<KugouQrLoginService>.Instance)));
            var sources = scope.Add(new SourceManagerForm(new RecordingAnnouncer(), new AuditSourceService(), _ => false));
            var downloads = scope.Add(new DownloadManagerForm(new RecordingAnnouncer(), new MutableDownloadWorkflow()));
            var settings = scope.Add(CreateSettings());
            var lyrics = scope.Add(CreateLyricsForm());
            var manual = scope.Add(new ManualSourceSwitchForm(new RecordingAnnouncer(), new AuditManualWorkflow(), Song(), _ => false));
            var import = scope.Add(CreatePlaylistImport());

            foreach (var label in new[]
                     {
                         Required(web, "WebLoginStatus"), Required(qr, "KugouLoginStatus"),
                         Required(sources, "SourceListStatus"), Required(downloads, "DownloadListStatus"),
                         Required(settings, "SpeechBackend"), Required(lyrics, "LyricsSongInfo"),
                         Required(manual, "ManualTargetSong"), Required(manual, "ManualSwitchStatus"),
                         Required(import, "ImportAccountStatus"), Required(import, "ImportStatus")
                     })
            {
                var status = Assert.IsType<Label>(label);
                AssertReportsItsOwnText(status);
                Assert.NotEmpty(status.Text.Trim());
            }

            // The production announcer mirrors every announcement into the shell's own live region label,
            // which is the only text an assistive technology can re-read after the utterance has passed.
            var native = new WinFormsAccessibleAnnouncer(
                Array.Empty<ITtsService>(), NullLogger<WinFormsAccessibleAnnouncer>.Instance);
            using var wired = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = native,
                BrowseSource = new FakeBrowseSource(),
                Playback = new RecordingPlayback(),
                TemporaryDirectory = TempDirectory()
            });
            native.Announce(new AccessibleAnnouncement("实时区域自检", AnnouncementKind.Status, "audit"));
            Assert.Equal("实时区域自检", wired.LiveRegionForTests.Text);
            Assert.Equal(wired.LiveRegionForTests.Text, wired.LiveRegionForTests.AccessibleName);
            Assert.Equal(wired.LiveRegionForTests.Text, wired.LiveRegionForTests.AccessibilityObject.Name);
        });
    }

    private static void AssertReportsItsOwnText(Label label)
    {
        Assert.True(
            string.IsNullOrWhiteSpace(label.AccessibleName),
            label.Name + " pins an AccessibleName, which hides its live Text from screen readers.");
        Assert.False(label.TabStop, label.Name + " puts a display-only label in the tab chain.");
        Assert.NotEqual(AccessibleRole.None, label.AccessibilityObject.Role);
        Assert.Equal(label.Text, label.AccessibilityObject.Name);
    }

    /// <summary>
    /// A control that is created hidden can never be focused, read or acted on, so it is dead interface
    /// rather than an empty state. Runtime visibility is legitimate (the download and source managers show
    /// their empty-state text on demand), which is why only the user interface files are in scope.
    /// </summary>
    [Fact]
    public void No_user_interface_is_created_hidden()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();
        foreach (var file in new[]
                 {
                     "WinFormsShell.Controls.cs", "WinFormsShell.Workflows.cs", "WinFormsToolForms.cs",
                     "WinFormsLoginForms.cs", "SettingsForm.cs", "WinFormsCommon.cs"
                 })
        {
            var path = System.IO.Path.Combine(root, "src", "LXMusicAccessible.App", file);
            foreach (var (index, line) in System.IO.File.ReadAllLines(path).Select((line, index) => (index, line)))
                if (line.Contains("Visible = false", StringComparison.Ordinal))
                    offenders.Add(file + ":" + (index + 1));
        }
        Assert.True(offenders.Count == 0, "Hidden controls are unreachable for screen reader users; delete them instead: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The menu bar is reached with Alt plus one character, and WinForms takes the character after the
    /// ampersand as that access key. An access key on a Chinese glyph cannot be pressed, so every entry of
    /// the main menu has to expose an ASCII access key, the top level must be unique, and no top-level key
    /// may collide with the Alt command bindings that the shell handles itself.
    /// </summary>
    [Fact]
    public void MainMenu_entries_expose_pressable_ascii_access_keys()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell();
            var entries = EnumerateMenu(shell.Menu!).Where(item => item.Text != "-").ToArray();
            Assert.NotEmpty(entries);
            foreach (var entry in entries)
                Assert.True(
                    entry.Mnemonic is >= 'A' and <= 'Z' or >= '0' and <= '9',
                    "Menu entry '" + entry.Text + "' has the unusable access key '" + entry.Mnemonic + "'.");

            var topLevel = shell.Menu!.MenuItems.Cast<MenuItem>().ToArray();
            Assert.Equal(topLevel.Length, topLevel.Select(item => item.Mnemonic).Distinct().Count());
            Assert.All(topLevel, item => Assert.DoesNotContain(item.Mnemonic, new[] { 'M', 'C', 'P', 'S', 'R' }));
            Assert.Equal(new[] { 'F', 'B', 'D', 'T', 'H' }, topLevel.Select(item => item.Mnemonic).ToArray());
        });
    }

    /// <summary>
    /// Ctrl+H is the standard "delete previous character" key of every Windows edit control, so the shell must
    /// not take it as a menu accelerator: typing in the search box or a settings path would otherwise hide the
    /// whole application. The command keeps its menu entry, its access key and its documented global binding
    /// Ctrl+Alt+H, which the menu text now names so the command stays discoverable without the keyboard clash.
    /// </summary>
    [Fact]
    public void Menu_bar_does_not_take_the_standard_edit_key_ctrl_h()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell();
            var entries = EnumerateMenu(shell.Menu!).ToArray();

            Assert.DoesNotContain(entries, entry => entry.Shortcut == Shortcut.CtrlH);
            var hide = entries.Single(entry => entry.Text.Contains("隐藏/显示窗口", StringComparison.Ordinal));
            Assert.Contains("Ctrl+Alt+H", hide.Text, StringComparison.Ordinal);
            Assert.Equal('H', hide.Mnemonic);
        });
    }

    /// <summary>
    /// WinForms breaks equal TabIndex values by z-order, so a control that never states its position in the
    /// tab order lands wherever the code happened to add it. Every focusable control created in the
    /// interface source therefore declares its TabIndex, either in its own initializer or through one of the
    /// naming helpers that take the index as an argument.
    /// </summary>
    [Fact]
    public void Interactive_controls_declare_their_tab_index_in_source()
    {
        var types = "Button|TextBox|ComboBox|ListBox|ListView|CheckBox|RadioButton|LinkLabel|RichTextBox|TabControl|DomainUpDown|NumericUpDown|WebView2";
        var created = new System.Text.RegularExpressions.Regex(@"(\w+) = new (" + types + @")\b");
        var declaredField = new System.Text.RegularExpressions.Regex(@"readonly (\w*(?:" + types + @")) (\w+) = new\(");
        var offenders = new List<string>();
        foreach (var file in new[]
                 {
                     "WinFormsShell.Controls.cs", "WinFormsShell.Workflows.cs", "WinFormsToolForms.cs",
                     "WinFormsLoginForms.cs", "SettingsForm.cs", "WinFormsCommon.cs"
                 })
        {
            var path = System.IO.Path.Combine(FindRepositoryRoot(), "src", "LXMusicAccessible.App", file);
            var source = System.IO.File.ReadAllText(path);
            var lines = System.IO.File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var createdMatch = created.Match(line);
                var fieldMatch = createdMatch.Success ? System.Text.RegularExpressions.Match.Empty : declaredField.Match(line);
                if (!createdMatch.Success && !fieldMatch.Success) continue;
                // The initializer may span several lines, so scan to the end of the declaration.
                var declared = false;
                for (var scan = index; scan < Math.Min(lines.Length, index + 12); scan++)
                {
                    if (lines[scan].Contains("TabIndex", StringComparison.Ordinal)) { declared = true; break; }
                    if (scan > index && lines[scan].TrimEnd().EndsWith("};", StringComparison.Ordinal)) break;
                }
                if (declared) continue;

                var control = createdMatch.Success ? createdMatch.Groups[1].Value : fieldMatch.Groups[2].Value;
                if (source.Contains("WinFormsUi.Name(" + control + ",", StringComparison.Ordinal)
                    || source.Contains(control + ".TabIndex", StringComparison.Ordinal))
                    continue;

                offenders.Add(file + ":" + (index + 1) + " creates a focusable control without an explicit TabIndex: " + line.Trim());
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }
    [Fact]
    public void Modal_and_tool_forms_have_predictable_escape_behavior()
    {
        WinFormsTestRunner.Run(() =>
        {
            Assert.NotNull(new TextInputForm("Test", new[] { ("Name:", "value") }).CancelButton);
            Assert.NotNull(new PlaylistLoginMethodForm("netease").CancelButton);
            Assert.NotNull(new SongDetailForm("Song", new[] { "value" }).CancelButton);
            Assert.NotNull(new SponsorForm(new RecordingAnnouncer()).CancelButton);

            var settings = CreateSettings();
            var web = new WebLoginForm("netease", new AuditImportService(), new RecordingAnnouncer(), NullLogger<WebLoginForm>.Instance);
            var guide = new UserGuideForm(new RecordingAnnouncer());
            var source = new SourceManagerForm(new RecordingAnnouncer(), new AuditSourceService(), _ => false);
            var downloads = new DownloadManagerForm(new RecordingAnnouncer(), new MutableDownloadWorkflow());
            var manual = new ManualSourceSwitchForm(new RecordingAnnouncer(), new AuditManualWorkflow(), Song(), _ => false);
            var lyrics = CreateLyricsForm();
            Form[] forms = { settings, web, guide, source, downloads, manual, lyrics };
            try
            {
                foreach (var form in forms)
                {
                    form.Show();
                    Application.DoEvents();
                    InvokeKeyDown(form, Keys.Escape);
                    Application.DoEvents();
                    Assert.True(form.IsDisposed, form.GetType().Name + " did not close on Escape.");
                }
            }
            finally
            {
                foreach (var form in forms) if (!form.IsDisposed) form.Dispose();
            }
        });
    }
    [Fact]
    public void Shell_song_lists_execute_only_on_enter_or_space_and_announce_playback()
    {
        WinFormsTestRunner.Run(() =>
        {
            var playback = new RecordingPlayback();
            var announcer = new RecordingAnnouncer();
            var shell = CreateShell(playback: playback);
            shell.GetType().GetField("_announcer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(shell, announcer);
            try
            {
                shell.Show();
                Application.DoEvents();
                var results = Assert.IsType<ListBox>(Required(shell, "SearchResults"));
                WinFormsUi.FillSongs(results, new[] { Song() });
                results.SelectedIndex = 0;
                results.Focus();

                RaiseKeyDown(results, Keys.Enter);
                WaitUntil(() => shell.PlaybackInfoForTests.Text.Contains("Song", StringComparison.Ordinal) && announcer.Announcements.Any(item => item.Text.StartsWith("正在播放：", StringComparison.Ordinal)));
                Assert.Equal(1, playback.PlayCalls);
            }
            finally
            {
                shell.Dispose();
            }
        });
    }

    [Fact]
    public void Source_manager_announces_external_source_changes()
    {
        WinFormsTestRunner.Run(() =>
        {
            var announcer = new RecordingAnnouncer();
            var service = new AuditSourceService();
            using var form = new SourceManagerForm(announcer, service, _ => false);
            form.Show();
            Application.DoEvents();
            var before = announcer.Announcements.Count;

            service.Replace(new MusicSource { Id = "a", Name = "A", Version = "1", Type = SourceType.Custom, TrustState = ScriptTrustState.UserApproved });
            WaitUntil(() => announcer.Announcements.Skip(before).Any(item => item.Text.Contains("音源列表已更新", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void Source_manager_command_is_announced_once_without_the_refresh_echo()
    {
        WinFormsTestRunner.Run(() =>
        {
            var announcer = new RecordingAnnouncer();
            var service = new AuditSourceService { DisableChangesSources = true };
            service.Replace(new MusicSource { Id = "a", Name = "A", Version = "1", Type = SourceType.Custom, TrustState = ScriptTrustState.UserApproved });
            using var form = new SourceManagerForm(announcer, service, _ => false);
            form.Show();
            Application.DoEvents();

            var list = Assert.IsType<ListBox>(Required(form, "音源"));
            list.SelectedIndex = 0;
            var disable = form.GetType().GetMethod("Disable", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(disable);
            disable!.Invoke(form, Array.Empty<object?>());
            WaitUntil(() => announcer.Announcements.Any(item => item.Text.Contains("已禁用音源：A", StringComparison.Ordinal)));
            Application.DoEvents();

            // The list did change, but the command already said 已禁用音源：A. Echoing 音源列表已更新 on top
            // of it would speak twice for one key press, so only changes from outside the window are named.
            Assert.DoesNotContain(announcer.Announcements, item => item.Text.Contains("音源列表已更新", StringComparison.Ordinal));
            Assert.Contains("已禁用", WinFormsUi.Selected<MusicSource>(list)!.StatusText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Playlist_import_cookie_login_announces_success()
    {
        WinFormsTestRunner.Run(() =>
        {
            var announcer = new RecordingAnnouncer();
            var service = new AuditImportService
            {
                Account = new AccountInfo { Platform = "netease", DisplayName = "Tester" },
                RequireCookieLoginBeforeAccount = true
            };
            using var form = new PlaylistImportForm(service, announcer, new AuditLoginFactory(), _ => { });
            form.Show();
            Application.DoEvents();
            ((Button)Required(form, "网易云音乐")).PerformClick();
            var cookie = Assert.IsType<TextBox>(Required(form, "Cookie"));
            cookie.Text = "fixture-cookie";
            ((Button)Required(form, "使用 Cookie 登录")).PerformClick();

            WaitUntil(() => announcer.Announcements.Any(item => item.Text.Contains("Cookie 登录成功", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void Download_manager_unselected_state_disables_task_actions()
    {
        WinFormsTestRunner.Run(() =>
        {
            var workflow = new MutableDownloadWorkflow();
            workflow.Add(new DownloadTask { Id = "failed", Music = new MusicInfo { Artist = "A", Title = "Failed" }, Status = DownloadTaskStatus.Failed });
            workflow.Add(new DownloadTask { Id = "done", Music = new MusicInfo { Artist = "C", Title = "Done" }, Status = DownloadTaskStatus.Completed, Progress = 100 });
            using var form = new DownloadManagerForm(new RecordingAnnouncer(), workflow);

            Assert.False(((Button)form.Controls.Find("取消", true).Single()).Enabled);
            Assert.False(((Button)form.Controls.Find("重试", true).Single()).Enabled);
            Assert.True(((Button)form.Controls.Find("清除已完成", true).Single()).Enabled);
        });
    }

    /// <summary>
    /// A completed task disables 取消 while the user is standing on that button. A disabled control cannot
    /// hold focus, so without a deliberate hand-over the keyboard user lands on the form itself and the
    /// screen reader stops having anything to read. The reading position must return to the task list.
    /// </summary>
    [Fact]
    public void Download_manager_returns_focus_to_the_task_list_when_an_update_disables_the_focused_button()
    {
        WinFormsTestRunner.Run(() =>
        {
            var workflow = new MutableDownloadWorkflow();
            var running = new DownloadTask
            {
                Id = "running",
                Music = new MusicInfo { Artist = "A", Title = "Running" },
                Status = DownloadTaskStatus.Downloading,
                Progress = 40
            };
            workflow.Add(running);
            using var form = new DownloadManagerForm(new RecordingAnnouncer(), workflow);
            form.Show();
            Application.DoEvents();

            var list = Assert.IsType<ListView>(Required(form, "DownloadTasks"));
            list.Items[0].Selected = true;
            Application.DoEvents();

            var cancel = (Button)Required(form, "取消");
            Assert.True(cancel.Enabled, "取消 must be enabled while a downloading task is selected.");
            cancel.Focus();
            Assert.Same(cancel, form.ActiveControl);

            running.Status = DownloadTaskStatus.Completed;
            running.Progress = 100;
            workflow.Add(new DownloadTask
            {
                Id = "extra",
                Music = new MusicInfo { Artist = "B", Title = "Extra" },
                Status = DownloadTaskStatus.Completed,
                Progress = 100
            });
            Application.DoEvents();

            Assert.False(cancel.Enabled, "取消 must be disabled once no selected task is downloading.");
            var tasks = Assert.IsType<ListView>(Required(form, "DownloadTasks"));
            Assert.Same(tasks, form.ActiveControl);
            Assert.True(tasks.Focused, "Focus was dropped on the form instead of returning to the task list.");
        });
    }

    /// <summary>
    /// ADR 0004 puts the import window on the platform command that starts the task. The playlist list is
    /// empty until a platform has been chosen and loaded, so landing there would announce a name with no
    /// content while the status line asks the user to pick a platform.
    /// </summary>
    [Fact]
    public void Playlist_import_opens_on_the_platform_command_that_starts_the_task()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var form = CreatePlaylistImport();
            form.Show();
            WaitUntil(() => form.ActiveControl is Button);

            Assert.Same(Required(form, "网易云音乐"), form.ActiveControl);
        });
    }

    /// <summary>
    /// The service can import one whole remote playlist, or all listed remote playlists as one explicit
    /// batch action. The preview list remains single-selection, so selected-song commands would still
    /// promise something nothing behind them can do.
    /// </summary>
    [Fact]
    public void Playlist_import_exposes_only_the_import_command_that_the_service_can_perform()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var form = CreatePlaylistImport();
            var commands = Descendants(form).OfType<Button>().Select(button => button.AccessibleName).ToArray();

            Assert.Equal(new[] { "导入当前歌单", "导入全部歌单" }, commands
                .Where(name => name is not null && name.StartsWith("导入", StringComparison.Ordinal))
                .ToArray());
            Assert.DoesNotContain("全选歌曲", commands);
            Assert.DoesNotContain("导入选中歌曲", commands);
            Assert.Equal(SelectionMode.One, Assert.IsType<ListBox>(Required(form, "歌曲预览")).SelectionMode);
        });
    }

    [Fact]
    public void Live_announcer_coalesces_progress_and_updates_the_real_live_region()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var live = new Label();
            var announcer = new WinFormsAccessibleAnnouncer(Array.Empty<ITtsService>(), NullLogger<WinFormsAccessibleAnnouncer>.Instance);
            announcer.Attach(live);
            announcer.Announce(new AccessibleAnnouncement("download 25%", AnnouncementKind.Progress, "download"));
            announcer.Announce(new AccessibleAnnouncement("download 50%", AnnouncementKind.Progress, "download"));

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && live.AccessibleName != "download 50%")
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }

            Assert.Equal("download 50%", live.AccessibleName);
            announcer.Announce(new AccessibleAnnouncement("download complete", AnnouncementKind.Success, "download"));
            Assert.Equal("download complete", live.AccessibleName);
        });
    }

    [Fact]
    public void Shared_song_fill_preserves_the_adjacent_selection_when_the_selected_song_is_removed()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var list = new ListBox();
            _ = list.Handle;
            list.Items.Add(new ObjectBox<MusicInfo>("first", new MusicInfo { Id = "first" }));
            list.Items.Add(new ObjectBox<MusicInfo>("second", new MusicInfo { Id = "second" }));
            list.SelectedIndex = 1;
            Assert.Equal(1, list.SelectedIndex);

            WinFormsUi.FillSongs(list, new[] { new MusicInfo { Id = "third" }, new MusicInfo { Id = "fourth" } });

            Assert.Equal(1, list.SelectedIndex);
            Assert.Equal("fourth", WinFormsUi.Selected<MusicInfo>(list)!.Id);
        });
    }
    /// <summary>
    /// Typing in a text box is not a command, and moving focus is not a command either: the settings boxes
    /// used to write the settings file the moment the user tabbed past them. A path now becomes a setting on
    /// Enter, and a path that was typed but never confirmed is still collected when the window closes so
    /// nothing is lost.
    /// </summary>
    [Fact]
    public void Settings_path_boxes_commit_on_enter_and_not_when_focus_moves()
    {
        WinFormsTestRunner.Run(() =>
        {
            var settings = new SettingsWorkflow(
                new RecordingSettingsStore(),
                new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, new RecordingHotkeyAdapter()),
                NullLogger<SettingsWorkflow>.Instance);
            settings.LoadAsync(IntPtr.Zero).GetAwaiter().GetResult();
            using var form = new SettingsForm(
                new RecordingAnnouncer(), settings, new RecordingTts(), new MutableDownloadWorkflow(), _ => false);
            form.Show();
            Application.DoEvents();

            var playerPath = Assert.IsType<TextBox>(Required(form, "第三方播放器路径"));
            playerPath.Focus();
            playerPath.Text = @"C:\fake\player.exe";
            Application.DoEvents();

            Required(form, "关闭行为").Focus();
            Application.DoEvents();
            Assert.NotEqual(@"C:\fake\player.exe", settings.Current.Settings.ThirdPartyPlayerPath);

            RaiseKeyDown(playerPath, Keys.Enter);
            Assert.Equal(@"C:\fake\player.exe", settings.Current.Settings.ThirdPartyPlayerPath);

            var downloadPath = Assert.IsType<TextBox>(Required(form, "下载目录"));
            downloadPath.Focus();
            downloadPath.Text = @"D:\music";
            form.Close();
            Assert.Equal(@"D:\music", settings.Current.Settings.DownloadPath);
        });
    }

    /// <summary>
    /// Both login windows are opened to finish one task and leave. Escape is the documented way out of every
    /// window in this application, so each one carries a native cancel button plus a form-level key preview,
    /// and the QR window parks the reading position on its first command instead of on the window itself.
    /// </summary>
    [Fact]
    public void Login_windows_publish_escape_and_land_on_a_reachable_control()
    {
        WinFormsTestRunner.Run(() =>
        {
            var qr = new KugouQrLoginForm(
                new AuditImportService(),
                new RecordingAnnouncer(),
                new KugouQrLoginService(
                    new MusicHttpClient(NullLogger<MusicHttpClient>.Instance),
                    NullLogger<KugouQrLoginService>.Instance));
            var web = new WebLoginForm(
                "netease", new AuditImportService(), new RecordingAnnouncer(), NullLogger<WebLoginForm>.Instance);
            try
            {
                Assert.True(qr.KeyPreview, "KugouQrLoginForm does not preview keys, so Escape never reaches it.");
                Assert.True(web.KeyPreview, "WebLoginForm does not preview keys, so Escape never reaches it.");
                Assert.NotNull(qr.CancelButton);
                Assert.NotNull(web.CancelButton);

                qr.Show();
                Application.DoEvents();
                var landing = qr.ActiveControl;
                Assert.NotNull(landing);
                Assert.Equal("刷新二维码", landing!.AccessibleName);
                Assert.True(landing.TabStop);

                // A failed QR fetch reports itself in the status line and the announcement. If it succeeds the
                // QR image is shown, and either way the window keeps the activation: a modal error box would
                // take the reading position away from the recovery command the user is standing on.
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    Application.DoEvents();
                    Thread.Sleep(25);
                }
                Assert.True(qr.ContainsFocus, "The QR window lost the keyboard focus, so a dialog took it away.");
            }
            finally
            {
                if (!qr.IsDisposed) qr.Close();
                if (!web.IsDisposed) web.Close();
                qr.Dispose();
                web.Dispose();
            }
        });
    }

    /// <summary>
    /// Every other entry of the 播放 menu shows its key on the right. F10 cannot be a MenuItem accelerator here
    /// because that would swallow the classic menu key and run the volume command twice, so 音量加 names its
    /// default key in the text and keeps &G as the access key.
    /// </summary>
    [Fact]
    public void Play_menu_names_the_default_key_of_volume_up()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell();
            var volumeUp = EnumerateMenu(shell.Menu!).Single(entry => entry.Text.Contains("音量加", StringComparison.Ordinal));

            Assert.Contains("F10", volumeUp.Text, StringComparison.Ordinal);
            Assert.Equal('G', volumeUp.Mnemonic);
            Assert.Equal(Shortcut.None, volumeUp.Shortcut);
        });
    }

    private static void Audit(Form form)
    {
        var errors = new List<string>();
        Inspect(form, form.Name, errors);

        foreach (var page in form.Controls.OfType<TabControl>().SelectMany(tabs => tabs.TabPages.Cast<Control>()))
        {
            // A page that is not selected reports Visible=false, so visibility cannot be a filter here.
            var visibleNamed = Descendants(page).Where(control => control.TabStop && IsInteractive(control))
                .Select(EffectiveName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .GroupBy(name => name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .ToArray();
            foreach (var duplicate in visibleNamed)
                errors.Add($"{form.Name}:{page.Text} has duplicate accessible name '{duplicate.Key}'.");
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static void Inspect(Control root, string formName, List<string> errors)
    {
        foreach (Control control in root.Controls)
        {
            var path = $"{root.GetType().Name}:{formName}:{control.Name}:{control.GetType().FullName}:{control.Parent?.GetType().FullName}";
            if (!string.IsNullOrWhiteSpace(control.AccessibleDescription))
                errors.Add(path + " has AccessibleDescription.");

            var name = EffectiveName(control);
            if (control is Label display && !string.IsNullOrWhiteSpace(display.AccessibleName)
                && display.AccessibleName != display.Text)
                errors.Add(path + $" pins AccessibleName '{display.AccessibleName}' over its live Text '{display.Text}'.");

            if (IsInteractive(control))
            {
                if (control.TabIndex < 0) errors.Add(path + " has a negative TabIndex.");
                if (IsContainer(control) && control is not TabControl && control.TabStop)
                    errors.Add(path + " is a focusable container.");
                if (string.IsNullOrWhiteSpace(name))
                    errors.Add(path + " has no accessible name.");
                else if (ContainsRoleWord(control, name))
                    errors.Add($"{path} accessible name repeats a role: '{name}'.");
                else if (!name.Any(IsChinese))
                    errors.Add($"{path} exposes the untranslated programmatic name '{name}' to screen readers.");
            }

            Inspect(control, formName, errors);
        }

        // Two focusable controls with the same TabIndex inside one container make the tab order
        // depend on the order the controls happened to be added, which users cannot predict.
        foreach (var group in root.Controls.Cast<Control>()
                     .Where(control => control.TabStop && IsInteractive(control))
                     .GroupBy(control => control.TabIndex)
                     .Where(group => group.Count() > 1))
            errors.Add($":{formName}:{root.Name} shares TabIndex {group.Key} between "
                + string.Join(" and ", group.Select(control => control.Name)));
    }

    private static bool IsChinese(char character) => character >= '\u4e00' && character <= '\u9fff';

    private static bool IsInteractive(Control control) =>
        !IsNativeCompositeChild(control)
        && (control is Button or TextBox or ComboBox or ListBox or ListView or CheckBox or RadioButton or LinkLabel
            or TabControl or RichTextBox or DomainUpDown or NumericUpDown
            || control.GetType().Name is "WebView2");

    private static bool IsNativeCompositeChild(Control control) => control.Parent is DomainUpDown or NumericUpDown;

    private static bool IsContainer(Control control) =>
        (control is ContainerControl && control is not (TabControl or DomainUpDown or NumericUpDown))
        || control is Panel or TableLayoutPanel or SplitContainer or FlowLayoutPanel;

    private static string EffectiveName(Control control)
    {
        if (!string.IsNullOrWhiteSpace(control.AccessibleName)) return control.AccessibleName;
        var accessible = control.AccessibilityObject.Name;
        return accessible ?? string.Empty;
    }

    private static Control ByAccessibleName(Control root, string name) => Descendants(root).Concat(new[] { root }).Single(control => control.AccessibleName == name);

    private static void InvokeKeyDown(Form form, Keys key)
    {
        var method = form.GetType().GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(form, new object[] { new KeyEventArgs(key) });
    }

    private static void RaiseKeyDown(Control control, Keys key)
    {
        var method = control.GetType().GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(control, new object[] { new KeyEventArgs(key) });
    }
    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            if (condition()) return;
            Thread.Sleep(20);
        }
        Assert.True(condition(), "The awaited UI condition was not reached.");
    }

    private static void UpdateDownloadSelection(Form form)
    {
        var method = typeof(DownloadManagerForm).GetMethod("UpdateButtonState", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(form, Array.Empty<object?>());
    }
    private static bool ContainsRoleWord(Control control, string name)
    {
        var words = control switch
        {
            Button => new[] { "按钮", "按键" },
            CheckBox or RadioButton => new[] { "复选框", "单选按钮" },
            ComboBox => new[] { "组合框", "下拉框" },
            TextBox => new[] { "输入框", "文本框", "编辑框" },
            ListBox or ListView => new[] { "列表控件" },
            TabControl => new[] { "标签页", "选项卡" },
            _ => Array.Empty<string>()
        };
        return words.Any(word => name.Contains(word, StringComparison.Ordinal));
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static Control Required(Control root, string name)
    {
        var matches = root.Controls.Find(name, true);
        Assert.True(matches.Length == 1, $"{name} count={matches.Length}");
        return matches[0];
    }

    private static IEnumerable<ListBox> EnumerateSongLists(Form form) =>
        Descendants(form).OfType<ListBox>().Where(list => list.Name.EndsWith("Songs", StringComparison.Ordinal) || list.Name == "SearchResults");

    private static IEnumerable<MenuItem> EnumerateMenu(Menu menu) => menu.MenuItems.Cast<MenuItem>()
        .SelectMany(item => new[] { item }.Concat(EnumerateMenu(item)));

    private static void InvokeProcessCmdKey(Form form, Keys keys)
    {
        var method = typeof(Form).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var parameters = new object?[] { new Message(), keys };
        method!.Invoke(form, parameters);
    }

    private static WinFormsShell CreateShell(FakeBrowseSource? source = null, IPlaybackCoordinator? playback = null) => new(new WinFormsShellServices
    {
        Announcer = new RecordingAnnouncer(),
        BrowseSource = source ?? new FakeBrowseSource(),
        Playback = playback ?? new RecordingPlayback(),
        TemporaryDirectory = TempDirectory()
    });

    private static SettingsForm CreateSettings()
    {
        var settings = new SettingsWorkflow(
            new RecordingSettingsStore(),
            new HotkeyManagerService(NullLogger<HotkeyManagerService>.Instance, new RecordingHotkeyAdapter()),
            NullLogger<SettingsWorkflow>.Instance);
        settings.LoadAsync(IntPtr.Zero).GetAwaiter().GetResult();
        return new SettingsForm(
            new RecordingAnnouncer(),
            settings,
            new RecordingTts(),
            new MutableDownloadWorkflow(),
            _ => false);
    }

    private static PlaylistImportForm CreatePlaylistImport()
    {
        var service = new AuditImportService();
        return new PlaylistImportForm(service, new RecordingAnnouncer(), new AuditLoginFactory(), _ => { });
    }

    private static LyricsForm CreateLyricsForm()
    {
        var lyrics = new LyricsWorkflow(
            NullLogger<LyricsWorkflow>.Instance,
            new AuditLyricService(),
            new RecordingPlayback(),
            new RecordingAudio(),
            new BackgroundWorkHost(NullLogger<BackgroundWorkHost>.Instance),
            new RecordingAnnouncer());
        return new LyricsForm(new RecordingAnnouncer(), lyrics, _ => false);
    }

    /// <summary>Walks up from the test binaries to the repository root, marked by verify.ps1.</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "verify.ps1"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new System.IO.DirectoryNotFoundException("Repository root was not found.");
    }

    private static string TempDirectory() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lx-audit-" + Guid.NewGuid().ToString("N"));

    private static MusicInfo Song() => new() { Id = "song", Title = "Song", Artist = "Artist", Source = "netease" };

    private sealed class FormScope : IDisposable
    {
        private readonly List<Form> _forms = new();

        public Form Add<TForm>(TForm form) where TForm : Form
        {
            _forms.Add(form);
            return form;
        }

        public void Dispose()
        {
            foreach (var form in Enumerable.Reverse(_forms)) form.Dispose();
        }
    }

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<UserSettings?>(new());
        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingHotkeyAdapter : IGlobalHotkeyAdapter
    {
        public bool Register(IntPtr windowHandle, int id, PlatformKey key, PlatformModifiers modifiers) => true;
        public bool Unregister(IntPtr windowHandle, int id) => true;
        public int LastRegisterError { get; }
    }

    private sealed class RecordingTts : ITtsService
    {
        public bool IsAvailable => false;
        public string ActiveBackendName => "Test";
        public Task SpeakAsync(string text) => Task.CompletedTask;
        public Task SpeakQueuedAsync(string text) => Task.CompletedTask;
        public void Stop() { }
        public void SetRate(double rate) { }
        public void SetVoice(string voiceId) { }
        public List<VoiceInfo> GetAvailableVoices() => new();
    }

    private sealed class MutableDownloadWorkflow : IDownloadWorkflow
    {
        private readonly ObservableCollection<DownloadTask> _tasks = new();
        public ReadOnlyObservableCollection<DownloadTask> Tasks { get; }

        public MutableDownloadWorkflow() => Tasks = new(_tasks);
        public DownloadRequest? LastRequest { get; private set; }
        public void Add(DownloadTask task) => _tasks.Add(task);

        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new DownloadTask());
        }
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RedownloadAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void Cancel(DownloadTask task) { }
    public void Pause(DownloadTask task) { }
    public void Resume(DownloadTask task) { }
    public bool Delete(DownloadTask task, bool deleteSourceFile) => true;
    public Task<bool> DeleteAsync(DownloadTask task, bool deleteSourceFile) => Task.FromResult(Delete(task, deleteSourceFile));
        public void ClearCompleted() => _tasks.Where(task => task.Status == DownloadTaskStatus.Completed).ToList().ForEach(task => _tasks.Remove(task));
        public string ResolveDownloadDirectory(string? requested = null) => requested ?? System.IO.Path.GetTempPath();
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }

    private sealed class AuditSourceService : IMusicSourceService
    {
        private EventHandler? _changed;
        private List<MusicSource> _sources = new();

        public event EventHandler? SourcesChanged
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public event EventHandler<LxScriptUpdateAlertEventArgs>? ScriptUpdateAlert
        {
            add { }
            remove { }
        }

        public List<MusicSource> GetAllSources() => _sources.ToList();
        public void Replace(params MusicSource[] sources)
        {
            _sources = sources.ToList();
            _changed?.Invoke(this, EventArgs.Empty);
        }

        public Task<ScriptActivationResult> EnableSourceAsync(MusicSource source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void DisableSource(MusicSource source) { }
        /// <summary>When set, disabling behaves like the real service: the stored source changes and the
        /// shared event fires after the save, which is what lets a command of the window be told from an
        /// outside change.</summary>
        public bool DisableChangesSources { get; init; }

        public Task<bool> DisableSourceAsync(MusicSource source, CancellationToken cancellationToken = default)
        {
            if (!DisableChangesSources) return Task.FromResult(true);
            // The real service saves on a background task and raises SourcesChanged there, so the deferred
            // refresh of the window is queued before the awaited command carries on. That order is what makes
            // the plain "the list changed" echo land on top of the command's own result.
            return Task.Run(() =>
            {
                source.IsEnabled = false;
                _changed?.Invoke(this, EventArgs.Empty);
                return true;
            }, cancellationToken);
        }
        public void RemoveSource(MusicSource source) { }
        public Task<bool> RemoveSourceAsync(MusicSource source, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RefreshSourceAsync(MusicSource source, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public MusicSourceService.MusicSourceRefreshResult RefreshResult { get; set; } = new(true, false, ScriptTrustState.UserApproved, true);
        public Task<MusicSourceService.MusicSourceRefreshResult> RefreshSourceDetailedAsync(MusicSource source, CancellationToken cancellationToken = default) => Task.FromResult(RefreshResult);
        public Task<bool> TestSourceAsync(MusicSource source) => Task.FromResult(true);
        public MusicSource? GetDefaultSource() => _sources.FirstOrDefault(source => source.IsDefault);
        public void SetDefaultSource(string id) { }
        public Task<bool> SetDefaultSourceAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public List<MusicSource> GetEnabledSources() => _sources.Where(source => source.IsEnabled).ToList();
        public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page = 1, int pageSize = 20, string searchType = "song", string sourceId = "all", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetPlayUrlAsync(MusicInfo music, string source, string quality = "128k", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId = "netease", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page = 1, int pageSize = 30, string sourceId = "netease", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<Ranking>> GetRankingsAsync(string sourceId = "netease", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LxLyricRaw?> GetLyricFromScriptsAsync(MusicInfo music, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetPlayUrlAsync(string musicId, string source, string quality = "128k", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MusicSource?> ImportFromUrlAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MusicSource?> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MusicSourceImportResult?> ImportFromUrlDetailedAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MusicSourceImportResult?> ImportFromFileDetailedAsync(string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateSourceAsync(MusicSource source) => throw new NotSupportedException();
        public Task<List<string>> GetQualitiesForSourceAsync(string sourceId) => throw new NotSupportedException();
        public Task InitializeScriptSourcesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PersistSourcesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PersistSourcesOrThrowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IMusicApi? GetApi(string id) => null;
    }


    private sealed class AuditImportService : IPlaylistImportService
    {
        public AccountInfo? Account { get; set; }
        public bool RequireCookieLoginBeforeAccount { get; set; }
        private bool CookieSubmitted { get; set; }
        public IReadOnlyList<IPlaylistImportProvider> Providers => Array.Empty<IPlaylistImportProvider>();
        public IPlaylistImportProvider? GetProvider(string platformId) => null;
        public Task EnsureAccountsLoadedAsync() => Task.CompletedTask;
        public AccountInfo? GetAccount(string platformId) => RequireCookieLoginBeforeAccount && !CookieSubmitted ? null : Account;
        public Task<AccountInfo?> LoginWithCookieAsync(string platformId, string cookieText)
        {
            CookieSubmitted = !string.IsNullOrWhiteSpace(cookieText);
            return Task.FromResult(GetAccount(platformId));
        }
        public Task<AccountInfo?> LoginWithBrowserResultAsync(string platformId, string cookieText, string? displayName, List<RemotePlaylist>? playlists) => Task.FromResult<AccountInfo?>(null);
        public Task LogoutAsync(string platformId) => Task.CompletedTask;
        public Task<List<RemotePlaylist>> GetMyPlaylistsAsync(string platformId) => Task.FromResult(new List<RemotePlaylist>());
        public Task<List<MusicInfo>> GetPlaylistSongsPreviewAsync(string platformId, RemotePlaylist remote) => Task.FromResult(new List<MusicInfo>());
        public Task<PlaylistImportResult> ImportPlaylistAsync(string platformId, RemotePlaylist remote, Action<string>? announce = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlaylistImportResult>> ImportPlaylistsAsync(string platformId, IReadOnlyList<RemotePlaylist> remotes, Action<string>? announce = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AuditLoginFactory : IPlaylistLoginWindowFactory
    {
        public PlaylistLoginMethod? SelectLoginMethod(string platform, Form? owner) => PlaylistLoginMethod.Cookie;
        public PlaylistLoginDialogResult ShowWebLogin(string platform, Form? owner) => new(false);
        public bool ShowKugouQrLogin(Form? owner) => false;
    }

    private sealed class AuditLyricService : ILyricService
    {
        public Task<LyricResult?> GetLyricAsync(MusicInfo music, CancellationToken cancellationToken = default) => Task.FromResult<LyricResult?>(null);
    }

    private sealed class AuditManualWorkflow : IManualSourceDiscoveryWorkflow
    {
        public Task<ManualSourceDiscoveryResult> DiscoverAsync(MusicInfo target, CancellationToken cancellationToken = default) =>
            Task.FromResult(ManualSourceDiscoveryResult.Failed("not used"));
        public Task<PlaybackResult> PlayAsync(ManualSourcePlaybackRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class RecordingPlayback : IPlaybackCoordinator
    {
        public int PlayCalls { get; private set; }
        public Task<PlaybackResult> PlayAsync(PlaybackRequest request)
        {
            PlayCalls++;
            return Task.FromResult(new PlaybackResult(PlaybackResultStatus.Playing, request.Selection, request.Origin, "test"));
        }

        public Task<PlaybackResult> ReplayAsync() => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Search, "test"));
        public void Pause() { }
        public void Stop() { }
        public Task<PlaybackResult> NextAsync() => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Search, "test"));
        public Task<PlaybackResult> PreviousAsync() => Task.FromResult(new PlaybackResult(PlaybackResultStatus.NoChange, CurrentMusic, PlaybackQueueOrigin.Search, "test"));
        public bool IsPlaying => false;
        public bool IsPaused => false;
        public Task ResumeAsync() => Task.CompletedTask;
        public MusicInfo? CurrentMusic => null;
        public PlayMode Mode { get; set; }
        public string DefaultQuality { get; set; } = "128k";
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<PlaybackQueueCompletedEventArgs>? QueueCompleted { add { } remove { } }
    }

    private sealed class RecordingAudio : IAudioPlaybackControl
    {
        public int VolumeChanges { get; private set; }
        public void SetVolume(double volume) => VolumeChanges++;
        public double Volume => 0.5;
        public void SyncVolumeFromDevice() { }
        public void SetPosition(TimeSpan position) { }
        public void SeekTo(TimeSpan position) { }
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public string? ActualQuality => null;
        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => Array.Empty<OutputDeviceInfo>();
        public int CurrentOutputDeviceIndex => -1;
        public string CurrentOutputDeviceName => "System";
        public string? SwitchToNextOutputDevice() => null;
        public string? SwitchToPreviousOutputDevice() => null;
        public string? SwitchOutputDevice(int index) => null;
    }
}
