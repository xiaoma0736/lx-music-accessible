# 音质定义统一与换源列表收敛设计

## 背景

用户现场反馈（歌曲「无奈的思绪 - 嘉慧」）：

- “播放音质 / 下载单曲音质”子菜单里，`hires`、`atmos`、`master` 等高品质显示“没有”（不可用）。
- 同一首歌的“切换音源”列表里，同一音源（聆澜音源）却列出：

```
聆澜音源(赞助版)[永久]，hires，无奈的思绪，嘉慧，kg
聆澜音源(赞助版)[永久]，atmos，无奈的思绪，嘉慧，kg
聆澜音源(赞助版)[永久]，master，无奈的思绪，嘉慧，kg
```

- 列表里这些音质是英文 id，而菜单里是中文标签，两处显示不一致。
- 换源列表里还混着大量解析不到播放链接的不可用项。

## 审查

### 音质定义散落在四处

| 位置 | 定义 | 问题 |
| --- | --- | --- |
| `QualityCatalog.Label` | 13 个 id 的中文标签 | 缺少脚本实际会声明的 `spatial`、`hifi` 等 id，未知 id 直接回落英文 |
| `WinFormsShell.TranslateQuality` | 13 个 id 的另一套文案 | 与 `QualityCatalog` 不一致（`flac` 显示“无损”而非“无损 FLAC”，`master` 显示“超清母带”而非“母带”） |
| `ManualSourceDiscoveryWorkflow.TranslateQuality` | 只映射 5 个 id | `hires`/`atmos`/`master` 直接显示英文，即用户看到的现象 |
| `SettingsForm` 音质下拉 | 5 个显示文本与反向映射 | 文案与目录不一致（“无损”/“Hi-Res”） |

脚本 `qualitys` 的实际取值（本机数据库实测）：`wy/ kw/ kg` 6 项、`tx` 8 项、`qs` 含 `spatial`、酷我直连配置含 `hifi`。`QualityCatalog.KnownQualities` 只有 13 项，且子菜单只渲染这个固定列表，脚本声明的 `spatial`/`hifi` 会被静默丢弃。

### “可播放”判定有两套

| 路径 | 判定 | 结果 |
| --- | --- | --- |
| `QualityProbeWorkflow`（播放/下载子菜单） | `MusicQuality.AcceptsDownload`：实际秩必须 ≥ 请求秩 | 脚本把 `hires` 请求解析成 16bit/44.1k FLAC 时判“不可用” |
| `MusicPlayerService.TryLoadAsync`（真实播放） | `MusicQuality.AcceptsPlayback`：无损族（秩 ≥ 4）之间互相接受 | 同一 URL 实际可以正常播放 |
| `ManualSourceDiscoveryWorkflow`（换源列表） | 只要解析出 URL（网关内部做过一次可播性校验） | 显示为可播放 |

于是同一个（接口，音质）组合在三处得到三种结论：子菜单“不可用”、换源列表“可播放”、真实播放“成功”。用户看到的“播放菜单没有、换源列表有”就是这个分裂。

### 结论

- 音质目录必须唯一：标签、秩、规范化只在一处定义，所有界面文本都从它派生。
- “可播放”必须与真实播放一致：子菜单与换源列表都使用播放判定。
- “可下载”必须与真实下载一致：下载子菜单使用下载判定（实际媒质必须不低于请求），并在不可下载时说明实际音质，让用户改选实际音质所在行。
- 换源列表只展示有可播放链接的结果。

## 设计

### 1. 唯一音质目录（`QualityCatalog`）

- 保留 `KnownQualities`（菜单占位顺序）、`Label`、`Normalize`、`Rank`、`Order`，补齐脚本可能声明的 id：
  - `spatial`（空间音频，秩 10）、`hifi`（Hi-Fi 无损，秩 5）。
  - 实际媒质可能出现的容器 token：`ape`、`alac`、`wav`、`aac`、`m4a`、`mp3`、`ogg`、`opus`、`wma`，全部给出中文或通用标签，避免英文泄露。
- 未收录的 id 仍原样显示（插件自定义音质不应被改写），但所有已收录 id 必须中文。
- 删除 `WinFormsShell.TranslateQuality` 与 `ManualSourceDiscoveryWorkflow.TranslateQuality`，统一调用 `QualityCatalog.Label`。
- 设置窗口的音质下拉显示文本改为目录标签（仍只提供 128k/192k/320k/flac/hires 五个默认值，不改变设置模型）。

### 2. 统一判定（`QualityOption`）

- `QualityOption` 增加 `Downloadable`。
- `Status == Playable` 由 `MusicQuality.AcceptsPlayback(requested, actual)` 决定（与 `MusicPlayerService.TryLoadAsync` 相同）。
- `Downloadable` 由 `MusicQuality.AcceptsDownload(requested, actual)` 决定（与 `DownloadWorkflow` 相同）。
- 播放子菜单按 `Playable` 启用；下载子菜单按 `Downloadable` 启用，未达到下载音质时显示“实际 X，低于所选音质”。
- 子菜单文本显示“实际 X”（当实际与请求不同），例如“Hi-Res，可播放（实际 无损 FLAC），聆澜音源”。

### 3. 子菜单渲染实际声明的音质

- 占位项仍来自 `KnownQualities`（探测中禁用）。
- 探测结果中不在占位列表的音质（如 `spatial`、插件自定义 id）追加到子菜单，避免“声明了却看不到”。

### 4. 换源列表只显示可播放结果

- `ManualSourceSwitchForm` 只渲染 `IsPlayable` 的候选；不可用候选不再出现在列表里。
- 结果消息由工作流生成并包含数量：“找到 N 个可播放音源，已隐藏 M 个不可用项” / “没有找到可播放音源”；超时保留已完成项并注明“换源查询超时，仅列出已完成的可播放项（N 个）”。
- 列表项标签使用目录中文标签，顺序仍为音源、音质、歌曲、歌手、平台。

## 非目标

- 不改变内置接口的音质声明，不虚构音质。
- 不改变下载的严格判定（请求母带却拿到 16bit FLAC 仍不能按母带下载）；改为在菜单里说明实际音质，让用户选择实际音质行。
- 换源列表不做逐候选的媒体二次探测：它回答的是“该接口能否给出可播放链接”，具体媒质以播放/下载时的校验为准。
- 不改变设置里可选的默认音质集合，只统一显示文案。

## 自动化验证

1. `QualityCatalog.Label` 覆盖 `spatial`/`hifi` 与常见容器 token；未知 id 原样保留。
2. 同一探测结果中，`hires` 请求得到 16bit FLAC 时：`Status == Playable` 且 `Downloadable == false`。
3. 播放子菜单对 `hires` 项启用；下载子菜单对同一项禁用并显示“实际 无损 FLAC，低于所选音质”。
4. 子菜单显示目录中文标签，脚本声明但不在 `KnownQualities` 的音质也会出现。
5. 换源窗口只列出可播放候选；不可用候选不进入列表；状态文本报告可播放数量与隐藏数量。
6. 设置窗口音质下拉的显示文本与目录标签一致。

完成前必须通过仓库 `verify.ps1` 本地质量门禁并记录执行的测试数量。

## 无障碍验收

- 菜单与列表项文本保持“音质，状态，来源”顺序，状态词可被读屏直接听到。
- 隐藏不可用项后，可见列表数量与状态文本一致；不新增控件、不改变 Tab 顺序与焦点规则。
- 结果消息仍只播报一次，并有可见文本镜像。
