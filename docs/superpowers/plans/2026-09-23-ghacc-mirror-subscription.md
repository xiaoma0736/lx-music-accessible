# ghacc Mirror Subscription Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a validated, cached mirror subscription with manual update, systemd timer management, and automatic mirror-list composition without requiring users to edit `/etc/ghacc.conf`.

**Architecture:** Extend `ghacc` with subscription source fetching, a dependency-free JSON object extractor for the known `mirrors.json` schema, an atomically replaced cache, and deterministic mirror ordering. Add `update`, `install-timer`, and `uninstall-timer` subcommands. Runtime continues to use the existing mirror scan and direct fallback.

**Tech Stack:** Bash, Git integration tests with stubbed `curl`/`git`/`mv`/`systemctl`, GNU/Linux systemd units, curl/wget.

## Global Constraints

- Do not modify global or repository Git configuration.
- Existing command behavior and direct GitHub fallback remain available.
- All new failure paths return nonzero.
- Subscription URLs and cached mirror URLs must use HTTPS.
- Maximum subscribed mirrors is controlled by `GHACC_SUBSCRIPTION_MAX_MIRRORS`, default 64.
- Cache writes must preserve an existing valid cache on update failure.
- Shell files use UTF-8 without BOM and LF endings.
- Tests must be deterministic and must not access the network.
- Tests override cache state with `GHACC_CACHE_DIR`; never mutate `/var/lib/ghacc` in tests.

## File Structure

- Modify `linux/ghacc.sh`: subscription configuration, parsing, cache update, mirror composition, status, timer commands.
- Modify `tests/test_ghacc.sh`: isolated cache setup, subscription curl stub controls, update/runtime/status/timer tests.
- Modify `linux/README.md`: Chinese documentation for subscription, cache, update, and timer commands.
- Modify `linux/ghacc.conf.example`: documented subscription defaults.

---

### Task 1: Subscription Fetch, Validation, and Cache Update

**Files:**
- Modify: `linux/ghacc.sh`
- Modify: `tests/test_ghacc.sh`
- Modify: `linux/ghacc.conf.example`

**Interfaces:**
- Produces environment/config scalars: `GHACC_SUBSCRIPTION` (default `1`), `GHACC_CACHE_DIR` (default `/var/lib/ghacc`), `GHACC_SUBSCRIPTION_MAX_MIRRORS` (default `64`).
- Produces config array: `GHACC_SUBSCRIPTION_URLS`; defaults to raw GitHub, jsDelivr, and one HTTPS proxy fallback.
- Produces `subscription_fetch_to OUTPUT`; sets global `SUBSCRIPTION_FETCH_SOURCE` on success.
- Produces `subscription_parse_file INPUT`; prints selected mirror URLs.
- Produces `update_command`; CLI command is `ghacc update`.
- Produces cache files `$GHACC_CACHE_DIR/mirrors.list` and `$GHACC_CACHE_DIR/mirrors.meta`.
- Preserves existing `fetch_to`, mirror scanning, and all current commands.

- [ ] **Step 1: Add isolated cache setup and subscription curl stub controls**

In `tests/test_ghacc.sh`, update `new_case()` so cache state is isolated. Change:

```bash
mkdir -p "$TMP/bin" "$TMP/work" "$TMP/download"
```

to:

```bash
mkdir -p "$TMP/bin" "$TMP/work" "$TMP/download" "$TMP/cache"
```

Immediately after the exported `GHACC_CONFIG` block, add:

```bash
export GHACC_CACHE_DIR="$TMP/cache"
```

In the generated curl stub, immediately before the `GHACC_TEST_CURL_REQUIRE_CONNECT_TIMEOUT` block, add:

```bash
if [[ -n "${GHACC_TEST_SUBSCRIPTION_FILE:-}" && "$url" == "${GHACC_TEST_SUBSCRIPTION_MATCH:-}"* ]]; then
  for ((i=1; i<=$#; i++)); do
    if [[ "${!i}" == "-o" ]]; then
      j=$((i + 1))
      cp "$GHACC_TEST_SUBSCRIPTION_FILE" "${!j}"
      exit 0
    fi
  done
fi
```

