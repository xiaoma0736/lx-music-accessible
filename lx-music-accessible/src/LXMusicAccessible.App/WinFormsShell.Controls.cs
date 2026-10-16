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
    public ITtsService? Tts { get; init; }
    public IMusicSourceService? Sources { get; init; }
    public IQualityProbeWorkflow? QualityProbe { get; init; }
    public IManualSourceDiscoveryWorkflow? ManualSources { get; init; }
    public ApplicationWindowFactory? Windows { get; init; }
    public DesktopLifecycleWorkflow? Desktop { get; init; }
    internal AnnouncementToggleService? Announcements { get; init; }
    public string? TemporaryDirectory { get; init; }
}

/// <summary>The native WinForms main shell: MainMenu, four fixed tabs, playback panel, status and tray-owned workflows.</summary>
public partial class WinFormsShell : Form
{
    private readonly System.ComponentModel.Container _ownedTimers = new();
    private readonly WinFormsShellServices _services;
    private readonly IAccessibleAnnouncer _announcer;
    private readonly MusicBrowseWorkflow _browse;
    private List<MusicInfo> _searchSongs = new();
    private List<MusicInfo> _categorySongs = new();
    private List<MusicInfo> _rankingSongs = new();
    private List<MusicInfo> _favoriteSongs = new();
    private PlayMode _playMode = PlayMode.Sequential;
    private double? _appliedAnnouncementRate;
    private string? _appliedAnnouncementVoiceId;
    private MusicInfo? _current;
    private bool _searchLoading;
    private bool _pageLoadRequested;
    private bool _categoryPlaylistsLoading;
    private int _categoryPlaylistsRevision;
    private bool _categorySongsLoading;
    private bool _rankingSongsLoading;
    private bool _savedSongsLoading;
    private MusicInfo? _openAlbum;
    private const double VolumeStep = 0.05;
    private int _lastPlaybackToggleTick;
    private bool _playbackToggleTickInitialized;
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
    protected MenuItem VoiceAnnouncementItem;
    private MenuItem? _playQualityMenu;
    protected MenuItem LyricAnnouncementItem;
    protected Label LiveRegion;
    protected Button CategorySongsLoadMore;
    protected Button SearchLoadMore;
    protected Button CategoryPlaylistsLoadMore;
    protected Button SavedPlaylistLoadMore;
    protected Button RankingSongsLoadMore;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ownedTimers.Dispose();
        base.Dispose(disposing);
    }
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

        // StatusBarPanel text is painted, never exposed: a StatusBar accessible object reports no children,
        // so the status, now-playing and clock text live in real child labels a screen reader can reach.
        // Their Text is the accessible name, which is why no fixed AccessibleName is set on them.
        var status = new StatusBar { TabStop = false, TabIndex = 900, ShowPanels = false, AccessibleName = "应用状态" };
        StatusText = new Label { Text = "正在初始化...", AutoSize = true, TabStop = false, TabIndex = 901, Name = "StatusText" };
        status.Controls.Add(StatusText);
        PlaybackInfoText = new Label { Text = "未播放", AutoSize = true, TabStop = false, TabIndex = 902, Name = "PlaybackInfo", Left = 280 };
        status.Controls.Add(PlaybackInfoText);
        var timeLabel = new Label { Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), AutoSize = true, TabStop = false, TabIndex = 903, Name = "ClockText", Left = 900 };
        status.Controls.Add(timeLabel);
        var statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        statusTimer.Tick += (_, _) => timeLabel.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        _ownedTimers.Add(statusTimer);
        statusTimer.Start();

        MainTabs = new TabControl { Name = "MainTabs", Dock = DockStyle.Fill, TabIndex = 1, AccessibleName = "导航" };
        MainTabs.Controls.Add(CreateSearchPage());
        MainTabs.Controls.Add(CreatePlaylistPage());
        MainTabs.Controls.Add(CreateCategoryPage());
        MainTabs.Controls.Add(CreateRankingPage());
        // Page content is filled in lazily, but only for a page the user opened with a navigation
        // command: the 导航 menu, Ctrl+1 to Ctrl+4, Ctrl+Up/Down, the Alt page keys or Ctrl+R.
        // Arrow keys and Ctrl+Tab on the tab headers are pure focus movement and must not start a
        // network request, a history load or an announcement.
        MainTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_pageLoadRequested) LoadTabPageContent(MainTabs.SelectedIndex);
        };

        CreatePlaybackState();

        // The mirror of every announcement has to be a line a reader can actually reach. A 1x1 label is
        // never rendered, so Narrator's scan mode, JAWS review and a magnifier user all skip it and the
        // sentence exists only as speech: with 语音播报 turned off, no status message was perceivable at
        // all. This is a real bottom row that carries the same text the reader speaks.
        LiveRegion = new Label
        {
            Name = "AnnouncementRegion",
            Text = string.Empty,
            AutoSize = false,
            Dock = DockStyle.Bottom,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            TabStop = false,
            TabIndex = 999
        };
        // Docking is resolved from the last control added back to the first, so the status bar keeps the
        // bottom edge, the announcement line sits directly above it and the pages fill the rest.
        Controls.Add(MainTabs);
        Controls.Add(LiveRegion);
        Controls.Add(status);
        if (services.Announcer is WinFormsAccessibleAnnouncer native) native.Attach(LiveRegion);
        AssignSongContextMenu(SearchResults);
        AssignSongContextMenu(FavoriteSongs);
        AssignSongContextMenu(CategorySongs);
        AssignSongContextMenu(RankingSongs);
        HookEvents();
        // Settings load before the shell is composed, so the persisted voice preference, play mode,
        // lyric announcement and download options apply at startup instead of waiting for a change.
        ApplySettings();
        StatusText.Text = "就绪";
        Announce("欢迎使用落雪音乐无障碍版", "welcome", speech: AnnouncementSpeech.None);
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
        var file = new MenuItem("文件(&F)");
        // Ctrl+H is the standard "delete previous character" key of every Windows edit control, so the
        // command keeps its place in the menu and its access key, but the accelerator is not taken: the
        // documented global binding for this action is Ctrl+Alt+H, shown here so it stays discoverable.
        file.MenuItems.Add(new MenuItem("隐藏/显示窗口（Ctrl+Alt+H）(&H)", (_, _) => ToggleVisibility()));
        file.MenuItems.Add(new MenuItem("打开本地文件(&O)", (_, _) => OpenLocalFile()));
        file.MenuItems.Add(new MenuItem("打开下载目录(&D)", (_, _) => _services.Downloads?.OpenDownloadDirectory()));
        file.MenuItems.Add(new MenuItem("-"));
        file.MenuItems.Add(new MenuItem("退出(&X)", (_, _) => _ = RequestExit(), Shortcut.CtrlQ));
        var play = new MenuItem("播放(&B)");
        play.MenuItems.Add(new MenuItem("播放/暂停(&P)", (_, _) => TogglePlayback()));
        play.MenuItems.Add(new MenuItem("停止(&S)", (_, _) => StopPlayback(), Shortcut.F6));
        play.MenuItems.Add(new MenuItem("上一首(&V)", (_, _) => _ = MovePlayback(true), Shortcut.F3));
        play.MenuItems.Add(new MenuItem("下一首(&N)", (_, _) => _ = MovePlayback(false), Shortcut.F4));
        play.MenuItems.Add(new MenuItem("-"));
        play.MenuItems.Add(new MenuItem("快退十秒(&R)", (_, _) => Seek(-10), Shortcut.F7));
        play.MenuItems.Add(new MenuItem("快进十秒(&F)", (_, _) => Seek(10), Shortcut.F8));
        play.MenuItems.Add(new MenuItem("音量减(&L)", (_, _) => ChangeVolume(-VolumeStep), Shortcut.F9));
        // F10 is not bound as a MenuItem shortcut: that accelerator is resolved through the configurable hotkey
        // table and must stay free for the classic menu key, so the default key is named in the text instead.
        play.MenuItems.Add(new MenuItem("音量加（默认 F10）(&G)", (_, _) => ChangeVolume(VolumeStep)));
        play.MenuItems.Add(new MenuItem("上一个播放设备(&E)", (_, _) => SwitchDevice(-1), Shortcut.F11));
        play.MenuItems.Add(new MenuItem("下一个播放设备(&X)", (_, _) => SwitchDevice(1), Shortcut.F12));
        play.MenuItems.Add(new MenuItem("切换播放模式(&M)", (_, _) => TogglePlayMode(), Shortcut.F1));
        play.MenuItems.Add(new MenuItem("下载当前歌曲(&W)", (_, _) => DownloadSelected(), Shortcut.F2));
        _playQualityMenu = new MenuItem("当前歌曲音质(&Q)");
        _playQualityMenu.Popup += (_, _) => _ = RefreshQualitySubmenuAsync(
            _playQualityMenu,
            this,
            _current,
            "当前歌曲音质",
            choice => PlayQualityAsync(choice.Song, choice.Quality, PlaybackQueueOrigin.Search),
            "play-quality");
        play.MenuItems.Add(_playQualityMenu);
        var navigate = new MenuItem("导航(&D)");
        navigate.MenuItems.Add(new MenuItem("搜索(&1)", (_, _) => SelectTab(0), Shortcut.Ctrl1));
        navigate.MenuItems.Add(new MenuItem("播放列表(&2)", (_, _) => SelectTab(1), Shortcut.Ctrl2));
        navigate.MenuItems.Add(new MenuItem("分类歌单(&3)", (_, _) => SelectTab(2), Shortcut.Ctrl3));
        navigate.MenuItems.Add(new MenuItem("排行榜(&4)", (_, _) => SelectTab(3), Shortcut.Ctrl4));
        navigate.MenuItems.Add(new MenuItem("歌词(&Y)", (_, _) => OpenLyrics()));
        var tools = new MenuItem("工具(&T)");
        tools.MenuItems.Add(new MenuItem("下载管理(&D)", (_, _) => OpenTool(() => _services.Windows?.CreateDownloadManager(this, HandleHotkey)), Shortcut.Ctrl5));
        tools.MenuItems.Add(new MenuItem("导入个人歌单(&I)", (_, _) => OpenTool(() => _services.Windows?.CreatePlaylistImport(this, HandleHotkey, AttachSongList, DetachSongList)), Shortcut.Ctrl6));
        tools.MenuItems.Add(new MenuItem("音源管理(&M)", (_, _) => OpenTool(() => _services.Windows?.CreateSourceManager(this, HandleHotkey)), Shortcut.Ctrl7));
        tools.MenuItems.Add(new MenuItem("设置(&S)", (_, _) => ShowSettings(null), Shortcut.Ctrl8));
        tools.MenuItems.Add(new MenuItem("-"));
        VoiceAnnouncementItem = new MenuItem("语音播报(&V)", (_, _) => _services.Announcements?.ToggleVoiceAnnouncement());
        LyricAnnouncementItem = new MenuItem("歌词播报(&L)", (_, _) => _services.Announcements?.ToggleLyricAnnouncement());
        tools.MenuItems.Add(VoiceAnnouncementItem);
        tools.MenuItems.Add(LyricAnnouncementItem);
        tools.MenuItems.Add(new MenuItem("歌词延后(&J)", (_, _) => _services.Lyrics?.AdjustOffset(500)));
        tools.MenuItems.Add(new MenuItem("歌词提前(&K)", (_, _) => _services.Lyrics?.AdjustOffset(-500)));
        tools.MenuItems.Add(new MenuItem("切换音源(&C)", (_, _) => OpenManualSource()));
        var help = new MenuItem("帮助(&H)");
        help.MenuItems.Add(new MenuItem("使用说明(&G)", (_, _) => OpenUserGuide()));
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
        SearchResults = WinFormsUi.ListBox("SearchResults", "搜索结果", 8);
        SearchResults.SelectionMode = SelectionMode.One;
        SearchResults.Size = new Size(830, 450);
        SearchLoadMore = new Button { Text = "加载更多歌曲", Width = 150, TabStop = false, TabIndex = 10, Name = "SearchLoadMore", AccessibleName = "加载更多搜索歌曲", Enabled = false };
        sourceLabel.SetBounds(10, 14, 0, 0);
        SearchSource.SetBounds(65, 10, 150, 24);
        typeLabel.SetBounds(225, 14, 0, 0);
        SearchType.SetBounds(265, 10, 100, 24);
        SearchText.SetBounds(10, 44, 300, 24);
        SearchButton.SetBounds(320, 43, 90, 26);
        SearchClearButton.SetBounds(420, 43, 80, 26);
        SearchResults.SetBounds(10, 78, 830, 450);
        SearchLoadMore.SetBounds(340, 536, 160, 26);
        page.Controls.AddRange(new Control[] { sourceLabel, SearchSource, typeLabel, SearchType, SearchText, SearchButton, SearchClearButton, SearchResults, SearchLoadMore });
        SearchButton.Click += (_, _) => _ = SearchAsync(true);
        SearchClearButton.Click += (_, _) => ClearSearch();
        // One owner routes search-box keys so asynchronous clear-all work cannot race the normal
        // Enter/search handler and emit a second, misleading result after history has been cleared.
        SearchText.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Up)
            {
                ApplyHistory(_browse.NavigateSearchHistory(SearchText.Text, -1));
            }
            else if (e.KeyCode == Keys.Down)
            {
                ApplyHistory(_browse.NavigateSearchHistory(SearchText.Text, 1));
            }
            else if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None
                && SearchText.Text == MusicBrowseWorkflow.ClearSearchHistoryText)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await DeleteHistory(true);
            }
            else if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await SearchAsync(true);
                ActiveControl = SearchResults;
            }
            else if (e.Control && e.KeyCode == Keys.Delete)
            {
                await DeleteHistory(false);
            }
            else if (e.Alt && e.KeyCode == Keys.Delete)
            {
                await DeleteHistory(true);
            }
        };
        SearchResults.PreviewKeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) e.IsInputKey = true;
        };
        SearchResults.KeyPress += (_, e) =>
        {
            if (e.KeyChar == (char)Keys.Return && TryStartSearchLoadMore()) e.Handled = true;
        };
        SearchResults.DoubleClick += async (_, _) =>
        {
            if (await TryActivateLoadMore(SearchResults, new KeyEventArgs(Keys.Enter)))
            {
                ActiveControl = SearchResults;
                return;
            }
            _ = ActivateSong(SearchResults);
        };
        SearchResults.KeyDown += async (_, e) =>
        {
            if (HandleSongListSpace(SearchResults, e)) return;
            if (await TryActivateLoadMore(SearchResults, e)) return;
            if (e.Shift && e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                OpenLyrics(FocusedItem<MusicInfo>(SearchResults));
                return;
            }
            if (e.KeyCode == Keys.Back && HandleBrowseBack(SearchResults)) return;
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                _ = ActivateSong(SearchResults);
            }
        };
        SearchLoadMore.Click += (_, _) => _ = SearchAsync(false);
        return page;
    }

    private TabPage CreatePlaylistPage()
    {
        var page = new TabPage("播放列表") { AccessibleName = "播放列表", Padding = new Padding(10) };
        OnlinePlaylists = WinFormsUi.ListBox("OnlinePlaylists", "我的歌单", 2);
        FavoriteSongs = WinFormsUi.ListBox("FavoriteSongs", "收藏歌曲", 3);
        FavoriteSongs.SelectionMode = SelectionMode.One;
        var newButton = CreateButton("新建歌单", 4, (_, _) => _ = ManagePlaylistAsync("新建歌单名称", name => _services.SavedPlaylists!.CreateAsync(name!)));
        var renameButton = CreateButton("重命名歌单", 5, (_, _) => _ = ManagePlaylistAsync("重命名歌单", name => _services.SavedPlaylists!.RenameSelectedAsync(name!)));
        var deleteButton = CreateButton("删除歌单", 6, (_, _) => _ = DeleteSavedPlaylistAsync());
        var refresh = CreateButton("刷新", 7, (_, _) => _ = RefreshSavedAsync());
        var remove = CreateButton("移除歌曲", 8, (_, _) => _ = RemoveFavoriteAsync());
        var clear = CreateButton("清空歌曲", 9, (_, _) => ClearFavorites());
        var openLocalFile = CreateButton("打开本地文件", 10, (_, _) => _ = AddLocalFilesToPlaylist());
        var openLocalFolder = CreateButton("打开本地文件夹", 11, (_, _) => _ = AddLocalFolderToPlaylist());
        newButton.SetBounds(10, 10, 95, 27);
        renameButton.SetBounds(112, 10, 105, 27);
        deleteButton.SetBounds(224, 10, 90, 27);
        refresh.SetBounds(321, 10, 75, 27);
        remove.SetBounds(403, 10, 90, 27);
        clear.SetBounds(500, 10, 90, 27);
        openLocalFile.SetBounds(597, 10, 125, 27);
        openLocalFolder.SetBounds(730, 10, 145, 27);
        OnlinePlaylists.SetBounds(10, 45, 250, 520);
        SavedPlaylistLoadMore = new Button { Text = "加载更多歌曲", Width = 160, TabStop = false, TabIndex = 12, Name = "SavedPlaylistLoadMore", AccessibleName = "加载更多收藏歌曲", Enabled = false };
        SavedPlaylistLoadMore.SetBounds(270, 570, 160, 26);
        FavoriteSongs.SetBounds(270, 45, 680, 520);
        page.Controls.AddRange(new Control[] { OnlinePlaylists, FavoriteSongs, SavedPlaylistLoadMore, newButton, renameButton, deleteButton, refresh, remove, clear, openLocalFile, openLocalFolder });
        OnlinePlaylists.SelectedIndexChanged += (_, _) =>
        {
            _services.SavedPlaylists?.Select(WinFormsUi.Selected<Playlist>(OnlinePlaylists));
            SavedPlaylistLoadMore.Enabled = false;
        };
        OnlinePlaylists.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && WinFormsUi.Selected<Playlist>(OnlinePlaylists) is not null)
            {
                e.Handled = true;
                await LoadSelectedSavedAsync();
                ActiveControl = FavoriteSongs;
            }
        };
        OnlinePlaylists.DoubleClick += (_, _) => _ = LoadSelectedSavedAsync();
        FavoriteSongs.DoubleClick += (_, _) => _ = ActivateSong(FavoriteSongs);
        SavedPlaylistLoadMore.Click += (_, _) => _ = LoadMoreSavedSongsAsync();
        FavoriteSongs.KeyDown += async (_, e) => { if (HandleSongListSpace(FavoriteSongs, e)) return;
            if (await TryActivateLoadMore(FavoriteSongs, e)) return; if (e.Shift && e.KeyCode == Keys.Enter) { e.Handled = true; OpenLyrics(FocusedItem<MusicInfo>(FavoriteSongs)); return; } if (e.KeyCode == Keys.Back && HandleBrowseBack(FavoriteSongs)) return; if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = ActivateSong(FavoriteSongs); } };
        return page;
    }

    private TabPage CreateCategoryPage()
    {
        var page = new TabPage("分类歌单") { AccessibleName = "分类歌单", Padding = new Padding(10) };
        var sourceLabel = WinFormsUi.Caption("音源：", 1);
        CategorySource = WinFormsUi.Combo("分类歌单音源", 2, "网易云音乐", "QQ音乐", "酷狗音乐", "酷我音乐", "咪咕音乐");
        var refresh = CreateButton("刷新", 3, (_, _) => LoadCategories());
        CategoryList = WinFormsUi.ListBox("CategoryList", "分类", 4);
        CategoryPlaylists = WinFormsUi.ListBox("CategoryPlaylists", "歌单", 5);
        CategorySongs = WinFormsUi.ListBox("CategorySongs", "分类歌曲", 6);
        CategorySongs.SelectionMode = SelectionMode.One;

        // Category playlists are paged by appending through the load-more command below. The earlier
        // first/previous/next page buttons and the page indicator were permanently hidden, so a screen
        // reader could never reach them; they are gone rather than renamed because they are not part of
        // the interface at all.

        CategoryPlaylistsLoadMore = CreateButton("加载更多歌单", 21, (_, _) => _ = LoadCategoryPlaylistsAsync(GetCurrentCategoryPage() + 1, append: true));
        CategoryPlaylistsLoadMore.Name = "CategoryPlaylistsLoadMore";
        CategoryPlaylistsLoadMore.AccessibleName = "加载更多歌单";
        CategoryPlaylistsLoadMore.TabStop = false;
        CategoryPlaylistsLoadMore.Enabled = false;

        CategorySongsLoadMore = CreateButton("加载更多歌曲", 23, (_, _) => _ = LoadMoreCategorySongsAsync());
        CategorySongsLoadMore.Name = "CategorySongsLoadMore";
        CategorySongsLoadMore.AccessibleName = "加载更多歌单歌曲";
        CategorySongsLoadMore.Enabled = false;
        CategorySongsLoadMore.TabStop = false;

        sourceLabel.SetBounds(10, 14, 0, 0);
        CategorySource.SetBounds(60, 10, 150, 24);
        refresh.SetBounds(220, 9, 80, 26);
        CategoryPlaylistsLoadMore.SetBounds(310, 9, 140, 26);
        CategoryList.SetBounds(10, 44, 175, 500);
        CategoryPlaylists.SetBounds(195, 44, 250, 500);
        CategorySongs.SetBounds(455, 44, 495, 500);
        CategorySongsLoadMore.SetBounds(455, 550, 160, 26);
        page.Controls.AddRange(new Control[]
        {
            sourceLabel, CategorySource, refresh,
            CategoryPlaylistsLoadMore, CategorySongsLoadMore, CategoryList, CategoryPlaylists, CategorySongs
        });
        CategoryList.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                await LoadCategoryPlaylistsAsync(1, append: false);
                ActiveControl = CategoryPlaylists;
            }
        };
        CategoryPlaylists.KeyDown += async (_, e) =>
        {
            if (await TryActivateLoadMore(CategoryPlaylists, e)) return;
            if (e.KeyCode == Keys.Back && HandleBrowseBack(CategoryPlaylists)) return;
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                await LoadCategorySongsAsync(1, append: false);
                ActiveControl = CategorySongs;
            }
        };
        CategorySongs.DoubleClick += (_, _) => _ = ActivateSong(CategorySongs, PlaybackQueueOrigin.Category);
        CategorySongs.KeyDown += async (_, e) => { if (HandleSongListSpace(CategorySongs, e)) return;
            if (await TryActivateLoadMore(CategorySongs, e)) return; if (e.Shift && e.KeyCode == Keys.Enter) { e.Handled = true; OpenLyrics(FocusedItem<MusicInfo>(CategorySongs)); return; } if (e.KeyCode == Keys.Back && HandleBrowseBack(CategorySongs)) return; if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = ActivateSong(CategorySongs, PlaybackQueueOrigin.Category); } };
        var playlistMenu = new ContextMenu();
        var favorite = new MenuItem("收藏整个歌单到在线收藏", (_, _) => FavoriteCategoryPlaylist());
        playlistMenu.MenuItems.Add(favorite);
        CategoryPlaylists.ContextMenu = playlistMenu;
        return page;
    }

    internal static IReadOnlyList<MusicInfo> CreateLocalMusicInfosForTests(IEnumerable<string> paths, out int skippedCount) =>
        CreateLocalMusicInfos(paths, out skippedCount);

    internal IAccessibleAnnouncer AnnouncerForTests => _announcer;
    internal Label StatusTextForTests => StatusText;
    internal Label PlaybackInfoForTests => PlaybackInfoText;
    internal Label LiveRegionForTests => LiveRegion;
    internal MenuItem VoiceAnnouncementItemForTests => VoiceAnnouncementItem;
    internal MenuItem LyricAnnouncementItemForTests => LyricAnnouncementItem;
    internal IReadOnlyList<BatchSelectionForm> OwnedBatchFormsForTests => _batchSelectionForms;
    internal void SelectTabForTests(int index) => SelectTab(index);
    internal void LoadCategoryPlaylistsForTests() => LoadCategoryPlaylists();
    internal Task LoadMoreCategoryPlaylistsForTests() => LoadCategoryPlaylistsAsync(GetCurrentCategoryPage() + 1, append: true);
    internal Task LoadCategorySongsForTests() => LoadCategorySongs();
    internal Task LoadMoreCategorySongsForTests() => LoadMoreCategorySongsAsync();
    internal Task LoadRankingSongsForTests() => LoadRankingSongs();
    internal Task LoadMoreRankingSongsForTests() => LoadMoreRankingSongsAsync();
    internal Task RefreshSavedPlaylistsForTests() => RefreshSavedAsync();
    internal Task LoadSelectedSavedForTests() => LoadSelectedSavedAsync();
    internal Task LoadMoreSavedSongsForTests() => LoadMoreSavedSongsAsync();
    internal Task ActivateSelectedSearchSongForTests() => ActivateSong(SearchResults);
    internal Task LoadMoreSearchResultsForTests() => SearchAsync(false);
    internal ListBox SearchResultsForTests => SearchResults;
    internal ListBox FavoriteSongsForTests => FavoriteSongs;
    internal ListBox RankingSongsForTests => RankingSongs;
    internal Button SearchLoadMoreForTests => SearchLoadMore;
    internal Button SavedPlaylistLoadMoreForTests => SavedPlaylistLoadMore;
    internal ListBox CategoryListForTests => CategoryList;
    internal ListBox CategoryPlaylistsForTests => CategoryPlaylists;
    internal ListBox CategorySongsForTests => CategorySongs;
    private TabPage CreateRankingPage()
    {
        var page = new TabPage("排行榜") { AccessibleName = "排行榜", Padding = new Padding(10) };
        var sourceLabel = WinFormsUi.Caption("音源：", 1);
        RankingSource = WinFormsUi.Combo("排行榜音源", 2, "网易云音乐", "QQ音乐", "酷狗音乐", "酷我音乐", "咪咕音乐");
        var refresh = CreateButton("刷新", 3, (_, _) => LoadRankings());
        RankingList = WinFormsUi.ListBox("RankingList", "排行榜", 4);
        RankingSongs = WinFormsUi.ListBox("RankingSongs", "排行榜歌曲", 5);
        RankingSongs.SelectionMode = SelectionMode.One;
        RankingSongsLoadMore = CreateButton("加载更多歌曲", 6, (_, _) => _ = LoadMoreRankingSongsAsync());
        RankingSongsLoadMore.Name = "RankingSongsLoadMore";
        RankingSongsLoadMore.AccessibleName = "加载更多榜单歌曲";
        RankingSongsLoadMore.Enabled = false;
        RankingSongsLoadMore.TabStop = false;
        sourceLabel.SetBounds(10, 14, 0, 0);
        RankingSource.SetBounds(60, 10, 150, 24);
        refresh.SetBounds(220, 9, 80, 26);
        RankingList.SetBounds(10, 44, 280, 520);
        RankingSongs.SetBounds(300, 44, 650, 490);
        RankingSongsLoadMore.SetBounds(300, 540, 160, 26);
        page.Controls.AddRange(new Control[] { sourceLabel, RankingSource, refresh, RankingList, RankingSongs, RankingSongsLoadMore });
        RankingList.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                await LoadRankingSongsAsync(1, append: false);
                ActiveControl = RankingSongs;
            }
        };
        RankingSongs.DoubleClick += (_, _) => _ = ActivateSong(RankingSongs, PlaybackQueueOrigin.Ranking);
        RankingSongs.KeyDown += async (_, e) => { if (HandleSongListSpace(RankingSongs, e)) return;
            if (await TryActivateLoadMore(RankingSongs, e)) return; if (e.Shift && e.KeyCode == Keys.Enter) { e.Handled = true; OpenLyrics(FocusedItem<MusicInfo>(RankingSongs)); return; } if (e.KeyCode == Keys.Back && HandleBrowseBack(RankingSongs)) return; if (e.KeyCode == Keys.Enter) { e.Handled = true; _ = ActivateSong(RankingSongs, PlaybackQueueOrigin.Ranking); } };
        var rankingMenu = new ContextMenu();
        rankingMenu.MenuItems.Add(new MenuItem("收藏整个榜单到在线收藏", (_, _) => FavoriteRanking()));
        RankingList.ContextMenu = rankingMenu;
        return page;
    }

    /// <summary>
    /// Keeps the status bar's now-playing text in step with the audio clock. There used to be five more
    /// playback labels here, but they were never added to the form, so every write to them was invisible
    /// to users and to assistive technology; the status bar label below is the single source of truth.
    /// </summary>
    private void CreatePlaybackState()
    {
        var positionTimer = new System.Windows.Forms.Timer { Interval = 500 };
        positionTimer.Tick += (_, _) => RefreshPlaybackStatus();
        _ownedTimers.Add(positionTimer);
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

    /// <summary>Translates the source-level quality number carried by MusicInfo.</summary>
    protected static string TranslateSource(string? sourceId) => sourceId?.Trim().ToLowerInvariant() switch
    {
        "netease" or "wy" => "网易云",
        "qq" or "tx" => "QQ音乐",
        "kg" => "酷狗",
        "kw" => "酷我",
        "migu" or "mg" => "咪咕",
        "local" => "本地",
        var id => string.IsNullOrWhiteSpace(id) ? "未知" : sourceId!.Trim()
    };

    protected static string TranslateQualityLevel(int quality) => quality switch
    {
        >= 999 => "无损",
        >= 320 => "极高",
        >= 192 => "较高",
        >= 128 => "标准",
        _ => "未知"
    };

    /// <summary>
    /// Actual stream quality is preferred because it describes the media that is really decoding. If BASS
    /// cannot report it (for example before the codec exposes bitrate), fall back to the source metadata
    /// instead of showing 未知 while a known-quality song is active.
    /// </summary>
    protected string PlaybackQualityText(MusicInfo? music)
    {
        if (music is null) return "未知";
        var actual = _services.Audio?.ActualQuality;
        return string.IsNullOrWhiteSpace(actual)
            ? TranslateQualityLevel(music.Quality)
            : TranslateQuality(actual);
    }

    private string BuildPlaybackStatusText()
    {
        if (_current is null) return "未播放";
        var text = $"{_current.Artist} - {_current.Title}，音质：{PlaybackQualityText(_current)}";
        if (_services.Playback?.IsPlaying == true && _services.Audio is not null)
        {
            text += $"，总时长 {FormatTime(_services.Audio.Duration)}，当前位置 {FormatTime(_services.Audio.Position)}";
        }
        return text;
    }

    protected void RefreshPlaybackStatus()
    {
        if (_services.Audio is not null)
        {
            try { _services.Audio.SyncVolumeFromDevice(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        }
        PlaybackInfoText.Text = BuildPlaybackStatusText();
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
            _services.Settings.HotkeysRegistrationFailed += (_, e) => AnnounceRegistrationFailures(e.FailedHotkeys);
            _services.Settings.SettingsChanged += (_, _) => ApplySettings();
            // Startup registration runs before this shell is resolved, so its failures are reported
            // from the recorded list as well; the event above only covers later re-attaches.
            AnnounceRegistrationFailures(_services.Settings.LastRegistrationFailures);
        }
        if (_services.Playback is not null)
            _services.Playback.QueueCompleted += (_, e) => BeginInvoke(() =>
            {
                Announce("播放队列已完成", "playback-queue");
            });
    }

    /// <summary>
    /// Reports each binding whose native registration failed, naming the actions and the likely cause,
    /// so a configured hotkey that cannot work is never silently dead.
    /// </summary>
    private void AnnounceRegistrationFailures(IReadOnlyList<Hotkey> failed)
    {
        if (failed.Count == 0) return;
        var names = string.Join("、", failed.Select(hotkey => string.IsNullOrWhiteSpace(hotkey.Name) ? hotkey.ActionId : hotkey.Name));
        var message = "以下全局快捷键注册失败，可能已被其他程序占用：" + names;
        if (InvokeRequired && IsHandleCreated) BeginInvoke(() => Announce(message, "hotkey-registration", AnnouncementKind.Error));
        else Announce(message, "hotkey-registration", AnnouncementKind.Error);
    }

    protected void ApplySettings()
    {
        if (_services.Settings is null) return;
        var settings = _services.Settings.Current.Settings;
        _playMode = settings.DefaultPlayMode ?? PlayMode.Sequential;
        if (_services.Playback is not null) _services.Playback.Mode = _playMode;
        if (_services.Lyrics is not null)
            _services.Lyrics.ApplySettings(settings.LyricAutoAnnounce, settings.LyricAutoScroll, _services.Lyrics.GetOffsetsSnapshot());
        _announcer.SpeechEnabled = settings.EnableVoiceAnnouncement;
        if (_announcer is WinFormsAccessibleAnnouncer nativeAnnouncer)
            nativeAnnouncer.SpeechScope = settings.AccessibilitySettings;
        // 朗读声音 and 朗读语速 are engine settings, so a stored preference only works when it is handed to
        // the speech backend here: this runs once at startup and again for every change the user makes.
        // Applying it is guarded by the last value, because this method also runs for unrelated settings
        // changes and a native voice switch mid-sentence would be work the user never asked for.
        if (_services.Tts is { IsAvailable: true } tts)
        {
            var rate = settings.AccessibilitySettings.AnnouncementRate;
            if (_appliedAnnouncementRate != rate)
            {
                _appliedAnnouncementRate = rate;
                tts.SetRate(rate);
            }

            var voiceId = settings.AccessibilitySettings.AnnouncementVoiceId;
            if (!string.Equals(_appliedAnnouncementVoiceId, voiceId, StringComparison.Ordinal))
            {
                _appliedAnnouncementVoiceId = voiceId;
                tts.SetVoice(voiceId);
            }
        }
        _services.Downloads?.ApplyPreferences(new(
            settings.DownloadLyric,
            settings.DownloadNameOrder,
            settings.DownloadNameIncludeQuality,
            settings.BatchDownloadDelaySeconds,
            settings.DownloadSpeedLimitKbps,
            settings.DownloadPath));
        RefreshAnnouncementMenuChecks();
    }

    /// <summary>
    /// The two announcement switches are states, not commands, so they carry a check mark. Screen
    /// readers announce a checked menu item, which is the only way to read the current setting back.
    /// </summary>
    private void RefreshAnnouncementMenuChecks()
    {
        if (_services.Settings is null) return;
        var settings = _services.Settings.Current.Settings;
        if (VoiceAnnouncementItem is not null) VoiceAnnouncementItem.Checked = settings.EnableVoiceAnnouncement;
        if (LyricAnnouncementItem is not null) LyricAnnouncementItem.Checked = settings.LyricAutoAnnounce;
    }

    protected static string PlayModeName(PlayMode mode) => mode switch
    {
        PlayMode.Shuffle => "随机播放",
        PlayMode.SingleLoop => "单曲循环",
        PlayMode.Loop => "列表循环",
        _ => "顺序播放"
    };
}
