# 音质探测可靠性与换源扫描并发设计

## 背景

上一轮（`docs/superpowers/specs/2026-10-16-batch-selection-quality-playback-design.md`）把“切换音源”改成“枚举全部已启用接口 × 全部声明音质”，并把“播放音质 / 下载单曲音质”子菜单改成真实探测。上线后出现两个回归：

1. 播放、下载的“音质”子菜单探测失败：菜单项统一显示“探测失败，请重新打开菜单”，或整轮没有任何可播放项。
2. 打开“切换音源”窗口后声音界面完全卡死，直到扫描结束（数分钟），期间连搜索、播放和退出都无响应。

本轮先彻底审查音质声明、URL 解析、媒体探测、播放校验四条链路，再按审计结论修复，不重写既有 seam。

## 现场证据

用户机器数据目录 `%LocalAppData%\lxmusic`，启用了 16 个脚本音源（`logs/app-20261016.log`：`脚本源初始化完成，共 16 个音源`）。

`logs/app-20261016.log` 21:50 段：

```
21:50:32.911 搜索音乐: 无奈的思绪 嘉慧, 页码: 1
21:50:32.915 QQ音乐搜索 / 酷狗音乐搜索 / 酷我音乐搜索 / 咪咕音乐搜索   ← 换源扫描的 5 个内置接口搜索
21:50:33.621 获取QQ音乐播放URL / 获取酷狗音乐播放URL / 获取酷我音乐播放URL ← 换源扫描的逐音质解析
21:50:34.542 搜索音乐: 无奈的思绪 嘉慧, 页码: 1                      ← 界面无响应，用户重复操作
21:52:18.011 http://search.kuwo.cn 返回（该请求在第 2 次搜索时才发出，等待 104 秒）
21:52:25.825 [ERR] Application shutdown timed out while persistence.
```

两次相同的“搜索音乐”相隔 1.6 秒，说明界面在第一次扫描开始后就已经失去响应；随后已经发出的 HTTP 请求要 104 秒才拿到续体，说明线程池被占满而不是网络慢。这个时间窗内**没有任何脚本源成功日志**，说明 16 个脚本 × 各自声明音质的解析都还堵在队列里。

## 代码路径审查

### 音质声明与实现

| 接口 | 声明音质（`GetSupportedQualities`） | `GetPlayUrlAsync` 实际映射 | 结论 |
| --- | --- | --- | --- |
| netease | `128k` `192k` `320k` `flac` `flac24bit` | `flac24bit`/`flac`→`999000`，`320k`→`320000`，`192k`→`192000`，其余 `128000` | 声明均有映射；`flac24bit` 与 `flac` 同码率，实际拿到 16bit 时探测会判不可用（符合“不得虚构音质”） |
| qq | `128k` | `vkey.GetVkeyServer`，音质参数不参与请求 | 声明即实现 |
| kg | `128k` | `m.kugou.com` 顶层 `url`/`backup_url` | 声明即实现 |
| kw | `128k` | `/api/v1/www/music/playUrl` | 声明即实现 |
| migu | `128k` | `song_info`，`rateType=3` | 声明即实现 |
| 脚本源 | 脚本 `qualitys` 声明（实测 `wy` 6 项、`tx` 8 项、`kg`/`kw` 6 项…） | 快速路径 + ClearScript V8 执行 `musicUrl` | 16 个脚本会让同一平台产生 8 条以上候选 |

### 探测链路（播放/下载音质子菜单）

`IQualityCandidateSource.GetCandidatesAsync`（`MusicSourceService`）→ 组内按 `Priority` 顺序 `ResolveAsync` → `QualityProbeWorkflow.ProbeMediaAsync`（Range GET 256 KB）→ `AudioQualityInspector.Identify` → `MusicQuality.AcceptsDownload`。

候选数量：某平台（如 `qq`）的歌曲，候选 = 该平台内置接口的声明音质 + 全部支持该平台的脚本接口声明音质；实测规模约 100 条。每个候选都要走一次脚本解析（可能创建 V8 引擎）和一次 256 KB HTTP 探测。

### 换源链路

`ManualSourceSwitchForm`（`Shown`/`ShowTarget`/按钮）→ `ManualSourceDiscoveryWorkflow.DiscoverAsync`（默认预算 30 秒）→ `GetPlaybackInterfaces`（内置 5 + 脚本 ≤16）→ 每接口 `SearchCandidatesAsync` → **每个声明音质一个 `ResolveExactAsync`**，`Task.WhenAll` 全并发。