In the generated test config, add:

```bash
GHACC_SUBSCRIPTION=1
GHACC_SUBSCRIPTION_URLS=(
  "https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
  "https://cdn.jsdelivr.net/gh/Miao-moe/gh-accelerator-api@main/mirrors.json"
)
```

Do not change existing assertions in this step.

- [ ] **Step 2: Add failing subscription update tests**

Append the following immediately before `echo "$PASS assertions passed"`:

```bash
subscription_json() {
  cat <<'EOF'
{
  "lastUpdated": "2026-09-23T00:00:00Z",
  "mirrors": [
    {
      "name": "active clone",
      "url": "https://sub-one.test/",
      "type": "file-download",
      "status": "active",
      "features": ["Releases", "Git Clone"]
    },
    {
      "name": "inactive",
      "url": "https://inactive.test/",
      "type": "file-download",
      "status": "inactive",
      "features": ["Git Clone"]
    },
    {
      "name": "wrong type",
      "url": "https://wrong-type.test/",
      "type": "direct-access",
      "status": "active",
      "features": ["Git Clone"]
    },
    {
      "name": "no clone",
      "url": "https://no-clone.test/",
      "type": "file-download",
      "status": "active",
      "features": ["Releases"]
    },
    {
      "name": "insecure",
      "url": "http://insecure.test/",
      "type": "file-download",
      "status": "active",
      "features": ["Git Clone"]
    }
  ]
}
EOF
}

new_case
subscription_json >"$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
expect_success "update writes validated subscription cache" env PATH="$TMP/bin:$PATH" "$SCRIPT" update
[[ -f "$GHACC_CACHE_DIR/mirrors.list" ]] || fail "subscription list was not created"
[[ -f "$GHACC_CACHE_DIR/mirrors.meta" ]] || fail "subscription metadata was not created"
assert_contains "$GHACC_CACHE_DIR/mirrors.list" "https://sub-one.test/"
assert_line_absent "$GHACC_CACHE_DIR/mirrors.list" "https://inactive.test/"
assert_line_absent "$GHACC_CACHE_DIR/mirrors.list" "https://wrong-type.test/"
assert_line_absent "$GHACC_CACHE_DIR/mirrors.list" "https://no-clone.test/"
assert_line_absent "$GHACC_CACHE_DIR/mirrors.list" "http://insecure.test/"
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" "source=https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" "count=1"

new_case
subscription_json >"$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
unset GHACC_TEST_SUBSCRIPTION_MATCH
expect_success "update falls back through subscription sources" env PATH="$TMP/bin:$PATH" "$SCRIPT" update
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://cdn.jsdelivr.net/gh/Miao-moe/gh-accelerator-api@main/mirrors.json"
assert_contains "$GHACC_CACHE_DIR/mirrors.list" "https://sub-one.test/"

new_case
printf '%s\n' "https://old.test/" >"$GHACC_CACHE_DIR/mirrors.list"
printf 'updated=old\nsource=old\ncount=1\n' >"$GHACC_CACHE_DIR/mirrors.meta"
printf '%s\n' 'not-json' >"$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
expect_failure_with "subscription validation failed" "malformed subscription preserves cache" env PATH="$TMP/bin:$PATH" "$SCRIPT" update
assert_contains "$GHACC_CACHE_DIR/mirrors.list" "https://old.test/"
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" "updated=old"
```

- [ ] **Step 3: Run tests and verify the new update cases fail**

Run:

```bash
bash tests/test_ghacc.sh
```

Expected: FAIL at `update writes validated subscription cache` because the `update` command is not implemented.

- [ ] **Step 4: Implement subscription defaults, parsing, and atomic update**

In `linux/ghacc.sh`, after `GHACC_FALLBACK_DIRECT`, add:

```bash
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
```

Change `load_config()` to preserve new environment scalars across config sourcing and to compose mirrors later:

