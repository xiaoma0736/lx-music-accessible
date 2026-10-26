using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using System.Windows.Forms;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class DownloadManagerLifecycleTests
{
    [Fact]
    public void Closing_manager_detaches_from_global_download_events()
    {
        WinFormsTestRunner.Run(() =>
        {
            var tasks = new ObservableCollection<DownloadTask>();
            var workflow = new ObservableWorkflow(tasks);
            var form = new DownloadManagerForm(new RecordingAnnouncer(), workflow);
            form.Show();
            Application.DoEvents();
            Assert.True(form.IsHandleCreated);

            form.Close();
            Application.DoEvents();
            Assert.True(form.IsDisposed);

            var exception = Record.Exception(() => tasks.Add(new DownloadTask
            {
                Music = new MusicInfo { Id = "new", Source = "test", Title = "New", Artist = "Artist" }
            }));

            Assert.Null(exception);
        });
    }
    private sealed class ObservableWorkflow : IDownloadWorkflow
    {
        public ObservableWorkflow(ObservableCollection<DownloadTask> tasks) => Tasks = new(tasks);

        public ReadOnlyObservableCollection<DownloadTask> Tasks { get; }
        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new DownloadTask());
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DownloadListResult(0, 0, 0, 0, 0, Array.Empty<DownloadTask>(), "ok"));
        public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RedownloadAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void Cancel(DownloadTask task) { }
        public void Pause(DownloadTask task) { }
        public void Resume(DownloadTask task) { }
        public bool Delete(DownloadTask task, bool deleteSourceFile) => true;
    public Task<bool> DeleteAsync(DownloadTask task, bool deleteSourceFile) => Task.FromResult(Delete(task, deleteSourceFile));
        public void ClearCompleted() { }
        public string ResolveDownloadDirectory(string? requested = null) => Path.GetTempPath();
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) => Task.FromResult<ExternalPlaybackTarget?>(null);
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ExternalPlaybackResult(true, "ok"));
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }
    private sealed class BlockingWorkflow : IDownloadWorkflow
    {
        private readonly TaskCompletionSource<DownloadTask> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ObservableCollection<DownloadTask> _tasks = new();
        public ReadOnlyObservableCollection<DownloadTask> Tasks { get; }
        public int CancelCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public List<(DownloadTask Task, bool DeleteSource)> Deleted { get; } = new();

        public BlockingWorkflow()
        {
            var task = new DownloadTask { Music = new MusicInfo { Id = "one", Source = "test", Title = "One", Artist = "Artist" }, Status = DownloadTaskStatus.Downloading };
            Tasks = new ReadOnlyObservableCollection<DownloadTask>(_tasks);
            _tasks.Add(task);
        }

        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) => _completion.Task;
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RedownloadAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void Cancel(DownloadTask task) => CancelCalls++;
        public void Pause(DownloadTask task)
        {
            PauseCalls++;
            task.Status = DownloadTaskStatus.Paused;
        }

        public void Resume(DownloadTask task)
        {
            ResumeCalls++;
            task.Status = DownloadTaskStatus.Downloading;
        }
        public bool Delete(DownloadTask task, bool deleteSourceFile)
        {
            Deleted.Add((task, deleteSourceFile));
            _tasks.Remove(task);
            return true;
        }
        public Task<bool> DeleteAsync(DownloadTask task, bool deleteSourceFile) => Task.FromResult(Delete(task, deleteSourceFile));
        public void ClearCompleted() { }
        public string ResolveDownloadDirectory(string? requested = null) => Path.GetTempPath();
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) => Task.FromResult<ExternalPlaybackTarget?>(null);
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ExternalPlaybackResult(true, "ok"));
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }

    [Fact]
    public void Manager_groups_tasks_and_cancel_calls_the_workflow()
    {
        WinFormsTestRunner.Run(() =>
        {
            var workflow = new BlockingWorkflow();
            using var form = new DownloadManagerForm(new RecordingAnnouncer(), workflow);
            form.Show();
            Application.DoEvents();
            var list = Assert.IsType<ComboBox>(form.Controls.Find("DownloadTaskFilter", true).Single());
            var tasks = Assert.IsType<ListView>(form.Controls.Find("DownloadTasks", true).Single());

            Assert.Contains("全部", list.Items.Cast<object>().Select(item => item.ToString()).ToArray());

            tasks.Items[0].Selected = true;
            Application.DoEvents();
            Assert.Single(tasks.SelectedIndices);
            var cancelButton = Assert.IsType<Button>(form.Controls.Find("取消", true).Single());
            Assert.True(cancelButton.Enabled, $"cancel enabled={cancelButton.Enabled} selected={tasks.SelectedIndices.Count} status={((DownloadTask)tasks.Items[0].Tag).Status}");
            cancelButton.PerformClick();

            Assert.Equal(1, workflow.CancelCalls);
        });
    }

    [Fact]
    public void Pause_resume_and_delete_are_sent_to_workflow()
    {
        WinFormsTestRunner.Run(() =>
        {
            var workflow = new BlockingWorkflow();
            using var form = new DownloadManagerForm(
                new RecordingAnnouncer(),
                workflow,
                confirmDelete: _ => true);
            form.Show();
            Application.DoEvents();
            var task = workflow.Tasks.Single();
            var tasks = Assert.IsType<ListView>(form.Controls.Find("DownloadTasks", true).Single());
            tasks.Items[0].Selected = true;
            Application.DoEvents();
            Assert.True(tasks.SelectedIndices.Count == 1, $"initial selection={tasks.SelectedIndices.Count}");

            var pauseButton = Assert.IsType<Button>(form.Controls.Find("暂停", true).Single());
            Assert.True(pauseButton.Enabled);
            pauseButton.PerformClick();
            Application.DoEvents();
            Assert.Equal(DownloadTaskStatus.Paused, task.Status);

            var resumeButton = Assert.IsType<Button>(form.Controls.Find("继续", true).Single());
            Assert.True(resumeButton.Enabled);
            resumeButton.PerformClick();
            Application.DoEvents();
            Assert.Equal(DownloadTaskStatus.Downloading, task.Status);
            var deleteButton = Assert.IsType<Button>(form.Controls.Find("删除", true).Single());
            Assert.True(deleteButton.Enabled, $"delete enabled={deleteButton.Enabled} selected={tasks.SelectedIndices.Count} status={task.Status}");
            deleteButton.PerformClick();

            Assert.Equal(1, workflow.PauseCalls);
            Assert.Equal(1, workflow.ResumeCalls);
            Assert.True(workflow.Deleted.Count == 1, $"deleted={workflow.Deleted.Count}");
            Assert.Equal((task, true), workflow.Deleted.Single());
        });
    }
}
