# GitHub Acceleration Wrapper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an on-demand Linux Bash command named `ghacc` that accelerates GitHub clone, pull, fetch, release, archive, raw, and download operations without changing global Git configuration.

**Architecture:** One executable Bash script exposes subcommands, probes an ordered mirror list, and invokes `git` with temporary `url.*.insteadOf` configuration. File downloads try accelerated HTTPS URLs first and retain direct GitHub fallback. A shell integration test uses stubbed `git` and `curl` executables so behavior is deterministic without network access.

**Tech Stack:** Bash, Git, curl or wget, POSIX filesystem paths, Bash integration tests.

## Global Constraints

- Target operating system is Linux; the primary deployment target is an Alibaba Cloud server.
- Do not modify `~/.gitconfig`, `/etc/gitconfig`, or repository Git configuration.
- Configuration lives in `/etc/ghacc.conf` by default and may be overridden with `GHACC_CONFIG`.
- Dependency policy: Bash and Git are required; curl is preferred; wget is used when curl is unavailable.
- Direct GitHub fallback must remain available.
- All failure paths return a nonzero exit status.
- Shell files must use LF line endings.

## File Structure

- Create `linux/ghacc.sh`: executable production script and CLI entry point.
- Create `tests/test_ghacc.sh`: deterministic Bash integration tests using temporary PATH stubs.
- Create `linux/README.md`: deployment and command documentation.
- Create `linux/ghacc.conf.example`: copy-ready configuration example.

---

### Task 1: Test Harness and Mirror Selection

**Files:**
- Create: `tests/test_ghacc.sh`
- Create: `linux/ghacc.sh`
- Create: `linux/ghacc.conf.example`

**Interfaces:**
- Produces: `GHACC_CONFIG` environment override; `/etc/ghacc.conf` may set `GHACC_MIRRORS`, `GHACC_TIMEOUT`, `GHACC_DOWNLOAD_DIR`, `GHACC_AUTO_SELECT`, and `GHACC_FALLBACK_DIRECT`.
- Produces: CLI subcommands `status`, `clone`, `pull`, `fetch`, `git`, `download`, `archive`, `release`, `help`.
- Produces: exit code 0 for success and nonzero for invalid arguments or failed transfers.

- [ ] **Step 1: Write failing tests for configuration, status output, and missing script failure**

Create `tests/test_ghacc.sh`:

