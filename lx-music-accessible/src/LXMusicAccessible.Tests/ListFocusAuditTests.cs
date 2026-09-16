using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LXMusicAccessible.Tests;

[Collection("WPF accessibility semantics")]
public sealed class ListFocusAuditTests
{
    [Fact]
    public void Every_focusable_list_is_present_in_the_explicit_accessibility_inventory()
    {
        var appDirectory = Path.Combine(FindRepositoryRoot(), "src", "LXMusicAccessible.App");
        var found = Directory.EnumerateFiles(appDirectory, "*.xaml")
            .SelectMany(path => XDocument.Load(path).Descendants()
                .Where(element => (element.Name.LocalName.EndsWith("ListBox", StringComparison.Ordinal)
                        || element.Name.LocalName.EndsWith("ListView", StringComparison.Ordinal)
                        || element.Name.LocalName is "DataGrid" or "ItemsControl")
                    && element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) is not null)
                .Select(element => $"{Path.GetFileName(path)}:{element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Value}"))
            .OrderBy(value => value)
            .ToArray();

        Assert.Equal(new[]
        {
            "DownloadManagerWindow.xaml:DownloadTaskList",
            "MainWindow.xaml:CategoryList",
            "MainWindow.xaml:CategoryPlaylistsListBox",
            "MainWindow.xaml:CategorySongsList",
            "MainWindow.xaml:FavoriteSongsList",
            "MainWindow.xaml:OnlinePlaylistList",
            "MainWindow.xaml:RankingList",
            "MainWindow.xaml:RankingSongsList",
            "MainWindow.xaml:SearchResultsList",
            "ManualSourceSwitchWindow.xaml:SourceOptionsList",
            "PlaylistImportWindow.xaml:MyPlaylistsList",
            "PlaylistImportWindow.xaml:PlaylistSongsList",
            "PlaylistLoginMethodWindow.xaml:MethodsList",
            "SettingsWindow.xaml:CategoryList",
            "SettingsWindow.xaml:HotkeyListBox",
            "SongDetailDialog.xaml:DetailsList",
            "SourceManagerWindow.xaml:SourceList"
        }, found);
    }

    [Fact]
    public async Task Successful_search_focuses_the_first_native_result_item()
    {
        await RunOnStaThreadAsync(async () =>
        {
            var source = new ControlledBrowseSource
            {
                SearchResults = [new() { Id = "song-1", Title = "第一首", Artist = "歌手" }]
            };
            using var host = await TestApplicationHost.StartAsync(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IMusicBrowseSource>(source));
            });
            var window = host.Services.GetRequiredService<MainWindow>();
            var workflow = host.Services.GetRequiredService<MusicBrowseWorkflow>();
            var settings = host.Services.GetRequiredService<SettingsWorkflow>();
            try
            {
                window.Show();
                PumpUntil(window.Dispatcher, () => settings.Current.Version > 0);
                PumpFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
                window.SearchBox.Text = "测试";
                window.SearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(
                    window.Dispatcher,
                    () => window.SearchResultsList.Items.Count == 1,
                    () => $"operations={source.OperationCount}, status={window.StatusText.Text}, items={window.SearchResultsList.Items.Count}, workflow={workflow.CurrentSearch.Items.Count}");

                var first = Assert.IsType<ListBoxItem>(
                    window.SearchResultsList.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Equal(0, window.SearchResultsList.SelectedIndex);
                Assert.Same(first, Keyboard.FocusedElement);
                Assert.Equal(1, source.OperationCount);
            }
            finally
            {
                window.Hide();
            }
        });
    }

    [Fact]
    public async Task Empty_search_focuses_the_result_list_shell()
    {
        await RunOnStaThreadAsync(async () =>
        {
            var source = new ControlledBrowseSource { SearchResults = [] };
            using var host = await TestApplicationHost.StartAsync(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IMusicBrowseSource>(source));
            });
            var window = host.Services.GetRequiredService<MainWindow>();
            try
            {
                window.Show();
                var settings = host.Services.GetRequiredService<SettingsWorkflow>();
                PumpUntil(window.Dispatcher, () => settings.Current.Version > 0);
                PumpFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
                window.SearchBox.Text = "没有结果";
                window.SearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window.Dispatcher, () => source.OperationCount == 1);

                Assert.Empty(window.SearchResultsList.Items);
                Assert.Same(window.SearchResultsList, Keyboard.FocusedElement);
            }
            finally
            {
                window.Hide();
            }
        });
    }

    [Fact]
    public async Task Search_completion_does_not_take_focus_back_after_the_user_leaves()
    {
        await RunOnStaThreadAsync(async () =>
        {
            var source = new ControlledBrowseSource
            {
                DelaySearch = true,
                SearchResults = [new() { Id = "song-1", Title = "第一首" }]
            };
            using var host = await TestApplicationHost.StartAsync(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IMusicBrowseSource>(source));
            });
            var window = host.Services.GetRequiredService<MainWindow>();
            try
            {
                window.Show();
                var settings = host.Services.GetRequiredService<SettingsWorkflow>();
                PumpUntil(window.Dispatcher, () => settings.Current.Version > 0);
                PumpFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
                window.SearchBox.Text = "测试";
                window.SearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window.Dispatcher, () => source.SearchStarted.Task.IsCompleted);

                Assert.True(window.SearchClearButton.Focus());
                source.ReleaseSearch.SetResult(null);
                PumpUntil(window.Dispatcher, () => window.SearchResultsList.Items.Count == 1);

                Assert.Same(window.SearchClearButton, Keyboard.FocusedElement);
                Assert.Equal(-1, window.SearchResultsList.SelectedIndex);
            }
            finally
            {
                window.Hide();
            }
        });
    }

    [Fact]
    public async Task Song_details_start_on_the_first_native_detail_item()
    {
        await RunOnStaThreadAsync(() =>
        {
            var window = new SongDetailDialog("测试歌曲", ["歌名：测试歌曲", "歌手：测试歌手"]);
            try
            {
                window.Show();
                Pump(window.Dispatcher);

                var first = Assert.IsType<ListBoxItem>(
                    window.DetailsList.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Equal(0, window.DetailsList.SelectedIndex);
                Assert.Same(first, Keyboard.FocusedElement);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Main_browse_hierarchies_focus_native_first_items_without_extra_operations()
    {
        await RunOnStaThreadAsync(async () =>
        {
            var source = new ControlledBrowseSource();
            using var host = await TestApplicationHost.StartAsync(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IMusicBrowseSource>(source));
            });
            var window = host.Services.GetRequiredService<MainWindow>();
            try
            {
                window.Show();
                var settings = host.Services.GetRequiredService<SettingsWorkflow>();
                PumpUntil(window.Dispatcher, () => settings.Current.Version > 0);
                PumpFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));

                window.MainTabControl.SelectedIndex = 2;
                window.CategoryRefreshBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window.Dispatcher, () => window.CategoryList.Items.Count == 1);
                var category = AssertFocusedFirst(window.CategoryList);
                RaiseKey(category, Key.Enter);
                PumpUntil(window.Dispatcher, () => window.CategoryPlaylistsListBox.Items.Count == 1);
                var playlist = AssertFocusedFirst(window.CategoryPlaylistsListBox);
                RaiseKey(playlist, Key.Enter);
                PumpUntil(window.Dispatcher, () => window.CategorySongsList.Items.Count == 1);
                AssertFocusedFirst(window.CategorySongsList);

                window.MainTabControl.SelectedIndex = 3;
                window.RefreshRankingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window.Dispatcher, () => window.RankingList.Items.Count == 1);
                var ranking = AssertFocusedFirst(window.RankingList);
                RaiseKey(ranking, Key.Enter);
                PumpUntil(window.Dispatcher, () => window.RankingSongsList.Items.Count == 1);
                AssertFocusedFirst(window.RankingSongsList);

                window.FavoriteSongsList.ItemsSource = source.SearchResults;
                window.MainTabControl.SelectedIndex = 1;
                Pump(window.Dispatcher);
                AssertFocusedFirst(window.FavoriteSongsList);
                Assert.Equal(5, source.OperationCount);
            }
            finally
            {
                window.Hide();
            }
        });
    }

    private static ListBoxItem AssertFocusedFirst(ListBox list)
    {
        var first = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.Equal(0, list.SelectedIndex);
        Assert.Same(first, Keyboard.FocusedElement);
        return first;
    }

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

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> condition, Func<string>? diagnostic = null)
    {
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            dispatcher);
        var poll = new DispatcherTimer(
            TimeSpan.FromMilliseconds(10),
            DispatcherPriority.Background,
            (_, _) => { if (condition()) frame.Continue = false; },
            dispatcher);
        timeout.Start();
        poll.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        poll.Stop();
        Assert.True(condition(), diagnostic?.Invoke());
        Pump(dispatcher);
    }

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

    private static void PumpFor(Dispatcher dispatcher, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Background, (_, _) => frame.Continue = false, dispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static Task RunOnStaThreadAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await action();
                    completion.SetResult(null);
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class ControlledBrowseSource : IMusicBrowseSource
    {
        public List<MusicInfo> SearchResults { get; init; } =
            [new() { Id = "song-1", Title = "第一首", Artist = "歌手" }];
        public bool DelaySearch { get; init; }
        public int OperationCount { get; private set; }
        public TaskCompletionSource<object?> SearchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object?> ReleaseSearch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PagedResult<MusicInfo>> SearchAsync(
            string query,
            int page,
            int pageSize,
            string searchType,
            string sourceId,
            CancellationToken cancellationToken)
        {
            OperationCount++;
            SearchStarted.TrySetResult(null);
            if (DelaySearch) await ReleaseSearch.Task.WaitAsync(cancellationToken);
            return new PagedResult<MusicInfo>
            {
                Items = SearchResults,
                CurrentPage = page,
                PageSize = pageSize,
                TotalItems = SearchResults.Count
            };
        }

        public Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId, CancellationToken cancellationToken) =>
            Count(new List<PlaylistCategory>
                { new() { Id = "category", Name = "分类", Tags = [new() { Id = "tag", Name = "标签" }] } });
        public Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page, int pageSize, string sourceId, CancellationToken cancellationToken) =>
            Count(Page(new List<Playlist> { new() { Id = "playlist", Name = "歌单" } }, page, pageSize));
        public Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page, int pageSize, CancellationToken cancellationToken) =>
            Count(Page(SearchResults, page, pageSize));
        public Task<List<Ranking>> GetRankingsAsync(string sourceId, CancellationToken cancellationToken) =>
            Count(new List<Ranking> { new() { Id = "ranking", Name = "榜单" } });
        public Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page, int pageSize, CancellationToken cancellationToken) =>
            Count(Page(SearchResults, page, pageSize));

        private Task<T> Count<T>(T value)
        {
            OperationCount++;
            return Task.FromResult(value);
        }

        private static PagedResult<T> Page<T>(List<T> items, int page, int pageSize) => new()
        {
            Items = items,
            CurrentPage = page,
            PageSize = pageSize,
            TotalItems = items.Count
        };
    }
}
