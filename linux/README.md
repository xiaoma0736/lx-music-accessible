# ghacc：阿里云 Linux GitHub 加速工具

`ghacc` 是一个按需使用的 Bash 包装器。它不会修改全局 Git 配置，也不会修改仓库 Git 配置。

## 环境要求

- 需要 Bash 运行 `ghacc`。
- `clone`、`pull`、`fetch` 和 `git` 命令需要 Git，并会使用这些 Git 命令已有的凭据管理器或 SSH 配置。
- 镜像检查、文件下载和 GitHub Release 元数据获取优先使用 `curl`，也可以使用 `wget`。如果两者都未安装，下载和 Release 命令会失败。

## 安装

```bash
sudo install -m 0755 linux/ghacc.sh /usr/local/bin/ghacc
sudo install -m 0644 linux/ghacc.conf.example /etc/ghacc.conf
```

编辑 `/etc/ghacc.conf`，把最稳定的镜像放到最前面。`GHACC_MIRRORS` 数组决定加速源尝试顺序。

## 常用命令

```bash
ghacc status
ghacc clone https://github.com/owner/repo.git
ghacc pull
ghacc fetch
ghacc git pull origin main
ghacc archive owner/repo main
ghacc release owner/repo latest
ghacc release owner/repo v1.2.3 app-linux-amd64.tar.gz
ghacc download https://github.com/owner/repo/releases/download/v1.2.3/example.zip
ghacc download https://raw.githubusercontent.com/owner/repo/main/README.md
```

当 `GHACC_AUTO_SELECT=1` 时，使用前会探测每个镜像。如果某个镜像探测成功，但实际的 Git、下载、archive 或 GitHub API 操作失败，`ghacc` 会尝试下一个已配置镜像，最后才考虑直连 GitHub。当 `GHACC_AUTO_SELECT=0` 时，会按配置顺序尝试镜像，但不做探测。只有所有镜像操作都失败且 `GHACC_FALLBACK_DIRECT=1` 时才直连 GitHub；把它设为 `0` 会禁用直连回退。被中断的操作会保留退出码 130。

## 配置优先级

`GHACC_TIMEOUT`、`GHACC_DOWNLOAD_DIR`、`GHACC_AUTO_SELECT` 和 `GHACC_FALLBACK_DIRECT` 的进程环境变量值会覆盖 `/etc/ghacc.conf` 中对应的值。`GHACC_MIRRORS` 由配置文件控制。空环境值是有意义的：例如 `GHACC_DOWNLOAD_DIR=""` 表示把文件保存到当前工作目录。`GHACC_TIMEOUT` 必须是不小于 0 的整数秒数。

## 私有仓库

Git 操作会把认证交给现有 Git 凭据管理器或 SSH 配置。该认证不会自动用于 GitHub API 的 Release 列表：对于私有仓库，除非 curl/wget 环境能独立提供有效的 GitHub API 凭据，否则 `ghacc release` 仍可能失败。
