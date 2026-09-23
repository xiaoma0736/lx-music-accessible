# ghacc Interactive Menu Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a dependency-free numbered Chinese menu to `ghacc` while preserving every existing subcommand, exit-code semantic, direct fallback behavior, and noninteractive automation behavior.

**Architecture:** Implement a pure-Bash stdin/stdout menu loop. Each real action runs inside a subshell so `die` failures return to the menu instead of exiting the program. Menu prompts use ordinary reads; timer actions require explicit confirmation and do not elevate privileges. With no arguments, an interactive TTY enters the menu while a noninteractive stream keeps the existing help behavior.

**Tech Stack:** Bash, existing ghacc command functions, deterministic Bash integration tests using PATH stubs and controlled stdin.

## Global Constraints

- Preserve all existing commands and direct GitHub fallback.
- Do not modify global/repository Git configuration.
- Every menu-reported failure must preserve the real nonzero command exit status.
- Timer install/uninstall must not run automatically from merely navigating the menu.
- Do not automatically elevate privileges with sudo.
- Noninteractive `ghacc` with no arguments must display help and return 0; it must not hang.
- Menu must be pure stdin/stdout with no new dependency, TUI library, color-only signal, or mouse requirement.
- Tests must be deterministic and offline; use PATH stubs and controlled stdin, never real Git/curl/systemd/network.
- Tests must use existing `new_case()` isolation for cache/config/logs.
- Shell files remain UTF-8 without BOM and LF; `.gitattributes` already enforces LF.
- Do not alter unrelated behavior except direct menu integration.

## Current Baseline

- Repository/main base for this feature: `31a9057`.
- Existing deterministic suite: 62 assertions passing.
- Existing CLI dispatch is at the bottom of `linux/ghacc.sh`.
- Existing test helpers include `new_case`, `expect_success`, `expect_failure_with`, `assert_contains`, `assert_git_invocation_count`, and stubbed `git`/`curl`/`systemctl`.

## File Structure

- Modify `linux/ghacc.sh`: menu helpers, action dispatch, default no-argument behavior, usage text.
- Modify `tests/test_ghacc.sh`: menu stdin helpers and coverage for navigation, actions, failures, timers, and TTY behavior.
- Modify `linux/README.md`: Chinese menu usage, interaction rules, system-operation confirmation, noninteractive compatibility.
- No new files or dependencies.

---

### Task 1: Menu Framework and Common Actions

**Files:**
- Modify: `linux/ghacc.sh`
- Modify: `tests/test_ghacc.sh`

**Interfaces:**
- Produces CLI: `ghacc menu`; rejects extra arguments with usage and exit 2.
- Produces `menu_command`: numbered loop returning 0 on `0` or EOF.
- Produces `menu_show`, `menu_prompt`, `menu_read_array`, `menu_pause`, `menu_run_action`.
- Produces `MENU_OUTPUT` and `MENU_STATUS` test globals via `run_menu`.
- Reuses existing `clone_command`, `pull_command`, `fetch_command`, `download_command`, `archive_command`, `release_command`, `update_command`, `status_command`, `git_command`, and `usage`.

- [ ] **Step 1: Add deterministic menu test helpers**

In `tests/test_ghacc.sh`, after `assert_timer_status()` and before the executable check, add:

```bash
MENU_OUTPUT=""
MENU_STATUS=0

run_menu() {
  local input="$1"
  shift
  MENU_STATUS=0
  MENU_OUTPUT="$(printf '%b' "$input" | "$@" 2>&1)" || MENU_STATUS=$?
}
```

This helper captures stdout and stderr so assertions can inspect menus, prompts, and result messages while `MENU_STATUS` preserves the pipeline/menu exit status.

- [ ] **Step 2: Add failing framework and common-action tests**

Append immediately before `echo "$PASS assertions passed"`:

