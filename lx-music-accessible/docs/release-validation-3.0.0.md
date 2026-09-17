# LX Music 无障碍版 3.0.0 发布验证记录

**状态：自动化发布验证已通过；公开发布前，真实键盘与读屏人工验收仍待执行。** 本文件是 3.0.0 的正式发布验证和人工验收记录。Issue #58–#63 均已在 GitHub 上关闭，状态为 `COMPLETED`；父规范 #57 保持打开，等待根工作流收尾。

## 发布范围与前提

- 产品版本：3.0.0；应用：.NET Framework 4.8 x64 原生 WinForms；发布：Windows x64。
- #58 业务层迁移、#59 WinForms 全量宿主、#60 LX 音源兼容、#61 五平台官方歌单、#62 WinForms 无障碍与键盘、#63 数据/安装器/发布已关闭。
- 自动化测试已覆盖上述实现区域：`Net48MigrationTests`、`WinFormsIssue59Tests`、`WinFormsAccessibilityAuditTests`、`LxScriptRuntimeCompatibilityTests`、`OfficialPlaylistProviderTests`、`MusicBrowsePagingTests`、`WinFormsPlaylistPagingTests`、`LegacyDataCompatibilityTests`、`DotNetFrameworkRequirementTests` 和 `ReleasePackagingTests`。
- 3.0 不提供应用 self-contained .NET 8 产物；安装器和应用均为 .NET Framework 4.8 x64 框架依赖程序；安装器启动时检测目标机运行时。

## 运行时、升级与安装器行为

- 必须使用 64 位 Windows。应用需要 Microsoft .NET Framework 4.8 或更新版本；安装器读取 32 位视图注册表 `Release` 值，要求不低于 `528040`。
- ZIP 不包含应用 .NET 运行时。安装器检测失败时显示“Microsoft .NET Framework 4.8 required”、官方下载地址和诊断路径；非交互 smoke 模式返回 exit code 2。
- 诊断写入 `%LocalAppData%\LXMusicAccessible\setup\net48-diagnostics.txt`，记录时间、`installed`、required release、当前 release 和诊断摘要。
- 网页登录需要 Microsoft Edge WebView2 Runtime。BASS/APE/FLAC/Opus、SQLite、ClearScript V8、Prism、Tolk、保益桥接和 WebView2 Loader 的 x64 原生库随应用分发；ClearScript V8 不可用时回退 Jint。
- 数据默认路径继续为 `%LocalAppData%\lxmusic`；目录指针 schema 为 1，默认目录所有权记录继续使用 `.lxmusic-default-owner.json`。覆盖安装保留 SQLite（schema `user_version=1`）、设置、歌单、登录凭据、快捷键和可信音源。
- 可执行文件名继续为 `落雪音乐无障碍版V2.0.exe` 以保留升级身份；其产品/文件版本和安装器注册表版本为 3.0.0。保留卸载会保留未知文件和用户数据；完全卸载只移除所有权清单允许的安装内容。

## LX 音源与官方平台

- LX 音源支持 `musicUrl`、`lyric`、`pic`、source/action/quality 声明、请求处理器、超时/取消、重定向、HTTP(S) 地址安全、响应大小限制、更新提醒、JSON 声明源、信任/启用/持久化、脚本摘要校验和常用 crypto/Buffer/zlib 兼容面。
- 搜索支持网易云、QQ、酷狗、酷我、咪咕。
- 官方分类歌单和排行榜支持网易云、QQ、酷狗、酷我、咪咕五个平台；分类/标签、推荐、歌单分页和歌曲分页由各 provider 实现，工作流执行 latest-wins 取消和显式触发加载。
- 个人歌单导入支持网易云、QQ、酷狗、酷我；登录方式按平台为网页登录、Cookie 或酷狗扫码。

## 3.0.0 产物

SHA-256 均为小写。`release-manifest.json` 记录 15 个载荷文件及以下两个发布产物；构建脚本已独立复核全部哈希。

