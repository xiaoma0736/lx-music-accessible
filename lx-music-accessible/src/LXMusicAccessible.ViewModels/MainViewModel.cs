using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Data;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace LXMusicAccessible.ViewModels;

/// <summary>
/// 主界面视图模型
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IAccessibilityService _accessibilityService;
    private readonly IMusicPlayerService _playerService;
    private readonly IMusicSourceService _musicSourceService;
    private readonly IDataService _dataService;
    private readonly IHotkeyManager _hotkeyManager;
    private readonly ITtsService _ttsService;

    private string _statusText = "就绪";
    private string _currentTime = "";
    private string _searchQuery = "";
    private bool _isLoopMode = false;
    private bool _isShuffleMode = false;
    private bool _autoPlay = true;
    private double _volume = 80;
    private double _position = 0;
    private double _duration = 0;
    private string _currentTitle = "未播放";
    private string _currentArtist = "";
    private string _currentAlbum = "";
    private double _previousVolume = 80;

    public MainViewModel(
        IAccessibilityService accessibilityService,
        IMusicPlayerService playerService,
        IMusicSourceService musicSourceService,
        IDataService dataService,
        IHotkeyManager hotkeyManager,
        ITtsService ttsService)
    {
        _accessibilityService = accessibilityService;
        _playerService = playerService;
        _musicSourceService = musicSourceService;
        _dataService = dataService;
        _hotkeyManager = hotkeyManager;
        _ttsService = ttsService;

        // 订阅播放器事件
        _playerService.StateChanged += PlayerService_StateChanged;

        // 初始化命令
        InitializeCommands();

        // 初始化快捷键
        InitializeHotkeys();

        // 加载设置
        Task.Run(async () => await LoadSettingsAsync());
    }

    private void PlayerService_StateChanged(object? sender, PlayerStateChangedEventArgs e)
    {
        if (Application.Current != null && Application.Current.Dispatcher != null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(IsPlaying));
                StatusText = GetStatusText(e.NewState);
                _accessibilityService.Announce(StatusText);
            });
        }
    }

    private string GetStatusText(PlayerState state) => state switch
    {
        Models.PlayerState.Playing => "正在播放",
        Models.PlayerState.Paused => "已暂停",
        Models.PlayerState.Stopped => "已停止",
        Models.PlayerState.Loading => "正在加载",
        Models.PlayerState.Error => "播放错误",
        _ => "未知状态"
    };

        // 计算属性
    public bool CanExecuteSearch => !string.IsNullOrWhiteSpace(SearchQuery);

    // 命令初始化
    private void InitializeCommands()
    {
        PlayPauseCommand = new AsyncRelayCommand(ExecutePlayPauseAsync);
        StopCommand = new RelayCommand(ExecuteStop);
        NextCommand = new RelayCommand(ExecuteNext);
        PreviousCommand = new RelayCommand(ExecutePrevious);
        SearchCommand = new AsyncRelayCommand(ExecuteSearchAsync);
        VolumeUpCommand = new RelayCommand(ExecuteVolumeUp);
        VolumeDownCommand = new RelayCommand(ExecuteVolumeDown);
        MuteCommand = new RelayCommand(ExecuteMute);
    }

    // 快捷键初始化
    private void InitializeHotkeys()
    {
        var hotkeys = new List<Hotkey>
        {
            new Hotkey { Id = "play-pause", Name = "播放/暂停", Key = Key.Space, Modifiers = ModifierKeys.Control, Command = "PlayPause", IsGlobal = false, IsEnabled = true },
            new Hotkey { Id = "stop", Name = "停止", Key = Key.S, Modifiers = ModifierKeys.Control, Command = "Stop", IsGlobal = false, IsEnabled = true },
            new Hotkey { Id = "next", Name = "下一首", Key = Key.Right, Modifiers = ModifierKeys.Control, Command = "Next", IsGlobal = false, IsEnabled = true },
            new Hotkey { Id = "previous", Name = "上一首", Key = Key.Left, Modifiers = ModifierKeys.Control, Command = "Previous", IsGlobal = false, IsEnabled = true },
            new Hotkey { Id = "volume-up", Name = "音量增加", Key = Key.Up, Modifiers = ModifierKeys.Control, Command = "VolumeUp", IsGlobal = false, IsEnabled = true },
            new Hotkey { Id = "volume-down", Name = "音量减少", Key = Key.Down, Modifiers = ModifierKeys.Control, Command = "VolumeDown", IsGlobal = false, IsEnabled = true },
            new Hotkey { Id = "mute", Name = "静音", Key = Key.M, Modifiers = ModifierKeys.Control, Command = "Mute", IsGlobal = false, IsEnabled = true },
            new Hotkey { Id = "search", Name = "搜索", Key = Key.F, Modifiers = ModifierKeys.Control, Command = "Search", IsGlobal = false, IsEnabled = true }
        };

        foreach (var hotkey in hotkeys)
        {
            _hotkeyManager.RegisterHotkey(hotkey);
        }
    }

    // 属性
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string CurrentTime
    {
        get => _currentTime;
        set => SetProperty(ref _currentTime, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                OnPropertyChanged(nameof(CanExecuteSearch));
            }
        }
    }

    public bool IsLoopMode
    {
        get => _isLoopMode;
        set => SetProperty(ref _isLoopMode, value);
    }

    public bool IsShuffleMode
    {
        get => _isShuffleMode;
        set => SetProperty(ref _isShuffleMode, value);
    }

    public bool AutoPlay
    {
        get => _autoPlay;
        set => SetProperty(ref _autoPlay, value);
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
            {
                _playerService.SetVolume(value / 100);
                _accessibilityService.Announce($"音量设置为 {(int)value}%");
            }
        }
    }

    public double Position
    {
        get => _position;
        set
        {
            if (SetProperty(ref _position, value))
            {
                _playerService.SetPosition(TimeSpan.FromSeconds(value));
            }
        }
    }

    public double Duration
    {
        get => _duration;
        set => SetProperty(ref _duration, value);
    }

    public string CurrentTitle
    {
        get => _currentTitle;
        set => SetProperty(ref _currentTitle, value);
    }

    public string CurrentArtist
    {
        get => _currentArtist;
        set => SetProperty(ref _currentArtist, value);
    }

    public string CurrentAlbum
    {
        get => _currentAlbum;
        set => SetProperty(ref _currentAlbum, value);
    }

    public bool IsPlaying => _playerService.IsPlaying;

    public ObservableCollection<MusicInfo> SearchResults { get; } = new();
    public ObservableCollection<Playlist> Playlists { get; } = new();
    public ObservableCollection<MusicInfo> CurrentPlaylistItems { get; } = new();
    public ObservableCollection<MusicInfo> PlayHistory { get; } = new();

    // 命令
    public IAsyncRelayCommand PlayPauseCommand { get; private set; } = null!;
    public IRelayCommand StopCommand { get; private set; } = null!;
    public IRelayCommand NextCommand { get; private set; } = null!;
    public IRelayCommand PreviousCommand { get; private set; } = null!;
    public IAsyncRelayCommand SearchCommand { get; private set; } = null!;
    public IRelayCommand VolumeUpCommand { get; private set; } = null!;
    public IRelayCommand VolumeDownCommand { get; private set; } = null!;
    public IRelayCommand MuteCommand { get; private set; } = null!;

    // 命令实现
    private async Task ExecutePlayPauseAsync()
    {
        if (IsPlaying)
        {
            _playerService.Pause();
        }
        else
        {
            if (CurrentPlaylistItems.Count > 0)
            {
                var currentMusic = _playerService.CurrentMusic;
                if (currentMusic != null)
                {
                    await _playerService.PlayAsync(currentMusic.PlayUrl ?? "", currentMusic);
                }
            }
        }
    }

    private void ExecuteStop()
    {
        _playerService.Stop();
    }

    private void ExecuteNext()
    {
        _playerService.Next();
    }

    private void ExecutePrevious()
    {
        _playerService.Previous();
    }

    private async Task ExecuteSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            _accessibilityService.Announce("请输入搜索关键词");
            return;
        }

        _accessibilityService.Announce($"正在搜索: {SearchQuery}");
        StatusText = "正在搜索...";

        try
        {
            var result = await _musicSourceService.SearchAsync(SearchQuery);
            
            SearchResults.Clear();
            foreach (var item in result.Items)
            {
                SearchResults.Add(item);
            }

            StatusText = $"找到 {result.TotalItems} 首歌曲";
            _accessibilityService.Announce(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = "搜索失败";
            _accessibilityService.Announce("搜索失败");
            System.Diagnostics.Debug.WriteLine($"搜索失败: {ex.Message}");
        }
    }

    private void ExecuteVolumeUp()
    {
        Volume = Math.Min(100, Volume + 10);
    }

    private void ExecuteVolumeDown()
    {
        Volume = Math.Max(0, Volume - 10);
    }

    private void ExecuteMute()
    {
        if (Volume > 0)
        {
            _previousVolume = Volume;
            Volume = 0;
        }
        else
        {
            Volume = _previousVolume > 0 ? _previousVolume : 80;
        }
    }

    // 方法
    public async Task LoadSettingsAsync()
    {
        try
        {
            var settings = await _dataService.GetSettingsAsync();
            if (settings != null)
            {
                AutoPlay = settings.AutoPlay;
                IsLoopMode = settings.LoopMode;
                IsShuffleMode = settings.ShuffleMode;
                Volume = settings.DefaultVolume * 100;
                await LoadPlaylistsAsync();
                await LoadPlayHistoryAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载设置失败: {ex.Message}");
        }
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            var settings = new UserSettings
            {
                DefaultVolume = Volume / 100,
                AutoPlay = AutoPlay,
                LoopMode = IsLoopMode,
                ShuffleMode = IsShuffleMode,
                WindowWidth = 1200,
                WindowHeight = 800,
                EnableScreenReader = true,
                EnableVoiceAnnouncement = true
            };

            await _dataService.SaveSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存设置失败: {ex.Message}");
        }
    }

    public async Task LoadPlaylistsAsync()
    {
        try
        {
            var playlists = await _dataService.GetPlaylistsAsync();
            Playlists.Clear();
            foreach (var playlist in playlists)
            {
                Playlists.Add(playlist);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载播放列表失败: {ex.Message}");
        }
    }

    public async Task LoadPlayHistoryAsync()
    {
        try
        {
            var history = await _dataService.GetPlayHistoryAsync(50);
            PlayHistory.Clear();
            foreach (var music in history)
            {
                PlayHistory.Add(music);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载播放历史失败: {ex.Message}");
        }
    }

    public void PlayMusic(MusicInfo music)
    {
        Task.Run(async () =>
        {
            await _playerService.PlayAsync(music.PlayUrl ?? "", music);
            CurrentTitle = music.Title;
            CurrentArtist = music.Artist;
            CurrentAlbum = music.Album;
        });
    }

    public void AddToPlaylist(MusicInfo music)
    {
        CurrentPlaylistItems.Add(music);
        _accessibilityService.Announce($"已添加到播放列表: {music.Title}");
    }

    public void PlayFromPlaylist(int index)
    {
        if (index >= 0 && index < CurrentPlaylistItems.Count)
        {
            PlayMusic(CurrentPlaylistItems[index]);
        }
    }

    public void ShufflePlaylist()
    {
        var random = new Random();
        var n = CurrentPlaylistItems.Count;
        while (n > 1)
        {
            n--;
            var k = random.Next(n + 1);
            (CurrentPlaylistItems[k], CurrentPlaylistItems[n]) = (CurrentPlaylistItems[n], CurrentPlaylistItems[k]);
        }
        _accessibilityService.Announce("播放列表已随机打乱");
    }
}

