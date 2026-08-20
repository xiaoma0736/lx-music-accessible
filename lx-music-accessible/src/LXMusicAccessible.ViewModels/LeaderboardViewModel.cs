using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace LXMusicAccessible.ViewModels;

/// <summary>
/// 排行榜视图模型
/// </summary>
public partial class LeaderboardViewModel : ObservableObject
{
    private readonly IMusicSourceService _musicSourceService;
    private readonly IAccessibilityService _accessibilityService;
    private readonly IMusicPlayerService _playerService;
    private readonly MusicSourceSelector _sourceSelector;

    private bool _isLoading = false;
    private string _status = "正在加载排行榜...";
    private int _currentPage = 1;
    private int _pageSize = 50;
    private int _totalItems = 0;
    private Ranking? _selectedRanking;
    private MusicSourceOption? _selectedSource;

    public LeaderboardViewModel(
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
        
        LoadRankingsAsync();
    }

    private void InitializeCommands()
    {
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        LoadRankingDetailCommand = new AsyncRelayCommand<Ranking>(ExecuteLoadRankingDetailAsync);
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

    public Ranking? SelectedRanking
    {
        get => _selectedRanking;
        set
        {
            if (SetProperty(ref _selectedRanking, value) && value != null)
            {
                _ = ExecuteLoadRankingDetailAsync(value);
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
                _ = LoadRankingsAsync();
            }
        }
    }

    public ObservableCollection<Ranking> Rankings { get; } = new();
    public ObservableCollection<MusicInfo> RankingSongs { get; } = new();
    public ObservableCollection<MusicSourceOption> AvailableSources => _sourceSelector.AvailableSources;

    // 计算属性
    public bool CanExecuteNextPage => CurrentPage < TotalPages && !IsLoading && SelectedRanking != null;
    public bool CanExecutePreviousPage => CurrentPage > 1 && !IsLoading && SelectedRanking != null;
    public int TotalPages => (int)Math.Ceiling((double)TotalItems / PageSize);

    // 命令
    public IAsyncRelayCommand RefreshCommand { get; private set; } = null!;
    public IAsyncRelayCommand<Ranking> LoadRankingDetailCommand { get; private set; } = null!;
    public IRelayCommand NextPageCommand { get; private set; } = null!;
    public IRelayCommand PreviousPageCommand { get; private set; } = null!;
    public IRelayCommand<MusicInfo> PlayCommand { get; private set; } = null!;
    public IRelayCommand<MusicSourceOption> SwitchSourceCommand { get; private set; } = null!;

    // 命令实现
    private async Task ExecuteRefreshAsync()
    {
        await LoadRankingsAsync();
        _accessibilityService.Announce("排行榜已刷新");
    }

    private async Task ExecuteLoadRankingDetailAsync(Ranking? ranking)
    {
        if (ranking == null) return;

        IsLoading = true;
        Status = $"正在加载 {ranking.Name}...";
        CurrentPage = 1;

        _accessibilityService.Announce($"正在加载 {ranking.Name}");

        try
        {
            var result = await _musicSourceService.GetRankingDetailAsync(ranking.Id, CurrentPage, PageSize);
            
            RankingSongs.Clear();
            foreach (var item in result.Items)
            {
                RankingSongs.Add(item);
            }

            TotalItems = result.TotalItems;
            
            if (result.TotalItems == 0)
            {
                Status = $"排行榜 {ranking.Name} 暂无歌曲";
                _accessibilityService.Announce($"排行榜 {ranking.Name} 暂无歌曲");
            }
            else
            {
                Status = $"{ranking.Name} 共 {TotalItems} 首歌曲，第 {CurrentPage} 页，共 {TotalPages} 页";
                _accessibilityService.Announce($"已加载 {ranking.Name}，共 {TotalItems} 首歌曲");
            }
        }
        catch (Exception ex)
        {
            Status = $"加载排行榜 {ranking.Name} 失败，请检查网络连接";
            _accessibilityService.Announce($"加载排行榜 {ranking.Name} 失败，请检查网络连接");
            System.Diagnostics.Debug.WriteLine($"加载排行榜详情失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ExecuteNextPage()
    {
        if (SelectedRanking == null) return;
        
        CurrentPage++;
        Task.Run(async () => await ExecuteLoadRankingDetailAsync(SelectedRanking));
    }

    private void ExecutePreviousPage()
    {
        if (SelectedRanking == null) return;
        
        CurrentPage--;
        Task.Run(async () => await ExecuteLoadRankingDetailAsync(SelectedRanking));
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
    private async Task LoadRankingsAsync()
    {
        IsLoading = true;
        Status = "正在加载排行榜...";

        try
        {
            var sourceId = SelectedSource?.Id ?? "netease";
            var rankings = await _musicSourceService.GetRankingsAsync(sourceId);
            
            Rankings.Clear();
            foreach (var ranking in rankings)
            {
                Rankings.Add(ranking);
            }

            if (rankings.Count == 0)
            {
                Status = "暂无排行榜";
                _accessibilityService.Announce("暂无排行榜");
            }
            else
            {
                Status = $"已加载 {rankings.Count} 个排行榜";
                _accessibilityService.Announce($"已加载 {rankings.Count} 个排行榜");
            }
        }
        catch (Exception ex)
        {
            Status = "加载排行榜失败，请检查网络连接";
            _accessibilityService.Announce("加载排行榜失败，请检查网络连接");
            System.Diagnostics.Debug.WriteLine($"加载排行榜失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void OnRankingSelected(Ranking ranking)
    {
        Status = $"已选择排行榜 {ranking.Name}";
        _accessibilityService.Announce($"已选择排行榜 {ranking.Name}");
    }
}