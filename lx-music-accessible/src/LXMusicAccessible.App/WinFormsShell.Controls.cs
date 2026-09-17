using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>Dependency seam used by the native shell and deterministic UI tests.</summary>
public sealed class WinFormsShellServices
{
    public IAccessibleAnnouncer? Announcer { get; init; }
    public IMusicBrowseSource? BrowseSource { get; init; }
    public IPlaybackCoordinator? Playback { get; init; }
    public IAudioPlaybackControl? Audio { get; init; }
    public MusicBrowseWorkflow? BrowseWorkflow { get; init; }
    public SavedPlaylistWorkflow? SavedPlaylists { get; init; }
    public LyricsWorkflow? Lyrics { get; init; }
    public IDownloadWorkflow? Downloads { get; init; }
    public SettingsWorkflow? Settings { get; init; }
    public IMusicSourceService? Sources { get; init; }
    public IManualSourceDiscoveryWorkflow? ManualSources { get; init; }
    public ApplicationWindowFactory? Windows { get; init; }
    public DesktopLifecycleWorkflow? Desktop { get; init; }
    internal AnnouncementToggleService? Announcements { get; init; }
    public string? TemporaryDirectory { get; init; }
}

/// <summary>The native WinForms main shell: MainMenu, four fixed tabs, playback panel, status and tray-owned workflows.</summary>
public partial class WinFormsShell : Form
{
    private readonly WinFormsShellServices _services;
    private readonly IAccessibleAnnouncer _announcer;
    private readonly MusicBrowseWorkflow _browse;
    private List<MusicInfo> _searchSongs = new();
    private List<MusicInfo> _categorySongs = new();
    private List<MusicInfo> _rankingSongs = new();
    private List<MusicInfo> _favoriteSongs = new();
        private PlayMode _playMode = PlayMode.Sequential;
    private MusicInfo? _current;
    private const double VolumeStep = 0.05;
    private int _lastPlaybackToggleTick;
    private const string BaseTitle = "落雪音乐无障碍版";

    protected TabControl MainTabs;
    protected ComboBox SearchSource;
    protected ComboBox SearchType;
    protected TextBox SearchText;
    protected Button SearchButton;
    protected Button SearchClearButton;
    protected ListBox SearchResults;
    protected ListBox OnlinePlaylists;
    protected ListBox FavoriteSongs;
    protected ComboBox CategorySource;
    protected ListBox CategoryList;
    protected ListBox CategoryPlaylists;
    protected ListBox CategorySongs;
    protected ComboBox RankingSource;
    protected ListBox RankingList;
    protected ListBox RankingSongs;
    protected Label StatusText;
    protected Label PlaybackInfoText;
    protected Label CurrentTitle;
    protected Label CurrentArtist;
    protected Label CurrentAlbum;
    protected Label CurrentPlayModeText;
    protected Label PositionText;
    protected Label LiveRegion;
    protected Button CategorySongsLoadMore;
    protected Button RankingSongsLoadMore;

    public WinFormsShell(WinFormsShellServices services)
    {
        _services = services;
        _announcer = services.Announcer ?? throw new ArgumentNullException(nameof(services));
        if (services.BrowseSource is null) throw new ArgumentNullException(nameof(services), "BrowseSource is required");
        _browse = services.BrowseWorkflow ?? new MusicBrowseWorkflow(
            services.BrowseSource,
            new BackgroundWorkHost(Microsoft.Extensions.Logging.Abstractions.NullLogger<BackgroundWorkHost>.Instance),
            _announcer,
            services.Playback!,
            new LocalSearchHistoryStore(System.IO.Path.Combine(services.TemporaryDirectory ?? System.IO.Path.GetTempPath(), "file-storage")),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicBrowseWorkflow>.Instance);
        Text = BaseTitle;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1120, 700);
        Size = new Size(1280, 760);
        KeyPreview = true;

        var menu = BuildMenu();
        Menu = menu;

