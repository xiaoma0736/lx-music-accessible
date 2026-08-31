using System.IO;
using System.Windows;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LXMusicAccessible.App;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Xunit;

namespace LXMusicAccessible.Tests;

[Collection("WPF accessibility semantics")]
public sealed class SourceManagerWindowTests
{
    [Fact]
    public void Main_window_has_one_source_menu_entry_and_no_embedded_source_page()
    {
        var root = FindRepositoryRoot();
        var mainXaml = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "MainWindow.xaml"));
        var sourceXaml = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "SourceManagerWindow.xaml"));
        var guide = File.ReadAllText(Path.Combine(
            root, "src", "LXMusicAccessible.App", "Docs", "使用说明.md"));

        Assert.Single(Regex.Matches(mainXaml, "Header=\"音源管理\\(_M\\)\"").Cast<Match>());
        Assert.DoesNotContain("<TabItem Header=\"音源管理\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"关闭\"", sourceXaml, StringComparison.Ordinal);
        Assert.Contains("Title=\"音源管理\"", sourceXaml, StringComparison.Ordinal);
        Assert.Contains("WindowStartupLocation=\"CenterOwner\"", sourceXaml, StringComparison.Ordinal);
        Assert.Contains("ResizeMode=\"CanResize\"", sourceXaml, StringComparison.Ordinal);
        Assert.Contains("Ctrl+5", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("| Alt+8 | 音源管理", guide, StringComparison.Ordinal);
        Assert.Contains("工具→音源管理", guide, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_source_list_is_the_initial_focus_and_exposes_a_concise_empty_state()
    {
        await RunOnStaThreadAsync(() =>
        {
            var sources = new RecordingSourceService();
            var window = new SourceManagerWindow(new RecordingAnnouncer(), sources, _ => false);
            try
            {
                window.Show();
                Pump(window.Dispatcher);

                Assert.Same(window.SourceList, Keyboard.FocusedElement);
                Assert.Equal("音源", new ListBoxAutomationPeer(window.SourceList).GetName());
                Assert.Equal("暂无音源", AutomationProperties.GetItemStatus(window.SourceList));
                Assert.Equal(Visibility.Visible, window.EmptySourceText.Visibility);
                Assert.Equal(0, sources.OperationCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Nonempty_source_list_starts_on_the_selected_first_item_without_side_effects()
    {
        await RunOnStaThreadAsync(() =>
        {
            var sources = new RecordingSourceService(
                Source("a", "甲"),
                Source("b", "乙"));
            var window = new SourceManagerWindow(new RecordingAnnouncer(), sources, _ => false);
            try
            {
                window.Show();
                Pump(window.Dispatcher);

                var first = Assert.IsType<ListBoxItem>(
                    window.SourceList.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Equal(0, window.SourceList.SelectedIndex);
                Assert.Same(window.SourceList.SelectedItem, window.SourceList.Items.CurrentItem);
                Assert.Same(first, Keyboard.FocusedElement);

                RaiseKey(first, Key.Down);
                Assert.Equal(1, window.SourceList.SelectedIndex);
                Assert.Equal(0, sources.OperationCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Tab_from_initial_source_item_leaves_the_list_in_both_directions()
    {
        await RunOnStaThreadAsync(() =>
        {
            var sources = new RecordingSourceService(Source("a", "甲"));
            var window = new SourceManagerWindow(new RecordingAnnouncer(), sources, _ => false);
            try
            {
                window.Show();
                Pump(window.Dispatcher);

                var initial = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
                Assert.True(initial.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(window.ImportLocalSourceBtn, Keyboard.FocusedElement);

                var first = Assert.IsType<ListBoxItem>(
                    window.SourceList.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.True(first.Focus());
                Assert.True(first.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)));
                Assert.Same(window.RefreshSourcesBtn, Keyboard.FocusedElement);
                Assert.Equal(0, sources.OperationCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Refresh_preserves_source_identity_and_uses_the_adjacent_index_when_removed()
    {
        await RunOnStaThreadAsync(() =>
        {
            var sources = new RecordingSourceService(
                Source("a", "甲"),
                Source("b", "乙"));
            var window = new SourceManagerWindow(new RecordingAnnouncer(), sources, _ => false);
            try
            {
                window.Show();
                Pump(window.Dispatcher);
                Assert.True(window.RefreshSourcesBtn.Focus());
                window.RefreshSourcesBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window.Dispatcher);
                var refreshedFirst = Assert.IsType<ListBoxItem>(
                    window.SourceList.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Same(refreshedFirst, Keyboard.FocusedElement);
                window.SourceList.SelectedIndex = 1;
                var selected = Assert.IsType<ListBoxItem>(
                    window.SourceList.ItemContainerGenerator.ContainerFromIndex(1));
                Assert.True(selected.Focus());

                sources.Replace(Source("a", "甲新版"), Source("b", "乙新版"));
                Pump(window.Dispatcher);
                Assert.Equal("b", Assert.IsType<MusicSource>(window.SourceList.SelectedItem).Id);
                var refreshed = Assert.IsType<ListBoxItem>(
                    window.SourceList.ItemContainerGenerator.ContainerFromIndex(1));
                Assert.Same(refreshed, Keyboard.FocusedElement);

                sources.Replace(Source("a", "甲新版"), Source("c", "丙"));
                Pump(window.Dispatcher);
                Assert.Equal("c", Assert.IsType<MusicSource>(window.SourceList.SelectedItem).Id);
                Assert.Equal(1, window.SourceList.SelectedIndex);
                Assert.Equal(0, sources.OperationCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Operation_can_finish_after_close_announces_globally_and_releases_subscription()
    {
        await RunOnStaThreadAsync(() =>
        {
            var sources = new RecordingSourceService(Source("a", "测试音源"));
            var announcer = new RecordingAnnouncer();
            SourceManagerWindow? window = null;
            sources.TestSource = _ =>
            {
                window!.Close();
                return Task.FromResult(true);
            };
            window = new SourceManagerWindow(announcer, sources, _ => false);
            window.Show();
            Pump(window.Dispatcher);
            window.SourceList.SelectedIndex = 0;

            var menu = Assert.IsType<ContextMenu>(window.FindResource("SourceContextMenu"));
            var test = menu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "检测有效性"));
            test.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.False(window.IsSourceSubscriptionActive);
            Assert.Equal(0, sources.SourceSubscriberCount);
            Assert.Contains("正在检测 测试音源 有效性...", announcer.Messages);
            Assert.Contains("测试音源 有效", announcer.Messages);
            Assert.Equal(1, sources.OperationCount);
        });
    }

    [Fact]
    public async Task Update_source_menu_refreshes_the_selected_network_source()
    {
        await RunOnStaThreadAsync(() =>
        {
            var announcer = new RecordingAnnouncer();
            var source = Source("remote", "Remote source");
            source.ScriptUrl = "https://fixture.invalid/source.js";
            var sources = new RecordingSourceService(source)
            {
                RefreshSource = item =>
                {
                    item.Version = "2.0";
                    return Task.FromResult(true);
                }
            };
            var window = new SourceManagerWindow(announcer, sources, _ => false);
            window.Show();
            Pump(window.Dispatcher);
            window.SourceList.SelectedIndex = 0;

            var menu = Assert.IsType<ContextMenu>(window.FindResource("SourceContextMenu"));
            var update = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "更新音源接口"));
            update.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(window.Dispatcher);

            Assert.Equal(1, sources.RefreshCount);
            Assert.Contains("正在更新音源接口：Remote source", announcer.Messages);
            Assert.Contains("Remote source 已更新至 2.0", announcer.Messages);
            window.Close();
        });
    }

    [Fact]
    public async Task Playback_hotkeys_are_delegated_but_escape_closes_locally()
    {
        await RunOnStaThreadAsync(() =>
        {
            var delegated = 0;
            var window = new SourceManagerWindow(
                new RecordingAnnouncer(),
                new RecordingSourceService(),
                _ => { delegated++; return true; });
            window.Show();
            Pump(window.Dispatcher);
            var presentation = PresentationSource.FromVisual(window)!;

            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, 0, Key.F5)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            Assert.Equal(1, delegated);

            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            Assert.Equal(1, delegated);
            Assert.False(window.IsSourceSubscriptionActive);
        });
    }

    private static MusicSource Source(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Version = "1.0",
        Type = SourceType.Custom,
        IsEnabled = true,
        TrustState = ScriptTrustState.UserApproved
    };

    private static void RaiseKey(UIElement element, Key key)
    {
        var source = PresentationSource.FromVisual(element);
        Assert.NotNull(source);
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
    }

    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "verify.ps1"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
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

    private sealed class RecordingSourceService(params MusicSource[] initial) : IMusicSourceService
    {
        private EventHandler? _sourcesChanged;
        private List<MusicSource> _sources = [.. initial];

        public int SourceSubscriberCount { get; private set; }
        public int OperationCount { get; private set; }
        public Func<MusicSource, Task<bool>> TestSource { get; set; } = _ => Task.FromResult(true);

        public event EventHandler? SourcesChanged
        {
            add { _sourcesChanged += value; SourceSubscriberCount++; }
            remove { _sourcesChanged -= value; SourceSubscriberCount--; }
        }

        public event EventHandler<LxScriptUpdateAlertEventArgs>? ScriptUpdateAlert
        {
            add { }
            remove { }
        }

        public void Replace(params MusicSource[] sources)
        {
            _sources = [.. sources];
            _sourcesChanged?.Invoke(this, EventArgs.Empty);
        }

        public List<MusicSource> GetAllSources() => [.. _sources];
        public async Task<bool> TestSourceAsync(MusicSource source)
        {
            OperationCount++;
            return await TestSource(source);
        }

        public Task<MusicSource?> ImportFromUrlAsync(string url, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<MusicSource?> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ScriptActivationResult> EnableSourceAsync(MusicSource source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void DisableSource(MusicSource source) => throw new NotSupportedException();
        public void RemoveSource(MusicSource source)
        {
            OperationCount++;
            _sources.RemoveAll(item => string.Equals(item.Id, source.Id, StringComparison.OrdinalIgnoreCase));
            _sourcesChanged?.Invoke(this, EventArgs.Empty);
        }
        public int RefreshCount { get; private set; }
        public Func<MusicSource, Task<bool>> RefreshSource { get; set; } = _ => Task.FromResult(false);

        public Task<bool> UpdateSourceAsync(MusicSource source) => throw new NotSupportedException();
        public Task<bool> RefreshSourceAsync(MusicSource source, CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return RefreshSource(source);
        }
        public MusicSource? GetDefaultSource() => _sources.FirstOrDefault();
        public void SetDefaultSource(string id) => throw new NotSupportedException();
        public Task<List<string>> GetQualitiesForSourceAsync(string sourceId) => throw new NotSupportedException();
        public Task PersistSourcesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public List<MusicSource> GetEnabledSources() => _sources.Where(source => source.IsEnabled).ToList();
        public Task<LxLyricRaw?> GetLyricFromScriptsAsync(MusicInfo music, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PagedResult<MusicInfo>> SearchAsync(string query, int page = 1, int pageSize = 20, string searchType = "song", string sourceId = "all", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string?> GetPlayUrlAsync(string musicId, string source, string quality = "128k", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string?> GetPlayUrlAsync(MusicInfo music, string source, string quality = "128k", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId = "netease", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page = 1, int pageSize = 30, string sourceId = "netease", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<List<Ranking>> GetRankingsAsync(string sourceId = "netease", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IMusicApi? GetApi(string id) => null;
    }
}
