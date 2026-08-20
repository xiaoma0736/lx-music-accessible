# 技术方案文档

## 1. 总体架构设计

### 1.1 项目结构
`
LXMusicAccessible/
├── src/
│   ├── LXMusicAccessible.App/           # WPF 应用程序入口
│   ├── LXMusicAccessible.Core/          # 核心业务逻辑
│   ├── LXMusicAccessible.Models/        # 数据模型
│   ├── LXMusicAccessible.Services/      # 服务层
│   ├── LXMusicAccessible.ViewModels/    # 视图模型
│   ├── LXMusicAccessible.Views/         # 视图
│   ├── LXMusicAccessible.Data/          # 数据访问层
│   ├── LXMusicAccessible.Audio/         # 音频播放
│   └── LXMusicAccessible.Tests/         # 单元测试
├── docs/                                # 文档
├── resources/                           # 资源文件
└── scripts/                             # 构建脚本
`

### 1.2 技术选型理由

#### WPF 选择理由
1. **成熟稳定**：微软官方支持，社区活跃
2. **无障碍支持完善**：原生支持 UI Automation
3. **XAML 声明式UI**：易于实现 MVVM 模式
4. **丰富的控件库**：满足复杂UI需求
5. **性能优秀**：硬件加速，流畅体验

#### .NET 8.0 选择理由
1. **长期支持**：LTS 版本，稳定可靠
2. **性能优化**：AOT 编译，启动速度快
3. **跨平台潜力**：未来可扩展到其他平台
4. **现代语言特性**：代码更简洁高效

## 2. 核心模块设计

### 2.1 无障碍导航模块
`csharp
// 核心接口
public interface IAccessibilityService
{
    void Announce(string message);
    void SetFocus(FrameworkElement element);
    void Navigate(NavigationDirection direction);
    event EventHandler<FocusChangedEventArgs> FocusChanged;
}

// UI Automation 集成
public class AutomationPeerHelper
{
    public static string GetAccessibleName(Control control);
    public static void SetAccessibleName(Control control, string name);
    public static AutomationPeer CreatePeer(Control control);
}
`

### 2.2 音乐播放模块
`csharp
// 播放器接口
public interface IMusicPlayer
{
    Task PlayAsync(string url);
    void Pause();
    void Stop();
    void Next();
    void Previous();
    void SetVolume(double volume);
    TimeSpan Position { get; set; }
    TimeSpan Duration { get; }
    bool IsPlaying { get; }
    event EventHandler<PlayerStateChangedEventArgs> StateChanged;
}

// 音频引擎选择：NAudio（推荐）
// 原因：
// 1. 支持多种音频格式
// 2. 跨平台（WAV、MP3、FLAC等）
// 3. 实时音效处理
// 4. 低延迟播放
`

### 2.3 音源管理模块
`csharp
// 音源接口
public interface IMusicSource
{
    string Name { get; }
    string Version { get; }
    Task<List<SearchResult>> SearchAsync(string query, int page = 1);
    Task<MusicInfo> GetMusicInfoAsync(string musicId);
    Task<string> GetPlayUrlAsync(string musicId);
    Task<List<Playlist>> GetPlaylistsAsync(int page = 1);
    Task<List<MusicInfo>> GetRankingAsync(string rankingId);
}

// 音源适配器（支持JS音源）
public class JScriptMusicSourceAdapter : IMusicSource
{
    private readonly JavaScriptEngine _engine;
    // 通过JavaScript引擎执行lx-music的音源脚本
}
`

### 2.4 歌词处理模块
`csharp
// 歌词服务
public interface ILyricService
{
    Task<Lyric> GetLyricAsync(string musicId);
    string GetCurrentLine(TimeSpan position);
    List<LyricLine> GetAllLines();
    event EventHandler<LyricLineChangedEventArgs> LineChanged;
}

// TTS 集成（prism接口）
public interface ITtsService
{
    Task SpeakAsync(string text);
    void Stop();
    void SetRate(double rate);
    void SetVoice(string voiceId);
}
`

### 2.5 快捷键管理模块
`csharp
// 快捷键定义
public class Hotkey
{
    public Key Key { get; set; }
    public ModifierKeys Modifiers { get; set; }
    public string Command { get; set; }
    public bool IsGlobal { get; set; }
}

// 全局快捷键管理
public interface IHotkeyManager
{
    void RegisterHotkey(Hotkey hotkey);
    void UnregisterHotkey(Hotkey hotkey);
    void RegisterGlobalHotkey(Hotkey hotkey);
    event EventHandler<HotkeyPressedEventArgs> HotkeyPressed;
}
`

### 2.6 数据存储模块
`csharp
// 数据库服务
public interface IDataService
{
    Task SavePlaylistAsync(Playlist playlist);
    Task<List<Playlist>> GetPlaylistsAsync();
    Task SaveSettingsAsync(Settings settings);
    Task<Settings> GetSettingsAsync();
    Task SaveMusicSourcesAsync(List<MusicSource> sources);
}

// SQLite 数据库设计
// - Playlists: 个人歌单
// - PlaylistItems: 歌单歌曲
// - Settings: 用户设置
// - MusicSources: 自定义音源
// - PlayHistory: 播放历史
`

