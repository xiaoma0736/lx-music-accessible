using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public static class WinFormsTestRunner
{
    public static void Run(Action test)
    {
        if (test is null) throw new ArgumentNullException(nameof(test));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    /// <summary>
    /// Runs an asynchronous UI body on a dedicated STA thread while pumping the WinForms message loop
    /// until the body finishes, so awaited continuations and failures are never lost. The body and every
    /// continuation it awaits stay on the STA thread, which is the only thread that may read control
    /// focus and selection state.
    /// </summary>
    public static void RunStaAsync(Func<Task> test, int timeoutSeconds = 30)
    {
        if (test is null) throw new ArgumentNullException(nameof(test));
        Exception? failure = null;
        // Not disposed: a timed-out run releases the pumping thread with Set instead, and disposing the
        // handle under that thread would surface as an ObjectDisposedException that kills the test host.
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            // Install the context before anything can hand this thread a thread-pool posting one.
            SynchronizationContext.SetSynchronizationContext(new StaSynchronizationContext());
            _ = CompleteAsync(test, exception => failure = exception, done);
            while (!done.IsSet)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!done.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            done.Set();
            throw new TimeoutException("The asynchronous STA UI test did not complete.");
        }
        thread.Join(TimeSpan.FromSeconds(5));
        if (failure is not null) throw new InvalidOperationException("STA UI test failure", failure);
    }

    private static async Task CompleteAsync(Func<Task> test, Action<Exception> capture, ManualResetEventSlim done)
    {
        try { await test(); }
        catch (Exception exception) { capture(exception); }
        finally { done.Set(); }
    }

    /// <summary>
    /// Pins every awaited continuation of a UI test body to the STA thread that pumps the message loop.
    /// A thread that has no context of its own is handed a plain thread-pool posting context the first
    /// time the message loop runs, so a body that yields would quietly resume - and read or write
    /// controls - on a pool thread, where focus and selection state cannot be observed. WinForms leaves
    /// an already installed context alone, and this one marshals through a control created on this
    /// thread, which is what the message loop of a real WinForms application provides.
    /// </summary>
    private sealed class StaSynchronizationContext : SynchronizationContext
    {
        private readonly Control _marshaller = new();

        public StaSynchronizationContext() => _ = _marshaller.Handle;

        public override void Post(SendOrPostCallback callback, object? state) => _marshaller.BeginInvoke(callback, state);

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (_marshaller.InvokeRequired) _marshaller.Invoke(callback, state);
            else callback(state);
        }
    }
}

public sealed class RecordingAnnouncer : IAccessibleAnnouncer
{
    public bool SpeechEnabled { get; set; }
    public List<AccessibleAnnouncement> Announcements { get; } = new();
    public void Announce(AccessibleAnnouncement announcement) => Announcements.Add(announcement);
}

public sealed class FakeBrowseSource : IMusicBrowseSource
{
    public int SearchCalls { get; private set; }
    public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page, int pageSize, string searchType, string sourceId, CancellationToken cancellationToken)
    {
        SearchCalls++;
        return Task.FromResult(new PagedResult<MusicInfo>
        {
            Items = new List<MusicInfo> { new() { Id = "1", Source = "netease", Title = "Song", Artist = "Artist" } },
            CurrentPage = page,
            PageSize = pageSize,
            TotalItems = 1,
            HasMore = false
        });
    }

    public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken cancellationToken) => Task.FromResult(new PagedResult<MusicInfo>());
    public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken cancellationToken) => Task.FromResult(new List<PlaylistCategory>());
    public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken cancellationToken) => Task.FromResult(new PagedResult<Playlist>());
    public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page, int pageSize, CancellationToken cancellationToken) => Task.FromResult(new PagedResult<MusicInfo>());
    public Task<List<Ranking>> GetRankingsAsync(string sourceId, CancellationToken cancellationToken) => Task.FromResult(new List<Ranking>());
    public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page, int pageSize, CancellationToken cancellationToken) => Task.FromResult(new PagedResult<MusicInfo>());
}

public sealed class FakeDownloadWorkflow : IDownloadWorkflow
{
    private readonly ObservableCollection<DownloadTask> _tasks = new();
    public ReadOnlyObservableCollection<DownloadTask> Tasks { get; }
    public int ClearCompletedCalls { get; private set; }
    public FakeDownloadWorkflow()
    {
        _tasks.Add(new DownloadTask { Music = new MusicInfo { Title = "Song" }, Status = DownloadTaskStatus.Completed, Progress = 100 });
        Tasks = new ReadOnlyObservableCollection<DownloadTask>(_tasks);
    }

    public void ApplyPreferences(DownloadPreferences preferences) { }
    public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new DownloadTask());
    public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DownloadListResult(0, 0, 0, 0, 0, Array.Empty<DownloadTask>(), "ok"));
    public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public void Cancel(DownloadTask task) { }
    public void Pause(DownloadTask task) { }
    public void Resume(DownloadTask task) { }
    public bool Delete(DownloadTask task, bool deleteSourceFile) => true;
    public void ClearCompleted() => ClearCompletedCalls++;
    public string ResolveDownloadDirectory(string? requested = null) => requested ?? Path.GetTempPath();
    public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
    public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) => Task.FromResult<ExternalPlaybackTarget?>(null);
    public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ExternalPlaybackResult(true, "ok"));
    public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
}

