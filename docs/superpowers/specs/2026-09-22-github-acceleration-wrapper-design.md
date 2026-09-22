# GitHub 加速命令包装器设计

## 目标

为阿里云 Linux 服务器提供一个按需使用的 GitHub 加速命令 `ghacc`，用于：

- 拉取 GitHub 项目更新
- 克隆 GitHub 仓库
- 下载 GitHub Release 文件
- 下载 GitHub 源码压缩包
- 下载 raw 文件

脚本不修改系统全局 Git 配置，避免影响其他工具。

## 使用方式

```bash
ghacc clone https://github.com/owner/repo.git
ghacc pull
ghacc fetch
ghacc git pull origin main
ghacc download https://github.com/owner/repo/releases/download/v1.0.0/file.zip
ghacc release owner/repo v1.0.0
ghacc archive owner/repo main
ghacc status
```

## 核心设计

- 实现为单个 Bash 脚本。
- 安装命令名：`/usr/local/bin/ghacc`。
- 默认配置文件：`/etc/ghacc.conf`。
- 依赖：
  - `bash`
  - `git`
  - 优先使用 `curl`
  - 没有curl 时使用 `wget`

### 加速源策略

脚本内置多个候选公共加速源，并允许用户在配置文件中自定义或调整优先级。

运行时按顺序检查：

1. 用户自定义加速源
2. 内置加速源
3. 直连 GitHub

选择第一个探测成功的加速源。探测失败或 Git 操作失败时，记录并尝试下一个候选；最终保留直连回退。

### 子命令

| 命令 | 说明 |
| --- | --- |
| `clone` | 克隆 GitHub 仓库 |
| `pull` | 在当前 Git 仓库执行拉取 |
| `fetch` | 在当前 Git 仓库执行抓取 |
| `git` | 透传 Git 参数，并对 github.com URL 做临时加速改写 |
| `download` | 加速下载任意 GitHub URL |
| `release` | 下载 Release 文件 |
| `archive` | 下载源码压缩包 |
| `status` | 探测加速源并列出当前可用项 |
| `help` | 显示帮助 |

## 配置项

```bash
GHACC_MIRRORS=(
  "https://example-custom-mirror.example/"
  "https://ghfast.top/"
  "https://gh-proxy.com/"
)
GHACC_TIMEOUT=8
GHACC_DOWNLOAD_DIR="$PWD"
GHACC_AUTO_SELECT=1
GHACC_FALLBACK_DIRECT=1
```

配置文件中不允许放置任何 GitHub 私人访问令牌明文；私有仓库优先依赖用户已有的 Git 凭据管理。

## 错误处理

- 所有加速源不可用时，按配置决定是否直连回退。
- 子命令参数不足时显示用法并返回非零状态。
- 下载命令使用失败时删除不完整文件。
- 输出当前使用的加速源，便于排查问题。

## 验收标准

- `ghacc status` 能显示候选加速源和可用性。
- `ghacc clone` 能克隆公开仓库。
- `ghacc pull` 能在当前仓库拉取更新。
- `ghacc download` 能下载 Release、archive、raw URL。
- 脚本不会改写 `~/.gitconfig` 或系统 Git 全局配置。
- 所有失败路径返回非零退出码。