## 3. 无障碍实现方案

### 3.1 UI Automation 支持
`xml
<!-- XAML 无障碍属性示例 -->
<TextBox x:Name="SearchBox"
         AutomationProperties.Name="音乐搜索框"
         AutomationProperties.HelpText="输入歌曲名、歌手或专辑进行搜索"
         AutomationProperties.IsRequiredForForm="True"/>
         
<Button Content="播放"
        AutomationProperties.Name="播放按钮"
        AutomationProperties.AcceleratorKey="Space"
        AutomationProperties.AutomationId="PlayButton"/>
`

### 3.2 焦点管理策略
`csharp
// 焦点导航规则
public enum NavigationDirection
{
    Next,
    Previous,
    Up,
    Down,
    Left,
    Right,
    First,
    Last
}

// 智能焦点管理器
public class FocusManager
{
    private List<FrameworkElement> _focusableElements;
    
    public void MoveToNext();
    public void MoveToPrevious();
    public void AnnounceCurrentFocus();
    public void ValidateFocusOrder();
}
`

### 3.3 语音反馈系统
`csharp
// 语音提示服务
public class AnnouncementService
{
    public void AnnounceAction(string action, string details = "");
    public void AnnounceError(string error);
    public void AnnounceNavigation(string location);
    public void AnnounceStatusChange(string status);
    
    // 屏幕阅读器API调用
    private void SendToScreenReader(string message);
}
`

## 4. 分页视图设计

### 4.1 分页控件
`csharp
// 分页视图模型
public class PagedViewModel<T> : ObservableObject
{
    private ObservableCollection<T> _items;
    private int _currentPage;
    private int _pageSize;
    private int _totalItems;
    
    public ObservableCollection<T> Items { get; }
    public int CurrentPage { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)_totalItems / _pageSize);
    
    public async Task LoadPageAsync(int page);
    public Task NextPageAsync();
    public Task PreviousPageAsync();
    public Task GoToPageAsync(int page);
}
`

### 4.2 虚拟列表优化
`csharp
// 虚拟化长列表（提高性能）
public class VirtualizedList<T>
{
    private List<T> _allItems;
    private List<T> _visibleItems;
    
    public void ScrollToIndex(int index);
    public void UpdateVisibleRange(int start, int end);
}
`

## 5. 音源集成方案

### 5.1 JavaScript 音源适配
`csharp
// 使用 JavaScript.NET 执行音源脚本
public class JScriptEngine
{
    private readonly JavaScriptContext _context;
    
    public object ExecuteScript(string script);
    public void RegisterFunction(string name, Delegate callback);
    public Task<T> CallAsync<T>(string functionName, params object[] args);
}
`

### 5.2 音源管理界面
`csharp
// 音源管理视图模型
public class MusicSourceManagerViewModel : ObservableObject
{
    private ObservableCollection<MusicSource> _sources;
    private MusicSource _selectedSource;
    
    public async Task ImportFromUrlAsync(string url);
    public async Task ImportFromFileAsync(string filePath);
    public async Task TestSourceAsync(MusicSource source);
    public void EnableSource(MusicSource source);
    public void DisableSource(MusicSource source);
    public void RemoveSource(MusicSource source);
}
`

## 6. 开发工具和构建配置

### 6.1 项目文件配置
`xml
<!-- LXMusicAccessible.App.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ApplicationIcon>app.ico</ApplicationIcon>
    <AssemblyName>LXMusicAccessible</AssemblyName>
  </PropertyGroup>
  
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.0" />
    <PackageReference Include="Serilog" Version="3.1.0" />
    <PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />
    <PackageReference Include="NAudio" Version="2.2.1" />
    <PackageReference Include="System.Text.Json" Version="8.0.0" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.0" />
    <PackageReference Include="HotkeyManager" Version="1.2.0" />
  </ItemGroup>
</Project>
`

### 6.2 构建和部署
`ash
# 开发构建
dotnet build

# 发布
dotnet publish -c Release -r win-x64 --self-contained

# 安装程序（使用 Inno Setup）
ISCC installer.iss
`

## 7. 测试策略

### 7.1 无障碍测试
- NVDA 测试套件
- 焦点导航测试
- 屏幕阅读器兼容性测试
- 键盘操作测试

### 7.2 功能测试
- 播放功能测试
- 音源集成测试
- 歌词同步测试
- 快捷键测试

## 8. 性能优化

1. **虚拟化列表**：大列表使用虚拟滚动
2. **异步加载**：所有网络操作异步处理
3. **缓存策略**：常用数据本地缓存
4. **延迟加载**：图片等资源按需加载
5. **数据库优化**：索引优化、查询优化

## 9. 安全考虑

1. **音源安全**：沙箱执行 JavaScript 代码
2. **数据加密**：敏感信息加密存储
3. **网络安全**：HTTPS、证书验证
4. **输入验证**：防止注入攻击
5. **权限管理**：文件系统访问控制