```bash
load_config() {
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
```

Add these helpers before `probe_url()`:

```bash
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
          if (depth == 0) { emit_object(buf); buf = "" }
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
    if ((status == 0 && -s "$output")); then
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

  local body list filtered meta count remote_updated now status=0
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

  mv -f "$list" "$GHACC_CACHE_DIR/mirrors.list" || {
    rm -f "$body" "$list" "$filtered" "$meta"
    log "ERROR: unable to replace subscription cache"
    return 1
  }
  mv -f "$meta" "$GHACC_CACHE_DIR/mirrors.meta" || {
    rm -f "$body" "$filtered" "$meta"
    log "ERROR: unable to replace subscription metadata"
    return 1
  }
  rm -f "$body" "$filtered"
  log "Updated mirrors: $count"
  log "Source: $SUBSCRIPTION_FETCH_SOURCE"
  log "Cache: $GHACC_CACHE_DIR/mirrors.list"
}
```

Add to the CLI case:

```bash
  update) shift; update_command "$@" ;;
```

Update `linux/ghacc.conf.example` comments and defaults so subscription is enabled and the array is documented.

- [ ] **Step 5: Run subscription tests and full suite**

Run:

```bash
bash tests/test_ghacc.sh
bash -n linux/ghacc.sh
```

Expected: PASS, including all pre-existing tests and the three new update tests.

- [ ] **Step 6: Commit**

```bash
git add linux/ghacc.sh linux/ghacc.conf.example tests/test_ghacc.sh
git commit -m "feat: add validated mirror subscription cache"
```

---

### Task 2: Runtime Mirror Composition and Subscription Disable Behavior

**Files:**
- Modify: `linux/ghacc.sh`
- Modify: `tests/test_ghacc.sh`

**Interfaces:**
- Consumes Task 1 cache and `load_config`.
- Produces final runtime ordering: manual `GHACC_MIRRORS`, cache mirrors, then `DEFAULT_MIRRORS`, deduplicated and trailing-slash normalized.
- Honors `GHACC_SUBSCRIPTION=0` by excluding cache mirrors.

- [ ] **Step 1: Add failing ordering tests**

Append before the final summary:

```bash
new_case
printf '%s\n' "https://cached.test/" "https://cached.test/" >"$GHACC_CACHE_DIR/mirrors.list"
expect_success "manual mirrors precede cached and built-in mirrors" env PATH="$TMP/bin:$PATH" GHACC_TEST_GIT_FAIL_MIRRORS="https://mirror.test/,https://cached.test/" "$SCRIPT" pull
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "url.https://ghfast.top/https://github.com/.insteadOf="
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "url.https://cached.test/https://github.com/.insteadOf="

new_case
printf '%s\n' "https://disabled-cache.test/" >"$GHACC_CACHE_DIR/mirrors.list"
expect_success "subscription disabled excludes cache mirrors" env PATH="$TMP/bin:$PATH" GHACC_SUBSCRIPTION=0 GHACC_TEST_GIT_FAIL_MIRRORS="https://mirror.test/,https://ghfast.top/" "$SCRIPT" pull
assert_line_absent "$GHACC_TEST_GIT_LOG_FILE" "url.https://disabled-cache.test/https://github.com/.insteadOf="
```

- [ ] **Step 2: Run tests and verify ordering cases fail**

Run:

```bash
bash tests/test_ghacc.sh
```

Expected: FAIL at `manual mirrors precede cached and built-in mirrors`.

- [ ] **Step 3: Verify composed ordering is already active**

Task 1 introduced composition in `load_config`. If either new test fails because cache/default ordering is wrong, adjust only `compose_mirrors()` and the cache-read branch in `load_config()`. Do not change `next_available_mirror()` behavior.

- [ ] **Step 4: Run full suite**

Run:

```bash
bash tests/test_ghacc.sh
bash -n linux/ghacc.sh
```

Expected: PASS, including both ordering tests.

- [ ] **Step 5: Commit**