```bash
#!/usr/bin/env bash
set -u
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/linux/ghacc.sh"
TMP=""
PASS=0

cleanup() {
  [[ -z "$TMP" || ! -d "$TMP" ]] || rm -rf "$TMP"
}
trap cleanup EXIT

new_case() {
  TMP="$(mktemp -d "${TMPDIR:-/tmp}/ghacc-test.XXXXXX")"
  mkdir -p "$TMP/bin" "$TMP/work" "$TMP/download"
  cat >"$TMP/bin/curl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$GHACC_TEST_CURL_LOG" >> "$GHACC_TEST_CURL_LOG_FILE"
if [[ "${GHACC_TEST_CURL_FAIL:-0}" == 1 ]]; then exit 22; fi
if [[ "$3" == *GHACC_TEST_HEADER ]]; then exit 22; fi
for ((i=1; i<=$#; i++)); do
  if [[ "${!i}" == "-o" ]]; then
    j=$((i + 1))
    printf 'test-content\n' > "${!j}"
  fi
done
exit 0
EOF
  chmod +x "$TMP/bin/curl"
  cat >"$TMP/bin/git" <<'EOF'
#!/usr/bin/env bash
printf 'git' >> "$GHACC_TEST_GIT_LOG_FILE"
for arg in "$@"; do printf '\n%s' "$arg" >> "$GHACC_TEST_GIT_LOG_FILE"; done
printf '\n---\n' >> "$GHACC_TEST_GIT_LOG_FILE"
if [[ "${GHACC_TEST_GIT_FAIL_ACCELERATED:-0}" == 1 && " $* " == *" insteadOf "* ]]; then
  exit 128
fi
if [[ "$1" == "remote" ]]; then
  printf 'https://github.com/owner/repo.git\n'
fi
exit 0
EOF
  chmod +x "$TMP/bin/git"
  cat >"$TMP/ghacc.conf" <<'EOF'
GHACC_MIRRORS=("https://mirror.test/" "https://fallback.test/")
GHACC_TIMEOUT=3
GHACC_DOWNLOAD_DIR=""
GHACC_AUTO_SELECT=1
GHACC_FALLBACK_DIRECT=1
EOF
  export GHACC_CONFIG="$TMP/ghacc.conf"
  export GHACC_TEST_CURL_LOG_FILE="$TMP/curl.log"
  export GHACC_TEST_GIT_LOG_FILE="$TMP/git.log"
  : >"$GHACC_TEST_CURL_LOG_FILE"
  : >"$GHACC_TEST_GIT_LOG_FILE"
}

assert_contains() {
  local file="$1" text="$2"
  if ! grep -Fq "$text" "$file"; then
    echo "Expected $file to contain: $text" >&2
    cat "$file" >&2
    return 1
  fi
}

expect_success() {
  local name="$1"; shift
  if "$@"; then
    PASS=$((PASS + 1))
    echo "ok - $name"
  else
    echo "not ok - $name" >&2
    exit 1
  fi
}

if [[ ! -x "$SCRIPT" ]]; then
  echo "not ok - ghacc executable must exist" >&2
  exit 1
fi

new_case
expect_success "status selects a healthy mirror" env PATH="$TMP/bin:$PATH" GHACC_TEST_CURL_LOG="https://mirror.test/https://github.com/robots.txt" "$SCRIPT" status
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://github.com/robots.txt"

new_case
expect_success "clone uses temporary insteadOf configuration" env PATH="$TMP/bin:$PATH" "$SCRIPT" clone https://github.com/owner/repo.git
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "url.https://mirror.test/https://github.com/.insteadOf=https://github.com/"

new_case
GHACC_TEST_GIT_FAIL_ACCELERATED=1
expect_success "git falls back to direct after accelerated failure" env PATH="$TMP/bin:$PATH" GHACC_TEST_GIT_FAIL_ACCELERATED=1 "$SCRIPT" pull
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "https://github.com/"
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "insteadOf"

new_case
expect_success "download creates a file through curl" env PATH="$TMP/bin:$PATH" "$SCRIPT" download https://github.com/owner/repo/releases/download/v1/file.zip
[[ -f "$TMP/download/file.zip" && "$(cat "$TMP/download/file.zip")" == "test-content" ]]
if [[ $? -eq 0 ]]; then PASS=$((PASS + 1)); echo "ok - download creates a file"; else echo "not ok - download creates a file" >&2; exit 1; fi

echo "$PASS assertions passed"
```

- [ ] **Step 2: Run the test and verify it fails because production script is absent**

Run: `bash tests/test_ghacc.sh`

Expected: FAIL with `not ok - ghacc executable must exist`.

- [ ] **Step 3: Implement executable skeleton, configuration loading, probing, and status**

Create `linux/ghacc.sh`:

```bash
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
GHACC_TIMEOUT=8
GHACC_DOWNLOAD_DIR=""
GHACC_AUTO_SELECT=1
GHACC_FALLBACK_DIRECT=1

log() { printf '[ghacc] %s\n' "$*" >&2; }
die() { log "ERROR: $*"; exit 2; }
have() { command -v "$1" >/dev/null 2>&1; }

load_config() {
  if [[ -n "${GHACC_CONFIG:-}" && -f "$GHACC_CONFIG" ]]; then
    # shellcheck disable=SC1090
    source "$GHACC_CONFIG"
  fi
  GHACC_TIMEOUT="${GHACC_TIMEOUT:-8}"
  GHACC_AUTO_SELECT="${GHACC_AUTO_SELECT:-1}"
  GHACC_FALLBACK_DIRECT="${GHACC_FALLBACK_DIRECT:-1}"
  GHACC_DOWNLOAD_DIR="${GHACC_DOWNLOAD_DIR:-$PWD}"
  [[ "$GHACC_TIMEOUT" =~ ^[0-9]+$ ]] || die "GHACC_TIMEOUT must be an integer"
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

select_mirror() {
  load_config
  local mirror
  for mirror in "${GHACC_MIRRORS[@]}"; do
    [[ "$mirror" == */ ]] || mirror="$mirror/"
    [[ "${GHACC_AUTO_SELECT:-1}" == 1 ]] || { SELECTED_MIRROR="$mirror"; return 0; }
    if probe_url "${mirror}https://github.com/robots.txt"; then
      SELECTED_MIRROR="$mirror"
      return 0
    fi
  done
  return 1
}

status_command() {
  load_config
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
Usage:
  ghacc clone <github-url> [git-options...]
  ghacc pull [git-options...]
  ghacc fetch [git-options...]
  ghacc git <git-arguments...>
  ghacc download <github-url>
  ghacc archive <owner/repo> [ref]
  ghacc release <owner/repo> <tag|latest> [asset-pattern]
  ghacc status
  ghacc help
EOF
}

load_config
case "${1:-help}" in
  status) shift; status_command "$@" ;;
  help|--help|-h|*) usage ;;
esac
```

