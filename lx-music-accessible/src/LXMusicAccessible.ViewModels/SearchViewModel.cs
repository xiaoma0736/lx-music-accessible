using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace LXMusicAccessible.ViewModels;

/// <summary>
/// 搜索界面视图模型
/// </summary>
public partial class SearchViewModel : ObservableObject
{
    private readonly IMusicSourceService _musicSourceService;
    private readonly IAccessibilityService _accessibilityService;
    private readonly IMusicPlayerService _playerService;

    private string _searchQuery = "";
    private bool _isLoading = false;
    private string _searchStatus = "请输入搜索关键词";
    private int _currentPage = 1;
    private int _pageSize = 20;
    private int _totalItems = 0;

    public SearchViewModel(
        IMusicSourceService musicSourceService,
        IAccessibilityService accessibilityService,
        IMusicPlayerService playerService)
    {
        _musicSourceService = musicSourceService;
        _accessibilityService = accessibilityService;
        _playerService = playerService;

        InitializeCommands();
    }

    private void InitializeCommands()
    {
        SearchCommand = new AsyncRelayCommand(ExecuteSearchAsync, () => CanExecuteSearchAsync);
        NextPageCommand = new RelayCommand(ExecuteNextPage, () => CanExecuteNextPage);
        PreviousPageCommand = new RelayCommand(ExecutePreviousPage, () => CanExecutePreviousPage);
        ClearCommand = new RelayCommand(ExecuteClear);
        PlayCommand = new RelayCommand<MusicInfo>(ExecutePlay);
    }

    // 属性
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                OnPropertyChanged(nameof(CanExecuteSearchAsync));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string SearchStatus
    {
        get => _searchStatus;
        set => SetProperty(ref _searchStatus, value);
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

    public ObservableCollection<MusicInfo> SearchResults { get; } = new();

    // 计算属性
    public bool CanExecuteSearchAsync => !string.IsNullOrWhiteSpace(SearchQuery) && !IsLoading;
    public bool CanExecuteNextPage => CurrentPage < TotalPages && !IsLoading;
    public bool CanExecutePreviousPage => CurrentPage > 1 && !IsLoading;
    public int TotalPages => (int)Math.Ceiling((double)TotalItems / PageSize);

    // 命令
    public IAsyncRelayCommand SearchCommand { get; private set; } = null!;
    public IRelayCommand NextPageCommand { get; private set; } = null!;
    public IRelayCommand PreviousPageCommand { get; private set; } = null!;
    public IRelayCommand ClearCommand { get; private set; } = null!;
    public IRelayCommand<MusicInfo> PlayCommand { get; private set; } = null!;

    // 命令实现
    private async Task ExecuteSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            _accessibilityService.Announce("请输入搜索关键词");
            return;
        }

        IsLoading = true;
        SearchStatus = "正在搜索...";
        CurrentPage = 1;

        _accessibilityService.Announce($"正在搜索: {SearchQuery}");

        try
        {
            var result = await _musicSourceService.SearchAsync(SearchQuery, CurrentPage, PageSize);
            
            SearchResults.Clear();
            foreach (var item in result.Items)
            {
                SearchResults.Add(item);
            }

            TotalItems = result.TotalItems;
            
            if (result.TotalItems == 0)
            {
                SearchStatus = $"未找到相关结果";
                _accessibilityService.Announce("未找到相关结果");
            }
            else
            {
                SearchStatus = $"找到 {TotalItems} 首歌曲，第 {CurrentPage} 页，共 {TotalPages} 页";
                _accessibilityService.Announce(SearchStatus);
            }
        }
        catch (Exception ex)
        {
            SearchStatus = "搜索失败，请检查网络连接";
            _accessibilityService.Announce("搜索失败，请检查网络连接");
            System.Diagnostics.Debug.WriteLine($"搜索失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            
            // 刷新命令状态
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ExecuteNextPage()
    {
        CurrentPage++;
        Task.Run(async () => await ExecuteSearchAsync());
    }

    private void ExecutePreviousPage()
    {
        CurrentPage--;
        Task.Run(async () => await ExecuteSearchAsync());
    }

    private void ExecuteClear()
    {
        SearchQuery = "";
        SearchResults.Clear();
        SearchStatus = "请输入搜索关键词";
        TotalItems = 0;
        CurrentPage = 1;
        _accessibilityService.Announce("搜索已清空");
        
        CommandManager.InvalidateRequerySuggested();
    }

    private async void ExecutePlay(MusicInfo? music)
    {
        if (music == null) return;

        _accessibilityService.Announce($"正在播放: {music.Title} - {music.Artist}");
        
        try
        {
            // 获取播放URL
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

    public void OnSearchResultSelected(MusicInfo music)
    {
        SearchStatus = $"已选择: {music.Title} - {music.Artist}";
        _accessibilityService.Announce($"已选择: {music.Title} - {music.Artist}");
    }

    public void FocusSearchBox()
    {
        _accessibilityService.Announce("搜索框，请输入搜索关键词");
    }
}
