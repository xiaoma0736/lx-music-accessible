# 主窗体管理功能拆窗评估 — 2026-08-28

## 范围与结论

本次只评估界面与窗口边界，不修改生产代码。第一手来源为当前 WPF 源码、自动化测试、既有无障碍审计和综合整改规范。

结论：拆分方向成立，但不应直接剪贴 XAML。推荐最终让主窗体只保留持续浏览/播放所需的四个页面：**搜索、播放列表、分类歌单、排行榜**，以及右侧正在播放区、播放菜单、托盘与全局热键宿主。以下功能各自形成独立窗体：

- `SettingsWindow`：设置，内部再按类别导航；
- `SourceManagerWindow`：音源管理；
- `DownloadManagerWindow`：下载任务；
- `PlaylistImportWindow`：导入个人歌单。

不推荐把这些功能重新放进一个“管理中心”大窗体。它只会把主窗体当前的混杂转移到另一个容器，并增加一层没有业务价值的导航。

拆分的首要前置条件是：**先让 `SettingsWorkflow.Current` 成为设置的唯一权威状态**。当前设置控件不只是显示数据；主窗体会在 Alt+F4 策略、下载音质/目录、第三方播放器、歌曲菜单和退出快照中直接读取控件。严格按设置页 `x:Name` 统计，`MainWindow.xaml.cs` 仍有约 80 处直接控件引用。若先移 XAML、后补状态同步，很容易出现“设置窗体关闭后运行时仍读到旧值”或退出时丢设置。

来源：`src/LXMusicAccessible.App/MainWindow.xaml:420-598`、`src/LXMusicAccessible.App/MainWindow.xaml.cs:295-302,685-1017,1946-1949,2305-2322`、`src/LXMusicAccessible.Services/SettingsWorkflow.cs:146-149,196-228,267-328`。

## 当前结构与实际接缝

`MainWindow.xaml` 当前包含 8 个主标签页；导入、设置、下载、音源分别是索引 2、5、6、7。菜单、Alt+1~8、切页焦点、刷新、页首/页尾焦点都依赖这些数字索引。拆页时必须一次性改完这些入口，不能只删 TabItem。

来源：`src/LXMusicAccessible.App/MainWindow.xaml:62-74,89-95,239-242,420-422,601-603,640-642`、`src/LXMusicAccessible.App/MainWindow.xaml.cs:235-281,346-416,593-649,3280-3314`。

业务状态已经大体位于可复用的深模块中：

- 设置由 `SettingsWorkflow` 负责默认值、不可变版本状态、校验、保存和热键注册；
- 下载由 `IDownloadWorkflow` 公开进程级 `Tasks` 集合及取消、重试、清理、打开目录等完整意图；
- 音源管理命令和 `SourcesChanged` 事件都在 `IMusicSourceService`；
- 导入逻辑已经集中在 `PlaylistImportView` 和 `IPlaylistImportService`，登录弹窗也已有独立窗口工厂。

因此本轮不需要再引入 ViewModel 基类、消息总线、导航框架或新的单实现接口。已有 `ApplicationWindowFactory` 已经是多窗口创建接缝，应直接扩展；已有 `LyricsWindow` 提供了“创建一次、关闭时隐藏、应用退出时真正关闭”的可复用窗体先例。

来源：`src/LXMusicAccessible.Services/SettingsWorkflow.cs:90-149,196-228,267-358`、`src/LXMusicAccessible.Services/DownloadWorkflow.cs:48-79,140-188,332-405`、`src/LXMusicAccessible.Services/IServiceInterfaces.cs:277-290,380-428`、`src/LXMusicAccessible.App/PlaylistImportView.xaml.cs:11-39`、`src/LXMusicAccessible.App/ApplicationWindowFactory.cs:7-28`、`src/LXMusicAccessible.App/LyricsWindow.xaml.cs:9-32,106-128`。

### WPF 平台约束