Create `linux/ghacc.conf.example`:

```bash
# /etc/ghacc.conf
GHACC_MIRRORS=(
  "https://ghfast.top/"
  "https://gh-proxy.com/"
  "https://ghproxy.net/"
  "https://gh.llkk.cc/"
)
GHACC_TIMEOUT=8
GHACC_DOWNLOAD_DIR=""
GHACC_AUTO_SELECT=1
GHACC_FALLBACK_DIRECT=1
```

- [ ] **Step 4: Run Task 1 tests**

Run: `bash tests/test_ghacc.sh`

Expected: status and missing-script checks pass; clone and git tests may remain red until Task 2.

- [ ] **Step 5: Commit**

```bash
git add linux/ghacc.sh linux/ghacc.conf.example tests/test_ghacc.sh
git commit -m "feat: add GitHub accelerator mirror selection"
```

---

### Task 2: Git Commands and Direct Fallback

**Files:**
- Modify: `linux/ghacc.sh`

**Interfaces:**
- Consumes: `select_mirror` and `load_config` from Task 1.
- Produces: `rewrite_args`, `run_git`, `clone_command`, `pull_command`, `fetch_command`, and `git_command`.

- [ ] **Step 1: Add failing expectations for accelerated Git invocation and direct fallback**

Append to `tests/test_ghacc.sh` before the final summary:

```bash
new_case
expect_success "pull includes GitHub rewrite args" env PATH="$TMP/bin:$PATH" "$SCRIPT" pull
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "url.https://mirror.test/https://github.com/.insteadOf=https://github.com/"

new_case
expect_success "fetch includes SSH rewrite args" env PATH="$TMP/bin:$PATH" "$SCRIPT" fetch
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "insteadOf=ssh://git@github.com/"

new_case
expect_success "git passes through arguments" env PATH="$TMP/bin:$PATH" "$SCRIPT" git log --oneline
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "log"
```

- [ ] **Step 2: Run tests and verify Git rewrite assertions fail**

Run: `bash tests/test_ghacc.sh`

Expected: FAIL in `pull includes GitHub rewrite args`.

- [ ] **Step 3: Implement temporary Git URL rewriting**

Insert above the CLI `case` in `linux/ghacc.sh`:

```bash
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

run_git() {
  load_config
  local -a cmd=(git)
  local direct_failed=0
  if select_mirror; then
    log "Using mirror: $SELECTED_MIRROR"
    while IFS= read -r arg; do cmd+=("$arg"); done < <(rewrite_args "$SELECTED_MIRROR")
    cmd+=("$@")
    if "${cmd[@]}"; then return 0; fi
    direct_failed=$?
    ((direct_failed == 130)) && return "$direct_failed"
    log "Accelerated Git operation failed; trying direct GitHub"
    cmd=(git "$@")
  else
    log "No mirror available; using direct GitHub"
    cmd=(git "$@")
  fi
  "${cmd[@]}"
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

pull_command() {
  [[ -d .git ]] || die "pull must be run inside a Git repository"
  run_git pull "$@"
}

fetch_command() {
  [[ -d .git ]] || die "fetch must be run inside a Git repository"
  run_git fetch "$@"
}

git_command() {
  [[ $# -ge 1 ]] || { usage >&2; return 2; }
  run_git "$@"
}
```

