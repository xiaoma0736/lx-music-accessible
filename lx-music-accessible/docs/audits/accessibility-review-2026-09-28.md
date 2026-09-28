# 无障碍全面审查 — 2026-09-28

## 一、范围、方法与结论

**范围**：`lx-music-accessible` 的全部用户界面与状态反馈链路 —— 主窗口（`WinFormsShell`）、7 个工具窗口（音源管理、下载管理、导入个人歌单、歌词、切换音源、使用说明、歌曲详情、赞助）、3 个登录窗口、设置窗口、通用输入对话框、原生菜单栏、系统托盘菜单、安装程序对话框，以及产生语音与状态提示的服务层（`MusicPlayerService`、`MusicBrowseWorkflow`、`DownloadWorkflow`、`LyricsWorkflow`、`SettingsWorkflow`）。

**基线**：`accessibility-development-constraints:winforms-accessibility` 技能规范（原生 WinForms 优先、语义名称、显式 TabIndex、纯容器不进 Tab 链、焦点移动不执行工作、传统菜单、ListView/ListBox 选择、显式命令激活），以及通用无障碍基线（状态消息必须可感知、失败不得静默）。

**方法**：

1. 逐个文件通读界面代码与提示调用点，按规范条目对照；
2. 依靠运行时审计：`WinFormsAccessibilityAuditTests.Every_instantiated_production_form_has_semantic_names_and_native_focus_policy` 实例化全部 14 个生产窗口并遍历每一层控件，检查名称、角色词、`AccessibleDescription`、容器焦点、同一容器内重复 `TabIndex`；
3. 依靠源码扫描：`Interactive_controls_declare_their_tab_index_in_source`、`No_user_interface_is_created_hidden`、`WinFormsIssue59Tests.WinFormsAccessibilityAudit`（禁止 `MenuStrip`/`ContextMenuStrip`/`ToolStrip`/`DataGridView`/`FlowLayoutPanel`）；
4. 语音冗余单独审查：`AccessibleSpeechPolicy` 白名单 + 调用点 `AnnouncementSpeech.None` 双重控制，由 `SpeechWhitelistTests` 逐条锁定；
5. 每轮改动都跑 Release 构建、完整测试集、`scripts/check-text.ps1`、`git diff --check`。

**结论**：界面整体符合规范。本轮修复 7 处问题（1 处让“语音播报”总开关真正生效，1 处把不可见的播报镜像改成可读的播报行，1 处修复第二次启动后窗口不可达，1 处纠正活动标识，2 处继续削减冗余朗读，1 处接通四个平台的登录流程）；上一轮在本审查过程中已修复 9 处，一并记录。另有 11 项有意偏离与设计决定写入第五节；第六节原来待决策的登录流程已按产品决定实现，见 F7。

**验证**：Release 构建 0 错误 0 警告；自动化测试 455 条全部通过（冗余朗读 +4 条、登录流程 +8 条、登录窗口工具栏与恢复命令 +1 条，登录标记修正并入既有用例）；`verify.ps1` 七步质量门全部通过；文本编码检查覆盖 200 个受版本控制文本文件通过；`git diff --check` 无空白错误。

## 二、逐条对照规范

### 2.1 可访问名称

| 规范要求 | 现状 | 结论 |
| --- | --- | --- |
| `AccessibleName` 只放语义标签 | 统一经 `WinFormsUi.Name`/`Caption`/`ListBox`/`Combo` 设置，名称即任务含义：搜索关键词、搜索结果、我的歌单、下载任务、朗读声音、设置分类、指南章节 | 符合 |
| 名称不含控件类型词 | 审计 `ContainsRoleWord` 按控件类型禁止“按钮/复选框/组合框/输入框/列表控件/选项卡”等词，任何名称含这些词即失败 | 符合 |
| 不给普通控件加冗长 `AccessibleDescription` | 全项目 `AccessibleDescription` 出现次数为 0，审计逐控件断言为空 | 符合 |
| 只读动态文本朗读自己的 `Text` | 状态栏 3 个标签、各窗口的空列表提示与状态标签都只设 `Text`；`AssertReportsItsOwnText` 断言它们不得固定 `AccessibleName`、不得进 Tab 链 | 符合 |
| 程序化英文名称不得泄漏给读屏器 | 审计要求每个可交互控件的生效名称至少含一个汉字，否则失败（挡住了只设 `Name` 不设 `AccessibleName` 的情况） | 符合 |