- `Show` 打开的是非模态窗口，`ShowDialog` 打开的是模态窗口；本评估中的“独立窗体”不等同于必须模态，模式应按任务是否需要与主窗体并行交互决定。[Microsoft Learn：Windows in WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/windows/)
- 非模态窗口不会自动与创建者建立关系；设置 `Owner` 后，拥有者关闭会带着从属窗口关闭，最小化/恢复也会形成一致行为。因此四个工具窗体都应显式设置主窗体为 Owner，而不是只在构造函数中保存主窗体引用。[Microsoft Learn：Window.Owner](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.owner?view=windowsdesktop-10.0)
- WPF 窗口一旦真正关闭，同一实例不能再次 `Show`；若采用复用实例，关闭入口必须转为 `Hide`，否则应在 `Closed` 后清空引用并按需创建新实例。两种策略都不能混用。[Microsoft Learn：How to close a window or dialog box](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/windows/how-to-close-window-dialog-box)
- 每个 `Window` 天然是独立焦点范围；键盘焦点全桌面只有一个，逻辑焦点可在焦点范围中保留。实现仍应显式记录打开入口并验证关闭后的具体回焦目标，不能只假设窗口激活会恢复到正确列表项。[Microsoft Learn：Focus overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/focus-overview)

## 候选拆分边界

| 候选 | 当前局部性 | 主要依赖/耦合 | 推荐窗体行为 | 风险 | 决策 |
| --- | --- | --- | --- | --- | --- |
| 设置 | XAML 已成连续区块，但运行时代码跨播放、歌词、下载、TTS、热键和退出 | `SettingsWorkflow`、播放/音频、`LyricsWorkflow`、`IDownloadWorkflow`、`ITtsService`、公告器；全局热键 HWND 仍属主窗体 | 单实例、Owner 绑定；关闭隐藏或保存后关闭；从冲突提示可直达“快捷键”类别 | 高 | 必拆，但先统一状态所有权 |
| 音源管理 | 列表、按钮、右键菜单和命令处理高度集中 | `IMusicSourceService`、公告器；`ScriptUpdateAlert` 是全局通知 | 单实例 Owner 窗体；本地导入/网络导入/检测必须由显式命令触发 | 低到中 | 第一批拆分，验证窗口模式 |
| 下载管理 | 视图很薄，任务集合已经由工作流持有 | `IDownloadWorkflow.Tasks`；重试/打开目录还需要当前下载目录 | 可长期打开的单实例 modeless 窗体 | 低（统一设置状态后） | 第二批拆分 |
| 导入个人歌单 | 已是独立 `UserControl`，账号与导入流程自持 | 歌曲预览的播放、歌词、右键收藏能力仍由主窗体挂接；Esc 有“先返回列表”语义 | 单实例 modeless 窗体，网页登录/扫码仍为子对话框 | 中到高 | 最后拆分 |

来源：`src/LXMusicAccessible.App/MainWindow.xaml:420-678`、`src/LXMusicAccessible.App/MainWindow.xaml.cs:128-142,685-1017,1863-1952,2305-2562`、`src/LXMusicAccessible.App/PlaylistImportView.xaml.cs:38-64,358-460`。

### 设置

设置是最有价值、也是最不能机械迁移的一块。`SettingsWorkflow` 已保存不可变的 `Current`，但大部分普通设置直到应用退出才通过 `CaptureAndBlockShutdown` 从控件回写；热键则已经即时编辑并保存。这是两套状态语义。

推荐保持现有产品语义，不在这轮同时发明“草稿/应用/取消”：普通设置变更时立即调用 `SettingsWorkflow.Edit` 更新 `Current`，并把需要即时生效的值应用到播放、歌词、下载或 TTS 模块；设置窗体关闭时调用 `SaveAsync(Current)`，退出时仍由现有关闭协调器兜底。主窗体的 Alt+F4、下载、第三方播放和退出快照改读 `Current`，而不是读另一个窗体的控件。

仍应留在主窗体的职责：

- `SettingsWorkflow.LoadAsync(windowHandle)`，因为全局热键注册需要主窗体 HWND；
- `WndProc`、低层小键盘钩子和全局热键动作派发；
- 运行态会话捕获：主窗口尺寸、音量、输出设备、当前播放位置；
- 启动时把已加载设置应用到播放器、歌词、下载和 TTS。

热键冲突目前由主窗体接收并切到设置标签。拆窗后可保留全局冲突接收者，但“重新设置”应打开 `SettingsWindow` 的快捷键类别并定位到冲突项；热键列表和录制状态不再由主窗体持有。