```bash
new_case
run_menu '0\n' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
[[ "$MENU_STATUS" -eq 0 ]] || fail "menu exit did not return 0"
for required in \
  'ghacc 菜单' \
  '1) 克隆 GitHub 仓库' \
  '2) 拉取当前仓库更新' \
  '3) 抓取当前仓库远端' \
  '4) 下载 GitHub 文件' \
  '5) 下载源码压缩包' \
  '6) 下载 Release 资产' \
  '7) 更新镜像订阅缓存' \
  '10) 查看加速源和定时器状态' \
  '11) 高级 Git 命令' \
  '12) 显示命令行帮助' \
  '0) 退出' \
  '请输入数字后按回车:'; do
  assert_contains /dev/stdin "$required" <<<"$MENU_OUTPUT"
done

new_case
run_menu '999\ninvalid\n0\n' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
[[ "$MENU_STATUS" -eq 0 ]] || fail "invalid choices exited nonzero"
assert_contains /dev/stdin '无效选择' <<<"$MENU_OUTPUT"
assert_git_invocation_count 0

new_case
run_menu '1\nhttps://github.com/owner/repo.git\n0\n' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
[[ "$MENU_STATUS" -eq 0 ]] || fail "clone menu returned nonzero"
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "url.https://mirror.test/https://github.com/.insteadOf=https://github.com/"
assert_contains /dev/stdin '操作完成，退出码: 0' <<<"$MENU_OUTPUT"

new_case
run_menu '2\n0\n' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "pull"
assert_contains /dev/stdin '操作完成，退出码: 0' <<<"$MENU_OUTPUT"

new_case
run_menu '3\n0\n' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "fetch"

new_case
run_menu '4\nhttps://raw.githubusercontent.com/owner/repo/main/README.md\n0\n' env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" "$SCRIPT" menu
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://raw.githubusercontent.com/owner/repo/main/README.md"
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://raw.githubusercontent.com/owner/repo/main/README.md"

new_case
run_menu '5\nowner/repo\nv1.2.3\n0\n' env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" "$SCRIPT" menu
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://codeload.github.com/owner/repo/tar.gz/v1.2.3"

new_case
run_menu '6\nowner/repo\nv1.2.3\napp-linux.tar.gz\n0\n' env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" "$SCRIPT" menu
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://api.github.com/repos/owner/repo/releases/tags/v1.2.3"
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://github.com/owner/repo/releases/download/v1.2.3/app-linux.tar.gz"

new_case
subscription_json >"$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
run_menu '7\n0\n' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
[[ -s "$GHACC_CACHE_DIR/mirrors.list" ]] || fail "menu update did not populate cache"
assert_contains /dev/stdin '操作完成，退出码: 0' <<<"$MENU_OUTPUT"

new_case
populate_status_cache
run_menu '10\n0\n' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
assert_contains /dev/stdin 'GitHub acceleration sources:' <<<"$MENU_OUTPUT"

new_case
run_menu '11\npull origin main\n0\n' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "pull"
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "origin"
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "main"

new_case
run_menu '12\n0\n' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
assert_contains /dev/stdin '用法:' <<<"$MENU_OUTPUT"

new_case
run_menu '1\nhttps://github.com/owner/repo.git\n0\n' env PATH="$TMP/bin:$PATH" GHACC_TEST_GIT_FAIL_ACCELERATED=1 GHACC_FALLBACK_DIRECT=0 "$SCRIPT" menu
[[ "$MENU_STATUS" -eq 0 ]] || fail "failed action should return to menu and exit normally"
assert_contains /dev/stdin '操作失败，退出码:' <<<"$MENU_OUTPUT"
assert_contains /dev/stdin '按回车返回菜单...' <<<"$MENU_OUTPUT"

new_case
run_menu '' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
[[ "$MENU_STATUS" -eq 0 ]] || fail "EOF should exit successfully"

new_case
expect_failure_with '用法:' 'menu rejects extra arguments' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu unexpected
```

- [ ] **Step 3: Run tests and verify menu cases fail**

Run:

```bash
bash tests/test_ghacc.sh
```

Expected: FAIL at the first menu test because `ghacc menu` is not implemented.

- [ ] **Step 4: Implement menu framework and action dispatch**

In `linux/ghacc.sh`, after `release_command()` and before `load_config`/CLI dispatch, add:

```bash
menu_show() {
  cat <<'EOF'
ghacc 菜单

  1) 克隆 GitHub 仓库
  2) 拉取当前仓库更新
  3) 抓取当前仓库远端
  4) 下载 GitHub 文件
  5) 下载源码压缩包
  6) 下载 Release 资产
  7) 更新镜像订阅缓存
  8) 安装并启用定时更新
  9) 停用并卸载定时更新
 10) 查看加速源和定时器状态
 11) 高级 Git 命令
 12) 显示命令行帮助
  0) 退出
EOF
  printf '%s' '请输入数字后按回车: '
}

menu_prompt() {
  local prompt="$1"
  printf '%s' "$prompt"
}

menu_read_line() {
  local reply="$1"
  read -r "$reply"
}

menu_pause() {
  local reply=""
  printf '%s' '按回车返回菜单...'
  read -r reply || return 0
  printf '\n'
}

menu_run_action() {
  local status=0
  ("$@") || status=$?
  if ((status == 0)); then
    printf '%s\n' '操作完成，退出码: 0'
  else
    printf '%s\n' "操作失败，退出码: $status"
  fi
  menu_pause
}

menu_prompt_value() {
  local label="$1" reply="$2"
  menu_prompt "$label"
  read -r "$reply"
}

menu_clone_action() {
  local url=""
  menu_prompt_value '请输入 GitHub 仓库地址: ' url || return 0
  [[ -n "$url" ]] || { printf '%s\n' '未输入仓库地址，已返回菜单。'; menu_pause; return 0; }
  menu_run_action clone_command "$url"
}

menu_download_action() {
  local url=""
  menu_prompt_value '请输入 GitHub 文件地址: ' url || return 0
  [[ -n "$url" ]] || { printf '%s\n' '未输入文件地址，已返回菜单。'; menu_pause; return 0; }
  menu_run_action download_command "$url"
}

menu_archive_action() {
  local repo="" ref=""
  menu_prompt_value '请输入 owner/repo: ' repo || return 0
  [[ -n "$repo" ]] || { printf '%s\n' '未输入 owner/repo，已返回菜单。'; menu_pause; return 0; }
  menu_prompt_value '请输入分支、标签或提交 [HEAD]: ' ref || return 0
  menu_run_action archive_command "$repo" "${ref:-HEAD}"
}

menu_release_action() {
  local repo="" tag="" asset=""
  menu_prompt_value '请输入 owner/repo: ' repo || return 0
  [[ -n "$repo" ]] || { printf '%s\n' '未输入 owner/repo，已返回菜单。'; menu_pause; return 0; }
  menu_prompt_value '请输入版本号或 latest [latest]: ' tag || return 0
  menu_prompt_value '请输入资产名称或关键词 [列出资产]: ' asset || return 0
  menu_run_action release_command "$repo" "${tag:-latest}" "$asset"
}

menu_git_action() {
  local input=""
  local -a git_args=()
  menu_prompt '请输入 Git 参数（空白分隔，例如 pull origin main）: '
  if ! read -r -a git_args; then return 0; fi
  ((${#git_args[@]} > 0)) || { printf '%s\n' '未输入 Git 参数，已返回菜单。'; menu_pause; return 0; }
  menu_run_action git_command "${git_args[@]}"
}

menu_command() {
  [[ $# -eq 0 ]] || { usage >&2; return 2; }
  local choice=""
  while :; do
    menu_show
    printf '\n'
    choice=""
    read -r choice || return 0
    case "$choice" in
      0) return 0 ;;
      1) menu_clone_action ;;
      2) menu_run_action pull_command ;;
      3) menu_run_action fetch_command ;;
      4) menu_download_action ;;
      5) menu_archive_action ;;
      6) menu_release_action ;;
      7) menu_run_action update_command ;;
      10) menu_run_action status_command ;;
      11) menu_git_action ;;
      12) clear 2>/dev/null || true; usage ;;
      *) printf '%s\n' '无效选择，请输入菜单中的数字。' ;;
    esac
  done
}
```

For item 12, if `clear` is unavailable or fails it must not affect behavior. If avoiding `clear` entirely is preferred for accessibility, simply call `usage` and print a separator. Replace:

```bash
      12) clear 2>/dev/null || true; usage ;;
```

with:

```bash
      12) printf '\n---- 命令行帮助 ----\n'; usage ;;
```

Add to the CLI case:

```bash
  menu) shift; menu_command "$@" ;;
```

Extend `usage()` with:

```text
  ghacc menu
      打开交互式数字菜单。
```

- [ ] **Step 5: Run full tests and syntax checks**

Run:

```bash
bash tests/test_ghacc.sh
bash -n linux/ghacc.sh
bash -n tests/test_ghacc.sh
git diff --check
```