### 2.2 键盘与焦点

| 规范要求 | 现状 | 结论 |
| --- | --- | --- |
| 每个可聚焦控件显式声明 `TabIndex` | 6 个界面文件由源码扫描强制；安装对话框手工补齐 1~5（`src/LXMusicAccessible.Setup/Program.cs:271-281`） | 符合 |
| Tab 顺序贴合任务流 | 主窗口每页先输入/选择控件、再命令按钮、再结果列表；设置各分类按“说明—编辑—命令”顺序；导入窗体先平台、再 Cookie、再歌单、再预览与导入 | 符合 |
| 焦点移动不得触发昂贵工作 | `WinFormsShell.Workflows.cs:94` 的 `ChangeTabPage` 用 `_pageLoadRequested` 门控：方向键与 `Ctrl+Tab` 只移动焦点，`Ctrl+1~4`、“导航”菜单、`Ctrl+Up/Down`、`Ctrl+R` 才加载；`Shell_selection_changes_do_not_start_browse_or_playback_work` 逐控件验证 | 符合 |
| 真实工作必须由显式动作触发 | 列表用 Enter/空格播放、Enter 进入下一级、Shift+Enter 看歌词、Backspace 返回上级、回车激活列表末尾“加载更多”条目；文本框路径只在 Enter 或关闭窗口时写入 | 符合 |
| 纯容器 `TabStop = false` | `StatusBar`、`Panel`、`FlowLayoutPanel`、说明性 `Label` 全部 `TabStop = false`；审计把“可聚焦容器”（`TabControl` 除外）判为错误 | 符合 |
| 避免重复/不稳定焦点目标 | 每类工具窗口单实例（`BringToolToFront`，`ADR-0002`）；设置关闭后把焦点还给打开前的控件（`ShowSettings`）；下载列表进度刷新禁用当前按钮时把焦点拉回任务列表；歌词窗口保持用户放好的阅读位置 | 符合 |

### 2.3 菜单

| 规范要求 | 现状 | 结论 |
| --- | --- | --- |
| 主菜单用 `MainMenu` + `MenuItem` | `BuildMenu` 使用 `MainMenu`；源码禁止 `MenuStrip`/`ToolStripMenuItem` | 符合 |
| 保留经典 Alt / F10 / 访问键 / 方向键行为 | 顶层 文件(F)、播放(B)、导航(D)、工具(T)、帮助(H)，全部条目带 ASCII 访问键并有测试；`Alt+F10` 仍可进入菜单 | 符合（F10 见偏离 D1） |
| 右键与按钮弹出菜单用 `ContextMenu` + `MenuItem` | 歌曲列表、分类歌单、榜单、音源管理、托盘菜单都用 `ContextMenu`；`Shortcut` 只用于标准键 | 符合 |
| 菜单文本简洁、必要时直接写状态 | “语音播报”“歌词播报”是带勾选的状态项；“音量加（默认 F10）”把默认键写进文本；“隐藏/显示窗口（Ctrl+Alt+H）”保留 `Ctrl+H` 给编辑框 | 符合 |
| 自定义 `Alt` 组合不破坏菜单入口 | `ProcessCmdKey` 只接管 `Alt+M/C/P/S/R` 五个明确绑定，`Alt+F10`、单 `Alt`、`F10+Alt` 交给基类（有回归测试） | 符合 |

### 2.4 列表与只读信息

| 规范要求 | 现状 | 结论 |
| --- | --- | --- |
| 结构化表格用原生 `ListView` | 下载管理 4 列（歌曲/状态/进度/文件）、`FullRowSelect`、`MultiSelect`、`HideSelection=false`、`AccessibleName`=下载任务 | 符合 |
| 长文本项用 `ListBox` 整体朗读 | 所有歌曲、歌单、榜单、分类、音源、快捷键列表都是 `ListBox` + `ObjectBox<T>`，一行=一个可朗读单元（“歌手 - 标题，专辑，时长，音源”） | 符合 |
| 只读集合用列表而不是散标签 | 歌曲详情、使用说明章节、歌词都是单一列表/文档控件；状态类信息集中在底部状态栏与播报行 | 符合 |
| 选中列表项不重载数据 | `OnlinePlaylists.SelectedIndexChanged` 只记录选择并禁用“加载更多”，加载由 Enter 或“加载歌单”触发；预览与导入是各自独立命令 | 符合 |