来源：`src/LXMusicAccessible.App/MainWindow.xaml.cs:685-758,761-938,955-1022,2798-2805,2898-3090`、`src/LXMusicAccessible.Services/SettingsWorkflow.cs:196-228,231-265,267-358`。

### 音源管理

音源页是最干净的拆分对象：按钮、列表、上下文菜单和处理器可以整体移动。`SourcesChanged` 订阅随管理窗体加载/关闭配对，避免重复打开后多次刷新。

`ScriptUpdateAlert` 不能跟着管理窗体关闭。脚本源在播放或浏览时也可能发出更新提示；当前主窗体把它送入全局公告。因此该订阅应留在始终存活的应用层/主窗体，或以后移入专门的进程级通知模块，本轮不应让它依赖 `SourceManagerWindow` 是否打开。

来源：`src/LXMusicAccessible.App/MainWindow.xaml:14-24,640-678`、`src/LXMusicAccessible.App/MainWindow.xaml.cs:140-197,2418-2562`、`src/LXMusicAccessible.Services/IServiceInterfaces.cs:282-290`。

### 下载管理

下载窗体可以直接绑定 `IDownloadWorkflow.Tasks`；关闭窗体不应取消下载，因为任务生命周期属于工作流而不是视图。取消、重试和清空仍由显式按钮触发。

唯一前置耦合是路径：当前重试和“打开下载文件夹”读取设置页的 `DownloadPathBox`。改为读取 `SettingsWorkflow.Current.Settings.DownloadPath` 后，下载管理页可以独立移动，且不需要给窗体传 `Func<string>` 之类的临时浅接口。

来源：`src/LXMusicAccessible.App/MainWindow.xaml:600-637`、`src/LXMusicAccessible.App/MainWindow.xaml.cs:142,2385-2416`、`src/LXMusicAccessible.Services/DownloadWorkflow.cs:53-79,179-188,332-405`。

### 导入个人歌单

`PlaylistImportView` 已经是独立视图，移入窗口本身不难；真正风险是歌曲预览。主窗体目前把导入视图的 `SongList` 加入外部歌曲列表集合，再为其挂接播放、歌词、批量下载、收藏和第三方播放器菜单。独立窗口不会收到主窗体的 `PreviewKeyDown`，因此应用键/Shift+F10、局部播放热键和 Esc 必须有明确转发契约。

本轮最小可行做法是沿用 `LyricsWindow` 已有的按键委托模式，让导入窗体把歌曲预览按键意图交给主窗体的现有歌曲操作；不要为了一次迁移先造完整命令总线。等歌曲操作出现第二个真正独立实现时，再把其深成共享模块。

导入窗体的 Esc 顺序必须保持：歌曲预览中先返回歌单列表；已在歌单列表时才关闭/隐藏窗体。导入完成提示中的“切换到播放列表标签页”应改为可执行的“打开播放列表”命令或清晰说明，并在回到主窗体时恢复到播放列表的合理焦点。

来源：`src/LXMusicAccessible.App/MainWindow.xaml.cs:128-129,1865-1952,2325-2381`、`src/LXMusicAccessible.App/PlaylistImportView.xaml.cs:358-460`、`src/LXMusicAccessible.App/LyricsWindow.xaml.cs:106-115`。

## 设置窗体的分类导航

### 方案比较

| 维度 | 原生 `TabControl` | 左侧原生 `ListBox` + 右侧内容 |
| --- | --- | --- |
| 纯键盘 | 平台原生：方向键切标签，Tab 进入内容；代码最少 | 方向键选类别，Tab 进入右侧首控件，Shift+Tab 回类别列表；只需很小的选择呈现代码 |
| 读屏 | 原生 tab/tab item 角色和选中状态可靠；现有审计已确认 Header 足以命名 | 原生 list/list item 角色和选中状态可靠；类别列表需短名称“设置类别”，右侧保留可见类别标题，不额外制造 HelpText 或重复播报 |
| 类别数量 | 7～8 个横向标题在窄窗口或 200% 缩放下容易拥挤、换行或截断 | 7～8 个短类别垂直排列稳定，右侧可独立滚动 |
| 焦点可预测性 | 切标签后原生行为可用，但各页首/末焦点仍需验证；不要复用主窗体复杂的索引式焦点逻辑 | 类别变化时焦点保持在列表项，用户按 Tab 才进入内容；后台更新不会抢焦点，规则更容易书面化和测试 |
| 200% 缩放 | 标签条先消耗横向空间，长标题风险较高 | 左列固定适中宽度，右列 `*`，每个类别内容使用垂直滚动；更适合当前 8 类设置 |
| 实现复杂度 | 最低 | 略高，但只需原生控件和本地内容切换，不需要导航框架 |

