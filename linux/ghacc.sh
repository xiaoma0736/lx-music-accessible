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

  # Parse a practical JSON subset structurally. Strings and container nesting are
  # tracked so that only objects inside the top-level "mirrors" array are emitted.
  awk '
    function parse_error() { parse_failed = 1; exit 2 }
    function is_whitespace(ch) {
      return ch == " " || ch == "\t" || ch == "\n" || ch == "\r"
    }
    function push(kind) {
      top++
      kinds[top] = kind
      states[top] = (kind == "object" ? 0 : 0)
      object_keys[top] = ""
    }
    function expects_value() {
      if (kinds[top] == "object") return states[top] == 2
      return states[top] == 0
    }

    function finish_value() {
      if (kinds[top] == "object") {
        if (states[top] != 2) parse_error()
        states[top] = 3
      } else {
        if (states[top] != 0) parse_error()
        states[top] = 1
      }
    }
    function begin_object(ch) {
      if (!expects_value()) parse_error()
      if (top == 0 && states[0] == 2 && object_keys[0] == "mirrors") {
        mirrors_seen = 1
        mirrors_valid = 0
      }
      if (mirror_array_level == top && kinds[top] == "array" && states[top] == 0) {
        in_mirror_object = 1
        mirror_buffer = ch
      }
      push("object")
    }
    function begin_array(ch) {
      if (!expects_value()) parse_error()
      if (top == 0 && states[0] == 2 && object_keys[0] == "mirrors") {
        mirror_array_level = top + 1
        mirrors_seen = 1
        mirrors_valid = 1
      }
      push("array")
    }
    function end_container(ch) {
      if ((ch == "}" && kinds[top] != "object") ||
          (ch == "]" && kinds[top] != "array")) parse_error()
      if (kinds[top] == "object" && states[top] != 3) parse_error()
      if (kinds[top] == "array" && states[top] != 1) parse_error()
      if (in_mirror_object && top == mirror_array_level + 1 && kinds[top] == "object") {
        emit_object(mirror_buffer)
        in_mirror_object = 0
      }
      top--
      if (top < 0) parse_error()
      if (top == mirror_array_level - 1) mirror_array_level = 0
      finish_value()
    }
    function hex_value(ch,    position) {
      ch = toupper(ch)
      position = index("0123456789ABCDEF", ch)
      if (position == 0) return -1
      return position - 1
    }
    function string_escape(ch) {
      if (ch == "\"") return "\""
      if (ch == "\\") return "\\"
      if (ch == "/") return "/"
      if (ch == "b") return "\b"
      if (ch == "f") return "\f"
      if (ch == "n") return "\n"
      if (ch == "r") return "\r"
      if (ch == "t") return "\t"
      if (ch == "u") return "\ru"
      parse_error()
    }
    function close_string() {
      if (kinds[top] == "object" && states[top] == 0) {
        object_keys[top] = string_value
        states[top] = 1
      } else if (expects_value()) {
        if (top == 0 && states[0] == 2 && object_keys[0] == "mirrors") {
          mirrors_seen = 1
          mirrors_valid = 0
        }
        if (top == 0 && states[0] == 2 && object_keys[0] == "lastUpdated") {
          last_updated_seen = 1
        }
        finish_value()
      } else {
        parse_error()
      }
    }
    function validate_primitive() {
      if (primitive_value == "true" || primitive_value == "false" ||
          primitive_value == "null" ||
          primitive_value ~ /^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$/) {
        primitive_value = ""
        finish_value()
        return
      }
      parse_error()
    }
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
    BEGIN {
      top = -1
      kinds[0] = "object"
      states[0] = 0
      object_keys[0] = ""
      mirror_array_level = 0
      mirrors_seen = 0
      mirrors_valid = 0
      last_updated_seen = 0
      root_closed = 0
    }
    {
      for (char_index = 1; char_index <= length($0); char_index++) {
        ch = substr($0, char_index, 1)
        if (in_mirror_object) mirror_buffer = mirror_buffer ch
        if (root_closed) {
          if (!is_whitespace(ch)) parse_error()
          continue
        }
        if (in_string) {
          if (escaped) {
            escaped = 0
            if (ch == "u") {
              unicode_count = 1
              unicode_value = 0
            } else {
              decoded = string_escape(ch)
              if (decoded != "\ru") string_value = string_value decoded
              else unicode_count = 1
              if (unicode_count) unicode_value = 0
            }
          } else if (ch == "\\") {
            escaped = 1
          } else if (ch == "\n" || ch == "\r") {
            parse_error()
          } else if (ch == "\"") {
            in_string = 0
            close_string()
          } else {
            string_value = string_value ch
          }
          continue
        }
        if (unicode_count) {
          digit = hex_value(ch)
          if (digit < 0) parse_error()
          unicode_value = unicode_value * 16 + digit
          unicode_count++
          if (unicode_count == 5) {
            unicode_count = 0
            escaped = 0
            if (unicode_value > 0 && unicode_value < 128) {
              string_value = string_value sprintf("%c", unicode_value)
            }
          }
          continue
        }
        if (is_whitespace(ch)) {
          if (primitive_value != "") validate_primitive()
          continue
        }
        if (primitive_value != "") {
          if (ch ~ /[A-Za-z0-9+.-]/) {
            primitive_value = primitive_value ch
            continue
          }
          validate_primitive()
        }
        if (ch == "\"") {
          if (!(kinds[top] == "object" && states[top] == 0) && !expects_value()) parse_error()
          in_string = 1
          escaped = 0
          unicode_count = 0
          string_value = ""
        } else if (ch == "{") {
          begin_object(ch)
        } else if (ch == "[") {
          begin_array(ch)
        } else if (ch == "}") {
          if (top == 0) {
            if (states[0] != 3) parse_error()
            root_closed = 1
          } else {
            end_container(ch)
          }
        } else if (ch == "]") {
          end_container(ch)
        } else if (ch == ":") {
          if (kinds[top] != "object" || states[top] != 1) parse_error()
          states[top] = 2
        } else if (ch == ",") {
          if (kinds[top] == "object" && states[top] == 3) {
            states[top] = 0
            object_keys[top] = ""
          } else if (kinds[top] == "array" && states[top] == 1) {
            states[top] = 0
          } else {
            parse_error()
          }
        } else if (ch ~ /^[-0-9tfn]$/) {
          if (!expects_value()) parse_error()
          if (top == 0 && states[0] == 2 && object_keys[0] == "mirrors") {
            mirrors_seen = 1
            mirrors_valid = 0
          }
          primitive_value = ch
        } else {
          parse_error()
        }
      }
      if (primitive_value != "") validate_primitive()
    }
    END {
      if (parse_failed || primitive_value != "" || in_string || unicode_count ||
          !root_closed || !mirrors_seen || !mirrors_valid ||
          mirror_array_level != 0 || !last_updated_seen ||
          top != 0 || states[0] != 3) {
        exit 2
      }
    }
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

