using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LXMusicAccessible.App;
using Xunit;

namespace LXMusicAccessible.Tests;

[Collection("WPF accessibility semantics")]
public sealed class ToolWindowCoordinatorTests
{
    [Fact]
    public async Task Source_manager_is_modal_single_instance_and_recreated_after_close()
    {
        await RunOnStaThreadAsync(() =>
        {
            var created = new List<RecordingToolWindow>();
            var coordinator = new ToolWindowCoordinator((_, _) =>
            {
                var window = new RecordingToolWindow();
                created.Add(window);
                return window;
            });
            var owner = new Window();

            Assert.Equal(
                ToolWindowOpenResult.Created,
                coordinator.ShowSourceManager(owner, _ => false, () => { }));
            Assert.Equal(1, created[0].ShowModalCount);
            Assert.True(coordinator.IsOpen(ToolWindowKind.SourceManager));

            Assert.Equal(
                ToolWindowOpenResult.Activated,
                coordinator.ShowSourceManager(owner, _ => false, () => { }));
            Assert.Single(created);
            Assert.Equal(1, created[0].ActivateCount);

            created[0].Close();
            Assert.False(coordinator.IsOpen(ToolWindowKind.SourceManager));
            coordinator.ShowSourceManager(owner, _ => false, () => { });
            Assert.Equal(2, created.Count);
        });
    }

    [Fact]
    public async Task Closing_modal_restores_the_owner_entry_focus()
    {
        await RunOnStaThreadAsync(() =>
        {
            var restored = 0;
            var owner = new Window();
            owner.Show();
            try
            {
                var coordinator = new ToolWindowCoordinator((_, _) =>
                    new RecordingToolWindow { CloseWhenShown = true });

                coordinator.ShowSourceManager(owner, _ => false, () => restored++);

                Assert.Equal(1, restored);
                Assert.False(coordinator.IsOpen(ToolWindowKind.SourceManager));
            }
            finally
            {
                owner.Close();
            }
        });
    }

    [Fact]
    public async Task Window_group_restores_only_recorded_members_and_their_active_focus()
    {
        await RunOnStaThreadAsync(() =>
        {
            var window = new RecordingToolWindow
            {
                Visible = true,
                Active = true,
                Focus = new object()
            };
            var coordinator = new ToolWindowCoordinator((_, _) => window);
            coordinator.ShowSourceManager(new Window(), _ => false, () => { });

            var snapshot = coordinator.HideVisibleWindows();
            Assert.False(window.Visible);
            Assert.Single(snapshot.VisibleMembers);
            Assert.Same(window, snapshot.ActiveWindow);

            Assert.True(coordinator.RestoreVisibleWindows(snapshot));
            Assert.True(window.Visible);
            Assert.Equal(1, window.RestoreCount);
            Assert.Equal(1, window.RestoreFocusCount);
            Assert.Same(window.Focus, window.RestoredFocus);

            window.Close();
            Assert.False(coordinator.RestoreVisibleWindows(snapshot));
            Assert.Equal(1, window.RestoreCount);
        });
    }

    [Fact]
    public async Task Modal_wpf_handle_restores_focus_after_the_dialog_becomes_visible()
    {
        await RunOnStaThreadAsync(() =>
        {
            var target = new Button { Content = "音源" };
            var window = new Window { Content = target };
            var handle = new WpfToolWindowHandle(ToolWindowKind.SourceManager, window, modal: true);
            var restored = false;

            handle.Restore();
            Assert.True(handle.RestoreFocus(target));
            _ = window.Dispatcher.BeginInvoke(() =>
            {
                restored = window.IsVisible
                    && window.IsActive
                    && ReferenceEquals(Keyboard.FocusedElement, target);
                window.Close();
            });
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.True(restored);
        });
    }

    private static Task RunOnStaThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            if (failure is null) completion.SetResult();
            else completion.SetException(failure);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class RecordingToolWindow : IToolWindowHandle
    {
        public ToolWindowKind Kind => ToolWindowKind.SourceManager;
        public bool IsVisible => Visible;
        public bool IsActive => Active;
        public bool IsClosed { get; private set; }
        public bool Visible { get; set; } = true;
        public bool Active { get; set; }
        public bool CloseWhenShown { get; set; }
        public object? Focus { get; set; }
        public object? RestoredFocus { get; private set; }
        public int ShowModalCount { get; private set; }
        public int ActivateCount { get; private set; }
        public int RestoreCount { get; private set; }
        public int RestoreFocusCount { get; private set; }
        public event EventHandler? Closed;

        public void ShowModal()
        {
            ShowModalCount++;
            Visible = true;
            if (CloseWhenShown) Close();
        }

        public void Hide() => Visible = false;

        public void Restore()
        {
            RestoreCount++;
            Visible = true;
        }

        public bool Activate()
        {
            ActivateCount++;
            Active = true;
            return true;
        }

        public object? CaptureFocus() => Focus;

        public bool RestoreFocus(object? focus)
        {
            RestoreFocusCount++;
            RestoredFocus = focus;
            return focus is not null;
        }

        public void Close()
        {
            if (IsClosed) return;
            IsClosed = true;
            Visible = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