推荐：**左侧原生类别列表 + 右侧内容**。原因不是自定义视觉，而是当前设置天然有 8 类；纵向类别在 200% 缩放下更稳，且可形成简单焦点契约。不得为列表实现自定义 AutomationPeer 或方向键；平台原生行为已经足够。

类别选择只切换本地内容，不得触发网络、文件写入或把焦点强移到右侧。焦点留在刚选中的类别，读屏会播报其名称和选中状态；用户按 Tab 后进入该类别第一个控件。右侧标题提供视觉与结构确认，无需再发 Live Region 公告。

这一建议遵循既有基线：原生 Header/Content/Text/Title 优先作为名称，普通控件零 HelpText；选择不执行真实工作，动态结果才通过共享公告器报告。

来源：`docs/audits/accessibility-semantics-baseline-2026-08-24.md` 的 “Name findings”“HelpText findings and zero-description policy”“Regression boundary”；`tasks/comprehensive-remediation-spec-2026-08-23.md:29,186-189,278-280,316-338`。

### 推荐类别与现有控件归属

| 类别 | 现有控件/内容 |
| --- | --- |
| 常规 | `AutoPlayCheckBox`；`CloseBehaviorCombo` 及托盘说明 |
| 播放 | `DefaultQualityCombo`；`ShufflePlayCheckBox`；`RepeatPlayCheckBox` |
| 歌词 | `LyricAnnounceCheckBox`；`LyricScrollCheckBox` |
| 下载 | `DownloadPathBox`/浏览；`DownloadQualityCombo`；`DownloadLyricCheckBox`；`DownloadNameQualityCheckBox`；`DownloadNameOrderCombo` |
| 外部播放器 | `ThirdPartyPlayerPathBox`/浏览及说明 |
| 存储与缓存 | `DataDirBox`/浏览；`ClearCacheBtn` 及缓存说明 |
| 无障碍 | `EnableScreenReaderCheckBox`；`EnableVoiceAnnouncementCheckBox`；`SpeechBackendText`；`VoiceCombo`；`SpeechRateCombo` |
| 快捷键 | 快捷键说明；`HotkeyListBox`；`ResetHotkeysBtn` |

“默认搜索音源”、窗口尺寸、音量、输出设备、歌词偏移和播放会话虽然也在 `UserSettings`，但当前不是设置页中的同类表单控件：默认搜索音源仍由搜索页选择负责；其余是运行态快照，继续由主窗体/对应工作流在退出时合并，不应为了分类完整性增加新控件。

来源：`src/LXMusicAccessible.App/MainWindow.xaml:427-595`、`src/LXMusicAccessible.Models/AppModels.cs:195-317`、`src/LXMusicAccessible.App/MainWindow.xaml.cs:955-1006`。

### 设置窗体焦点与缩放契约

- 从普通“设置”菜单或 Alt+6 打开：记住发起控件，聚焦上次选中的类别项；首次为“常规”。
- 类别列表中方向键只更换类别并保持焦点；Tab 进入右侧第一个可用控件，Shift+Tab 返回类别列表。
- 从快捷键冲突打开：选中“快捷键”类别并聚焦对应冲突项；这是一条明确的用户动作链，不是后台抢焦点。
- Esc/Alt+F4/“关闭”只关闭或隐藏设置窗体；返回主窗体后恢复到打开设置前的控件。热键录制中 Esc 必须先取消录制。
- 右侧每类独立垂直滚动；文本允许换行，输入框使用可伸缩列，避免当前多个固定宽度横向 `StackPanel` 在 200% 缩放下截断。
- 第一版不增加窄宽度自动折叠、汉堡菜单或响应式导航框架；先在 100%、150%、200% 做人工验证，只有实测不满足时再增加布局分支。