Expected: all existing and new tests pass; no syntax or whitespace errors.

- [ ] **Step 6: Commit**

```bash
git add linux/ghacc.sh tests/test_ghacc.sh
git commit -m "feat: add interactive ghacc menu"
```

---

### Task 2: Timer Confirmation, Non-root Guidance, and No-Argument TTY Behavior

**Files:**
- Modify: `linux/ghacc.sh`
- Modify: `tests/test_ghacc.sh`

**Interfaces:**
- Produces menu actions 8 and 9 with explicit `y`/`Y` confirmation.
- Produces `menu_effective_uid`; `GHACC_TEST_EUID` deterministically overrides `$EUID` in tests.
- Produces non-root guidance for `sudo ghacc install-timer` / `sudo ghacc uninstall-timer`; no automatic sudo.
- Produces no-argument dispatch: interactive TTY enters `menu`; noninteractive stream uses existing help behavior.
- Produces internal test override `GHACC_MENU_FORCE_TTY=1`; it affects only the no-argument branch.

- [ ] **Step 1: Add failing timer and default-dispatch tests**

Append before the final summary:

```bash
new_case
export GHACC_TEST_EUID=1000
run_menu '8\ny\n0\n' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
assert_contains /dev/stdin 'sudo ghacc install-timer' <<<"$MENU_OUTPUT"
assert_contains /dev/stdin '不自动提权' <<<"$MENU_OUTPUT"
[[ ! -s "$GHACC_TEST_SYSTEMCTL_LOG_FILE" ]] || fail "non-root menu invoked systemctl"
# Initialize the log after proving no command ran.
: >"$GHACC_TEST_SYSTEMCTL_LOG_FILE"

new_case
export GHACC_TEST_EUID=0
run_menu '9\nn\n0\n' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
assert_contains /dev/stdin '已取消' <<<"$MENU_OUTPUT"
[[ ! -s "$GHACC_TEST_SYSTEMCTL_LOG_FILE" ]] || fail "unconfirmed uninstall invoked systemctl"

new_case
export GHACC_TEST_EUID=0
mkdir -p "$TMP/systemd"
cat >"$TMP/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GHACC_TEST_SYSTEMCTL_LOG_FILE"
exit 0
EOF
chmod +x "$TMP/bin/systemctl"
export GHACC_SYSTEMD_UNIT_DIR="$TMP/systemd"
export GHACC_SYSTEMCTL="$TMP/bin/systemctl"
: >"$GHACC_TEST_SYSTEMCTL_LOG_FILE"
run_menu '8\ny\n\n9\ny\n\n0\n' env PATH="$TMP/bin:$PATH" "$SCRIPT" menu
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "enable --now ghacc-update.timer"
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "disable --now ghacc-update.timer"

new_case
menu_status=0
menu_output="$(env PATH="$TMP/bin:$PATH" "$SCRIPT" </dev/null 2>&1)" || menu_status=$?
[[ "$menu_status" -eq 0 ]] || fail "noninteractive no-argument ghacc failed"
assert_contains /dev/stdin '用法:' <<<"$menu_output"
assert_line_absent /dev/stdin 'ghacc 菜单' <<<"$menu_output"

new_case
run_menu '0\n' env PATH="$TMP/bin:$PATH" GHACC_MENU_FORCE_TTY=1 "$SCRIPT"
assert_contains /dev/stdin 'ghacc 菜单' <<<"$MENU_OUTPUT"
```

- [ ] **Step 2: Run tests and verify timer/default cases fail**

Run:

```bash
bash tests/test_ghacc.sh
```

Expected: FAIL because menu items 8 and 9 currently call timer commands without confirmation, and no-argument TTY dispatch is absent.

- [ ] **Step 3: Implement confirmation, UID guidance, and no-argument dispatch**

Add after `menu_release_action()`:

```bash
menu_effective_uid() {
  printf '%s\n' "${GHACC_TEST_EUID:-$EUID}"
}

menu_confirm() {
  local prompt="$1" answer=""
  printf '%s\n' '这是系统级操作，会安装、启用、停用或卸载 systemd timer。'
  menu_prompt "$prompt"
  read -r answer || return 1
  [[ "$answer" == "y" || "$answer" == "Y" ]]
}

menu_install_timer_action() {
  menu_confirm '确认安装并启用定时更新？请输入 y 确认: ' || {
    printf '%s\n' '已取消，未修改系统。'
    menu_pause
    return 0
  }
  if [[ "$(menu_effective_uid)" != 0 ]]; then
    printf '%s\n' '当前不是 root。请退出菜单后执行: sudo ghacc install-timer'
    printf '%s\n' '脚本不自动提权。'
    menu_pause
    return 0
  fi
  menu_run_action install_timer_command
}

menu_uninstall_timer_action() {
  menu_confirm '确认停用并卸载定时更新？请输入 y 确认: ' || {
    printf '%s\n' '已取消，未修改系统。'
    menu_pause
    return 0
  }
  if [[ "$(menu_effective_uid)" != 0 ]]; then
    printf '%s\n' '当前不是 root。请退出菜单后执行: sudo ghacc uninstall-timer'
    printf '%s\n' '脚本不自动提权。'
    menu_pause
    return 0
  fi
  menu_run_action uninstall_timer_command
}
```

Change menu cases 8 and 9:

```bash
      8) menu_install_timer_action ;;
      9) menu_uninstall_timer_action ;;
```

Before `load_config` and CLI dispatch, replace the current no-argument/default behavior with:

```bash
if (($# == 0)); then
  if [[ -n "${GHACC_MENU_FORCE_TTY:-}" || { -t 0 && -t 1; } ]]; then
    set -- menu
  else
    set -- help
  fi
fi

load_config
case "${1:-help}" in
```

Keep all existing cases unchanged after this insertion. `GHACC_MENU_FORCE_TTY` exists solely for deterministic testing and has no effect when arguments are supplied.

- [ ] **Step 4: Run full tests and syntax checks**

Run:

```bash
bash tests/test_ghacc.sh
bash -n linux/ghacc.sh
bash -n tests/test_ghacc.sh
git diff --check
```

Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add linux/ghacc.sh tests/test_ghacc.sh
git commit -m "feat: confirm menu system operations"
```

---

### Task 3: Chinese Documentation and Final Compatibility Verification

**Files:**
- Modify: `linux/README.md`

**Interfaces:**
- Documents `ghacc`, `ghacc menu`, menu numbering, prompts, result feedback, timer confirmation, non-root behavior, and noninteractive compatibility.
- No production interface changes.

- [ ] **Step 1: Update Chinese README**

After the existing “常用命令” section, add a new section titled “交互式菜单”:

```markdown
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
```

Update the help/command examples to mention:

```bash
ghacc menu
```

Do not remove existing command examples.

- [ ] **Step 2: Verify help text is complete**

Run:

```bash
bash linux/ghacc.sh help | grep -F 'ghacc menu'
```

Expected: output contains the menu usage line.

- [ ] **Step 3: Run full regression verification**

Run:

```bash
bash tests/test_ghacc.sh
bash -n linux/ghacc.sh
bash -n tests/test_ghacc.sh
git diff --check
```

Expected: all tests pass, syntax checks clean, whitespace check clean.

- [ ] **Step 4: Check LF and executable metadata**

Run:

```bash
git ls-files --eol -- linux/ghacc.sh tests/test_ghacc.sh
git ls-files -s linux/ghacc.sh tests/test_ghacc.sh
```

Expected: working tree and index use LF; both files remain mode `100755`.

- [ ] **Step 5: Commit**

```bash
git add linux/README.md
git commit -m "docs: document ghacc interactive menu"
```

---

## Final Review Checklist

- [ ] `ghacc menu` works explicitly and `ghacc` enters it only on an interactive TTY with no arguments.
- [ ] Noninteractive no-argument invocation prints help, returns 0, and never waits for menu input.
- [ ] Every numbered action maps to the approved function.
- [ ] A failing action displays its real nonzero exit code and returns to the menu.
- [ ] Invalid input never executes a command.
- [ ] EOF exits successfully.
- [ ] Timer actions require explicit `y`/`Y`, do not auto-elevate, and non-root users receive the exact sudo command.
- [ ] Existing CLI commands, direct fallback, mirror ordering, subscription cache, and timer lifecycle tests remain green.
- [ ] Usage and README include the menu.
- [ ] `bash tests/test_ghacc.sh`, `bash -n linux/ghacc.sh`, `bash -n tests/test_ghacc.sh`, and `git diff --check` pass.