| 产物 | 字节 | SHA-256 |
| --- | ---: | --- |
| `LXMusicAccessible-3.0.0-win-x64-framework-dependent.zip` | 22,006,165 | `23f494a79ce67409aab20d25fe41b221a9f22584c616123078e85f174c9b10d1` |
| `LXMusicAccessible-Setup-3.0.0-win-x64.exe` | 22,639,104 | `f873620b453092bfaeb54678fd54c7d2ba6208b5ee91ac8637ebb24773dd533c` |
| `release-manifest.json`（清单，不入哈希清单） | 4,587 | 构建内逐字段校验 |

清单运行时元数据：`version=3.0.0`、`runtime=win-x64`、`framework=net48`、`sqliteVersion=3.53.3`；生成时间 `2026-09-17T04:05:30.1681085Z`。

## 自动化回归与最终质量门

- 功能回归覆盖搜索、播放/停止/上一首/下一首/seek/音量、下载、本地和在线歌单、五平台官方歌单、排行榜、设置、音源、登录/导入、歌词、局部/全局热键和托盘生命周期。对应测试包括 `MainWindowShellTests`、`Issue49PlaybackSourceSafetyTests`、`DownloadWorkflowTests`、`PlaylistSnapshotTests`、`OfficialPlaylistProviderTests`、`SettingsWorkflowTests`、`ScriptSourceLifecycleTests`、`PlaylistLoginHostTests`、`LyricsWorkflowTests`、`Issue49HotkeyTests` 和 `DesktopLifecycleWorkflowTests`。
- `powershell -NoProfile -ExecutionPolicy Bypass -File build_release.ps1`：exit 0。10 个应用原生映像和安装器 EXE 均验证为 x64；SQLite 加载版本 3.53.3，基线 3.50.2；产物内容、版本和 SHA-256 校验通过。
- `powershell -ExecutionPolicy Bypass -File verify.ps1`：exit 0。
  - Restore：通过。
  - 依赖审计：High/Critical advisories 为 0。
  - Release build：通过；warnings treated as errors，未出现 warning/error。
  - 离线测试：**executed 216，passed 216，failed 0，skipped 0**。
  - 文本检查：通过，183 个 tracked text files；`git diff --check` 通过。
  - 主机/异常静态检查：通过，139 个 C# source files。
  - 发布重建/产物/SQLite/native/hash 检查：通过。
- 安装器 smoke 全部通过：first install 成功；upgrade 成功且 ownership ID 不变；preserving uninstall `accepted=True; failures=0; preserved=True`；complete uninstall `accepted=True; failures=0; preserved=False`；repeat uninstall `accepted=True; failures=0; preserved=False`。
- 验证中仅有 Git 的 CRLF working-copy 提示，非测试/构建错误；UTF-8 检查通过。没有其他警告或错误。

## 键盘与读屏自动化结论与限制

- 已自动验证：语义 AccessibleName、原生菜单/标签/列表、显式 Enter/Space/菜单触发、焦点落点和恢复、空列表 shell 焦点、后台更新不抢焦点、选择变化不触发网络/播放/删除/导入、禁用状态、进度/成功/失败 UIA 通知和可选语音策略。
- 自动化边界：没有启动 Narrator/NVDA/JAWS，没有合成完整真实键盘会话，也没有进行真实音乐服务或真人可听性评价。这些是下节的公开发布前置条件。

## 公开发布前人工验收清单

**所有行必须在公开发布前填写 Owner 和 Result。任何失败必须记录缺陷并重新验证；未全部通过不得公开发布。**

### A. 真实键盘-only 工作流

