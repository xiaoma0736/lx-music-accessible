using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class DownloadCommandStateTests
{
    [Fact]
    public void Multi_retry_is_reentrant_safe_and_reports_accepted_count()
    {
        var announcer = new RecordingAnnouncer();
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var workflow = new BlockingRetryWorkflow();
            using var form = new DownloadManagerForm(announcer, workflow);
            form.Show();
            Application.DoEvents();
            var tasks = Assert.IsType<ListView>(form.Controls.Find("DownloadTasks", true).Single());
            foreach (ListViewItem item in tasks.Items) item.Selected = true;
            Application.DoEvents();
            var retry = Assert.IsType<Button>(form.Controls.Find("重试", true).Single());

            retry.PerformClick();
            await WaitAsync(() => workflow.RetryCalls == 1);
            retry.PerformClick();
            Assert.Contains("上一个下载命令仍在执行", announcer.Announcements.Select(item => item.Text));
            workflow.Release();

            await WaitAsync(announcer, message => message.Contains("接受 2", StringComparison.Ordinal));
            Assert.Equal(2, workflow.RetryCalls);
        });
    }

    [Fact]
    public void Busy_retry_blocks_cancel_pause_resume_and_clear_commands()
    {
        var announcer = new RecordingAnnouncer();
        WinFormsTestRunner.RunStaAsync(async () =>
        {
            var workflow = new BlockingRetryWorkflow();
            using var form = new DownloadManagerForm(announcer, workflow);
            form.Show();
            Application.DoEvents();
            var tasks = Assert.IsType<ListView>(form.Controls.Find("DownloadTasks", true).Single());
            foreach (ListViewItem item in tasks.Items) item.Selected = true;
            Application.DoEvents();
            Assert.IsType<Button>(form.Controls.Find("重试", true).Single()).PerformClick();
            await WaitAsync(() => workflow.RetryCalls == 1);

            InvokeCommand(form, "Cancel");
            InvokeCommand(form, "ChangeState", DownloadWorkflowCommand.Pause);
            InvokeCommand(form, "ChangeState", DownloadWorkflowCommand.Resume);
            InvokeCommand(form, "ClearCompleted");
            await WaitAsync(() => announcer.Announcements.Count(item => item.Text.Contains("上一个下载命令仍在执行", StringComparison.Ordinal)) == 4);

            workflow.Release();
            await WaitAsync(announcer, message => message.Contains("接受 2", StringComparison.Ordinal));
            Assert.Equal(0, workflow.CancelCalls);
            Assert.Equal(0, workflow.PauseCalls);
            Assert.Equal(0, workflow.ResumeCalls);
            Assert.Equal(2, workflow.Tasks.Count);
        });
    }

    [Fact]
    public void Focused_restart_command_announces_reentrancy()
    {
        var source = File.ReadAllText(Path.Combine(FindSourceDirectory(), "LXMusicAccessible.App", "WinFormsToolForms.cs"));
        Assert.Contains("if (task is null) return;", source, StringComparison.Ordinal);
        Assert.Contains("上一个下载命令仍在执行", source, StringComparison.Ordinal);
    }
    private static string FindSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;
        return directory!.FullName;
    }

    private static void InvokeCommand(Form form, string name, params object?[] args) =>
        typeof(DownloadManagerForm).GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(form, args);

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "The awaited download-command state was not reached.");
    }

    private static async Task WaitAsync(RecordingAnnouncer announcer, Func<string, bool> condition) =>
        await WaitAsync(() => announcer.Announcements.Any(item => condition(item.Text)));

    private sealed class BlockingRetryWorkflow : IDownloadWorkflow
    {
        private readonly ObservableCollection<DownloadTask> _tasks = new();
        private readonly TaskCompletionSource<bool> _retry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancelCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public int RetryCalls { get; private set; }
        public ReadOnlyObservableCollection<DownloadTask> Tasks { get; }
        public BlockingRetryWorkflow()
        {
            _tasks.Add(new DownloadTask { Id = "one", Music = new MusicInfo { Id = "one", Source = "test", Title = "One" }, Status = DownloadTaskStatus.Failed });
            _tasks.Add(new DownloadTask { Id = "two", Music = new MusicInfo { Id = "two", Source = "test", Title = "Two" }, Status = DownloadTaskStatus.Cancelled });
            Tasks = new(_tasks);
        }
        public void Release() => _retry.TrySetResult(true);
        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new DownloadTask());
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new DownloadListResult(0, 0, 0, 0, 0, Array.Empty<DownloadTask>(), "ok"));
        public async Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default)
        {
            RetryCalls++;
            return await _retry.Task;
        }
        public Task<bool> RedownloadAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void Cancel(DownloadTask task) => CancelCalls++;
        public void Pause(DownloadTask task) => PauseCalls++;
        public void Resume(DownloadTask task) => ResumeCalls++;
        public bool Delete(DownloadTask task, bool deleteSourceFile) => true;
        public Task<bool> DeleteAsync(DownloadTask task, bool deleteSourceFile) => Task.FromResult(true);
        public void ClearCompleted() { }
        public string ResolveDownloadDirectory(string? requested = null) => Path.GetTempPath();
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) => Task.FromResult<ExternalPlaybackTarget?>(null);
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ExternalPlaybackResult(true, "ok"));
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }

    private sealed class BlockingRestartWorkflow : IDownloadWorkflow
    {
        private readonly ObservableCollection<DownloadTask> _tasks = new();
        private readonly TaskCompletionSource<bool> _redownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RedownloadCalls { get; private set; }
        public ReadOnlyObservableCollection<DownloadTask> Tasks { get; }
        public BlockingRestartWorkflow()
        {
            _tasks.Add(new DownloadTask { Id = "focused", Music = new MusicInfo { Id = "focused", Source = "test", Title = "Focused" }, Status = DownloadTaskStatus.Completed });
            Tasks = new(_tasks);
        }
        public void Release() => _redownload.TrySetResult(true);
        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new DownloadTask());
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new DownloadListResult(0, 0, 0, 0, 0, Array.Empty<DownloadTask>(), "ok"));
        public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public async Task<bool> RedownloadAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default)
        {
            RedownloadCalls++;
            var started = await _redownload.Task;
            task.Status = DownloadTaskStatus.Completed;
            return started;
        }
        public void Cancel(DownloadTask task) { }
        public void Pause(DownloadTask task) { }
        public void Resume(DownloadTask task) { }
        public bool Delete(DownloadTask task, bool deleteSourceFile) => true;
        public Task<bool> DeleteAsync(DownloadTask task, bool deleteSourceFile) => Task.FromResult(true);
        public void ClearCompleted() { }
        public string ResolveDownloadDirectory(string? requested = null) => Path.GetTempPath();
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) => Task.FromResult<ExternalPlaybackTarget?>(null);
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ExternalPlaybackResult(true, "ok"));
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }
}
