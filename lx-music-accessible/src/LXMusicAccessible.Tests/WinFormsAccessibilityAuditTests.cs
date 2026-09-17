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
            Audit(scope.Add(CreateShell()));
            Audit(scope.Add(CreateSettings()));
            Audit(scope.Add(new SourceManagerForm(new RecordingAnnouncer(), new AuditSourceService(), _ => false)));
            Audit(scope.Add(new DownloadManagerForm(new RecordingAnnouncer(), new MutableDownloadWorkflow())));
            Audit(scope.Add(CreatePlaylistImport()));
            Audit(scope.Add(CreateLyricsForm()));
            Audit(scope.Add(new ManualSourceSwitchForm(new RecordingAnnouncer(), new AuditManualWorkflow(), Song(), _ => false)));
            Audit(scope.Add(new UserGuideForm(new RecordingAnnouncer())));
            Audit(scope.Add(new SongDetailForm("Song", new[] { "标题：Song" })));
            Audit(scope.Add(new SponsorForm(new RecordingAnnouncer())));
            Audit(scope.Add(new TextInputForm("Test", new[] { ("Name:", "value") })));
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
    public async Task Shell_selection_changes_do_not_start_browse_or_playback_work()
    {
        var source = new FakeBrowseSource();
        var playback = new RecordingPlayback();
        await Task.Yield();
        WinFormsTestRunner.Run(async () =>
        {
            await Task.Yield();
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
    public void Shell_does_not_intercept_f10_away_from_the_native_menu()
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
            InvokeProcessCmdKey(shell, Keys.F10);
            Assert.Equal(0, audio.VolumeChanges);
        });
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
                var results = Assert.IsType<ListBox>(Required(shell, "搜索结果"));
                WinFormsUi.FillSongs(results, new[] { Song() });
                results.SelectedIndex = 0;
                results.Focus();

                RaiseKeyDown(results, Keys.Enter);
                WaitUntil(() => ByAccessibleName(shell, "播放信息").Text.Contains("Song", StringComparison.Ordinal) && announcer.Announcements.Any(item => item.Text.StartsWith("正在播放：", StringComparison.Ordinal)));
                Assert.Equal(1, playback.PlayCalls);
            }
            finally
            {
                shell.Dispose();
            }
        });
    }

    [Fact]
    public void Shell_download_hotkey_announces_the_queued_song()
    {
        WinFormsTestRunner.Run(() =>
        {
            var announcer = new RecordingAnnouncer();
            var downloads = new MutableDownloadWorkflow();
            var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = announcer,
                BrowseSource = new FakeBrowseSource(),
                Playback = new RecordingPlayback(),
                Downloads = downloads,
                TemporaryDirectory = TempDirectory()
            });
            try
            {
                shell.Show();
                Application.DoEvents();
                var results = Assert.IsType<ListBox>(Required(shell, "搜索结果"));
                WinFormsUi.FillSongs(results, new[] { Song() });
                results.SelectedIndex = 0;
                results.Focus();

                InvokeProcessCmdKey(shell, Keys.F2);
                WaitUntil(() => announcer.Announcements.Any(item => item.Text == "已加入下载：Song"));
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
    public void Playlist_import_cookie_login_announces_success()
    {
        WinFormsTestRunner.Run(() =>
        {
            var announcer = new RecordingAnnouncer();
            var service = new AuditImportService { Account = new AccountInfo { Platform = "netease", DisplayName = "Tester" } };
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
    private static void Audit(Form form)
    {
        var errors = new List<string>();
        Inspect(form, form.Name, errors);

        foreach (var page in form.Controls.OfType<TabControl>().SelectMany(tabs => tabs.TabPages.Cast<Control>()))
        {
            var visibleNamed = Descendants(page).Where(control => control.TabStop && IsInteractive(control) && control.Visible)
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
            if (IsInteractive(control))
            {
                if (control.TabIndex < 0) errors.Add(path + " has a negative TabIndex.");
                if (IsContainer(control) && control is not TabControl && control.TabStop)
                    errors.Add(path + " is a focusable container.");
                if (string.IsNullOrWhiteSpace(name))
                    errors.Add(path + " has no accessible name.");
                else if (ContainsRoleWord(control, name))
                    errors.Add($"{path} accessible name repeats a role: '{name}'.");
            }

            Inspect(control, formName, errors);
        }
    }

    private static bool IsInteractive(Control control) =>
        control is Button or TextBox or ComboBox or ListBox or ListView or CheckBox or RadioButton or LinkLabel or TabControl
        || control.GetType().Name is "WebView2";

    private static bool IsContainer(Control control) =>
        control is ContainerControl or Panel or TableLayoutPanel or SplitContainer or FlowLayoutPanel;

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
            _ => false,
            _ => { });
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
        public void Add(DownloadTask task) => _tasks.Add(task);

        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new DownloadTask());
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Cancel(DownloadTask task) { }
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
        public void RemoveSource(MusicSource source) { }
        public Task<bool> RefreshSourceAsync(MusicSource source, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> TestSourceAsync(MusicSource source) => Task.FromResult(true);
        public MusicSource? GetDefaultSource() => _sources.FirstOrDefault(source => source.IsDefault);
        public void SetDefaultSource(string id) { }
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
        public Task<bool> UpdateSourceAsync(MusicSource source) => throw new NotSupportedException();
        public Task<List<string>> GetQualitiesForSourceAsync(string sourceId) => throw new NotSupportedException();
        public Task PersistSourcesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IMusicApi? GetApi(string id) => null;
    }


    private sealed class AuditImportService : IPlaylistImportService
    {
        public AccountInfo? Account { get; set; }
        public IReadOnlyList<IPlaylistImportProvider> Providers => Array.Empty<IPlaylistImportProvider>();
        public IPlaylistImportProvider? GetProvider(string platformId) => null;
        public Task EnsureAccountsLoadedAsync() => Task.CompletedTask;
        public AccountInfo? GetAccount(string platformId) => Account;
        public Task<AccountInfo?> LoginWithCookieAsync(string platformId, string cookieText) => Task.FromResult(Account);
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