| ID | 检查 | Owner | Result / Notes |
| --- | --- | --- | --- |
| K1 | 只用键盘完成安装、首次启动、搜索/浏览、播放/暂停/停止、上一首/下一首、下载、歌单导入、源切换、设置、歌词和退出 | 待指定 | 未运行 |
| K2 | `Alt`、`F10`、助记键、`Tab`、`Shift+Tab`、`Enter`、`Space`、方向键、`Home`、`End`、`Page Up/Down`、`Delete`、`Esc` 在每个生产窗体中遵循原生 WinForms 行为 | 待指定 | 未运行 |
| K3 | 非空列表聚焦当前/第一项，空列表保持列表 shell 焦点；后台刷新不抢焦点；对话框恢复 owner/focal task；选择或焦点变化不触发网络、播放、删除或导入 | 待指定 | 未运行 |
| K4 | 禁用操作、进度、完成、取消、源变化、登录/导入结果和错误不看屏幕也可感知 | 待指定 | 未运行 |
| K5 | 托盘显示/恢复、播放命令、退出、全局/窗口热键、热键录制/清除/取消 | 待指定 | 未运行 |

### B. Narrator / NVDA / JAWS

每个读屏器单独完成 B1–B4，并记录 Windows 版本、读屏器版本、发声输出和日期。

| ID | 检查 | 读屏器 / 版本 | Owner | Result / Notes |
| --- | --- | --- | --- | --- |
| B1 | 逐窗体检查简洁角色/任务名、列表/表格语义、选择和状态、无重复控件类型词、无冗余 HelpText/描述 | 待指定 | 待指定 | 未运行 |
| B2 | 播放、下载进度/完成/错误、歌单导入、源变化、设置错误、登录/二维码/网页状态和歌词可被实时通知或以其他可靠方式感知 | 待指定 | 待指定 | 未运行 |
| B3 | 搜索、浏览分页、歌单预览/导入、源刷新/启用/禁用/删除、下载状态、对话框关闭、窗口隐藏/恢复后的焦点顺序/名称/状态公告 | 待指定 | 待指定 | 未运行 |
| B4 | 记录每个读屏器已知限制、变通方法、失败用例和截图/日志引用 | 待指定 | 待指定 | 未运行 |

### C. 五平台官方歌单真实浏览

在至少 Narrator 加 NVDA（JAWS 可用时也执行）下，逐平台完成。

| 平台 | 分类/标签 | 歌单分页 | 歌曲分页 | 显式激活 | 快速变化取消 | 空/错误状态 | 焦点稳定 | Owner | Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 网易云 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 待指定 | 未运行 |
| QQ | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 待指定 | 未运行 |
| 酷狗 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 待指定 | 未运行 |
| 酷我 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 待指定 | 未运行 |
| 咪咕 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 未运行 | 待指定 | 未运行 |

### D. 安装/升级真实验收

| ID | 检查 | Owner | Result / Notes |
| --- | --- | --- | --- |
| D1 | 安装、首次启动、升级保留数据、保留卸载、完全卸载和重复卸载尽量键盘-only 完成 | 待指定 | 未运行 |
| D2 | 缺少/旧版 .NET Framework 4.8 时确认错误、官方下载路径和诊断文件可见、可操作 | 待指定 | 未运行 |
| D3 | 保留卸载保留用户内容；完全卸载移除应用但不删除父目录/用户数据；重复卸载安全 | 待指定 | 未运行 |

## 最终发布签署

| 角色 | 名称 | 日期 | 结论 |
| --- | --- | --- | --- |
| 自动化发布验证 | Codex ticket owner | 2026-09-17 | 通过（本文件上方证据） |
| 真实键盘验收 | 待指定 | 待定 | 未通过/未运行 |
| Narrator 验收 | 待指定 | 待定 | 未通过/未运行 |
| NVDA 验收 | 待指定 | 待定 | 未通过/未运行 |
| JAWS 验收 | 待指定 | 待定 | 未通过/未运行 |
| 发布批准 | 待指定 | 待定 | 未批准 |

真实键盘、Narrator、NVDA、JAWS 和五平台浏览验收尚未运行；这些人工结果 pending 时，本版本不得标记为公开发布可访问。