Update the CLI case:

```bash
case "${1:-help}" in
  clone) shift; clone_command "$@" ;;
  pull) shift; pull_command "$@" ;;
  fetch) shift; fetch_command "$@" ;;
  git) shift; git_command "$@" ;;
  status) shift; status_command "$@" ;;
  help|--help|-h|*) usage ;;
esac
```

- [ ] **Step 4: Run tests and verify all current assertions pass**

Run: `bash tests/test_ghacc.sh`

Expected: PASS with all assertions printed as `ok`.

- [ ] **Step 5: Commit**

```bash
git add linux/ghacc.sh tests/test_ghacc.sh
git commit -m "feat: accelerate GitHub Git operations"
```

---

### Task 3: Downloads, Archives, Releases, and Documentation

**Files:**
- Modify: `linux/ghacc.sh`
- Create: `linux/README.md`

**Interfaces:**
- Consumes: `load_config`, `select_mirror`, and status output helpers.
- Produces: `download_url`, `download_command`, `archive_command`, and `release_command`.

- [ ] **Step 1: Add failing tests for download, archive, and release URL construction**

Append before the final test summary:

```bash
new_case
expect_success "archive downloads codeload tarball" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" "$SCRIPT" archive owner/repo v1.2.3
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://codeload.github.com/owner/repo/tar.gz/v1.2.3"

new_case
expect_success "release downloads an exact asset" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" "$SCRIPT" release owner/repo v1.2.3 app-linux.tar.gz
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://github.com/owner/repo/releases/download/v1.2.3/app-linux.tar.gz"

new_case
GHACC_TEST_CURL_FAIL=1
expect_success "download falls back to direct GitHub" env PATH="$TMP/bin:$PATH" GHACC_TEST_CURL_FAIL=1 "$SCRIPT" download https://raw.githubusercontent.com/owner/repo/main/README.md
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://raw.githubusercontent.com/owner/repo/main/README.md"
```

- [ ] **Step 2: Run tests and verify download assertions fail**

Run: `bash tests/test_ghacc.sh`

Expected: FAIL with missing archive output file or missing accelerated URL.

- [ ] **Step 3: Implement download fallback, archive, and release commands**

Insert above the CLI case:

```bash
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
  local url="$1" prefix="$2" output="$3" tmp
  tmp="$(mktemp "${output}.ghacc.XXXXXX")" || return 1
  if fetch_to "${prefix}${url}" "$tmp"; then
    mv -f "$tmp" "$output"
    return 0
  fi
  rm -f "$tmp"
  return 1
}

output_path() {
  local url="$1"
  if [[ -n "$GHACC_DOWNLOAD_DIR" ]]; then mkdir -p "$GHACC_DOWNLOAD_DIR"; fi
  printf '%s/%s\n' "${GHACC_DOWNLOAD_DIR:-$PWD}" "$(basename "${url%%\?*}")"
}

download_command() {
  load_config
  [[ $# -eq 1 ]] || { usage >&2; return 2; }
  local url="$1" output downloaded=0 prefix
  allowed_download_url "$url" || die "URL host is not an allowed GitHub download host"
  output="$(output_path "$url")"
  if select_mirror; then
    log "Using mirror: $SELECTED_MIRROR"
    if download_url "$url" "$SELECTED_MIRROR" "$output"; then downloaded=1; fi
  fi
  if ((downloaded == 0)); then
    [[ "$GHACC_FALLBACK_DIRECT" == 1 ]] || die "No mirror available and direct fallback is disabled"
    log "Using direct GitHub"
    download_url "$url" "" "$output" || die "Download failed: $url"
  fi
  log "Saved: $output"
}

validate_owner_repo() {
  [[ "$1" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || die "owner/repo must use the form owner/repo"
}

validate_ref() {
  [[ "$1" =~ ^[A-Za-z0-9._/-]+$ ]] || die "ref contains unsupported characters"
}

archive_command() {
  load_config
  [[ $# -ge 1 && $# -le 2 ]] || { usage >&2; return 2; }
  validate_owner_repo "$1"
  local ref="${2:-HEAD}" repo="${1##*/}" file url output
  validate_ref "$ref"
  url="https://codeload.github.com/$1/tar.gz/$ref"
  file="${repo}-${ref//\//-}.tar.gz"
  output="${GHACC_DOWNLOAD_DIR:+$GHACC_DOWNLOAD_DIR/}$file"
  [[ -n "$GHACC_DOWNLOAD_DIR" ]] && mkdir -p "$GHACC_DOWNLOAD_DIR"
  if select_mirror; then
    log "Using mirror: $SELECTED_MIRROR"
    download_url "$url" "$SELECTED_MIRROR" "$output" && { log "Saved: $output"; return 0; }
  fi
  [[ "$GHACC_FALLBACK_DIRECT" == 1 ]] || die "No mirror available and direct fallback is disabled"
  log "Using direct GitHub"
  download_url "$url" "" "$output" || die "Archive download failed"
  log "Saved: $output"
}

github_api_assets() {
  local owner_repo="$1" endpoint="$2" url result
  for url in "https://api.github.com/repos/$owner_repo/$endpoint" "${SELECTED_MIRROR:-}https://api.github.com/repos/$owner_repo/$endpoint"; do
    [[ -n "$url" ]] || continue
    result="$(mktemp)" || return 1
    if have curl; then
      curl -fsSL --connect-timeout "$GHACC_TIMEOUT" -H "Accept: application/vnd.github+json" -H "User-Agent: ghacc/$GHACC_VERSION" -o "$result" "$url"
    else
      wget -q --timeout="$GHACC_TIMEOUT" --user-agent="ghacc/$GHACC_VERSION" -O "$result" "$url"
    fi
    if [[ -s "$result" ]]; then
      grep -o '"browser_download_url":[[:space:]]*"[^"]+"' "$result" | cut -d'"' -f4
      rm -f "$result"
      return 0
    fi
    rm -f "$result"
  done
  return 1
}

release_command() {
  load_config
  [[ $# -ge 2 && $# -le 3 ]] || { usage >&2; return 2; }
  validate_owner_repo "$1"
  local repo_owner="$1" tag="$2" pattern="${3:-}" endpoint assets asset selected
  select_mirror >/dev/null 2>&1 || true
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
```

Extend the CLI case:

```bash
  download) shift; download_command "$@" ;;
  archive) shift; archive_command "$@" ;;
  release) shift; release_command "$@" ;;
```

- [ ] **Step 4: Run all tests**

Run: `bash tests/test_ghacc.sh`

Expected: PASS and a nonzero-test-count summary.

- [ ] **Step 5: Run ShellCheck if available**

Run: `if command -v shellcheck >/dev/null 2>&1; then shellcheck linux/ghacc.sh tests/test_ghacc.sh; fi`

Expected: no ShellCheck findings when installed; no output when not installed.

- [ ] **Step 6: Create user documentation**

Create `linux/README.md`:

````markdown
# ghacc: Alibaba Cloud Linux GitHub accelerator

`ghacc` is an on-demand wrapper. It never changes global or repository Git configuration.

## Install

```bash
sudo install -m 0755 linux/ghacc.sh /usr/local/bin/ghacc
sudo install -m 0644 linux/ghacc.conf.example /etc/ghacc.conf
```

Edit `/etc/ghacc.conf` to put your preferred mirror first.

## Common commands

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

`latest` without an asset pattern lists Release assets. If acceleration fails, the script falls back to direct GitHub unless `GHACC_FALLBACK_DIRECT=0`.

## Private repositories

Authentication is delegated to your existing Git credential helper or SSH setup. Do not put tokens in `/etc/ghacc.conf`.
````

- [ ] **Step 7: Final verification**

Run: `bash tests/test_ghacc.sh && bash -n linux/ghacc.sh && bash -n tests/test_ghacc.sh`

Expected: tests pass and syntax checks produce no output.

- [ ] **Step 8: Commit**

```bash
git add linux/ghacc.sh linux/README.md tests/test_ghacc.sh
git commit -m "feat: add accelerated GitHub downloads"
```