### 2.5 复查清单执行结果

- **纯键盘走查**：14 个窗口全部有可预测初始焦点（列表优先，空列表也聚焦；导入窗体聚焦“网易云音乐”命令，见 `ADR-0004`；歌词与说明窗口聚焦阅读控件；扫码窗口聚焦“刷新二维码”）。
- **Esc / CancelButton**：`Modal_and_tool_forms_have_predictable_escape_behavior` 覆盖对话框 `CancelButton` 与 7 个窗口按 `Esc` 关闭；主窗口 `Esc` 是隐藏到托盘（偏离 D6）。
- **无隐藏界面**：源码扫描禁止 `Visible = false` 创建控件；空列表提示通过前置显示而不是隐藏。
- **状态消息可感知**：见第三节 F2 与第五节 D3。
- **语音冗余**：见第三节与第四节语音部分。

## 三、本轮修复

### F1 “播放模式”绕过“语音播报”总开关

- **问题**：`TogglePlayMode` 用 `AnnouncementSpeech.Always` 播报播放模式，而 `Always` 的语义是“忽略总开关”。用户关掉“语音播报”后，按 F1 仍然被朗读，和“总开关”这个名称矛盾；真正需要独立通道的只有逐句歌词（它有自己的“歌词播报”项）。
- **证据**：`WinFormsShell.Workflows.cs:718`（修复前为 `AnnouncementSpeech.Always`）；`WinFormsCommon.cs:177` 中 `Always` 绕过 `SpeechEnabled`；`LyricsWorkflow.cs:344,363` 使用 `Always` 属正确用法。
- **影响**：关掉朗读的用户每次切模式都被打断一次，属于语音冗余。
- **修复**：改为默认 `FollowPreference`，模式变化仍写进可见播报行，信息不丢。
- **回归测试**：`PlaybackHotkeyStatusRegressionTests.Play_mode_is_spoken_while_announcements_are_on_and_stays_silent_when_they_are_off`。

### F2 播报镜像是不可见的 1×1 标签，关闭朗读后状态无处可查

- **问题**：`WinFormsAccessibleAnnouncer` 把每条提示写进主窗口的 `LiveRegion`，但该标签尺寸 1×1、不渲染，也不在 Tab 链里。朗读开着时没有影响；一旦用户关掉“语音播报”，下载完成、收藏结果、登录结果、快捷键录制提示等就只剩这个看不见的镜像，讲述人扫描模式、JAWS 审阅光标和放大镜用户都拿不到。
- **证据**：修复前 `WinFormsShell.Controls.cs:138` `new Label { Size = new Size(1, 1) }`；使用说明与窗口内“统一播报区域”的描述都承诺有一条可读的提示区。
- **影响**：状态消息只有一个感知通道，违反“状态必须可用多种方式感知”的基线要求。
- **修复**：`LiveRegion` 改为真实可见的一行 `Name = "AnnouncementRegion"`，`Dock = Bottom`、高 22、左对齐，位置在页面区与状态栏之间（`WinFormsShell.Controls.cs:139-155`）；控件顺序按停靠解析规则改为 页面 → 播报行 → 状态栏，状态栏仍占最底边。
- **回归测试**：`PlaybackHotkeyStatusRegressionTests.Announcement_mirror_is_a_visible_line_above_the_status_bar`（断言可见、高度≥20、宽度>300、不与状态栏重叠、文本与 UIA 名称一致）。
- **文档**：使用说明第一节、第二节、第九节“无障碍”与 FAQ 第 7/10 条，`README.md`、`KEYBOARD_NAVIGATION_GUIDE.md` 同步说明“语音播报”是除逐句歌词外所有提示的总开关，以及播报行的存在。

### F3 第二次启动只翻转 `Visible`，隐藏到托盘的窗口仍不可达

