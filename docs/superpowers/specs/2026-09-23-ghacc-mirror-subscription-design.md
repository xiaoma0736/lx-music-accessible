# ghacc 镜像订阅与定时更新设计

## 目标

让用户安装 `ghacc` 后不需要手动编辑 `/etc/ghacc.conf`。脚本可以定期从维护中的公开镜像列表订阅源拉取节点，验证后生成本地缓存，运行时自动使用最新镜像列表。

现有 Git、下载、Release、archive 和直连回退行为保持不变。

## 订阅源

默认订阅源使用：

```text
https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json
```

考虑到 GitHub Raw 在国内和阿里云环境可能不稳定，更新器按顺序尝试多个获取入口：

1. GitHub Raw 直连。
2. jsDelivr。
3. 内置稳定镜像代理回退。

任一入口成功即停止，后续入口不再请求。

## 数据筛选与验证

订阅 JSON 中只保留同时满足以下条件的镜像：

- `status == active`
- `type == file-download`
- `features` 包含 `Git Clone`
- URL 使用 HTTPS
- URL 非空且长度不超过 2048
- URL 不包含引号、空白和控制字符

更新器还会执行以下限制：

- 最多保留 64 个镜像。
- 去除重复 URL。
- `mirrors` 字段缺失、不是数组或筛选后为空时，本次更新失败。
- JSON 明显不合法时，本次更新失败。

订阅源本身仍属于信任边界。脚本通过 HTTPS、结构校验和数量限制降低明显风险，但不能消除远程镜像列表被污染的风险。用户可用 `GHACC_SUBSCRIPTION=0` 禁用订阅。

## 本地缓存

更新结果写入：

```text
/var/lib/ghacc/mirrors.list
/var/lib/ghacc/mirrors.meta
```

`mirrors.list` 每行保存一个 HTTPS 镜像 URL。  
`mirrors.meta` 记录最近更新时间、成功来源、镜像数量和订阅源最后更新时间。

写入必须使用同目录临时文件和原子 `mv`。更新中断时不能留下被 `load_config` 读取的不完整缓存。测试可通过 `GHACC_CACHE_DIR` 覆盖缓存目录。

## 镜像优先级

运行时镜像顺序如下：

1. `/etc/ghacc.conf` 或环境中的手动 `GHACC_MIRRORS`。
2. 订阅缓存 `mirrors.list`。
3. 内置静态兜底镜像。
4. 直连 GitHub。

重复 URL 会去重。已有手动配置继续优先生效；新用户无需编辑配置文件。

`GHACC_SUBSCRIPTION_MAX_MIRRORS` 只限制订阅缓存保留数量，不限制手动镜像数量。

## 新命令

### `ghacc update`

手动拉取订阅源、验证、刷新本地缓存。成功时显示来源、镜像数量和缓存路径；失败时保留旧缓存并返回非零退出码。

### `ghacc install-timer`

安装 systemd service 和 timer。默认触发时间为每天 `04:30`，并允许最多 30 分钟随机延迟。用户可通过 `GHACC_UPDATE_SCHEDULE` 覆盖 calendar 表达式。

服务至少配置：

```ini
Type=oneshot
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ReadWritePaths=/var/lib/ghacc
```

实际读写路径根据 `GHACC_CACHE_DIR` 的运行时覆盖在测试中变化；系统安装时使用 `/var/lib/ghacc`。

### `ghacc uninstall-timer`

停止并禁用 `ghacc-update.timer`，删除本工具安装的 service 和 timer 单元。

### `ghacc status`

在现有镜像探测结果前显示：

- 订阅是否启用。
- 缓存是否存在。
- 最近更新时间。
- 缓存镜像数量。
- systemd timer 是否 enabled/active。

systemd 不可用时显示相应提示，不把 `status` 判为失败。

## 失败策略

订阅失败时不删除旧缓存，继续使用上一次成功缓存。没有旧缓存时回退内置静态镜像。`ghacc update` 的失败路径必须返回非零退出码并输出明确错误。

systemd timer 安装失败时返回非零退出码，并且不能留下被 systemctl 标记为 enabled 但文件缺失的半安装状态。

## 配置项

新增：

```bash
GHACC_SUBSCRIPTION=1
GHACC_UPDATE_SCHEDULE="*-*-* 04:30:00"
GHACC_SUBSCRIPTION_MAX_MIRRORS=64
GHACC_SUBSCRIPTION_URLS=(
  "https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
  "https://cdn.jsdelivr.net/gh/Miao-moe/gh-accelerator-api@main/mirrors.json"
)
```

保留现有：

```bash
GHACC_MIRRORS=(...)
```

作为手动优先镜像；该数组可以为空。环境变量中的标量配置继续覆盖配置文件。`GHACC_MIRRORS` 和 `GHACC_SUBSCRIPTION_URLS` 数组由配置文件控制。

## 兼容性

- 现有子命令、退出码和 Git 进程内改写行为不变。
- 现有手动 `GHACC_MIRRORS` 继续可用并排在订阅缓存前。
- 没有 systemd 的环境仍可手动执行 `ghacc update` 或使用外部 cron。
- `curl` 仍是首选下载器，`wget` 是回退。

## 验收标准

- `ghacc update` 能解析合法订阅、筛选目标镜像、去重并原子写入缓存。
- 非法 JSON、空镜像列表、非 HTTPS URL、超长 URL、超量镜像等测试路径返回失败且不破坏旧缓存。
- `load_config` 按“手动镜像、订阅缓存、内置兜底”去重合并。
- `GHACC_SUBSCRIPTION=0` 时运行时忽略订阅缓存。
- `ghacc install-timer` 和 `ghacc uninstall-timer` 能生成或移除 systemd 单元内容，测试不依赖真实 systemd。
- `ghacc status` 显示订阅缓存信息，并在没有 systemd 时保持成功。
- 所有新增失败路径返回非零退出码。
- Bash 语法检查和现有确定性集成测试全部通过。
