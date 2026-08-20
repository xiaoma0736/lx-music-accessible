using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace LXMusicAccessible.ViewModels;

/// <summary>
/// 分类歌单视图模型
/// </summary>
public partial class PlaylistViewModel : ObservableObject
{
    private readonly IMusicSourceService _musicSourceService;
    private readonly IAccessibilityService _accessibilityService;
    private readonly IMusicPlayerService _playerService;
    private readonly MusicSourceSelector _sourceSelector;

    private bool _isLoading = false;
    private string _status = "正在加载歌单...";
    private int _currentPage = 1;
    private int _pageSize = 20;
    private int _totalItems = 0;
    private PlaylistCategory? _selectedCategory;
    private PlaylistTag? _selectedTag;
    private Playlist? _selectedPlaylist;
    private MusicSourceOption? _selectedSource;

    public PlaylistViewModel(
        IMusicSourceService musicSourceService,
        IAccessibilityService accessibilityService,
        IMusicPlayerService playerService)
    {
        _musicSourceService = musicSourceService;
        _accessibilityService = accessibilityService;
        _playerService = playerService;
        _sourceSelector = new MusicSourceSelector();

        InitializeCommands();
        
        // 默认选择第一个音源
        SelectedSource = _sourceSelector.AvailableSources.FirstOrDefault();
        
        LoadCategoriesAsync();
    }

    private void InitializeCommands()
    {
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        LoadPlaylistsByCategoryCommand = new AsyncRelayCommand<string>(ExecuteLoadPlaylistsByCategoryAsync);
        LoadPlaylistDetailCommand = new AsyncRelayCommand<Playlist>(ExecuteLoadPlaylistDetailAsync);
        NextPageCommand = new RelayCommand(ExecuteNextPage, () => CanExecuteNextPage);
        PreviousPageCommand = new RelayCommand(ExecutePreviousPage, () => CanExecutePreviousPage);
        PlayCommand = new RelayCommand<MusicInfo>(ExecutePlay);
        SwitchSourceCommand = new RelayCommand<MusicSourceOption>(ExecuteSwitchSource);
    }

    // 属性
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public int CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    public int PageSize
    {
        get => _pageSize;
        set => SetProperty(ref _pageSize, value);
    }

    public int TotalItems
    {
        get => _totalItems;
        set => SetProperty(ref _totalItems, value);
    }

