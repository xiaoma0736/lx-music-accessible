using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class DownloadTaskContextMenuTests
{
    private sealed class ObservableWorkflow : IDownloadWorkflow
    {
        private readonly ObservableCollection<DownloadTask> _tasks;
        public ObservableWorkflow(ObservableCollection<DownloadTask> tasks)
        {
            _tasks = tasks;
            Tasks = new(tasks);
        }
        public ReadOnlyObservableCollection<DownloadTask> Tasks { get; }
        public int CancelCalls { get; private set; }
        public int RedownloadCalls { get; private set; }
        public int RetryCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public List<(DownloadTask Task, bool DeleteSource)> Deleted { get; } = new();
        public void ApplyPreferences(DownloadPreferences preferences) { }
        public Task<DownloadTask> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new DownloadTask());
        public Task<DownloadListResult> DownloadListAsync(DownloadListRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DownloadListResult(0, 0, 0, 0, 0, Array.Empty<DownloadTask>(), "ok"));
        public Task<bool> RetryAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default)
        {
            RetryCalls++;
            return Task.FromResult(true);
        }
        public Task<bool> RedownloadAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default)
        {
            RedownloadCalls++;
            return Task.FromResult(true);
        }
        public void Cancel(DownloadTask task) => CancelCalls++;
        public void Pause(DownloadTask task) => PauseCalls++;
        public void Resume(DownloadTask task) => ResumeCalls++;
        public bool Delete(DownloadTask task, bool deleteSourceFile)
        {
            DeleteCalls++;
            return true;
        }
        public Task<bool> DeleteAsync(DownloadTask task, bool deleteSourceFile) => Task.FromResult(Delete(task, deleteSourceFile));
        public void ClearCompleted() { }
        public string ResolveDownloadDirectory(string? requested = null) => System.IO.Path.GetTempPath();
        public ExternalPlaybackResult OpenDownloadDirectory(string? requested = null) => new(true, "ok");
        public Task<ExternalPlaybackTarget?> PrepareExternalPlaybackAsync(MusicInfo music, string quality, CancellationToken cancellationToken = default) => Task.FromResult<ExternalPlaybackTarget?>(null);
        public Task<ExternalPlaybackResult> PlayExternallyAsync(ExternalPlaybackRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ExternalPlaybackResult(true, "ok"));
        public DownloadCacheCleanupResult ClearTemporaryFiles() => new(0, 0);
    }

    [Fact]
    public void Context_menu_targets_only_the_focused_task()
    {
        WinFormsTestRunner.Run(() =>
        {
            var workflow = new ObservableWorkflow(new System.Collections.ObjectModel.ObservableCollection<DownloadTask>());
            using var form = new DownloadManagerForm(new RecordingAnnouncer(), workflow, confirmDelete: _ => true);
            form.Show();
            Application.DoEvents();
            var list = Assert.IsType<ListView>(form.Controls.Find("DownloadTasks", true).Single());
            var menu = Assert.IsType<ContextMenu>(list.ContextMenu);
            var completed = new DownloadTask { Id = "completed", Music = new MusicInfo { Id = "completed", Source = "test", Title = "Completed" }, Status = DownloadTaskStatus.Completed };
            var failed = new DownloadTask { Id = "failed", Music = new MusicInfo { Id = "failed", Source = "test", Title = "Failed" }, Status = DownloadTaskStatus.Failed };
            var paused = new DownloadTask { Id = "paused", Music = new MusicInfo { Id = "paused", Source = "test", Title = "Paused" }, Status = DownloadTaskStatus.Paused };

            void SetFocused(DownloadTask task)
            {
                var item = new ListViewItem(task.DisplayName) { Tag = task };
                list.Items.Add(item);
                item.Selected = true;
                item.Focused = true;
                form.OnTaskMenuPopup();
                Application.DoEvents();
            }

            MenuItem Required(string text) => Assert.Single(menu.MenuItems.Cast<MenuItem>(), item => item.Text == text);

            SetFocused(completed);
            var redownload = Required("手动重新下载");
            RaisePopup(redownload);
            Assert.True(redownload.Enabled);
            redownload.PerformClick();

            SetFocused(failed);
            var retry = Required("重试下载");
            RaisePopup(retry);
            Assert.True(retry.Enabled);
            retry.PerformClick();

            SetFocused(paused);
            var resume = Required("继续下载");
            RaisePopup(resume);
            Assert.True(resume.Enabled);
            resume.PerformClick();

            SetFocused(paused);

            var stop = Required("停止下载");
            RaisePopup(stop);
            Assert.True(stop.Enabled);


            var remove = Required("移除下载");
            RaisePopup(remove);
            remove.PerformClick();

            Assert.Equal(1, workflow.RedownloadCalls);
            Assert.Equal(1, workflow.RetryCalls);
            Assert.Equal(1, workflow.ResumeCalls);
            Assert.Equal(1, workflow.DeleteCalls);
        });
    }

    private static void RaisePopup(MenuItem item)
    {
        var method = typeof(MenuItem).GetMethod("OnPopup", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(item, [EventArgs.Empty]);
        Application.DoEvents();
    }
}