- **问题**：单实例检测把已存在的窗口设为 `Visible = true`。但“隐藏到托盘”同时把窗口置为最小化并取消任务栏按钮，仅翻转 `Visible` 会得到一个在屏幕上却无法用键盘或 Alt+Tab 找到的窗口，用户第二次启动程序后没有任何反馈。
- **证据**：修复前 `Program.cs:165` `shell.BeginInvoke(() => shell.Visible = true)`；`WinFormsDesktopIntegration.HideToTray()` 显式设置 `WindowState = Minimized`、`ShowInTaskbar = false`、`Visible = false`，并从属窗口一并隐藏。
- **影响**：一个明确的用户动作（再次启动程序）静默失败，且窗口失去可达性；对读屏用户是彻底失联。
- **修复**：新增 `WinFormsShell.RestoreForSecondLaunch()`（`WinFormsShell.Workflows.cs:1097`），走与托盘“显示主窗口”完全相同的 `DesktopCommand.Restore` 路径（恢复任务栏按钮、还原窗口、激活、带回从属工具窗口），没有桌面服务时才退化为直接还原。
- **回归测试**：`PlaybackHotkeyStatusRegressionTests.Second_launch_restores_a_window_that_was_hidden_to_the_tray`（隐藏后断言不可见，恢复后断言可见、`ShowInTaskbar` 为真、窗口状态为 `Normal`）。

### F4 “禁用音源”复用了“启用音源”的活动标识

- **问题**：`SourceManagerForm.Disable()` 用 `source-enable` 播报禁用结果，标识与实际命令相反，后续按活动做降噪或诊断时会误判。
- **修复**：新增 `source-disable` 白名单项并改用该标识，成功结果改为 `AnnouncementKind.Success`（`WinFormsToolForms.cs:154-160`、`AccessibleSpeechPolicy.cs:43`）。
- **回归测试**：`SpeechWhitelistTests` 允许清单加入 `source-disable`。

### F5 “已返回上一级列表”只靠白名单沉默，调用点上看不出来

**问题**：`HandleBrowseBack` 与 `ReturnToSearchAsync` 的 5 条“已返回歌单列表/分类列表/榜单列表/搜索结果”不朗读，靠的是 `AccessibleSpeechPolicy` 里没有 `browse-back` 这一族。但“回显一次焦点移动”本身就属于规范要求削减的噪声：读屏器一定会朗读刚刚取得焦点的列表名称，再念一句“已返回×列表”是把同一件事说两遍。只靠白名单意味着以后有人把这一族加进白名单就会悄悄回潮。

**修复**：5 个调用点全部显式传 `speech: AnnouncementSpeech.None`（播报行仍然留下记录，供关闭朗读的用户查看），并在方法注释里写明“返回的结果就是焦点移动”；`Issue72BrowseNavigationTests` 在原有“焦点落在上一级列表”断言之后补一条 `Speech == AnnouncementSpeech.None` 断言锁住它。

### F6 音源管理窗口把一次命令念成两句话

**问题**：窗口内的导入、设为默认、启用、禁用、删除、检查更新都由命令自己播报结果（如“已禁用音源：X”）。同时 `MusicSourceService` 在后台保存完成后触发 `SourcesChanged`，窗口的延迟刷新 `RefreshSourcesAfterExternalChange` 又补一句“音源列表已更新”。两句都是同一次按键的产物，而且谁先出现取决于“事件排队的延迟刷新”和“命令的 await 续体”谁先跑：续体先跑时列表已经刷新，回显自动消失；事件先跑时用户听到两条互不相干的提示。这类顺序依赖的不确定性对话术引擎用户尤其刺耳。

**修复**：新增 `volatile bool _sourcesChangingByThisWindow`，六个命令经 `InSourceCommand(...)` 执行变更，事件处理在触发的那一刻读取标记并把它带进延迟刷新；标记为真时列表照旧刷新、选中项照旧恢复，只是不再补那句回显。来自窗口之外的变化（另一个窗口导入、外部改脚本目录）仍然朗读，这正是该提示唯一有用的场景。

**测试**：`Source_manager_command_is_announced_once_without_the_refresh_echo` 用“保存后再后台触发事件”的假服务复现真实顺序，去掉标记即失败；既有的 `Source_manager_announces_external_source_changes` 继续锁住外部变化必须朗读。

### F7 四个平台的登录窗口全部没有入口，说明书写明的登录流程走不到

**问题**：`使用说明` 第六点七节承诺“按下平台按钮会打开该平台的登录方式窗体”，FAQ 第 6 条与系统要求里的 Edge WebView2 也都建立在这条路径上；但 `IPlaylistLoginWindowFactory` 的三个方法在产品代码里没有任何调用点，`PlaylistImportForm._login` 只被赋值后闲置。用户实际只能用粘贴 Cookie 一条路，网页登录与酷狗扫码窗体是进不去的死代码。此外 `WebLoginForm.LoginUrl` 缺少酷狗分支，任何以 `kg` 打开的网页登录都会落到网易云网址并按网易云 Cookie 校验；`CompleteAsync` 只在点击“完成登录”时才读取会话，与文档承诺的“自动检测登录状态并关闭网页”不符；页面每次跳转都重播同一句说明。

