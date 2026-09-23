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
ghacc menu
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

## 交互式菜单

在 SSH 终端直接运行：

```bash
ghacc
```

如果当前是交互终端，会显示数字菜单；输入数字后按回车执行。也可以显式运行：

```bash
ghacc menu
```

菜单包含克隆、拉取、抓取、下载源码/Release、更新镜像订阅、管理定时更新、查看状态和高级 Git 命令。

每个操作完成后会显示真实退出码；按回车返回菜单。输入 `0` 或按 `Ctrl+D` 退出菜单。

安装和卸载定时更新是系统级操作，菜单会要求输入 `y` 确认。脚本不会自动提权；非 root 用户会看到 `sudo ghacc install-timer` 或 `sudo ghacc uninstall-timer` 建议。

自动化脚本、cron、CI 或管道中直接运行 `ghacc` 不会进入菜单；此时显示命令行帮助。请继续使用明确的子命令。

## 订阅镜像源

默认订阅源来自 Miao-moe 的 `gh-accelerator-api`，包括 GitHub Raw、jsDelivr 和镜像代理地址。`ghacc update` 会按顺序尝试这些来源，只保留 `status` 为 `active`、`type` 为 `file-download` 且支持 `Git Clone` 的 HTTPS 镜像；来源会去重并加上尾部斜杠。验证通过的列表和元数据保存在 `GHACC_CACHE_DIR`（默认 `/var/lib/ghacc`）中。

## 定时更新

在 systemd 系统上，`ghacc install-timer` 会安装 `ghacc-update.service` 和 `ghacc-update.timer`，并执行 `daemon-reload` 后启用和启动 timer。默认计划为 `*-*-* 04:30:00`，可通过 `GHACC_UPDATE_SCHEDULE` 调整。`ghacc uninstall-timer` 会停用 timer、删除这两个 unit 并重新加载 systemd 配置。

## 配置优先级

运行时的加速源顺序是：手动配置的 `GHACC_MIRRORS`，然后是订阅缓存，最后是内置兜底镜像；重复项会去重。`GHACC_SUBSCRIPTION=0` 会禁用订阅缓存读取和 `ghacc update`。`GHACC_TIMEOUT`、`GHACC_DOWNLOAD_DIR`、`GHACC_AUTO_SELECT` 和 `GHACC_FALLBACK_DIRECT` 的进程环境变量值会覆盖 `/etc/ghacc.conf` 中对应的值。空环境值是有意义的：例如 `GHACC_DOWNLOAD_DIR=""` 表示把文件保存到当前工作目录。`GHACC_TIMEOUT` 必须是不小于 0 的整数秒数。

## 安全说明

远程镜像列表是信任边界。经过筛选的列表仍可能改变实际使用的第三方加速源。如需严格信任，请设置 `GHACC_SUBSCRIPTION=0` 并手动维护 `GHACC_MIRRORS`。

## 私有仓库

Git 操作会把认证交给现有 Git 凭据管理器或 SSH 配置。该认证不会自动用于 GitHub API 的 Release 列表：对于私有仓库，除非 curl/wget 环境能独立提供有效的 GitHub API 凭据，否则 `ghacc release` 仍可能失败。
