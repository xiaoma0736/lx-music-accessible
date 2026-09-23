#!/usr/bin/env bash
set -u
set -o pipefail

GHACC_VERSION="1.0.0"
GHACC_CONFIG="${GHACC_CONFIG:-/etc/ghacc.conf}"
DEFAULT_MIRRORS=(
  "https://ghfast.top/"
  "https://gh-proxy.com/"
  "https://ghproxy.net/"
  "https://gh.llkk.cc/"
)
GHACC_MIRRORS=("${DEFAULT_MIRRORS[@]}")
GHACC_TIMEOUT="${GHACC_TIMEOUT-8}"
GHACC_DOWNLOAD_DIR="${GHACC_DOWNLOAD_DIR:-}"
GHACC_AUTO_SELECT="${GHACC_AUTO_SELECT-1}"
GHACC_FALLBACK_DIRECT="${GHACC_FALLBACK_DIRECT-1}"

DEFAULT_SUBSCRIPTION_URLS=(
  "https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
  "https://cdn.jsdelivr.net/gh/Miao-moe/gh-accelerator-api@main/mirrors.json"
  "https://ghfast.top/https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
)
GHACC_SUBSCRIPTION_URLS=("${DEFAULT_SUBSCRIPTION_URLS[@]}")
GHACC_SUBSCRIPTION="${GHACC_SUBSCRIPTION-1}"
GHACC_CACHE_DIR="${GHACC_CACHE_DIR:-/var/lib/ghacc}"
GHACC_SUBSCRIPTION_MAX_MIRRORS="${GHACC_SUBSCRIPTION_MAX_MIRRORS-64}"
GHACC_UPDATE_SCHEDULE="${GHACC_UPDATE_SCHEDULE-*-*-* 04:30:00}"
GHACC_MIRRORS=()

log() { printf '[ghacc] %s\n' "$*" >&2; }
die() { log "ERROR: $*"; exit 2; }
have() { command -v "$1" >/dev/null 2>&1; }

load_config() {
  # Startup initialization preserves empty scalar values as deliberate overrides.
  local timeout="$GHACC_TIMEOUT" download_dir="$GHACC_DOWNLOAD_DIR"
  local auto_select="$GHACC_AUTO_SELECT" fallback_direct="$GHACC_FALLBACK_DIRECT"
  local subscription="$GHACC_SUBSCRIPTION" cache_dir="$GHACC_CACHE_DIR"
  local subscription_max="$GHACC_SUBSCRIPTION_MAX_MIRRORS" update_schedule="$GHACC_UPDATE_SCHEDULE"
  local -a manual_mirrors=() cached_mirrors=()

  if [[ -n "${GHACC_CONFIG:-}" && -f "$GHACC_CONFIG" ]]; then
    # shellcheck disable=SC1090
    source "$GHACC_CONFIG"
  fi

  GHACC_TIMEOUT="$timeout"
  GHACC_DOWNLOAD_DIR="$download_dir"
  GHACC_AUTO_SELECT="$auto_select"
  GHACC_FALLBACK_DIRECT="$fallback_direct"
  GHACC_SUBSCRIPTION="$subscription"
  GHACC_CACHE_DIR="$cache_dir"
  GHACC_SUBSCRIPTION_MAX_MIRRORS="$subscription_max"
  GHACC_UPDATE_SCHEDULE="$update_schedule"
  [[ "$GHACC_TIMEOUT" =~ ^[0-9]+$ ]] || die "GHACC_TIMEOUT must be an integer"
  [[ "$GHACC_SUBSCRIPTION" == 0 || "$GHACC_SUBSCRIPTION" == 1 ]] || die "GHACC_SUBSCRIPTION must be 0 or 1"
  [[ "$GHACC_SUBSCRIPTION_MAX_MIRRORS" =~ ^[1-9][0-9]*$ ]] || die "GHACC_SUBSCRIPTION_MAX_MIRRORS must be a positive integer"

  manual_mirrors=("${GHACC_MIRRORS[@]}")
  if [[ "$GHACC_SUBSCRIPTION" == 1 && -s "$GHACC_CACHE_DIR/mirrors.list" ]]; then
    while IFS= read -r mirror; do
      [[ -n "$mirror" ]] || continue
      valid_subscription_url "$mirror" || die "invalid mirror in subscription cache: $mirror"
      cached_mirrors+=("$mirror")
    done <"$GHACC_CACHE_DIR/mirrors.list"
  fi
  compose_mirrors "${manual_mirrors[@]}" "${cached_mirrors[@]}" "${DEFAULT_MIRRORS[@]}"
}