**修复**：

1. 平台按钮改为“选择平台并进入登录方式窗体”（`SelectPlatform`）：按文档承诺打开该平台的登录方式列表（四个平台统一“网页登录、粘贴 Cookie 登录”，酷狗多一项“APP 扫码登录”），并按所选方式分派——网页登录、酷狗扫码、或把焦点交给 Cookie 输入框；取消选择时焦点回到刚才的平台按钮。
2. 登录结果以“登录窗口报告完成”并且“服务里确实存下了账号”两者同时成立为准（服务只在平台确认会话后才保存账号；以前登录过、这次取消不算成功）：成功时播报账号名并把焦点交给“加载歌单”这一步，否则说明原因并回到原平台命令。
3. 网页登录窗口加入会话监视：每两秒读取一次浏览器 Cookie，只有出现该平台真正的登录标记（网易云 `MUSIC_U`、QQ `uin`+`qm_keyst`/`qqmusic_key`、酷狗 `KugooID`+`t` 或 `userid`+`token`（见 F8）、酷我 `userid`+`websid`，且 id 为 `0` 不算）才自动提交确认并关闭窗口。每次页面加载只自动确认一次，“刷新页面”或页面再次跳转都会重新开启一次；因此一次被平台拒绝的确认不会每两秒重播，改用“完成登录”或粘贴 Cookie 即可。
4. 补齐酷狗官网地址与平台名称，页面跳转多次只说明一次（同 F5 的削减冗余原则）。

**测试**：`PlaylistLoginFlowTests` 9 条锁定入口分派、三种登录结果、四个平台的登录方式列表与使用说明一致、各平台登录标记与各平台官网地址，以及网页登录窗口的四个工具栏命令。

### F8 酷狗网页登录等的是扫码登录才写的 Cookie，网页路径永远等不到

**问题**：F7 实现的会话监视要求酷狗同时出现 `userid` 与 `token`。这两个名字是酷狗 APP 扫码接口写进账号的（`KugouQrLoginForm.SaveAsync` 拼的正是 `userid=…; token=…`），而酷狗网页登录后浏览器真正下发的会话是 `KugooID` 与 `t`——`KugouPlaylistProvider.ExtractUid/ExtractToken` 读的就是这两个名字，注释里写明 `t` 是网页版、`token` 是客户端版。结果：走粘贴 Cookie 或扫码能登录，走“网页登录”时自动检测永远不触发，用户只能自己找到“完成登录”按钮，与“登录后本窗口会自动关闭”的说明相反；而文档承诺的四平台网页登录实际只有三个平台可用。

**修复**：`WebLoginForm.HasLoginCookies` 的酷狗分支改为两个来源都算：`KugooID` 或 `userid` 提供账号 id，`t` 或 `token` 提供会话密钥，并且 id 值为 `0` 时视为未登录（匿名访问也会留下 id 为 0 的 Cookie）。判定所用的名字与 `KugouPlaylistProvider` 实际读取的名字逐一对齐，并写在方法的 XML 注释里，避免下一轮再各改各的。

**测试**：`Web_login_waits_for_the_session_cookie_the_platform_actually_sets` 增加 `KugooID=88; t=abc` 判为已登录、`KugooID=0; t=abc` 判为未登录两条断言。

### F9 网页登录窗口每次打开都清空浏览器 Cookie，老账号必须重新扫码，也没有换账号的出口

**问题**：`WebLoginForm` 在 `Shown` 里无条件调用 `CookieManager.DeleteAllCookies()`。WebView2 的用户数据目录本来是持久的，登录状态可以跨窗口复用；每次开场清空就把这个优势丢掉了：昨天在网页里登录过的用户，今天按“网页登录”仍要重新扫码或重输密码，对读屏器用户是每天重复一次最难的步骤（二维码只能靠旁人代扫）。同时反向的问题也存在：确实要换账号时，窗口里没有任何入口能丢掉旧状态，只能退出程序去找浏览器目录。

**修复**：