来源：`src/LXMusicAccessible.App/MainWindow.xaml:427-595`、`src/LXMusicAccessible.App/MainWindow.xaml.cs:287-303,2929-2938,2999-3065`、`tasks/comprehensive-remediation-spec-2026-08-23.md:186-189,336-338`。

## 窗口生命周期与全局能力

### 建议

- 每类窗体最多一个活动实例。再次执行菜单或快捷键时应激活已有实例，不得创建重复设置编辑器或重复订阅事件。
- 窗体与主窗体建立 Owner 关系；泛用打开入口记录原焦点，关闭后显式恢复。只依赖 WPF 默认激活不足以保证恢复到具体列表项。
- 设置、音源可采用 Owner 绑定的单实例窗体；下载、导入建议采用 modeless，便于长时间监控或与主窗体并行操作。无论采用哪种窗口模式，下载/导入任务的生命周期都属于工作流而不是视图；关闭窗体不应隐式取消任务。若为一致性全部 modeless，也必须执行同样的单实例与焦点契约。
- Esc 在子窗体内只处理子窗体，不调用 `DesktopLifecycleWorkflow.ExecuteAsync(Escape)`，否则会把主窗体一起隐藏到托盘。
- 主窗体隐藏/退出时，对所有可复用窗体调用类似 `PrepareForAppShutdown` 的显式收尾，再真正关闭并解绑事件。
- `WpfAccessibleAnnouncer` 只能附着一个 LiveRegion；子窗体复用同一个 `IAccessibleAnnouncer`，不能再次 `Attach`。主窗体隐藏时的 UIA 实机表现必须纳入人工读屏检查。

来源：`src/LXMusicAccessible.App/DesktopLifecycleWorkflow.cs:58-116,128-163,205-245`、`src/LXMusicAccessible.App/LyricsWindow.xaml.cs:106-128`、`src/LXMusicAccessible.App/WpfAccessibleAnnouncer.cs:21-40,42-55`、`src/LXMusicAccessible.App/MainWindow.xaml.cs:3389-3412`。

## 导航兼容性

保留现有 Alt+1~8 肌肉记忆，不因主标签数量减少而重新编号：

| 快捷键 | 重构后行为 |
| --- | --- |
| Alt+1 | 主窗体：搜索 |
| Alt+2 | 主窗体：播放列表 |
| Alt+3 | 打开/激活导入个人歌单窗体 |
| Alt+4 | 主窗体：分类歌单 |
| Alt+5 | 主窗体：排行榜 |
| Alt+6 | 打开/激活设置窗体 |
| Alt+7 | 打开/激活下载管理窗体 |
| Alt+8 | 打开/激活音源管理窗体 |

主窗体只剩四个标签后，不再用数值索引表达页面身份。最小改法是给四个 `TabItem` 命名并按对象引用导航/判断，不需要再加导航枚举或框架。菜单命令和 Alt 快捷键都调用同一个打开/导航方法。关于窗口、使用说明和导入页底部现有“Alt+1~8 切换页面/Alt+3”文案同步改为“导航页面或打开功能窗体”。

来源：`src/LXMusicAccessible.App/MainWindow.xaml.cs:266-281,400-416,2735-2753`、`src/LXMusicAccessible.App/PlaylistImportView.xaml:110-111`。

## 推荐迁移顺序