脚本路径 `MusicSourceService.ResolveExactAsync` → `TryResolveScriptUrlAsync`：快速路径 → `IsPlayableAsync`（HTTP，6 秒）→ 失败再 `LxScriptEngine.GetPlayUrlAsync`（`Task.Run` 内 `new V8ScriptEngine`，96 MB 堆上限，执行整段脚本）→ 再一次 `IsPlayableAsync`。

## 缺陷

### 探测侧（对应问题 1）

- **D1 单点异常中断整轮探测**：`ProbeCoreAsync` 只在调用 `ResolveAsync` 时 `try/catch`，`ProbeMediaAsync` 抛出的 `HttpRequestException`、`TaskCanceledException`（8 秒超时）等会直接冒泡到 `ProbeAsync` 的兜底分支，整轮探测变成“音质探测失败：TaskCanceledException”，全部音质一起失败，包括本来可用的音质。
- **D2 失败结果被永久缓存**：兜底分支写入 `QualityProbeResult(音质探测失败…)`，但既不设置 `ExpiresAt` 也不消费 `IsSuccess`。`ProbeAsync` 的缓存判定条件是 `IsCompleted && ExpiresAt > now`，失败条目 `ExpiresAt` 为默认值，于是命中 `Started` 分支并直接返回缓存里的失败结果。`Invalidate` 在生产代码中没有任何调用者，因此一次瞬时失败会让这首歌的子菜单在本次进程生命周期内永远失败。
- **D3 探测请求缺少 User-Agent**：`ProbeMediaAsync` 只设置 `Range`；而播放路径的 `MusicSourceService.IsPlayableAsync` 明确加了 `Mozilla/5.0 (Windows NT 10.0; Win64; x64)`。同一批 CDN 对“无标识的裸请求”返回 403 或 HTML 错误页，探测会判成不可用，而播放却能成功。
- **D4 非音频大响应体被当成请求音质**：`actual is null && (contentType 为 audio/* || bytes.Length > 65536)` 时直接把请求音质当作“实际音质”。错误页/跳转页只要超过 64 KB 就会让子菜单显示“可播放”，用户回车后播放失败。
- **D5 探测并发与时长无界**：音质组之间串行、候选之间串行，每个候选 8 秒上限，约 100 个候选最坏可以拖到十几分钟；同时探测本身也会像 D6 一样并发创建 V8 引擎，是问题 2 的同源风险。

### 换源侧（对应问题 2）

- **D6 解析并发完全无上限**：`DiscoverCoreAsync` 对“接口 × 声明音质”做 `Task.WhenAll`，16 个脚本存在时同时起步 100 条以上解析。每条脚本解析可能创建独立 `V8ScriptEngine`（`MaxOldSpaceSize = 96`），并且在 `Task.Run` 里用 `invocation.GetAwaiter().GetResult()` 阻塞线程池线程，执行至多 30 秒。线程池增长速度为每秒一到两个线程，于是整个进程（含 WinForms 消息处理和 HTTP 续体）被饿死，表现为“卡死”；现场日志里 HTTP 响应延迟 104 秒、退出超时正是这个现象。
- **D7 窗口无重入保护与取消**：`ManualSourceSwitchForm` 每次 `Shown`、`ShowTarget` 或点“搜索可用音源”都 `_ = SearchAsync()`，使用默认 `CancellationToken`，既不取消上一轮也不忽略过期结果，重复操作会把并发量成倍放大（现场日志中两次相同搜索相隔 1.6 秒）。
- **D8 过期结果被静默丢弃**：超时/取消后已经解析成功的候选全部丢弃，用户重开窗口要从零重扫，进一步加重 D6。

## 修复设计

### A. 探测可靠性（Ticket 1）

文件：`src/LXMusicAccessible.Services/QualityProbeWorkflow.cs`、`src/LXMusicAccessible.Services/AudioQualityInspector.cs`。