```bash
git add linux/ghacc.sh tests/test_ghacc.sh
git commit -m "feat: compose subscribed mirrors at runtime"
```

---

### Task 3: Subscription Status and systemd Timer Management

**Files:**
- Modify: `linux/ghacc.sh`
- Modify: `tests/test_ghacc.sh`
- Modify: `linux/README.md`

**Interfaces:**
- Produces `subscription_status`; used by `status_command`.
- Produces `run_systemctl`; `GHACC_SYSTEMCTL` overrides the systemctl executable for tests.
- Produces `GHACC_SYSTEMD_UNIT_DIR` test override; production default is `/etc/systemd/system`.
- Produces `install_timer_command` and `uninstall_timer_command`; CLI commands are `ghacc install-timer` and `ghacc uninstall-timer`.
- Produces units named `ghacc-update.service` and `ghacc-update.timer`.

- [ ] **Step 1: Add failing status and timer tests**

Append before the final summary:

```bash
new_case
mkdir -p "$TMP/systemd"
cat >"$TMP/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GHACC_TEST_SYSTEMCTL_LOG_FILE"
exit 0
EOF
chmod +x "$TMP/bin/systemctl"
export GHACC_TEST_SYSTEMCTL_LOG_FILE="$TMP/systemctl.log"
: >"$GHACC_TEST_SYSTEMCTL_LOG_FILE"
printf '%s\n' "https://status-cache.test/" >"$GHACC_CACHE_DIR/mirrors.list"
cat >"$GHACC_CACHE_DIR/mirrors.meta" <<'EOF'
updated=2026-09-23T00:00:00Z
source=https://subscription.test/mirrors.json
count=1
subscription_updated=2026-09-23T00:00:00Z
EOF
expect_success "status shows subscription cache" env PATH="$TMP/bin:$PATH" "$SCRIPT" status
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "is-enabled ghacc-update.timer"

new_case
mkdir -p "$TMP/systemd"
export GHACC_SYSTEMD_UNIT_DIR="$TMP/systemd"
export GHACC_SYSTEMCTL="$TMP/bin/systemctl"
: >"$GHACC_TEST_SYSTEMCTL_LOG_FILE"
expect_success "install-timer writes and enables units" env PATH="$TMP/bin:$PATH" "$SCRIPT" install-timer
[[ -f "$TMP/systemd/ghacc-update.service" ]] || fail "service unit missing"
[[ -f "$TMP/systemd/ghacc-update.timer" ]] || fail "timer unit missing"
assert_contains "$TMP/systemd/ghacc-update.service" "Type=oneshot"
assert_contains "$TMP/systemd/ghacc-update.service" "NoNewPrivileges=true"
assert_contains "$TMP/systemd/ghacc-update.service" "ReadWritePaths=$GHACC_CACHE_DIR"
assert_contains "$TMP/systemd/ghacc-update.timer" "$GHACC_UPDATE_SCHEDULE"
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "daemon-reload"
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "enable --now ghacc-update.timer"

expect_success "uninstall-timer disables and removes units" env PATH="$TMP/bin:$PATH" "$SCRIPT" uninstall-timer
[[ ! -e "$TMP/systemd/ghacc-update.service" ]] || fail "service unit still exists"
[[ ! -e "$TMP/systemd/ghacc-update.timer" ]] || fail "timer unit still exists"
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "disable --now ghacc-update.timer"
```

- [ ] **Step 2: Run tests and verify status/timer cases fail**

Run:

```bash
bash tests/test_ghacc.sh
```

Expected: FAIL at `status shows subscription cache` or the timer command invocation.

- [ ] **Step 3: Implement status and timer management**

Add before `status_command()`:

```bash
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
      grep -E '^(updated|source|subscription_updated)=' "$GHACC_CACHE_DIR/mirrors.meta" || true
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
  local unit_dir="$1" script_path="$2"
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
  cat >"$unit_dir/ghacc-update.timer" <<EOF
[Unit]
Description=Periodically update ghacc mirrors

[Timer]
OnCalendar=$GHACC_UPDATE_SCHEDULE
RandomizedDelaySec=30m
Persistent=true
Unit=ghacc-update.timer

[Install]
WantedBy=timers.target
EOF
}

install_timer_command() {
  load_config
  [[ $# -eq 0 ]] || { usage >&2; return 2; }
  local unit_dir="${GHACC_SYSTEMD_UNIT_DIR:-/etc/systemd/system}"
  local script_path="${BASH_SOURCE[0]}"
  [[ -d "$unit_dir" && -w "$unit_dir" ]] || die "systemd unit directory is not writable: $unit_dir"
  write_timer_units "$unit_dir" "$script_path"
  run_systemctl daemon-reload
  run_systemctl enable --now ghacc-update.timer
  log "Installed update timer"
}

uninstall_timer_command() {
  load_config
  [[ $# -eq 0 ]] || { usage >&2; return 2; }
  local unit_dir="${GHACC_SYSTEMD_UNIT_DIR:-/etc/systemd/system}"
  run_systemctl disable --now ghacc-update.timer
  rm -f "$unit_dir/ghacc-update.timer" "$unit_dir/ghacc-update.service"
  run_systemctl daemon-reload
  log "Removed update timer"
}
```

Call `subscription_status` at the beginning of `status_command()` after `load_config`. Add CLI cases:

```bash
  install-timer) shift; install_timer_command "$@" ;;
  uninstall-timer) shift; uninstall_timer_command "$@" ;;
```

Extend the usage text with `update`, `install-timer`, and `uninstall-timer`.

- [ ] **Step 4: Run full suite**

Run:

```bash
bash tests/test_ghacc.sh
bash -n linux/ghacc.sh
```

Expected: PASS, including status and timer tests.

- [ ] **Step 5: Update Chinese documentation**

In `linux/README.md`, add sections after “常用命令”:

- 订阅镜像源：说明默认来源、筛选规则、缓存位置、`ghacc update`。
- 定时更新：说明 `ghacc install-timer`、systemd unit 名称、默认计划和卸载命令。
- 配置优先级：说明手动 `GHACC_MIRRORS` 优先于订阅缓存，订阅缓存优先于内置兜底；`GHACC_SUBSCRIPTION=0` 禁用订阅。
- 安全说明：远程镜像列表是信任边界；如需严格信任，请设置 `GHACC_SUBSCRIPTION=0` 并手动维护镜像。

In `linux/ghacc.conf.example`, add commented examples for `GHACC_SUBSCRIPTION`, `GHACC_UPDATE_SCHEDULE`, `GHACC_SUBSCRIPTION_MAX_MIRRORS`, and `GHACC_SUBSCRIPTION_URLS`.

- [ ] **Step 6: Run final verification**

Run:

```bash
bash tests/test_ghacc.sh
bash -n linux/ghacc.sh
bash -n tests/test_ghacc.sh
git diff --check
```

Expected: all tests pass, syntax checks clean, and whitespace check clean.

- [ ] **Step 7: Commit**

```bash
git add linux/ghacc.sh linux/README.md linux/ghacc.conf.example tests/test_ghacc.sh
git commit -m "feat: add scheduled mirror subscription updates"
```

---

## Final Review Checklist

- [ ] All new tests fail before their production code exists and pass afterward.
- [ ] Cache directory is isolated in every test by `GHACC_CACHE_DIR`.
- [ ] Network access is stubbed in every subscription test.
- [ ] `GHACC_SUBSCRIPTION=0` prevents cache mirrors from being used.
- [ ] Failed update preserves both `mirrors.list` and `mirrors.meta`.
- [ ] Runtime ordering is manual, cached, built-in, deduplicated.
- [ ] Timer units include the required systemd hardening.
- [ ] Chinese documentation explains subscription trust and configuration precedence.
- [ ] `bash tests/test_ghacc.sh`, `bash -n linux/ghacc.sh`, `bash -n tests/test_ghacc.sh`, and `git diff --check` pass.