1. 开场不再清 Cookie。已登录的用户打开网页登录窗口后，两秒内的自动确认就会完成登录并关闭窗口，焦点直接落到“加载歌单”。
2. 工具栏新增第三个命令“清除登录记录”（`TabIndex` 3，“取消”顺移到 4，网页焦点序仍在其后）：清掉本程序浏览器的登录 Cookie、重新打开该平台登录页、播报一次“已清除浏览器登录记录，请在网页中重新登录。”，并把说明与自动确认重新开启，用户随后在页面里登录新账号即可。它只影响网页登录窗口自己的浏览器会话，不删除软件已保存的账号，也不影响粘贴 Cookie。
3. 自动确认改为“每次页面加载一次”（`NavigationCompleted` 里重新允许一次），这样沿用的旧 Cookie 被平台拒绝时不至于把整个窗口锁死在手动确认上，也不会退化成每两秒重播同一句失败。

**测试**：`Web_login_window_offers_the_recovery_commands_around_the_page` 锁定四个命令的名称、`AccessibleName`、`TabStop` 与 `TabIndex` 顺序，并锁定 `CancelButton` 仍是“取消”。

## 四、本审查周期内此前已修复（记录备查）

| # | 问题 | 修复 | 位置 |
| --- | --- | --- | --- |
| S1 | “朗读声音”下拉框保存显示文字，语音后端解析不到，重开窗口丢选择 | 列表项改为携带原生 `VoiceInfo` 的 `ObjectBox`，语音列表先于读取设置构建，选择按标识恢复 | `SettingsForm.cs:102-121` |
| S2 | “朗读声音/朗读语速”从未传给语音引擎，是死设置 | 桌面外壳持有 `ITtsService`，`ApplySettings` startup 与每次改动都下发语速与语音，并按上次值防重复切换原生语音 | `WinFormsShell.Controls.cs:25,610-635`、`WinFormsAppHost.cs:417` |
| S3 | 托盘菜单条目没有访问键，键盘无法选择 | 托盘菜单抽成 `BuildTrayMenu`，五项分别 显示主窗口(&S)、播放/暂停(&P)、上一首(&V)、下一首(&N)、退出(&X) | `WinFormsAppHost.cs:220-229` |
| S4 | 下载管理按按钮文字判断该启用哪个命令，改文案就失灵 | 保留三个按钮引用，按所选任务状态直接设置 `Enabled` | `WinFormsToolForms.cs:283-303` |
| S5 | 导入个人歌单打开时聚焦空的歌单列表 | 改为聚焦“网易云音乐”这一开始任务的命令（`ADR-0004`） | `WinFormsToolForms.cs:400` |
| S6 | 退出保存失败时同一句话朗读两遍（模态框 + 播报） | 提示只镜像到播报区，模态对话框即语音通道 | `ApplicationShutdown.cs:190-204` |
| S7 | 歌单预览成功后再念“已预览歌曲”打断读屏器 | 该回声 `AnnouncementSpeech.None`，仅“没有找到歌曲”仍朗读 | `WinFormsToolForms.cs:470-477` |
| S8 | 安装对话框依赖添加顺序、附注用灰色低对比文字 | 显式 `TabIndex` 1~5、说明标签 `TabStop = false`、去掉灰色前景 | `Setup/Program.cs:271-281` |
| S9 | 暂停/停止/音量、浏览与加载回显、返回上级、欢迎语等冗余朗读；歌词窗口句首序号被当作正文反复朗读 | 语音白名单 + 调用点显式静音；歌词改用不带序号的 `DisplayText`（有断言禁止行首数字） | `AccessibleSpeechPolicy.cs:17-57`、`WinFormsToolForms.cs:578` |

## 五、有意偏离与设计决定