1. 单候选隔离：`ResolveAsync` + `ProbeMediaAsync` 整体包在 `try/catch` 内，`OperationCanceledException` 只在外部令牌取消时向上抛出，其余异常（含单候选超时）只让该候选不可用，继续尝试同音质的下一个候选。某音质全部候选都抛异常时该音质状态为 `QualityAvailability.Failed`（界面呈现“探测失败，可重试”），而不是整轮失败。
2. 请求标识与播放校验对齐：探测请求加 `User-Agent`，取值与 `MusicSourceService.IsPlayableAsync` 一致。
3. 缓存语义：只有成功结果进入 10 分钟 TTL；失败结果不得被后续 `ProbeAsync` 复用（沿用 `IsSuccess` 字段或删除条目），重新打开菜单必须重新探测。
4. 去掉 D4 的假阳性：仅当 `Identify` 认出音频魔数或内容类型为 `audio/*` 时才认可；删除 `bytes.Length > 65536` 的兜底。同时给 `AudioQualityInspector` 补 `ftyp`（m4a/aac）识别，避免常见容器落到“无法识别”。
5. 并发与时长有界：音质组之间用有界并发（上限 4）探测；同一音质内部仍按接口优先级顺序尝试，首个可播放即停止（保持“优先使用高优先级接口”的既有语义）。整轮探测有墙钟预算（建议 20 秒），预算耗尽时未完成的音质标记为 `QualityAvailability.Failed`（可重试），已确认的结果照常返回。

### B. 换源扫描并发与取消（Ticket 2）

文件：`src/LXMusicAccessible.Services/ManualSourceSwitchWorkflow.cs`、`src/LXMusicAccessible.App/WinFormsToolForms.cs`。

1. 全局并发闸门：`ManualSourceDiscoveryWorkflow` 内用 `SemaphoreSlim` 限制同时进行的音质解析数量为 8（不小于单接口最大声明音质数，因此“同一接口的每个音质都会起步”的既有契约仍然成立），保证最多 8 个 V8 实例/8 条解析在飞。
2. 取消快速生效：解析入口与闸门等待都要检查令牌；取消后不再排队新的候选。
3. 窗口重入保护：`ManualSourceSwitchForm` 只允许一轮扫描在飞；新的搜索或切换目标先取消上一轮（`CancellationTokenSource`），并用世代号忽略过期结果；扫描期间状态行为“正在查询可用的音源，请稍候”，搜索按钮保持可聚焦但明确忙碌；窗口关闭时取消在飞扫描。
4. 结果不被浪费：取消/超时后若已有可用候选，仍按 `Success` 返回可播放项，消息说明未扫完（可选，若实现成本高则保留超时消息）。

## 非目标

- 不改动内置接口的音质声明（审计结论：声明与实现一致，不虚构音质）。
- 不改变下载管理、专辑/批量下载的业务规则。
- 不重构 `LxScriptEngine` 的 V8 执行模型（`Task.Run` + 阻塞等待）；本轮只保证并发数量有界。
- 不引入新的依赖或第三方 UI 框架。

## 自动化验证

必须新增或更新确定性测试（离线，禁止真实网络）：

1. 单候选解析抛异常 / 探测超时后，同一音质的后续候选仍被尝试，且其它音质不受影响。
2. 整轮探测失败不得被缓存：第二次 `ProbeAsync` 必须重新发起解析与探测。
3. 探测请求包含非空 `User-Agent`。
4. `text/html` 且大于 64 KB 的响应体必须判为不可用；`ftyp` 容器可被识别。
5. 探测并发有界：同时进行的音质组探测不超过上限 4；单个候选超时只影响该候选，同组后续候选继续被尝试。
6. 换源扫描并发有界：并发解析数不超过闸门上限；已有测试“某音源全部音质先起步再完成”保持通过。
7. 换源窗口重入：连续两次搜索只保留最后一次结果，第一次被取消；关闭窗口取消在飞扫描。

完成前必须通过仓库 `verify.ps1` 本地质量门禁，并在工单中记录执行的测试数量。

## 无障碍验收

- 探测状态与扫描状态仍然只通过既有播报通道发声一次，并且有可见文本镜像。
- 扫描期间窗口保持可响应：焦点不丢失、`Esc` 仍可关闭、“搜索可用音源”按钮可再次激活。
- 进度、超时、失败与完成消息使用既有 `AnnouncementKind`（`Busy`/`Success`/`Error`），不新增自绘控件。
- 列表项标签保持稳定排序（音源、音质、状态、歌曲、歌手），不可播放项仍可见但禁用。