        var status = new StatusBar { TabStop = false, TabIndex = 900, ShowPanels = false };
        StatusText = new Label { Text = "正在初始化...", AutoSize = true, TabStop = false, TabIndex = 901 };
        status.Controls.Add(StatusText);
        PlaybackInfoText = new Label { Text = "未播放", AutoSize = true, TabStop = false, TabIndex = 902, AccessibleName = "播放信息", Left = 280 };
        status.Controls.Add(PlaybackInfoText);
        var timeLabel = new Label { Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), AutoSize = true, TabStop = false, TabIndex = 903, AccessibleName = "当前时间", Left = 900 };
        status.Controls.Add(timeLabel);
        var statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        statusTimer.Tick += (_, _) => timeLabel.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        statusTimer.Start();

        MainTabs = new TabControl { Name = "MainTabs", Dock = DockStyle.Fill, TabIndex = 1, AccessibleName = "导航" };
        MainTabs.Controls.Add(CreateSearchPage());
        MainTabs.Controls.Add(CreatePlaylistPage());
        MainTabs.Controls.Add(CreateCategoryPage());
        MainTabs.Controls.Add(CreateRankingPage());
        MainTabs.SelectedIndexChanged += (_, _) =>
        {
            if (MainTabs.SelectedIndex == 1 && services.SavedPlaylists is not null && FavoriteSongs.Items.Count == 0)
                _ = RunAsync(() => services.SavedPlaylists.LoadAsync(), result => RenderSaved(result.Snapshot, result.Message), "刷新播放列表");
            else if (MainTabs.SelectedIndex == 2 && CategoryList.Items.Count == 0)
                LoadCategories();
            else if (MainTabs.SelectedIndex == 3 && RankingList.Items.Count == 0)
                LoadRankings();
        };

        CreatePlaybackState();

        LiveRegion = new Label { Text = string.Empty, Size = new Size(1, 1), TabStop = false, TabIndex = 999 };
        Controls.Add(MainTabs);
        Controls.Add(status);
        Controls.Add(LiveRegion);
        if (services.Announcer is WinFormsAccessibleAnnouncer native) native.Attach(LiveRegion);
        AssignSongContextMenu(FavoriteSongs);
        AssignSongContextMenu(CategorySongs);
        AssignSongContextMenu(RankingSongs);
        HookEvents();
        StatusText.Text = "就绪";
        Announce("欢迎使用落雪音乐无障碍版", "welcome");
        _ = _browse.LoadSearchHistoryAsync();
    }

    public Task WaitForWorkAsync() => Task.Delay(250);

    protected void Announce(string message, string? activityId = null, AnnouncementKind kind = AnnouncementKind.Status, AnnouncementSpeech speech = AnnouncementSpeech.FollowPreference)
    {
        if (!string.IsNullOrWhiteSpace(message))
            _announcer.Announce(new AccessibleAnnouncement(message, kind, activityId, speech));
    }

    private MainMenu BuildMenu()
    {
        var menu = new MainMenu();
        var file = new MenuItem("&文件");
        file.MenuItems.Add(new MenuItem("隐藏/显示窗口(&H)", (_, _) => ToggleVisibility(), Shortcut.CtrlH));
        file.MenuItems.Add(new MenuItem("打开本地文件(&O)", (_, _) => OpenLocalFile()));
        file.MenuItems.Add(new MenuItem("打开下载目录(&D)", (_, _) => _services.Downloads?.OpenDownloadDirectory()));
        file.MenuItems.Add(new MenuItem("-"));
        file.MenuItems.Add(new MenuItem("退出(&X)", (_, _) => _ = RequestExit(), Shortcut.CtrlQ));
        var play = new MenuItem("&播放");
        play.MenuItems.Add(new MenuItem("播放/暂停(&P)", (_, _) => TogglePlayback()));
        play.MenuItems.Add(new MenuItem("停止(&S)", (_, _) => StopPlayback(), Shortcut.F6));
        play.MenuItems.Add(new MenuItem("上一首(&V)", (_, _) => _ = MovePlayback(true), Shortcut.F3));
        play.MenuItems.Add(new MenuItem("下一首(&N)", (_, _) => _ = MovePlayback(false), Shortcut.F4));
        play.MenuItems.Add(new MenuItem("-"));
        play.MenuItems.Add(new MenuItem("快退十秒(&R)", (_, _) => Seek(-10), Shortcut.F7));
        play.MenuItems.Add(new MenuItem("快进十秒(&F)", (_, _) => Seek(10), Shortcut.F8));
        play.MenuItems.Add(new MenuItem("音量减(&L)", (_, _) => ChangeVolume(-VolumeStep), Shortcut.F9));
        play.MenuItems.Add(new MenuItem("音量加(&G)", (_, _) => ChangeVolume(VolumeStep)));
        play.MenuItems.Add(new MenuItem("上一个播放设备(&E)", (_, _) => SwitchDevice(-1), Shortcut.F11));
        play.MenuItems.Add(new MenuItem("下一个播放设备(&X)", (_, _) => SwitchDevice(1), Shortcut.F12));
        play.MenuItems.Add(new MenuItem("切换播放模式(&M)", (_, _) => TogglePlayMode(), Shortcut.F1));
        play.MenuItems.Add(new MenuItem("下载当前歌曲(&W)", (_, _) => DownloadSelected(), Shortcut.F2));
        var navigate = new MenuItem("&导航");
        navigate.MenuItems.Add(new MenuItem("搜索(&1)", (_, _) => SelectTab(0), Shortcut.Ctrl1));
        navigate.MenuItems.Add(new MenuItem("播放列表(&2)", (_, _) => SelectTab(1), Shortcut.Ctrl2));
        navigate.MenuItems.Add(new MenuItem("分类歌单(&3)", (_, _) => SelectTab(2), Shortcut.Ctrl3));
        navigate.MenuItems.Add(new MenuItem("排行榜(&4)", (_, _) => SelectTab(3), Shortcut.Ctrl4));
        navigate.MenuItems.Add(new MenuItem("歌词(&Y)", (_, _) => OpenLyrics()));
        var tools = new MenuItem("&工具");
        tools.MenuItems.Add(new MenuItem("下载管理(&D)", (_, _) => OpenTool(() => _services.Windows?.CreateDownloadManager(this, HandleHotkey)), Shortcut.Ctrl5));
        tools.MenuItems.Add(new MenuItem("导入个人歌单(&I)", (_, _) => OpenTool(() => _services.Windows?.CreatePlaylistImport(this, HandleHotkey, AttachSongList, DetachSongList)), Shortcut.Ctrl6));
        tools.MenuItems.Add(new MenuItem("音源管理(&M)", (_, _) => OpenTool(() => _services.Windows?.CreateSourceManager(this, HandleHotkey)), Shortcut.Ctrl7));
        tools.MenuItems.Add(new MenuItem("设置(&S)", (_, _) => ShowSettings(null), Shortcut.Ctrl8));
        tools.MenuItems.Add(new MenuItem("-"));
        tools.MenuItems.Add(new MenuItem("切换语音播报(&V)", (_, _) => _services.Announcements?.ToggleVoiceAnnouncement()));
        tools.MenuItems.Add(new MenuItem("切换歌词播报(&L)", (_, _) => _services.Announcements?.ToggleLyricAnnouncement()));
        tools.MenuItems.Add(new MenuItem("歌词延后(&J)", (_, _) => _services.Lyrics?.AdjustOffset(500)));
        tools.MenuItems.Add(new MenuItem("歌词提前(&K)", (_, _) => _services.Lyrics?.AdjustOffset(-500)));
        tools.MenuItems.Add(new MenuItem("切换音源(&C)", (_, _) => OpenManualSource()));
        var help = new MenuItem("&帮助");
        help.MenuItems.Add(new MenuItem("使用说明(&G)", (_, _) => _services.Windows?.CreateUserGuide(this).Show()));
        help.MenuItems.Add(new MenuItem("赞助(&Z)", (_, _) => _services.Windows?.CreateSponsor(this).ShowDialog(this)));
        help.MenuItems.Add(new MenuItem("关于(&A)", (_, _) => MessageBox.Show(this, "落雪音乐无障碍版 3.0\n专为盲人用户优化的音乐播放器。", "关于", MessageBoxButtons.OK, MessageBoxIcon.Information)));
        foreach (var item in new[] { file, play, navigate, tools, help })
        {
            item.OwnerDraw = false;
            menu.MenuItems.Add(item);
        }
        return menu;
    }

    private TabPage CreateSearchPage()
    {
        var page = new TabPage("搜索") { AccessibleName = "搜索", Padding = new Padding(10), AutoScroll = true };
        var sourceLabel = WinFormsUi.Caption("音源：", 1);
        SearchSource = WinFormsUi.Combo("搜索音源", 2, "全部音源", "网易云音乐", "QQ音乐", "酷狗音乐", "酷我音乐", "咪咕音乐");
        var typeLabel = WinFormsUi.Caption("类型：", 3);
        SearchType = WinFormsUi.Combo("搜索类型", 4, "歌曲", "歌手", "专辑");
        SearchText = new TextBox { Width = 300 };
        WinFormsUi.Name(SearchText, "搜索关键词", 5);
        SearchButton = new Button { Text = "搜索", Width = 90 };
        WinFormsUi.Name(SearchButton, "搜索", 6);
        SearchClearButton = new Button { Text = "清空", Width = 80 };
        WinFormsUi.Name(SearchClearButton, "清空搜索", 7);
        SearchResults = WinFormsUi.ListBox("搜索结果", 8);
        SearchResults.Size = new Size(830, 450);
        var previous = new Button { Text = "上一页", Width = 80, TabStop = false, TabIndex = 9 };
        var next = new Button { Text = "加载更多歌曲", Width = 150, TabStop = false, TabIndex = 10, Name = "SearchLoadMore", AccessibleName = "加载更多歌曲" };
        var pageInfo = new Label { Text = "第 1 页", AutoSize = true, TabStop = false, TabIndex = 11, AccessibleName = "搜索页码" };
        sourceLabel.SetBounds(10, 14, 0, 0);
        SearchSource.SetBounds(65, 10, 150, 24);
        typeLabel.SetBounds(225, 14, 0, 0);
        SearchType.SetBounds(265, 10, 100, 24);
        SearchText.SetBounds(10, 44, 300, 24);
        SearchButton.SetBounds(320, 43, 90, 26);
        SearchClearButton.SetBounds(420, 43, 80, 26);
        SearchResults.SetBounds(10, 78, 830, 450);
        previous.Visible = false;
        pageInfo.Visible = false;
        next.SetBounds(340, 536, 160, 26);
        page.Controls.AddRange(new Control[] { sourceLabel, SearchSource, typeLabel, SearchType, SearchText, SearchButton, SearchClearButton, SearchResults, previous, pageInfo, next });
        SearchButton.Click += (_, _) => _ = SearchAsync(true);
        SearchClearButton.Click += (_, _) => ClearSearch();
        SearchText.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Up) ApplyHistory(_browse.NavigateSearchHistory(SearchText.Text, -1));
            else if (e.KeyCode == Keys.Down) ApplyHistory(_browse.NavigateSearchHistory(SearchText.Text, 1));
            else if (e.Control && e.KeyCode == Keys.Delete) _ = DeleteHistory(false);
            else if (e.Alt && e.KeyCode == Keys.Delete) _ = DeleteHistory(true);
        };
        SearchText.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None)
            {
                e.SuppressKeyPress = true;
                _ = SearchAsync(true);
            }
        };
        SearchResults.DoubleClick += (_, _) => _ = ActivateSong(SearchResults);
        SearchResults.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                _ = ActivateSong(SearchResults);
            }
            else if (e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                TogglePlayback();
            }
        };
        previous.Click += (_, _) => Announce("已到第一页", "search-page");
        next.Click += (_, _) => _ = SearchAsync(false);
        return page;
    }

    private TabPage CreatePlaylistPage()
    {
        var page = new TabPage("播放列表") { AccessibleName = "播放列表", Padding = new Padding(10) };
        OnlinePlaylists = WinFormsUi.ListBox("播放列表", 2);
        FavoriteSongs = WinFormsUi.ListBox("收藏歌曲", 3);
        var newButton = CreateButton("新建歌单", 4, (_, _) => _ = ManagePlaylistAsync("新建歌单名称", name => _services.SavedPlaylists!.CreateAsync(name!)));
        var renameButton = CreateButton("重命名歌单", 5, (_, _) => _ = ManagePlaylistAsync("重命名歌单", name => _services.SavedPlaylists!.RenameSelectedAsync(name!)));
        var deleteButton = CreateButton("删除歌单", 6, (_, _) => _ = DeleteSavedPlaylistAsync());
        var refresh = CreateButton("刷新", 7, (_, _) => _ = RunAsync(() => _services.SavedPlaylists!.LoadAsync(), r => RenderSaved(r.Snapshot, r.Message), "刷新播放列表"));
        var remove = CreateButton("移除歌曲", 8, (_, _) => _ = RemoveFavoriteAsync());
        var clear = CreateButton("清空歌曲", 9, (_, _) => ClearFavorites());
        newButton.SetBounds(10, 10, 95, 27);
        renameButton.SetBounds(112, 10, 105, 27);
        deleteButton.SetBounds(224, 10, 90, 27);
        refresh.SetBounds(321, 10, 75, 27);
        remove.SetBounds(403, 10, 90, 27);
        clear.SetBounds(500, 10, 90, 27);
        OnlinePlaylists.SetBounds(10, 45, 250, 520);
        FavoriteSongs.SetBounds(270, 45, 680, 520);
        page.Controls.AddRange(new Control[] { OnlinePlaylists, FavoriteSongs, newButton, renameButton, deleteButton, refresh, remove, clear });
        OnlinePlaylists.SelectedIndexChanged += (_, _) => _services.SavedPlaylists?.Select(WinFormsUi.Selected<Playlist>(OnlinePlaylists));
        OnlinePlaylists.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) _ = LoadSelectedSavedAsync(); };
        OnlinePlaylists.DoubleClick += (_, _) => _ = LoadSelectedSavedAsync();
        FavoriteSongs.DoubleClick += (_, _) => _ = ActivateSong(FavoriteSongs);
        FavoriteSongs.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = ActivateSong(FavoriteSongs); } else if (e.KeyCode == Keys.Space) { e.Handled = true; TogglePlayback(); } };
        return page;
    }

    private TabPage CreateCategoryPage()
    {
        var page = new TabPage("分类歌单") { AccessibleName = "分类歌单", Padding = new Padding(10) };
        var sourceLabel = WinFormsUi.Caption("音源：", 1);
        CategorySource = WinFormsUi.Combo("分类歌单音源", 2, "网易云音乐", "QQ音乐", "酷狗音乐", "酷我音乐", "咪咕音乐");
        var refresh = CreateButton("刷新", 3, (_, _) => LoadCategories());
        CategoryList = WinFormsUi.ListBox("分类", 4);
        CategoryPlaylists = WinFormsUi.ListBox("歌单", 5);
        CategorySongs = WinFormsUi.ListBox("分类歌曲", 6);

        var playlistPrevious = CreateButton("上一页", 20, (_, _) => LoadCategoryPlaylists(GetCurrentCategoryPage() - 1));
        var playlistNext = CreateButton("下一页", 21, (_, _) => LoadCategoryPlaylists(GetCurrentCategoryPage() + 1));
        var playlistPage = new Label { Text = "第 1 页", AutoSize = true, TabStop = false };
        WinFormsUi.Name(playlistPage, "CategoryPlaylistPage", 22, false);
        playlistPrevious.Name = playlistPrevious.AccessibleName = "CategoryPlaylistPreviousPage";
        playlistNext.Name = playlistNext.AccessibleName = "CategoryPlaylistNextPage";
        playlistPrevious.TabStop = playlistNext.TabStop = false;
        playlistPrevious.TabIndex = 20; playlistNext.TabIndex = 21;

        CategorySongsLoadMore = CreateButton("加载更多歌曲", 23, (_, _) => _ = LoadMoreCategorySongsAsync());
        CategorySongsLoadMore.Name = CategorySongsLoadMore.AccessibleName = "CategorySongsLoadMore";
        CategorySongsLoadMore.TabStop = false;

        sourceLabel.SetBounds(10, 14, 0, 0);
        CategorySource.SetBounds(60, 10, 150, 24);
        refresh.SetBounds(220, 9, 80, 26);
        playlistPrevious.SetBounds(310, 9, 70, 26);
        playlistNext.SetBounds(385, 9, 70, 26);
        playlistPage.SetBounds(460, 13, 80, 20);
        CategoryList.SetBounds(10, 44, 175, 500);
        CategoryPlaylists.SetBounds(195, 44, 250, 500);
        CategorySongs.SetBounds(455, 44, 495, 500);
        CategorySongsLoadMore.SetBounds(455, 550, 160, 26);
        page.Controls.AddRange(new Control[]
        {
            sourceLabel, CategorySource, refresh, playlistPrevious, playlistNext, playlistPage,
            CategorySongsLoadMore, CategoryList, CategoryPlaylists, CategorySongs
        });
        CategoryList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) LoadCategoryPlaylists(); };
        CategoryPlaylists.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) _ = LoadCategorySongs(); };
        CategorySongs.DoubleClick += (_, _) => _ = ActivateSong(CategorySongs, PlaybackQueueOrigin.Category);
        CategorySongs.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = ActivateSong(CategorySongs, PlaybackQueueOrigin.Category); } else if (e.KeyCode == Keys.Space) { e.Handled = true; TogglePlayback(); } };
        var playlistMenu = new ContextMenu();
        var favorite = new MenuItem("收藏整个歌单到在线收藏", (_, _) => FavoriteCategoryPlaylist());
        playlistMenu.MenuItems.Add(favorite);
        CategoryPlaylists.ContextMenu = playlistMenu;
        return page;
    }

    internal IAccessibleAnnouncer AnnouncerForTests => _announcer;
    internal Label StatusTextForTests => StatusText;
    internal Label PlaybackInfoForTests => PlaybackInfoText;
    internal void LoadCategoryPlaylistsForTests() => LoadCategoryPlaylists();
    internal Task LoadCategorySongsForTests() => LoadCategorySongs();
    internal Task LoadMoreCategorySongsForTests() => LoadMoreCategorySongsAsync();
    internal Task LoadRankingSongsForTests() => LoadRankingSongs();
    internal ListBox CategoryListForTests => CategoryList;
    internal ListBox CategoryPlaylistsForTests => CategoryPlaylists;
    internal ListBox CategorySongsForTests => CategorySongs;
    private TabPage CreateRankingPage()
    {
        var page = new TabPage("排行榜") { AccessibleName = "排行榜", Padding = new Padding(10) };
        var sourceLabel = WinFormsUi.Caption("音源：", 1);
        RankingSource = WinFormsUi.Combo("排行榜音源", 2, "网易云音乐", "QQ音乐", "酷狗音乐", "酷我音乐", "咪咕音乐");
        var refresh = CreateButton("刷新", 3, (_, _) => LoadRankings());
        RankingList = WinFormsUi.ListBox("排行榜", 4);
        RankingSongs = WinFormsUi.ListBox("排行榜歌曲", 5);
        RankingSongsLoadMore = CreateButton("加载更多歌曲", 6, (_, _) => _ = LoadMoreRankingSongsAsync());
        RankingSongsLoadMore.Name = RankingSongsLoadMore.AccessibleName = "RankingSongsLoadMore";
        RankingSongsLoadMore.TabStop = false;
        sourceLabel.SetBounds(10, 14, 0, 0);
        RankingSource.SetBounds(60, 10, 150, 24);
        refresh.SetBounds(220, 9, 80, 26);
        RankingList.SetBounds(10, 44, 280, 520);
        RankingSongs.SetBounds(300, 44, 650, 490);
        RankingSongsLoadMore.SetBounds(300, 540, 160, 26);
        page.Controls.AddRange(new Control[] { sourceLabel, RankingSource, refresh, RankingList, RankingSongs, RankingSongsLoadMore });
        RankingList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) _ = LoadRankingSongs(); };
        RankingSongs.DoubleClick += (_, _) => _ = ActivateSong(RankingSongs, PlaybackQueueOrigin.Ranking);
        RankingSongs.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = ActivateSong(RankingSongs, PlaybackQueueOrigin.Ranking); } else if (e.KeyCode == Keys.Space) { e.Handled = true; TogglePlayback(); } };
        var rankingMenu = new ContextMenu();
        rankingMenu.MenuItems.Add(new MenuItem("收藏整个榜单到在线收藏", (_, _) => FavoriteRanking()));
        RankingList.ContextMenu = rankingMenu;
        return page;
    }

    private void CreatePlaybackState()
    {
        CurrentTitle = new Label { Text = "未播放", Visible = false, TabStop = false };
        CurrentArtist = new Label { Visible = false, TabStop = false };
        CurrentAlbum = new Label { Visible = false, TabStop = false };
        CurrentPlayModeText = new Label { Text = "播放模式：顺序播放", Visible = false, TabStop = false };
        PositionText = new Label { Text = "00:00 / 00:00", Visible = false, TabStop = false };
        var positionTimer = new System.Windows.Forms.Timer { Interval = 500 };
        positionTimer.Tick += (_, _) => RefreshPlaybackStatus();
        positionTimer.Start();
    }
    protected static Button CreateButton(string text, int tabIndex, EventHandler onClick, Func<bool, bool>? state = null)
    {
        var button = new Button { Text = text };
        WinFormsUi.Name(button, text, tabIndex);
        button.Click += onClick;
        return button;
    }

    protected static string SourceId(string? display) => display switch
    {
        "网易云音乐" => "netease",
        "QQ音乐" => "qq",
        "酷狗音乐" => "kg",
        "酷我音乐" => "kw",
        "咪咕音乐" => "migu",
        _ => "all"
    };

    protected static string TranslateQuality(string? quality) => quality switch
    {
        "128k" => "标准",
        "192k" => "较高",
        "320k" => "极高",
        "flac" => "无损",
        "flac24bit" => "Hi-Res 无损",
        "hires" => "Hi-Res",
        "atmos" => "杜比全景声",
        "atmos_plus" => "杜比全景声+",
        "master" => "超清母带",
        "jyeffect" => "高清环绕声",
        "sky" => "沉浸环绕声",
        "dolby" => "杜比",
        "jymaster" => "超清母带",
        _ => string.IsNullOrWhiteSpace(quality) ? "未知" : quality
    };

    protected static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    protected void RefreshPlaybackStatus()
    {
        if (_services.Audio is null)
        {
            PlaybackInfoText.Text = _current is null ? "未播放" : $"{_current.Artist} - {_current.Title}";
            return;
        }
        try { _services.Audio.SyncVolumeFromDevice(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        PositionText.Text = $"{FormatTime(_services.Audio.Position)} / {FormatTime(_services.Audio.Duration)}";
        if (!_services.Playback?.IsPlaying == true)
        {
            PlaybackInfoText.Text = _current is null ? "未播放" : $"{_current.Artist} - {_current.Title}";
            return;
        }
        PlaybackInfoText.Text = $"{_current?.Artist} - {_current?.Title}，{TranslateQuality(_services.Audio.ActualQuality)}，总时长 {FormatTime(_services.Audio.Duration)}，当前位置 {FormatTime(_services.Audio.Position)}";
    }

    protected void ApplyHistory(SearchHistoryNavigation navigation)
    {
        if (!navigation.Handled) return;
        SearchText.Text = navigation.Text;
        Announce(navigation.Text, "search-history");
    }

    protected void HookEvents()
    {
        if (_services.Settings is not null)
        {
            _services.Settings.HotkeyPressed += (_, e) => ExecuteHotkey(e.Hotkey.ActionId);
            _services.Settings.SettingsChanged += (_, _) => ApplySettings();
        }
        if (_services.Playback is not null)
            _services.Playback.QueueCompleted += (_, e) => BeginInvoke(() =>
            {
                Announce("播放队列已完成", "playback-queue");
            });
    }

    protected void ApplySettings()
    {
        if (_services.Settings is null) return;
        var settings = _services.Settings.Current.Settings;
        _playMode = settings.DefaultPlayMode ?? PlayMode.Sequential;
        CurrentPlayModeText.Text = "播放模式：" + PlayModeName(_playMode);
        if (_services.Lyrics is not null)
            _services.Lyrics.ApplySettings(settings.LyricAutoAnnounce, settings.LyricAutoScroll, _services.Lyrics.GetOffsetsSnapshot());
        _services.Downloads?.ApplyPreferences(new(settings.DownloadLyric, settings.DownloadNameOrder, settings.DownloadNameIncludeQuality));
    }

    protected static string PlayModeName(PlayMode mode) => mode switch
    {
        PlayMode.Shuffle => "随机播放",
        PlayMode.SingleLoop => "单曲循环",
        PlayMode.Loop => "列表循环",
        _ => "顺序播放"
    };
}