| # | 偏离 | 理由与缓解 |
| --- | --- | --- |
| D1 | F10 默认是“音量加”，不承担经典菜单键 | 播放类快捷键按整排功能键布局，用户更需要 F10 调音量；单 `Alt`、`Alt+F10`、菜单访问键均可进入菜单，可在“工具→设置→快捷键”改键；使用说明第二节与 FAQ 第 8 条明确写出，`Play_menu_names_the_default_key_of_volume_up` 锁定菜单文本 |
| D2 | “加载更多”按钮 `TabStop = false` | 同一命令以列表末尾的“加载更多”条目提供，键盘在列表内按回车即可执行；按钮只为鼠标与可见性保留，避免在五个列表之间插入额外 Tab 停靠 |
| D3 | 播报行不参与 Tab 焦点 | WinForms 的 `Label` 不可聚焦，读屏器只能通过审阅光标读取；真正朗读由 TTS 承担。若后续要支持“光标停在播报行反复朗读”，需改为只读文本控件并新增一个 Tab 停靠，属于界面结构调整 |
| D4 | 下拉框用方向键浏览时即写入设置 | 设置项都是内存级即时生效（`ADR-0001` 2026-09-28 修订），有 `_loading` 防回写；文本路径这类昂贵或易错的内容仍只在 Enter/关闭窗口时提交（`Settings_path_boxes_commit_on_enter_and_not_when_focus_moves`） |
| D5 | 不使用 `AccessibleDescription` 提供任何补充说明 | 规范要求并测试锁定；需要的上下文全部放进名称或邻近状态标签 |
| D6 | 主窗口 `Esc` 隐藏到托盘而不是关闭 | 播放器需要持续后台播放；`Alt+F4` 行为由设置决定，FAQ 与速查表写明 |
| D7 | 没有应用清单的 PerMonitorV2 声明，高 DPI 下由系统位图缩放 | 全部布局是固定像素坐标，声明系统 DPI 感知会让界面在 200% 屏上变得过小；当前策略放大但字形清晰。发布检查表仍按 100%、150% 和 200% 三档人工验收 |
| D8 | UI 字体不统一：设置窗口是“Microsoft YaHei UI 9pt”，其余窗口跟随系统默认字体（本机为宋体 9pt） | 统一需重排所有固定坐标并逐窗回归，收益主要是低视力用户的字形；记录为后续改进项，本轮不改 |
| D9 | 登录窗口使用 `FlowLayoutPanel`（`TabStop = false`） | 符合“纯容器不进 Tab 链”；与项目内早期测试的 `FlowLayoutPanel` 禁用清单不一致，但该清单只在主窗口和一个工具窗口上执行。P1 接通登录窗体后这一偏离仍然存在，维持原样 |
| D10 | 21 个 WPF 时代测试文件仍留在仓库但被 `<Compile Remove>` 排除（含断言 `MainWindow.xaml` 的旧审计） | 它们不参与构建，既不保护也不产生假阳性；属于死代码，建议删除而不是恢复 |
| D11 | 安装程序对话框不受运行时审计保护（独立程序集，不在 App 内） | 目前靠源码显式 `TabIndex`/`TabStop` 与注释约束；若要纳入审计需要跨程序集实例化对话框，成本高、收益低 |

## 六、产品决策：登录流程（已定并实现）

**P1 登录方式窗口在生产代码中没有入口。** 审查时 `IPlaylistLoginWindowFactory` 的三个方法没有任何调用者，`PlaylistImportForm` 只保存平台标识，因此“选择平台后打开登录方式窗口”“网页登录”“酷狗 APP 扫码登录”都到不了用户面前，唯一可用的是粘贴 Cookie；而使用说明第 6.7 节步骤 1~2、FAQ 第 6 条和系统要求里的 WebView2 都在描述这套流程。当时列为需要产品决策的两个选项：**接通**（补一个命令显式调用工厂）或**收缩**（删掉三个窗口并改写文档）。

**决定（2026-09-28）**：接通，并且网易云、QQ、酷狗、酷我四个平台都要能登录，网页与二维码两种方式都算数。已按该决定实现，见第三节 F7。

**本轮验证到的层次（必须如实区分）**：