public class WinFormsShellAccessibilityTests
{
    private static WinFormsShell CreateShell(RecordingAnnouncer announcer, FakeBrowseSource source)
    {
        return new WinFormsShell(new WinFormsShellServices
        {
            Announcer = announcer,
            BrowseSource = source,
            TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-winforms-tests-" + Guid.NewGuid().ToString("N"))
        });
    }

    [Fact]
    public void ShellUsesTraditionalMenusTabsListsAndSemanticNames()
    {
        WinFormsTestRunner.Run(() =>
        {
            using var shell = CreateShell(new RecordingAnnouncer(), new FakeBrowseSource());
            Assert.IsType<MainMenu>(shell.Menu);
            var tabs = Assert.IsType<TabControl>(shell.Controls.Find("MainTabs", true)[0]);
            Assert.Equal(4, tabs.TabPages.Count);
            Assert.Equal("搜索关键词", shell.Controls.Find("搜索关键词", true)[0].AccessibleName);
            Assert.Equal("我的歌单", shell.Controls.Find("OnlinePlaylists", true)[0].AccessibleName);
            Assert.Equal("分类歌曲", shell.Controls.Find("CategorySongs", true)[0].AccessibleName);
            Assert.Equal("排行榜歌曲", shell.Controls.Find("RankingSongs", true)[0].AccessibleName);
            WinFormsAccessibilityAudit.AssertAccessible(shell);
            WinFormsAccessibilityAudit.AssertNoForbiddenControls(shell);
        });
    }

    [Fact]
    public void SelectionChangesDoNotTriggerSearch()
    {
        WinFormsTestRunner.Run(() =>
        {
            var source = new FakeBrowseSource();
            using var shell = CreateShell(new RecordingAnnouncer(), source);
            shell.Controls.Find("搜索音源", true)[0].Text = "网易云音乐";
            shell.Controls.Find("搜索类型", true)[0].Text = "专辑";
            shell.Controls.Find("分类歌单音源", true)[0].Text = "QQ音乐";
            shell.Controls.Find("排行榜音源", true)[0].Text = "酷狗音乐";
            Assert.Equal(0, source.SearchCalls);
        });
    }
}

public class WinFormsToolFormsTests
{
    [Fact]
    public void DownloadManagerUsesNativeListAndOnlyExplicitClear()
    {
        WinFormsTestRunner.Run(() =>
        {
            var workflow = new FakeDownloadWorkflow();
            using var form = new DownloadManagerForm(new RecordingAnnouncer(), workflow);
            var list = Assert.IsType<ListView>(form.Controls.Find("DownloadTasks", true)[0]);
            Assert.Equal("下载任务", list.AccessibleName);
            Assert.True(list.Items.Count == 1, "service=" + workflow.Tasks.Count + ";list=" + list.Items.Count);
            Assert.Contains("100%", list.Items[0].SubItems[2].Text, StringComparison.Ordinal);
            WinFormsAccessibilityAudit.AssertAccessible(form);
            WinFormsAccessibilityAudit.AssertNoForbiddenControls(form);
            Assert.True(form.Controls.Find("清除已完成", true).Length == 1);
        });
    }

    [Fact]
    public void RequiredNativeFormsExist()
    {
        WinFormsTestRunner.Run(() =>
        {
            var types = typeof(WinFormsShell).Assembly.GetTypes().Select(type => type.FullName).ToList();
            Assert.Contains(typeof(SettingsForm).FullName, types);
            Assert.Contains(typeof(SourceManagerForm).FullName, types);
            Assert.Contains(typeof(PlaylistImportForm).FullName, types);
            Assert.Contains(typeof(LyricsForm).FullName, types);
            Assert.Contains(typeof(ManualSourceSwitchForm).FullName, types);
            Assert.Contains(typeof(UserGuideForm).FullName, types);
            Assert.Contains(typeof(PlaylistLoginMethodForm).FullName, types);
            Assert.Contains(typeof(WebLoginForm).FullName, types);
            Assert.Contains(typeof(KugouQrLoginForm).FullName, types);
        });
    }
}

public static class WinFormsAccessibilityAudit
{
    private static readonly HashSet<string> Forbidden = new() { "MenuStrip", "ContextMenuStrip", "ToolStrip", "ToolStripMenuItem", "DataGridView", "FlowLayoutPanel" };

    public static void AssertNoForbiddenControls(Control root)
    {
        var offenders = Descendants(root).Where(control => Forbidden.Contains(control.GetType().Name)).ToList();
        Assert.True(offenders.Count == 0, "Forbidden controls: " + string.Join(", ", offenders.Select(c => c.GetType().Name)));
    }

    public static void AssertAccessible(Control root)
    {
        var errors = new List<string>();
        foreach (var control in Descendants(root))
        {
            if (!control.TabStop) continue;
            if (control is ContainerControl or UserControl or Panel or TableLayoutPanel or SplitContainer or TabControl or StatusBar) continue;
            if (string.IsNullOrWhiteSpace(control.AccessibleName)) errors.Add(control.GetType().Name + " has no AccessibleName");
            if (control.TabIndex < 0) errors.Add(control.GetType().Name + " has invalid TabIndex");
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