    public PlaylistCategory? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value) && value != null)
            {
                _ = ExecuteLoadPlaylistsByCategoryAsync(value.Tags.FirstOrDefault()?.Id ?? "all");
            }
        }
    }

    public PlaylistTag? SelectedTag
    {
        get => _selectedTag;
        set
        {
            if (SetProperty(ref _selectedTag, value) && value != null)
            {
                _ = ExecuteLoadPlaylistsByCategoryAsync(value.Id);
            }
        }
    }

    public Playlist? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            if (SetProperty(ref _selectedPlaylist, value) && value != null)
            {
                _ = ExecuteLoadPlaylistDetailAsync(value);
            }
        }
    }

    public MusicSourceOption? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value) && value != null)
            {
                _ = LoadCategoriesAsync();
            }
        }
    }

    public ObservableCollection<PlaylistCategory> Categories { get; } = new();
    public ObservableCollection<Playlist> Playlists { get; } = new();
    public ObservableCollection<MusicInfo> PlaylistSongs { get; } = new();
    public ObservableCollection<MusicSourceOption> AvailableSources => _sourceSelector.AvailableSources;

    // 计算属性
    public bool CanExecuteNextPage => CurrentPage < TotalPages && !IsLoading && SelectedPlaylist != null;
    public bool CanExecutePreviousPage => CurrentPage > 1 && !IsLoading && SelectedPlaylist != null;
    public int TotalPages => (int)Math.Ceiling((double)TotalItems / PageSize);

    // 命令
    public IAsyncRelayCommand RefreshCommand { get; private set; } = null!;
    public IAsyncRelayCommand<string> LoadPlaylistsByCategoryCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Playlist> LoadPlaylistDetailCommand { get; private set; } = null!;
    public IRelayCommand NextPageCommand { get; private set; } = null!;
    public IRelayCommand PreviousPageCommand { get; private set; } = null!;
    public IRelayCommand<MusicInfo> PlayCommand { get; private set; } = null!;
    public IRelayCommand<MusicSourceOption> SwitchSourceCommand { get; private set; } = null!;

    // 命令实现
    private async Task ExecuteRefreshAsync()
    {
        await LoadCategoriesAsync();
        _accessibilityService.Announce("歌单已刷新");
    }

    private async Task ExecuteLoadPlaylistsByCategoryAsync(string? tagId)
    {
        if (string.IsNullOrEmpty(tagId)) return;

        IsLoading = true;
        Status = "正在加载歌单列表...";
        CurrentPage = 1;

        _accessibilityService.Announce($"正在加载歌单");

        try
        {
            var sourceId = SelectedSource?.Id ?? "netease";
            var result = await _musicSourceService.GetPlaylistsByCategoryAsync(tagId, CurrentPage, PageSize, sourceId);
            
            Playlists.Clear();
            foreach (var item in result.Items)
            {
                Playlists.Add(item);
            }

            TotalItems = result.TotalItems;
            
            if (result.TotalItems == 0)
            {
                Status = "该分类暂无歌单";
                _accessibilityService.Announce("该分类暂无歌单");
            }
            else
            {
                Status = $"共 {TotalItems} 个歌单，第 {CurrentPage} 页，共 {TotalPages} 页";
                _accessibilityService.Announce($"找到 {TotalItems} 个歌单");
            }
        }
        catch (Exception ex)
        {
            Status = "加载歌单失败，请检查网络连接";
            _accessibilityService.Announce("加载歌单失败，请检查网络连接");
            System.Diagnostics.Debug.WriteLine($"加载歌单失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ExecuteLoadPlaylistDetailAsync(Playlist? playlist)
    {
        if (playlist == null) return;

        IsLoading = true;
        Status = $"正在加载 {playlist.Name}...";
        CurrentPage = 1;

        _accessibilityService.Announce($"正在加载歌单 {playlist.Name}");

        try
        {
            var result = await _musicSourceService.GetPlaylistDetailAsync(playlist.Id, CurrentPage, PageSize);
            
            PlaylistSongs.Clear();
            foreach (var item in result.Items)
            {
                PlaylistSongs.Add(item);
            }

            TotalItems = result.TotalItems;
            
            if (result.TotalItems == 0)
            {
                Status = $"歌单 {playlist.Name} 暂无歌曲";
                _accessibilityService.Announce($"歌单 {playlist.Name} 暂无歌曲");
            }
            else
            {
                Status = $"{playlist.Name} 共 {TotalItems} 首歌曲，第 {CurrentPage} 页，共 {TotalPages} 页";
                _accessibilityService.Announce($"已加载 {playlist.Name}，共 {TotalItems} 首歌曲");
            }
        }
        catch (Exception ex)
        {
            Status = $"加载歌单 {playlist.Name} 失败，请检查网络连接";
            _accessibilityService.Announce($"加载歌单 {playlist.Name} 失败，请检查网络连接");
            System.Diagnostics.Debug.WriteLine($"加载歌单详情失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ExecuteNextPage()
    {
        if (SelectedPlaylist == null) return;
        
        CurrentPage++;
        Task.Run(async () => await ExecuteLoadPlaylistDetailAsync(SelectedPlaylist));
    }

    private void ExecutePreviousPage()
    {
        if (SelectedPlaylist == null) return;
        
        CurrentPage--;
        Task.Run(async () => await ExecuteLoadPlaylistDetailAsync(SelectedPlaylist));
    }

    private async void ExecutePlay(MusicInfo? music)
    {
        if (music == null) return;

        _accessibilityService.Announce($"正在播放: {music.Title} - {music.Artist}");
        
        try
        {
            var playUrl = await _musicSourceService.GetPlayUrlAsync(music, music.Source);
            
            if (string.IsNullOrEmpty(playUrl))
            {
                _accessibilityService.Announce("获取播放链接失败");
                return;
            }

            music.PlayUrl = playUrl;
            await _playerService.PlayAsync(playUrl, music);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"播放失败: {ex.Message}");
            _accessibilityService.Announce("播放失败，请检查网络连接");
        }
    }

    private void ExecuteSwitchSource(MusicSourceOption? source)
    {
        if (source == null) return;
        
        SelectedSource = source;
        Status = $"已切换到 {source.Name}";
        _accessibilityService.Announce($"已切换到 {source.Name}");
    }

    // 私有方法
    private async Task LoadCategoriesAsync()
    {
        IsLoading = true;
        Status = "正在加载歌单分类...";

        try
        {
            var sourceId = SelectedSource?.Id ?? "netease";
            var categories = await _musicSourceService.GetPlaylistCategoriesAsync(sourceId);
            
            Categories.Clear();
            foreach (var category in categories)
            {
                Categories.Add(category);
            }

            if (categories.Count == 0)
            {
                Status = "暂无歌单分类";
                _accessibilityService.Announce("暂无歌单分类");
            }
            else
            {
                Status = $"已加载 {categories.Count} 个分类";
                _accessibilityService.Announce($"已加载 {categories.Count} 个歌单分类");
            }
        }
        catch (Exception ex)
        {
            Status = "加载歌单分类失败，请检查网络连接";
            _accessibilityService.Announce("加载歌单分类失败，请检查网络连接");
            System.Diagnostics.Debug.WriteLine($"加载歌单分类失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void OnCategorySelected(PlaylistCategory category)
    {
        Status = $"已选择分类 {category.Name}";
        _accessibilityService.Announce($"已选择分类 {category.Name}");
    }

    public void OnPlaylistSelected(Playlist playlist)
    {
        Status = $"已选择歌单 {playlist.Name}";
        _accessibilityService.Announce($"已选择歌单 {playlist.Name}");
    }
}