- 已用自动化验证：平台按钮确实打开该平台的登录方式窗体、三种登录选择各自的分派、登录成功/未成功/取消三条路径的状态文案与焦点去处、四个平台的登录方式列表与使用说明一致、各平台登录 Cookie 标记的判定（含 QQ 匿名访问留下的 `uin=0` 不算登录、酷狗网页版 `KugooID`+`t` 与扫码版 `userid`+`token` 都算登录）、四个平台各自的官网地址、网页登录窗口四个工具栏命令的名称与 Tab 顺序。
- 未能自动化验证（本机没有真实账号，也不应把真实凭据写进测试）：真实登录一次能否在各平台官网走完、平台接口是否确认这次会话、酷狗扫码轮询的真实返回、以及酷我在页面内反爬场景下的昵称/歌单拉取。网页登录仍保留“完成登录”与“刷新页面”两个手动出口，就是为这条未验证路径准备的恢复手段。
- 各平台官网在窗口内的实际登录界面（扫码区、手机号输入框、验证码）由平台自己实现，键盘可达性只能由平台决定；这一点无法由本程序保证，粘贴 Cookie 是不依赖网页可达性的替代路径。
- 酷我网页登录目前只把 Cookie 交给服务确认（`LoginWithBrowserResultAsync` 的昵称与歌单参数仍为 `null`）：账号能存下、昵称回退为“酷我音乐用户”，我的歌单依赖 `nplserver` 兜底接口。要拿到真实昵称需要在页面内执行脚本取反爬结果，属于后续增强，不在本轮改动内。
- 四个平台的登录方式列表统一为“网页登录、粘贴 Cookie 登录”，酷狗再额外保留“APP 扫码登录”；各平台官网自带的扫码/短信/密码登录在网页登录里同样可用。`WebLoginForm` 已补齐酷狗官网地址与该平台的登录标记，`PlaylistLoginMethodForm.Methods` 与之对齐，并有测试锁定这张表。

## 七、验证记录

| 项目 | 命令 | 结果 |
| --- | --- | --- |
| Release 构建 | `dotnet build src/LXMusicAccessible.sln -c Release` | 0 错误 0 警告 |
| 自动化测试 | `dotnet test src/LXMusicAccessible.sln -c Release --no-build` | 455 通过 / 0 失败 / 0 跳过（F7 +8、F8/F9 +1，冗余朗读 +4；耗时 17 秒） |
| 测试挂起复现 | `dotnet test … --no-build`（F9 之前的一次运行） | 同一命令有一次挂起 3 分钟无输出，手工结束；随后带 `--blame` 的完整运行 20.8 秒 455 条全通过，Blame 报告“所有测试都已运行完毕，将不生成序列文件”，即本轮没有测试卡在运行中。与第四节记录的既有不稳定性同象，仍列在待排查项 |
| 完整质量门 | `powershell -File verify.ps1` | 7 步全部通过：还原、依赖高危漏洞 0、Release 构建 0 错误 0 警告、离线测试 455 通过、测试计数、文本与源码约束（200 个受控文本文件、153 个 C# 源文件）、干净发布与安装程序冒烟（`artifacts/release`）。F8/F9 改动后重跑一次，同样七步全通过 |
| 新增未受控文件的编码 | 逐文件字节检查 | 本轮新建或改动的 `WinFormsLoginForms.cs`、`PlaylistLoginFlowTests.cs`、`使用说明.md`、`KEYBOARD_NAVIGATION_GUIDE.md` 与本审查记录均为 UTF-8 无 BOM、行尾全部是 CRLF（没有孤立回车、没有重复换行、没有只含 LF 的行），且不含 NUL 字节 |
| 清理 | 删除会话遗留脚本与临时文件 | 25 个 `.tmp-*` 脚本/片段、`__pycache__/`、父目录的一次 `verify.ps1` 转储日志；并从版本库中删除 `src/kw_smoke.csx`（一次性联网冒烟脚本，内含疑似真实酷我账号 id 与 websid，不属于任何工程、也不参与构建） |
| 文本编码 | `powershell -File scripts/check-text.ps1` | 200 个受控文本文件通过 |
| 单独运行测试类 | `dotnet test --filter FullyQualifiedName~Issue74ListActivationFocusTests` | 既有波动，与本轮改动无关：`Search_load_more_entry_activates_on_double_click` 在本类单独运行时稳定失败，整集 455 条运行时通过。已确认失败点是搜索结果中始终没有出现“加载更多”条目；`MusicBrowseWorkflow` 在结果被更新的选择取代时返回 `Superseded`、消息为空、界面既跳过填充也不朗读，因此怀疑该类用例之间存在后台工作/选择版本的耦合。成因尚未定位，不臆测修复，留待单独排查 |
| 质量门一次性现象 | 第一次 `verify.ps1` | 第 4 步离线测试在 `Issue74ListActivationFocusTests` 附近挂起（测试宿主 CPU 不再增长，9 分钟无进展，手工结束）；完全相同的命令随后 16 秒跑完 446 条，第二次 `verify.ps1` 七步全通过。属同一类既有不稳定的另一面，与本轮改动无关，一并写进待排查项 |