write_subscription_list() {
  : >"$1"
}

write_subscription_meta() {
  {
    printf 'updated=%s\n' "$2"
    printf 'source=%s\n' "$3"
    printf 'count=%s\n' "$4"
    printf 'subscription_updated=%s\n' "$5"
  } >"$1"
}

update_command() {
  load_config
  [[ "$GHACC_SUBSCRIPTION" == 1 ]] || die "mirror subscription is disabled"
  mkdir -p "$GHACC_CACHE_DIR" || die "cannot create cache directory: $GHACC_CACHE_DIR"
  [[ -d "$GHACC_CACHE_DIR" && -w "$GHACC_CACHE_DIR" ]] || die "cache directory is not writable: $GHACC_CACHE_DIR"

  local body list filtered meta count remote_updated now source_url status=0
  local list_backup="" old_list=0
  local -a cleanup_paths=()
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

  if ! write_subscription_list "$list"; then
    rm -f "$body" "$list" "$filtered" "$meta"
    log "ERROR: unable to write staged subscription list"
    return 1
  fi

  local url canonical grep_status
  while IFS= read -r url; do
    [[ -n "$url" ]] || continue
    valid_subscription_url "$url" || continue
    canonical="$url"
    [[ "$canonical" == */ ]] || canonical="$canonical/"
    grep -Fxsq "$canonical" "$list"
    grep_status=$?
    if ((grep_status == 1)); then
      if ! printf '%s\n' "$canonical" >>"$list"; then
        rm -f "$body" "$list" "$filtered" "$meta"
        log "ERROR: unable to write staged subscription list"
        return 1
      fi
    elif ((grep_status != 0)); then
      rm -f "$body" "$list" "$filtered" "$meta"
      log "ERROR: unable to inspect staged subscription list"
      return 1
    fi
    count="$(wc -l <"$list")"
    status=$?
    if ((status != 0)) || [[ ! "$count" =~ ^[0-9]+$ ]]; then
      rm -f "$body" "$list" "$filtered" "$meta"
      log "ERROR: unable to inspect staged subscription list"
      return 1
    fi
    ((count < GHACC_SUBSCRIPTION_MAX_MIRRORS)) || break
  done <"$filtered"

  count="$(wc -l <"$list")"
  status=$?
  if ((status != 0)) || [[ ! "$count" =~ ^[1-9][0-9]*$ ]]; then
    rm -f "$body" "$list" "$filtered" "$meta"
    log "ERROR: subscription validation failed"
    return 1
  fi

  remote_updated="$(subscription_string_field "$body" "lastUpdated")"
  now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  if ! write_subscription_meta "$meta" "$now" "$SUBSCRIPTION_FETCH_SOURCE" "$count" "$remote_updated"; then
    rm -f "$body" "$list" "$filtered" "$meta"
    log "ERROR: metadata staging failed"
    return 1
  fi

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

  if ! mv -f "$list" "$GHACC_CACHE_DIR/mirrors.list"; then
    rm -f "$body" "$list" "$filtered" "$meta" "$list_backup"
    log "ERROR: unable to replace subscription cache"
    return 1
  fi

  if ! mv -f "$meta" "$GHACC_CACHE_DIR/mirrors.meta"; then
    rm -f "$body" "$filtered"
    if ((old_list)); then
      if ! mv -f "$list_backup" "$GHACC_CACHE_DIR/mirrors.list"; then
        log "ERROR: unable to restore subscription cache; recovery data retained"
        return 1
      fi
      list_backup=""
    else
      if ! rm -f "$GHACC_CACHE_DIR/mirrors.list"; then
        log "ERROR: unable to remove partial subscription cache"
        return 1
      fi
    fi
    cleanup_paths=("$body" "$filtered" "$meta")
    if [[ -n "$list_backup" && -e "$list_backup" ]]; then cleanup_paths+=("$list_backup"); fi
    if ! rm -f "${cleanup_paths[@]}"; then
      log "ERROR: unable to clean staged subscription files"
      return 1
    fi
    log "ERROR: unable to replace subscription metadata"
    return 1
  fi

  cleanup_paths=("$body" "$filtered")
  if [[ -n "$list_backup" && -e "$list_backup" ]]; then cleanup_paths+=("$list_backup"); fi
  if ! rm -f "${cleanup_paths[@]}"; then
    log "ERROR: unable to clean staged subscription files"
    return 1
  fi
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
  local enabled_status active_status active_state
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
  run_systemctl is-enabled ghacc-update.timer >/dev/null 2>&1
  enabled_status=$?
  active_state="$(run_systemctl is-active ghacc-update.timer 2>/dev/null)"
  active_status=$?
  if ((enabled_status == 0)); then
    printf '%s\n' "Update timer: enabled"
  elif ((enabled_status == 1)); then
    printf '%s\n' "Update timer: not enabled"
  elif ((enabled_status == 126 || enabled_status == 127)); then
    printf '%s\n' "Update timer: systemd unavailable"
  else
    printf '%s\n' "Update timer: unknown"
  fi
  if ((active_status == 126 || active_status == 127)); then
    printf '%s\n' "Update timer active: systemd unavailable"
  elif [[ "$active_state" == "active" ]]; then
    printf '%s\n' "Update timer active: active"
  elif [[ "$active_state" == "inactive" ]]; then
    printf '%s\n' "Update timer active: inactive"
  elif [[ "$active_state" == "failed" ]]; then
    printf '%s\n' "Update timer active: failed"
  else
    printf '%s\n' "Update timer active: unknown"
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
  [[ -f "$script_path" ]] || die "script is not an existing regular file: $script_path"
  if ! script_path="$(realpath -e -- "$script_path")"; then
    die "cannot canonicalize script path: $script_path"
  fi
  [[ -n "$script_path" && "$script_path" == /* ]] || die "script path is not absolute: $script_path"
  [[ "$script_path" =~ ^/[A-Za-z0-9/._-]+$ ]] || die "unsafe script path for systemd: $script_path"
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

  ghacc menu
      打开交互式数字菜单。

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
      12) printf '\n---- 命令行帮助 ----\n'; usage ;;
      *) printf '%s\n' '无效选择，请输入菜单中的数字。' ;;
    esac
  done
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
  menu) shift; menu_command "$@" ;;
  help|--help|-h) usage ;;
  *)
    usage
    exit 2
    ;;
esac