compose_mirrors() {
  GHACC_MIRRORS=()
  local url canonical existing
  for url in "$@"; do
    [[ -n "$url" ]] || continue
    canonical="$url"
    [[ "$canonical" == */ ]] || canonical="$canonical/"
    existing=""
    for existing in "${GHACC_MIRRORS[@]}"; do
      [[ "$existing" == "$canonical" ]] && break
    done
    if [[ -z "$existing" || "$existing" != "$canonical" ]]; then
      GHACC_MIRRORS+=("$canonical")
    fi
  done
}

valid_subscription_url() {
  local url="$1"
  [[ "$url" == https://?* ]] || return 1
  ((${#url} <= 2048)) || return 1
  case "$url" in
    *[[:space:]]*|*\"*|*"'"*|*\<*|*\>*|*\\*|*\`*) return 1 ;;
  esac
  [[ "$url" != *[[:cntrl:]]* ]] || return 1
}

subscription_string_field() {
  local file="$1" key="$2"
  awk -v wanted="$key" '
    function string_after(text,    pos, rest, colon, quote, out, i, ch, escaped) {
      pos = index(text, "\"" wanted "\"")
      if (pos == 0) return ""
      rest = substr(text, pos + length(wanted) + 2)
      colon = index(rest, ":")
      if (colon == 0) return ""
      rest = substr(rest, colon + 1)
      quote = index(rest, "\"")
      if (quote == 0) return ""
      rest = substr(rest, quote + 1)
      escaped = 0
      for (i = 1; i <= length(rest); i++) {
        ch = substr(rest, i, 1)
        if (escaped) { escaped = 0; continue }
        if (ch == "\\") { escaped = 1; continue }
        if (ch == "\"") return out
        out = out ch
      }
      return ""
    }
    { text = text $0 }
    END { print string_after(text) }
  ' "$file"
}

subscription_parse_file() {
  local file="$1"
  [[ -s "$file" ]] || return 1
  [[ "$(head -c 1 "$file")" == "{" ]] || return 1
  grep -Fq '"lastUpdated"' "$file" || return 1
  grep -Fq '"mirrors"' "$file" || return 1
  awk '
    function string_field(text, key,    pos, rest, colon, quote, out, i, ch, escaped) {
      pos = index(text, "\"" key "\"")
      if (pos == 0) return ""
      rest = substr(text, pos + length(key) + 2)
      colon = index(rest, ":")
      if (colon == 0) return ""
      rest = substr(rest, colon + 1)
      quote = index(rest, "\"")
      if (quote == 0) return ""
      rest = substr(rest, quote + 1)
      escaped = 0
      out = ""
      for (i = 1; i <= length(rest); i++) {
        ch = substr(rest, i, 1)
        if (escaped) { escaped = 0; continue }
        if (ch == "\\") { escaped = 1; continue }
        if (ch == "\"") return out
        out = out ch
      }
      return ""
    }
    function emit_object(text,    url, status, type) {
      url = string_field(text, "url")
      status = string_field(text, "status")
      type = string_field(text, "type")
      if (url == "" || status != "active" || type != "file-download") return
      if (index(text, "\"Git Clone\"") == 0) return
      print url
    }
    {
      count = length($0)
      for (i = 1; i <= count; i++) {
        ch = substr($0, i, 1)
        if (depth == 0) {
          if (ch == "{") { depth = 1; buf = ch }
          continue
        }
        buf = buf ch
        if (in_string) {
          if (escaped) escaped = 0
          else if (ch == "\\") escaped = 1
          else if (ch == "\"") in_string = 0
          continue
        }
        if (ch == "\"") { in_string = 1; continue }
        if (ch == "{") depth++
        if (ch == "}") {
          depth--
          if (depth == 1) {
            emit_object(buf)
            buf = ""
          } else if (depth == 0) {
            buf = ""
          }
        }
      }
    }
    END { if (depth != 0) exit 2 }
  ' "$file"
}

subscription_fetch_to() {
  local output="$1" source_url status
  SUBSCRIPTION_FETCH_SOURCE=""
  local -a sources=("${GHACC_SUBSCRIPTION_URLS[@]}")
  for source_url in "${sources[@]}"; do
    [[ -n "$source_url" ]] || continue
    log "Trying subscription source: $source_url"
    rm -f "$output"
    if have curl; then
      curl -fsSL --connect-timeout "$GHACC_TIMEOUT" --max-time 60 -o "$output" "$source_url"
    else
      wget -q --timeout="$GHACC_TIMEOUT" --tries=2 -O "$output" "$source_url"
    fi
    status=$?
    if ((status == 0)) && [[ -s "$output" ]]; then
      SUBSCRIPTION_FETCH_SOURCE="$source_url"
      return 0
    fi
    rm -f "$output"
  done
  return 1
}

update_command() {
  load_config
  [[ "$GHACC_SUBSCRIPTION" == 1 ]] || die "mirror subscription is disabled"
  mkdir -p "$GHACC_CACHE_DIR" || die "cannot create cache directory: $GHACC_CACHE_DIR"
  [[ -d "$GHACC_CACHE_DIR" && -w "$GHACC_CACHE_DIR" ]] || die "cache directory is not writable: $GHACC_CACHE_DIR"

  local body list filtered meta count remote_updated now source_url status=0
  local list_backup="" old_list=0
  for source_url in "${GHACC_SUBSCRIPTION_URLS[@]}"; do
    valid_subscription_url "$source_url" || {
      log "ERROR: invalid subscription source: $source_url"
      return 2
    }
  done
  body="$(mktemp "$GHACC_CACHE_DIR/.subscription-body.XXXXXX")" || return 1
  list="$(mktemp "$GHACC_CACHE_DIR/.subscription-list.XXXXXX")" || { rm -f "$body"; return 1; }
  filtered="$(mktemp "$GHACC_CACHE_DIR/.subscription-filtered.XXXXXX")" || { rm -f "$body" "$list"; return 1; }
  meta="$(mktemp "$GHACC_CACHE_DIR/.subscription-meta.XXXXXX")" || { rm -f "$body" "$list" "$filtered"; return 1; }

  subscription_fetch_to "$body"
  status=$?
  if ((status != 0)); then
    rm -f "$body" "$list" "$filtered" "$meta"
    log "ERROR: all subscription sources failed; existing cache preserved"
    return "$status"
  fi

  subscription_parse_file "$body" >"$filtered"
  status=$?
  if ((status != 0)); then
    rm -f "$body" "$list" "$filtered" "$meta"
    log "ERROR: subscription validation failed"
    return "$status"
  fi

  : >"$list"
  local url canonical
  while IFS= read -r url; do
    [[ -n "$url" ]] || continue
    valid_subscription_url "$url" || continue
    canonical="$url"
    [[ "$canonical" == */ ]] || canonical="$canonical/"
    grep -Fxsq "$canonical" "$list" || printf '%s\n' "$canonical" >>"$list"
    [[ "$(wc -l <"$list")" -lt "$GHACC_SUBSCRIPTION_MAX_MIRRORS" ]] || break
  done <"$filtered"

  count="$(wc -l <"$list")"
  if [[ ! "$count" =~ ^[1-9][0-9]*$ ]]; then
    rm -f "$body" "$list" "$filtered" "$meta"
    log "ERROR: subscription validation failed"
    return 1
  fi

  remote_updated="$(subscription_string_field "$body" "lastUpdated")"
  now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  {
    printf 'updated=%s\n' "$now"
    printf 'source=%s\n' "$SUBSCRIPTION_FETCH_SOURCE"
    printf 'count=%s\n' "$count"
    printf 'subscription_updated=%s\n' "$remote_updated"
  } >"$meta"

  if [[ -e "$GHACC_CACHE_DIR/mirrors.list" ]]; then
    old_list=1
    list_backup="$(mktemp "$GHACC_CACHE_DIR/.mirrors.list.backup.XXXXXX")" || {
      rm -f "$body" "$list" "$filtered" "$meta"
      return 1
    }
    cp -p "$GHACC_CACHE_DIR/mirrors.list" "$list_backup" || {
      rm -f "$body" "$list" "$filtered" "$meta" "$list_backup"
      log "ERROR: unable to back up subscription cache"
      return 1
    }
  fi

  mv -f "$list" "$GHACC_CACHE_DIR/mirrors.list" || {
    rm -f "$body" "$list" "$filtered" "$meta" "$list_backup"
    log "ERROR: unable to replace subscription cache"
    return 1
  }
  mv -f "$meta" "$GHACC_CACHE_DIR/mirrors.meta" || {
    rm -f "$body" "$filtered" "$meta"
    if ((old_list)); then
      mv -f "$list_backup" "$GHACC_CACHE_DIR/mirrors.list" || {
        rm -f "$GHACC_CACHE_DIR/mirrors.list"
        log "ERROR: unable to restore subscription cache"
        return 1
      }
    else
      rm -f "$GHACC_CACHE_DIR/mirrors.list"
    fi
    rm -f "$list_backup"
    log "ERROR: unable to replace subscription metadata"
    return 1
  }
  rm -f "$body" "$filtered" "$list_backup"
  log "Updated mirrors: $count"
  log "Source: $SUBSCRIPTION_FETCH_SOURCE"
  log "Cache: $GHACC_CACHE_DIR/mirrors.list"
}

probe_url() {
  local url="$1"
  if have curl; then
    curl -fsSI --max-time "$GHACC_TIMEOUT" "$url" >/dev/null 2>&1
  elif have wget; then
    wget -q --spider --timeout="$GHACC_TIMEOUT" "$url"
  else
    die "curl or wget is required"
  fi
}

reset_mirror_scan() {
  MIRROR_INDEX=0
  AVAILABLE_MIRROR=""
}

next_available_mirror() {
  local mirror
  while ((MIRROR_INDEX < ${#GHACC_MIRRORS[@]})); do
    mirror="${GHACC_MIRRORS[MIRROR_INDEX]}"
    MIRROR_INDEX=$((MIRROR_INDEX + 1))
    [[ "$mirror" == */ ]] || mirror="$mirror/"
    if [[ "$GHACC_AUTO_SELECT" != 1 ]] || probe_url "${mirror}https://github.com/robots.txt"; then
      AVAILABLE_MIRROR="$mirror"
      return 0
    fi
  done
  return 1
}

run_with_mirrors() {
  local operation="$1" status
  shift
  reset_mirror_scan
  while next_available_mirror; do
    log "Using mirror: $AVAILABLE_MIRROR"
    "$operation" "$AVAILABLE_MIRROR" "$@"
    status=$?
    if ((status == 0)); then return 0; fi
    if ((status == 130)); then return 130; fi
    log "Mirror operation failed; trying next acceleration source"
  done
  return 1
}

run_systemctl() {
  local systemctl="${GHACC_SYSTEMCTL:-systemctl}"
  "$systemctl" "$@"
}

subscription_status() {
  if [[ "$GHACC_SUBSCRIPTION" == 1 ]]; then
    printf '%s\n' "Subscription: enabled"
  else
    printf '%s\n' "Subscription: disabled"
  fi
  if [[ -s "$GHACC_CACHE_DIR/mirrors.list" ]]; then
    printf 'Cache: %s\n' "$GHACC_CACHE_DIR/mirrors.list"
    printf 'Cached mirrors: %s\n' "$(wc -l <"$GHACC_CACHE_DIR/mirrors.list")"
    if [[ -f "$GHACC_CACHE_DIR/mirrors.meta" ]]; then
      grep -E '^(updated|source|count|subscription_updated)=' "$GHACC_CACHE_DIR/mirrors.meta" || true
    fi
  else
    printf '%s\n' "Cache: not populated"
  fi
  if run_systemctl is-enabled ghacc-update.timer >/dev/null 2>&1; then
    printf '%s\n' "Update timer: enabled"
  else
    printf '%s\n' "Update timer: not enabled"
  fi
}

write_timer_units() {
  local unit_dir="$1" script_path="$2" write_status
  cat >"$unit_dir/ghacc-update.service" <<EOF
[Unit]
Description=ghacc mirror subscription update
Wants=network-online.target
After=network-online.target

[Service]
Type=oneshot
ExecStart=$script_path update
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ReadWritePaths=$GHACC_CACHE_DIR
EOF
  write_status=$?
  if ((write_status != 0)); then
    rm -f "$unit_dir/ghacc-update.service" "$unit_dir/ghacc-update.timer" || return 1
    return "$write_status"
  fi
  cat >"$unit_dir/ghacc-update.timer" <<EOF
[Unit]
Description=Periodically update ghacc mirrors

[Timer]
OnCalendar=$GHACC_UPDATE_SCHEDULE
RandomizedDelaySec=30m
Persistent=true
Unit=ghacc-update.service

[Install]
WantedBy=timers.target
EOF
  write_status=$?
  if ((write_status != 0)); then
    rm -f "$unit_dir/ghacc-update.service" "$unit_dir/ghacc-update.timer" || return 1
    return "$write_status"
  fi
}

install_timer_command() {
  load_config
  [[ $# -eq 0 ]] || { usage >&2; return 2; }
  local unit_dir="${GHACC_SYSTEMD_UNIT_DIR:-/etc/systemd/system}"
  local script_path="${BASH_SOURCE[0]}"
  mkdir -p "$GHACC_CACHE_DIR" || {
    log "ERROR: cannot create cache directory: $GHACC_CACHE_DIR"
    return 1
  }
  [[ -d "$GHACC_CACHE_DIR" && -w "$GHACC_CACHE_DIR" ]] || {
    log "ERROR: cache directory is not writable: $GHACC_CACHE_DIR"
    return 1
  }
  [[ -d "$unit_dir" && -w "$unit_dir" ]] || die "systemd unit directory is not writable: $unit_dir"
  write_timer_units "$unit_dir" "$script_path" || return $?
  run_systemctl daemon-reload || return $?
  run_systemctl enable --now ghacc-update.timer || return $?
  log "Installed update timer"
}

uninstall_timer_command() {
  load_config
  [[ $# -eq 0 ]] || { usage >&2; return 2; }
  local unit_dir="${GHACC_SYSTEMD_UNIT_DIR:-/etc/systemd/system}"
  run_systemctl disable --now ghacc-update.timer || return $?
  rm -f "$unit_dir/ghacc-update.timer" "$unit_dir/ghacc-update.service" || return $?
  run_systemctl daemon-reload || return $?
  log "Removed update timer"
}

status_command() {
  load_config
  subscription_status
  local mirror healthy=0
  printf '%s\n' "GitHub acceleration sources:"
  for mirror in "${GHACC_MIRRORS[@]}"; do
    [[ "$mirror" == */ ]] || mirror="$mirror/"
    if [[ "$GHACC_AUTO_SELECT" != 1 ]] || probe_url "${mirror}https://github.com/robots.txt"; then
      printf '  [available] %s\n' "$mirror"
      healthy=$((healthy + 1))
    else
      printf '  [unavailable] %s\n' "$mirror"
    fi
  done
  if ((healthy)); then printf 'Healthy mirrors: %d\n' "$healthy"; return 0; fi
  if [[ "$GHACC_FALLBACK_DIRECT" == 1 ]]; then printf 'No mirror available; direct fallback enabled.\n'; return 0; fi
  return 1
}

usage() {
  cat <<'EOF'
用法:
  ghacc clone <github-url> [git 选项...]
      克隆 GitHub 仓库。

  ghacc pull [git 选项...]
      在当前 Git 仓库中拉取更新。

  ghacc fetch [git 选项...]
      在当前 Git 仓库中抓取远端更新。

  ghacc git <git 参数...>
      执行 Git 命令，并对 github.com 地址做临时加速改写。

  ghacc download <github-url>
      下载 GitHub Release 文件、raw 文件或源码文件。

  ghacc archive <owner/repo> [ref]
      下载仓库源码压缩包；ref 可以是分支、标签或提交。

  ghacc release <owner/repo> <tag|latest> [资产名称或关键词]
      读取 Release 资产列表；提供资产名称或关键词时下载对应文件。

  ghacc update
      更新镜像订阅缓存。

  ghacc install-timer
      安装并启用定时更新 systemd timer。

  ghacc uninstall-timer
      停用并卸载定时更新 timer。

  ghacc status
      检查并显示已配置加速源的状态。

  ghacc help
      显示帮助。
EOF
}

rewrite_args() {
  local prefix="$1" target
  while IFS= read -r target; do
    printf '%s\n' "-c"
    printf '%s\n' "url.${prefix}${target}.insteadOf=${target}"
  done <<'EOF'
https://github.com/
ssh://git@github.com/
git@github.com:
EOF
}

git_mirror_operation() {
  local mirror="$1"
  shift
  local -a cmd=(git)
  local arg
  while IFS= read -r arg; do cmd+=("$arg"); done < <(rewrite_args "$mirror")
  cmd+=("$@")
  "${cmd[@]}"
}

run_git() {
  load_config
  local status=0
  run_with_mirrors git_mirror_operation "$@"
  status=$?
  if ((status == 0)); then return 0; fi
  if ((status == 130)); then return 130; fi
  if [[ "$GHACC_FALLBACK_DIRECT" != 1 ]]; then
    log "No mirror operation succeeded and direct fallback is disabled"
    return "$status"
  fi
  log "No mirror operation succeeded; using direct GitHub"
  git "$@"
  status=$?
  if ((status == 130)); then return 130; fi
  return "$status"
}

is_github_repo_input() {
  case "$1" in
    https://github.com/*|http://github.com/*|ssh://git@github.com/*|git@github.com:*) return 0 ;;
    *) return 1 ;;
  esac
}

clone_command() {
  [[ $# -ge 1 ]] || { usage >&2; return 2; }
  is_github_repo_input "$1" || die "clone requires a github.com repository URL"
  run_git clone "$@"
}

inside_git_worktree() {
  [[ "$(git rev-parse --is-inside-work-tree 2>/dev/null)" == "true" ]]
}

pull_command() {
  inside_git_worktree || die "pull must be run inside a Git repository"
  run_git pull "$@"
}

fetch_command() {
  inside_git_worktree || die "fetch must be run inside a Git repository"
  run_git fetch "$@"
}

git_command() {
  [[ $# -ge 1 ]] || { usage >&2; return 2; }
  run_git "$@"
}

allowed_download_url() {
  case "$1" in
    https://github.com/*|https://raw.githubusercontent.com/*|https://codeload.github.com/*|https://objects.githubusercontent.com/*) return 0 ;;
    *) return 1 ;;
  esac
}

fetch_to() {
  local url="$1" output="$2"
  if have curl; then
    curl -fL --retry 2 --connect-timeout "$GHACC_TIMEOUT" --max-time 600 -o "$output" "$url"
  else
    wget -q --timeout="$GHACC_TIMEOUT" --tries=2 -O "$output" "$url"
  fi
}

download_url() {
  local url="$1" prefix="$2" output="$3" tmp status
  tmp="$(mktemp "${output}.ghacc.XXXXXX")" || return 1
  fetch_to "${prefix}${url}" "$tmp"
  status=$?
  if ((status != 0)); then
    rm -f "$tmp"
    return "$status"
  fi
  mv -f "$tmp" "$output"
  status=$?
  if ((status != 0)); then rm -f "$tmp"; fi
  return "$status"
}

download_mirror_operation() {
  local mirror="$1" url="$2" output="$3"
  download_url "$url" "$mirror" "$output"
}

safe_download_basename() {
  local url="$1" basename
  basename="$(basename "${url%%\?*}")" || return 1
  case "$basename" in
    ""|.|..)
      log "ERROR: unsafe download basename: $basename"
      return 2
      ;;
    *)
      printf '%s\n' "$basename"
      ;;
  esac
}

output_path() {
  local url="$1" basename
  basename="$(safe_download_basename "$url")" || return 2
  if [[ -n "$GHACC_DOWNLOAD_DIR" ]]; then mkdir -p "$GHACC_DOWNLOAD_DIR"; fi
  printf '%s/%s\n' "${GHACC_DOWNLOAD_DIR:-$PWD}" "$basename"
}

download_command() {
  load_config
  [[ $# -eq 1 ]] || { usage >&2; return 2; }
  local url="$1" output status=0
  allowed_download_url "$url" || die "URL host is not an allowed GitHub download host"
  output="$(output_path "$url")" || exit $?
  run_with_mirrors download_mirror_operation "$url" "$output"
  status=$?
  if ((status == 0)); then
    log "Saved: $output"
    return 0
  fi
  if ((status == 130)); then return 130; fi
  if [[ "$GHACC_FALLBACK_DIRECT" != 1 ]]; then
    die "No mirror operation succeeded and direct fallback is disabled"
  fi
  log "No mirror operation succeeded; using direct GitHub"
  download_url "$url" "" "$output"
  status=$?
  if ((status == 0)); then
    log "Saved: $output"
    return 0
  fi
  if ((status == 130)); then return 130; fi
  log "ERROR: Download failed: $url"
  return "$status"
}

validate_owner_repo() {
  [[ "$1" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || die "owner/repo must use the form owner/repo"
}

validate_ref() {
  [[ "$1" =~ ^[A-Za-z0-9._/-]+$ ]] || die "ref contains unsupported characters"
}

archive_mirror_operation() {
  local mirror="$1" url="$2" output="$3"
  download_url "$url" "$mirror" "$output"
}

archive_command() {
  load_config
  [[ $# -ge 1 && $# -le 2 ]] || { usage >&2; return 2; }
  validate_owner_repo "$1"
  local ref="${2:-HEAD}" repo="${1##*/}" file url output status=0
  validate_ref "$ref"
  url="https://codeload.github.com/$1/tar.gz/$ref"
  file="${repo}-${ref//\//-}.tar.gz"
  output="${GHACC_DOWNLOAD_DIR:+$GHACC_DOWNLOAD_DIR/}$file"
  [[ -n "$GHACC_DOWNLOAD_DIR" ]] && mkdir -p "$GHACC_DOWNLOAD_DIR"
  run_with_mirrors archive_mirror_operation "$url" "$output"
  status=$?
  if ((status == 0)); then
    log "Saved: $output"
    return 0
  fi
  if ((status == 130)); then return 130; fi
  if [[ "$GHACC_FALLBACK_DIRECT" != 1 ]]; then
    die "No mirror operation succeeded and direct fallback is disabled"
  fi
  log "No mirror operation succeeded; using direct GitHub"
  download_url "$url" "" "$output"
  status=$?
  if ((status == 0)); then
    log "Saved: $output"
    return 0
  fi
  if ((status == 130)); then return 130; fi
  log "ERROR: Archive download failed"
  return "$status"
}

fetch_api_assets() {
  local url="$1" result assets
  result="$(mktemp)" || return 1
  if have curl; then
    curl -fsSL --connect-timeout "$GHACC_TIMEOUT" -H "Accept: application/vnd.github+json" -H "User-Agent: ghacc/$GHACC_VERSION" -o "$result" "$url"
  else
    wget -q --timeout="$GHACC_TIMEOUT" --user-agent="ghacc/$GHACC_VERSION" -O "$result" "$url"
  fi
  if [[ -s "$result" ]]; then
    if assets="$(grep -Eo '"browser_download_url":[[:space:]]*"[^"]+"' "$result" | cut -d'"' -f4)" && [[ -n "$assets" ]]; then
      printf '%s\n' "$assets"
      rm -f "$result"
      return 0
    fi
  fi
  rm -f "$result"
  return 1
}

api_mirror_operation() {
  local mirror="$1" owner_repo="$2" endpoint="$3"
  fetch_api_assets "${mirror}https://api.github.com/repos/$owner_repo/$endpoint"
}

github_api_assets() {
  local owner_repo="$1" endpoint="$2" status=0
  run_with_mirrors api_mirror_operation "$owner_repo" "$endpoint"
  status=$?
  if ((status == 0)); then return 0; fi
  if ((status == 130)); then return 130; fi
  if [[ "$GHACC_FALLBACK_DIRECT" != 1 ]]; then return "$status"; fi
  fetch_api_assets "https://api.github.com/repos/$owner_repo/$endpoint"
}

release_command() {
  load_config
  [[ $# -ge 2 && $# -le 3 ]] || { usage >&2; return 2; }
  validate_owner_repo "$1"
  local repo_owner="$1" tag="$2" pattern="${3:-}" endpoint assets asset selected
  if [[ "$tag" == "latest" ]]; then endpoint="releases/latest"; else endpoint="releases/tags/$tag"; fi
  assets="$(github_api_assets "$repo_owner" "$endpoint")" || die "Unable to read release assets"
  if [[ -z "$pattern" ]]; then
    printf '%s\n' "$assets"
    log "Specify an asset name to download one file"
    return 0
  fi
  selected=""
  while IFS= read -r asset; do
    if [[ "$(basename "$asset")" == "$pattern" || "$(basename "$asset")" == *"$pattern"* ]]; then selected="$asset"; break; fi
  done <<EOF
$assets
EOF
  [[ -n "$selected" ]] || die "No release asset matches: $pattern"
  download_command "$selected"
}

load_config
case "${1:-help}" in
  clone) shift; clone_command "$@" ;;
  pull) shift; pull_command "$@" ;;
  fetch) shift; fetch_command "$@" ;;
  git) shift; git_command "$@" ;;
  download) shift; download_command "$@" ;;
  archive) shift; archive_command "$@" ;;
  release) shift; release_command "$@" ;;
  update) shift; update_command "$@" ;;
  install-timer) shift; install_timer_command "$@" ;;
  uninstall-timer) shift; uninstall_timer_command "$@" ;;
  status)
    shift
    if (($#)); then
      log "错误：status 不接受参数"
      printf '用法: ghacc status\n' >&2
      exit 2
    fi
    status_command "$@"
    ;;
  help|--help|-h) usage ;;
  *)
    usage
    exit 2
    ;;
esac