1. **锁定交互契约与回归测试。** 记录 Alt+1~8、菜单、Esc/Alt+F4、关闭后焦点、读屏名称和 200% 缩放预期；先修测试中对 `MainWindow.xaml` 文件名和控件位置的硬编码。
2. **统一设置状态所有权。** 所有设置编辑先进入 `SettingsWorkflow.Current`；主窗体运行逻辑不再读取设置控件。保持现有退出保存兜底。
3. **拆 `SourceManagerWindow`。** 它的局部性最好，可先验证窗口工厂、Owner、单实例、Esc、焦点恢复和事件解绑。全局 `ScriptUpdateAlert` 留在常驻层。
4. **拆分类 `SettingsWindow`。** 采用左侧类别列表 + 右侧滚动内容；迁移热键录制/冲突定位，保留 HWND 与全局派发在主窗体。
5. **拆 `DownloadManagerWindow`。** 直接绑定工作流任务，以 `SettingsWorkflow.Current` 提供目录；隐藏窗体不取消任务。
6. **拆 `PlaylistImportWindow`。** 最后处理歌曲预览能力、局部按键和 Esc 两级返回；登录子窗体 Owner 改为导入窗体。
7. **删除旧标签与索引分支。** 主窗体按命名 TabItem 导航，保留 Alt+1~8 目的地；同步帮助文案和测试。

该顺序刻意先建立状态和一个低风险窗口样板，再移动高耦合界面；不需要一次性改写 MainWindow 或引入 MVVM 框架。

## 风险与验证清单

### 主要风险

- **双重设置状态**：窗体控件、`SettingsWorkflow.Current` 与运行模块不一致。
- **重复事件订阅**：隐藏/重开音源窗体导致一次 `SourcesChanged` 多次刷新或播报。
- **全局提示丢失**：把 `ScriptUpdateAlert` 绑定到仅在打开时存在的窗体。
- **子窗体按键失效**：主窗体 `PreviewKeyDown` 不会处理另一个窗口的路由事件，导致局部热键、应用键或 Esc 语义改变。
- **退出/托盘竞态**：子窗体仍可交互时主窗体已进入 `CaptureAndBlockShutdown`。
- **无障碍回退**：移动 XAML 后现有静态测试仍只查 `MainWindow.xaml`，形成假绿。
- **高缩放截断**：直接复制当前固定宽度横向布局到更窄的设置窗体。

### 自动化验证

- `ApplicationWindowFactory` 和宿主注册只产生一个共享业务模块实例；窗口重复打开不重复订阅。
- 设置编辑后 `SettingsWorkflow.Current`、播放/歌词/下载/TTS 的运行状态和最终持久化一致；设置窗体从未打开也能安全退出。
- Alt+1~8 保留原目的地；主窗体四个标签的焦点首尾规则不再引用旧索引。
- Source 列表重开后一次 `SourcesChanged` 只刷新一次；`ScriptUpdateAlert` 在管理窗体关闭时仍公告。
- 下载窗体隐藏后任务继续更新，重开看到同一 `Tasks` 集合。
- 导入预览的 Enter/Shift+Enter/应用键/Shift+F10/Esc 行为保持。
- 无障碍扫描覆盖新增全部 XAML：焦点控件有原生或短显式名称、零普通 HelpText、音源/下载/快捷键动态状态仍只在聚焦 Name 出现一次。

现有测试需要特别迁移的硬编码包括：`AccessibilitySemanticsTests` 对 `CloseBehaviorCombo`、`SourceList`、`DownloadTaskList` 所在 `MainWindow.xaml` 的假设，以及 `SettingsWorkflowTests` 对主窗体包含快捷键列表样式的假设。

来源：`src/LXMusicAccessible.Tests/AccessibilitySemanticsTests.cs:63-107,138-188,224-334,591-675`、`src/LXMusicAccessible.Tests/SettingsWorkflowTests.cs:203-226`、`src/LXMusicAccessible.Tests/ApplicationHostTests.cs:20-74`。

### 人工验证

- 只用键盘完成四个独立窗体的打开、类别切换、操作、关闭和回焦。
- Narrator 与 NVDA 检查窗口标题、类别选中状态、控件名称、忙碌/成功/错误公告；条件允许时补 JAWS。
- 100%、150%、200% 缩放检查设置分类、长路径、下载任务和音源列表，无截断、遮挡或必须横向滚动的主操作。
- 设置/音源/下载/导入窗体分别打开时验证 Esc、Alt+F4、托盘隐藏/恢复和应用退出。

自动化与人工边界沿用仓库现行规范：确定性的 UIA 语义、事件和焦点副作用自动化；真实键盘与读屏器流程保留为发布前人工检查。

来源：`docs/testing.md` 的 “WPF accessibility semantics”；`tasks/comprehensive-remediation-spec-2026-08-23.md:316-338`。
