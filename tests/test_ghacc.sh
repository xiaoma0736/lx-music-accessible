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
  cleanup
  TMP="$(mktemp -d "${TMPDIR:-/tmp}/ghacc-test.XXXXXX")"
  mkdir -p "$TMP/bin" "$TMP/work" "$TMP/download" "$TMP/cache"
  cat >"$TMP/bin/curl" <<'EOF'
#!/usr/bin/env bash
url="${*: -1}"
printf '%s\n' "$url" >> "$GHACC_TEST_CURL_LOG_FILE"
has_output=0
for ((i=1; i<$#; i++)); do
  if [[ "${!i}" == "-o" ]]; then has_output=1; fi
done
if [[ "$3" == *GHACC_TEST_HEADER ]]; then exit 22; fi
if [[ "${GHACC_TEST_CURL_FAIL:-0}" == 1 && "$has_output" == 1 ]]; then exit 22; fi
if [[ "${GHACC_TEST_CURL_FAIL_DIRECT:-0}" == 1 && "$has_output" == 1 && "$url" == https://* ]]; then exit 22; fi
if [[ -n "${GHACC_TEST_SUBSCRIPTION_FILE:-}" && "$url" == "${GHACC_TEST_SUBSCRIPTION_MATCH:-}"* ]]; then
  for ((i=1; i<=$#; i++)); do
    if [[ "${!i}" == "-o" ]]; then
      j=$((i + 1))
      cp "$GHACC_TEST_SUBSCRIPTION_FILE" "${!j}"
      exit 0
    fi
  done
fi
if [[ -n "${GHACC_TEST_CURL_REQUIRE_CONNECT_TIMEOUT:-}" && "$has_output" == 1 ]]; then
  previous=""
  for argument in "$@"; do
    if [[ "$previous" == "--connect-timeout" ]]; then
      printf 'TIMEOUT got=%q want=%q\n' "$argument" "$GHACC_TEST_CURL_REQUIRE_CONNECT_TIMEOUT" >> "$GHACC_TEST_CURL_LOG_FILE"
    fi
    if [[ "$previous" == "--connect-timeout" && "$argument" != "$GHACC_TEST_CURL_REQUIRE_CONNECT_TIMEOUT" ]]; then
      exit 22
    fi
    previous="$argument"
  done
fi
if [[ -n "${GHACC_TEST_CURL_FAIL_MIRRORS:-}" && "$has_output" == 1 ]]; then
  IFS=',' read -r -a failed_mirrors <<<"$GHACC_TEST_CURL_FAIL_MIRRORS"
  for failed_mirror in "${failed_mirrors[@]}"; do
    if [[ "${url:0:${#failed_mirror}}" == "$failed_mirror" ]]; then exit 22; fi
  done
fi
for ((i=1; i<=$#; i++)); do
  if [[ "${!i}" == "-o" ]]; then
    j=$((i + 1))
    if [[ "$url" == *"https://api.github.com/"* ]]; then
      api_response='{"assets":[{"browser_download_url":"https://github.com/owner/repo/releases/download/v1.2.3/app-linux.tar.gz"}]}'
      if [[ -n "${GHACC_TEST_API_RESPONSE:-}" ]]; then api_response="$GHACC_TEST_API_RESPONSE"; fi
      printf '%s\n' "$api_response" > "${!j}"
    else
      printf 'test-content\n' > "${!j}"
    fi
  fi
done
exit 0
EOF
  chmod +x "$TMP/bin/curl"
  cat >"$TMP/bin/git" <<'EOF'
#!/usr/bin/env bash
if [[ "$1" == "rev-parse" && "$2" == "--is-inside-work-tree" ]]; then
  inside=1
  if [[ "${GHACC_TEST_GIT_WORKTREE:-1}" != 1 ]]; then inside=0; fi
  if [[ -n "${GHACC_TEST_REPO_ROOT:-}" ]]; then
    current="$(pwd -P)"
    case "$current" in
      "$GHACC_TEST_REPO_ROOT"|"$GHACC_TEST_REPO_ROOT"/*) ;;
      *) inside=0 ;;
    esac
  fi
  if ((inside)); then printf 'true\n'; else printf 'false\n'; fi
  exit 0
fi
printf 'git' >> "$GHACC_TEST_GIT_LOG_FILE"
for arg in "$@"; do printf '\n%s' "$arg" >> "$GHACC_TEST_GIT_LOG_FILE"; done
printf '\n---\n' >> "$GHACC_TEST_GIT_LOG_FILE"
for arg in "$@"; do
  if [[ "$arg" == *"insteadOf="* ]]; then
    if [[ "${GHACC_TEST_GIT_FAIL_ACCELERATED:-0}" == 1 ]]; then
      exit 128
    fi
    if [[ -n "${GHACC_TEST_GIT_FAIL_MIRRORS:-}" ]]; then
      IFS=',' read -r -a failed_git_mirrors <<<"$GHACC_TEST_GIT_FAIL_MIRRORS"
      for failed_git_mirror in "${failed_git_mirrors[@]}"; do
        if [[ "$arg" == "url.${failed_git_mirror}"*".insteadOf="* ]]; then
          exit 128
        fi
      done
    fi
  fi
done
if [[ "$1" == "remote" ]]; then
  printf 'https://github.com/owner/repo.git\n'
fi
exit 0
EOF
  chmod +x "$TMP/bin/git"
  cat >"$TMP/ghacc.conf" <<EOF
GHACC_MIRRORS=("https://mirror.test/" "https://fallback.test/")
GHACC_TIMEOUT=3
GHACC_DOWNLOAD_DIR="$TMP/config-download"
GHACC_AUTO_SELECT=1
GHACC_FALLBACK_DIRECT=1
GHACC_SUBSCRIPTION=1
GHACC_SUBSCRIPTION_URLS=(
  "https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
  "https://cdn.jsdelivr.net/gh/Miao-moe/gh-accelerator-api@main/mirrors.json"
)
EOF
mkdir -p "$TMP/config-download"
  export GHACC_CONFIG="$TMP/ghacc.conf"
  export GHACC_CACHE_DIR="$TMP/cache"
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
    fail "assertion failed: $text"
  fi
}

fail() {
  echo "not ok - $*" >&2
  exit 1
}

assert_line_absent() {
  local file="$1" text="$2"
  if grep -Fxq "$text" "$file"; then
    echo "Expected $file not to contain line: $text" >&2
    cat "$file" >&2
    fail "assertion failed: unexpected line $text"
  fi
}

expect_failure_with() {
  local expected="$1" name="$2"; shift 2
  local output status=0
  output="$("$@" 2>&1)" || status=$?
  if ((status == 0)); then
    fail "$name"
  fi
  if ! grep -Fq "$expected" <<<"$output"; then
    echo "Expected failure output to contain: $expected" >&2
    printf '%s\n' "$output" >&2
    fail "$name"
  fi
  PASS=$((PASS + 1))
  echo "ok - $name"
}

expect_failure_without() {
  local forbidden="$1" name="$2"; shift 2
  local output status=0
  output="$("$@" 2>&1)" || status=$?
  if ((status == 0)); then
    fail "$name"
  fi
  if grep -Fq "$forbidden" <<<"$output"; then
    echo "Failure output must not contain: $forbidden" >&2
    printf '%s\n' "$output" >&2
    fail "$name"
  fi
  PASS=$((PASS + 1))
  echo "ok - $name"
}

expect_success() {
  local name="$1"; shift
  if "$@"; then
    PASS=$((PASS + 1))
    echo "ok - $name"
  else
    fail "$name"
  fi
}

last_git_invocation() {
  awk 'BEGIN { RS="---\n" } NF { last = $0 } END { print last }' "$1"
}

assert_last_git_direct() {
  local invocation required="$1"
  invocation="$(last_git_invocation "$GHACC_TEST_GIT_LOG_FILE")"
  if grep -Fq 'insteadOf=' <<<"$invocation"; then
    echo "Expected final Git invocation to be direct, got:" >&2
    printf '%s\n' "$invocation" >&2
    fail "assertion failed: final Git invocation is not direct"
  fi
  if ! grep -Fq "$required" <<<"$invocation"; then
    echo "Expected final direct Git invocation to contain: $required" >&2
    printf '%s\n' "$invocation" >&2
    fail "assertion failed: final Git invocation is not $required"
  fi
}

assert_git_invocation_count() {
  local expected="$1" actual
  actual="$(grep -c '^---$' "$GHACC_TEST_GIT_LOG_FILE")"
  if [[ "$actual" != "$expected" ]]; then
    echo "Expected $expected Git invocations, got $actual:" >&2
    cat "$GHACC_TEST_GIT_LOG_FILE" >&2
    fail "assertion failed: Git invocation count"
  fi
}

git_attempt_sequence() {
  awk '
    BEGIN { RS = "---\n" }
    NF {
      url = ""
      count = split($0, lines, "\n")
      marker = "https://github.com/.insteadOf="
      for (i = 1; i <= count; i++) {
        if (index(lines[i], "url.") == 1) {
          pos = index(lines[i], marker)
          if (pos > 0) {
            url = substr(lines[i], 5, pos - 5)
            break
          }
        }
      }
      if (url != "") print url
    }
  ' "$1"
}

expect_failure() {
  local name="$1"; shift
  if "$@"; then
    fail "$name"
  else
    PASS=$((PASS + 1))
    echo "ok - $name"
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
expect_failure "status rejects extra arguments" env PATH="$TMP/bin:$PATH" "$SCRIPT" status unexpected
expect_failure "unknown command fails" env PATH="$TMP/bin:$PATH" "$SCRIPT" definitely-unknown
expect_failure "listed transfer command rejects missing arguments" env PATH="$TMP/bin:$PATH" "$SCRIPT" download

new_case
expect_success "pull includes GitHub rewrite args" env PATH="$TMP/bin:$PATH" "$SCRIPT" pull
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "url.https://mirror.test/https://github.com/.insteadOf=https://github.com/"

new_case
expect_success "fetch includes SSH rewrite args" env PATH="$TMP/bin:$PATH" "$SCRIPT" fetch
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "insteadOf=ssh://git@github.com/"

new_case
expect_success "git passes through arguments" env PATH="$TMP/bin:$PATH" "$SCRIPT" git log --oneline
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "log"

new_case
expect_success "clone includes GitHub rewrite args" env PATH="$TMP/bin:$PATH" "$SCRIPT" clone https://github.com/owner/repo.git
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "url.https://mirror.test/https://github.com/.insteadOf=https://github.com/"

new_case
expect_success "git uses next mirror after first accelerated failure" env PATH="$TMP/bin:$PATH" GHACC_TEST_GIT_FAIL_MIRRORS="https://mirror.test/" "$SCRIPT" pull
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "url.https://fallback.test/https://github.com/.insteadOf="
assert_git_invocation_count 2

new_case
expect_success "git falls back to direct after all accelerated failures" env PATH="$TMP/bin:$PATH" GHACC_TEST_GIT_FAIL_ACCELERATED=1 "$SCRIPT" pull
assert_last_git_direct "pull"
assert_git_invocation_count 7

new_case
expect_failure_with "direct fallback is disabled" "git honors disabled direct fallback" env PATH="$TMP/bin:$PATH" GHACC_TEST_GIT_FAIL_ACCELERATED=1 GHACC_FALLBACK_DIRECT=0 "$SCRIPT" pull
assert_git_invocation_count 6

new_case
expect_success "archive downloads codeload tarball" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" "$SCRIPT" archive owner/repo v1.2.3
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://codeload.github.com/owner/repo/tar.gz/v1.2.3"

new_case
expect_success "archive uses next mirror after transfer failure" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_TEST_CURL_FAIL_MIRRORS="https://mirror.test/" "$SCRIPT" archive owner/repo v1.2.3
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://fallback.test/https://codeload.github.com/owner/repo/tar.gz/v1.2.3"
assert_line_absent "$GHACC_TEST_CURL_LOG_FILE" "https://codeload.github.com/owner/repo/tar.gz/v1.2.3"

new_case
expect_failure_with "direct fallback is disabled" "archive honors disabled direct fallback" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_FALLBACK_DIRECT=0 GHACC_TEST_CURL_FAIL=1 "$SCRIPT" archive owner/repo v1.2.3
assert_line_absent "$GHACC_TEST_CURL_LOG_FILE" "https://codeload.github.com/owner/repo/tar.gz/v1.2.3"

new_case
expect_success "download uses next mirror after transfer failure" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_TEST_CURL_FAIL_MIRRORS="https://mirror.test/" "$SCRIPT" download https://raw.githubusercontent.com/owner/repo/main/README.md
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://fallback.test/https://raw.githubusercontent.com/owner/repo/main/README.md"
assert_line_absent "$GHACC_TEST_CURL_LOG_FILE" "https://raw.githubusercontent.com/owner/repo/main/README.md"

new_case
expect_success "download falls back to direct after transfer failure" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_TEST_CURL_FAIL_MIRRORS="https://mirror.test/,https://fallback.test/,https://ghfast.top/,https://gh-proxy.com/,https://ghproxy.net/,https://gh.llkk.cc/" "$SCRIPT" download https://raw.githubusercontent.com/owner/repo/main/README.md
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://ghfast.top/https://raw.githubusercontent.com/owner/repo/main/README.md"
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://raw.githubusercontent.com/owner/repo/main/README.md"

new_case
expect_failure_with "direct fallback is disabled" "download honors disabled direct fallback" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_TEST_CURL_FAIL=1 GHACC_FALLBACK_DIRECT=0 "$SCRIPT" download https://raw.githubusercontent.com/owner/repo/main/README.md
assert_line_absent "$GHACC_TEST_CURL_LOG_FILE" "https://raw.githubusercontent.com/owner/repo/main/README.md"

new_case
for unsafe_name in '.' '..'; do
  expect_failure_with "unsafe download basename: $unsafe_name" "download rejects basename $unsafe_name" env PATH="$TMP/bin:$PATH" "$SCRIPT" download "https://raw.githubusercontent.com/owner/repo/main/$unsafe_name"
done
assert_line_absent "$GHACC_TEST_CURL_LOG_FILE" "https://raw.githubusercontent.com/owner/repo/main/.."
assert_line_absent "$GHACC_TEST_CURL_LOG_FILE" "https://raw.githubusercontent.com/owner/repo/main/."

new_case
mkdir -p "$TMP/repo/.git" "$TMP/repo/subdir"
expect_success "pull works from a repository subdirectory" env PATH="$TMP/bin:$PATH" GHACC_TEST_REPO_ROOT="$TMP/repo" bash -c 'cd "$1" && exec "$2" pull' bash "$TMP/repo/subdir" "$SCRIPT"
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "url.https://mirror.test/https://github.com/.insteadOf="

new_case
mkdir -p "$TMP/not-repo"
expect_failure_with "pull must be run inside a Git repository" "pull rejects a non-repository" env PATH="$TMP/bin:$PATH" GHACC_TEST_GIT_WORKTREE=0 "$SCRIPT" pull

new_case
expect_success "release metadata uses next mirror after API failure" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_TEST_CURL_FAIL_MIRRORS="https://mirror.test/" "$SCRIPT" release owner/repo v1.2.3 app-linux.tar.gz
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://fallback.test/https://api.github.com/repos/owner/repo/releases/tags/v1.2.3"
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://github.com/owner/repo/releases/download/v1.2.3/app-linux.tar.gz"

new_case
expect_success "environment overrides sourced download dir, timeout, and auto-select" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/env-download" GHACC_TIMEOUT=99 GHACC_AUTO_SELECT=0 GHACC_TEST_CURL_REQUIRE_CONNECT_TIMEOUT=99 "$SCRIPT" archive owner/repo v1.2.3
[[ -f "$TMP/env-download/repo-v1.2.3.tar.gz" ]] || fail "environment GHACC_DOWNLOAD_DIR override was not used"
[[ -z "$(find "$TMP/config-download" -type f -print -quit)" ]] || fail "config GHACC_DOWNLOAD_DIR was used instead of environment"
if grep -Fq "https://mirror.test/https://github.com/robots.txt" "$GHACC_TEST_CURL_LOG_FILE"; then
  fail "GHACC_AUTO_SELECT=0 did not override config after sourcing"
fi

new_case
expect_failure_with "direct fallback is disabled" "environment overrides sourced direct fallback" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_FALLBACK_DIRECT=0 GHACC_TEST_CURL_FAIL_MIRRORS="https://mirror.test/,https://fallback.test/,https://ghfast.top/,https://gh-proxy.com/,https://ghproxy.net/,https://gh.llkk.cc/" "$SCRIPT" download https://raw.githubusercontent.com/owner/repo/main/README.md
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://ghfast.top/https://raw.githubusercontent.com/owner/repo/main/README.md"
assert_line_absent "$GHACC_TEST_CURL_LOG_FILE" "https://raw.githubusercontent.com/owner/repo/main/README.md"

new_case
expect_success "empty environment download dir overrides config" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="" bash -c 'cd "$1" && exec "$2" archive owner/repo v1.2.3' bash "$TMP/work" "$SCRIPT"
[[ -f "$TMP/work/repo-v1.2.3.tar.gz" ]] || fail "empty GHACC_DOWNLOAD_DIR did not select current directory"
[[ -z "$(find "$TMP/config-download" -type f -print -quit)" ]] || fail "config download dir was used despite empty environment override"

new_case
expect_success "download falls back to direct GitHub" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_TEST_CURL_FAIL_MIRRORS="https://mirror.test/,https://fallback.test/,https://ghfast.top/,https://gh-proxy.com/,https://ghproxy.net/,https://gh.llkk.cc/" "$SCRIPT" download https://raw.githubusercontent.com/owner/repo/main/README.md
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://raw.githubusercontent.com/owner/repo/main/README.md"

new_case
cat >"$TMP/bin/mv" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GHACC_TEST_MV_LOG_FILE"
exit 73
EOF
chmod +x "$TMP/bin/mv"
export GHACC_TEST_MV_LOG_FILE="$TMP/mv.log"
: >"$GHACC_TEST_MV_LOG_FILE"
expect_failure_without "Saved:" "failed rename does not report Saved" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" "$SCRIPT" download https://raw.githubusercontent.com/owner/repo/main/README.md
assert_contains "$GHACC_TEST_MV_LOG_FILE" "$TMP/download/README.md"

new_case
expect_failure_with "Unable to read release assets" "malformed release metadata fails" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_TEST_API_RESPONSE=not-json "$SCRIPT" release owner/repo v1.2.3 app-linux.tar.gz

new_case
expect_failure_with "Unable to read release assets" "release metadata honors direct fallback disabled" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_FALLBACK_DIRECT=0 GHACC_TEST_API_RESPONSE=not-json "$SCRIPT" release owner/repo v1.2.3 app-linux.tar.gz
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://api.github.com/repos/owner/repo/releases/tags/v1.2.3"
assert_line_absent "$GHACC_TEST_CURL_LOG_FILE" "https://api.github.com/repos/owner/repo/releases/tags/v1.2.3"

new_case
expect_success "release metadata falls back to direct GitHub" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_TEST_CURL_FAIL_MIRRORS="https://mirror.test/,https://fallback.test/,https://ghfast.top/,https://gh-proxy.com/,https://ghproxy.net/,https://gh.llkk.cc/" "$SCRIPT" release owner/repo v1.2.3 app-linux.tar.gz
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://ghfast.top/https://api.github.com/repos/owner/repo/releases/tags/v1.2.3"
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://api.github.com/repos/owner/repo/releases/tags/v1.2.3"
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://github.com/owner/repo/releases/download/v1.2.3/app-linux.tar.gz"

new_case
expect_success "help displays Chinese usage" env PATH="$TMP/bin:$PATH" bash -c '"$1" help | grep -Fq "用法:"' bash "$SCRIPT"

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
export GHACC_TEST_SUBSCRIPTION_MATCH="https://cdn.jsdelivr.net/gh/Miao-moe/gh-accelerator-api@main/mirrors.json"
export GHACC_TEST_CURL_FAIL_MIRRORS="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
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

new_case
printf '%s\n' "https://old.test/" >"$GHACC_CACHE_DIR/mirrors.list"
printf 'updated=old\nsource=old\ncount=1\n' >"$GHACC_CACHE_DIR/mirrors.meta"
cat >"$GHACC_CONFIG" <<'EOF'
GHACC_MIRRORS=("https://mirror.test/")
GHACC_TIMEOUT=3
GHACC_AUTO_SELECT=1
GHACC_FALLBACK_DIRECT=1
GHACC_SUBSCRIPTION=1
GHACC_SUBSCRIPTION_URLS=(
  "https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
  "http://insecure-source.test/mirrors.json"
)
EOF
subscription_json >"$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
expect_failure_with "invalid subscription source: http://insecure-source.test/mirrors.json" "update rejects invalid source before fetching" env PATH="$TMP/bin:$PATH" "$SCRIPT" update
[[ ! -s "$GHACC_TEST_CURL_LOG_FILE" ]] || fail "invalid subscription source caused a fetch"
assert_contains "$GHACC_CACHE_DIR/mirrors.list" "https://old.test/"
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" "updated=old"

new_case
printf '%s\n' "https://old.test/" >"$GHACC_CACHE_DIR/mirrors.list"
printf 'updated=old\nsource=old\ncount=1\n' >"$GHACC_CACHE_DIR/mirrors.meta"
subscription_json >"$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
unset GHACC_TEST_CURL_FAIL_MIRRORS
real_mv="$(command -v mv)"
cat >"$TMP/bin/mv" <<EOF
#!/usr/bin/env bash
if [[ "\${GHACC_TEST_MV_FAIL_META:-0}" == 1 && "\$3" == */mirrors.meta ]]; then
  exit 73
fi
exec "$real_mv" "\$@"
EOF
chmod +x "$TMP/bin/mv"
expect_failure_with "unable to replace subscription metadata" "failed metadata replacement restores old cache pair" env PATH="$TMP/bin:$PATH" GHACC_TEST_MV_FAIL_META=1 "$SCRIPT" update
assert_contains "$GHACC_CACHE_DIR/mirrors.list" "https://old.test/"
assert_line_absent "$GHACC_CACHE_DIR/mirrors.list" "https://sub-one.test/"
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" "updated=old"
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" "source=old"
subscription_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.subscription-*' -print -quit)"
backup_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.mirrors.list.backup.*' -print -quit)"
[[ -z "$subscription_staged" && -z "$backup_staged" ]] || fail "failed update left staged files"
new_case
printf '%s\n' 'https://old.test/' >"$GHACC_CACHE_DIR/mirrors.list"
printf 'updated=old\nsource=old\ncount=1\n' >"$GHACC_CACHE_DIR/mirrors.meta"
subscription_json >"$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
unset GHACC_TEST_CURL_FAIL_MIRRORS
real_mktemp="$(command -v mktemp)"
cat >"$TMP/bin/mktemp" <<EOF
#!/usr/bin/env bash
case "\$*" in
*/.subscription-meta.XXXXXX)
  printf '%s\n' "\$GHACC_CACHE_DIR/.subscription-meta-missing/meta"
  exit 0
  ;;
esac
exec "$real_mktemp" "\$@"
EOF
chmod +x "$TMP/bin/mktemp"
expect_failure_with 'metadata staging failed' 'failed metadata staging preserves cache' env PATH="$TMP/bin:$PATH" "$SCRIPT" update
assert_contains "$GHACC_CACHE_DIR/mirrors.list" 'https://old.test/'
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" 'updated=old'
subscription_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.subscription-*' -print -quit)"
backup_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.mirrors.list.backup.*' -print -quit)"
[[ -z "$subscription_staged" && -z "$backup_staged" ]] || fail 'failed metadata staging left staged files'

new_case
printf '%s\n' 'https://old.test/' >"$GHACC_CACHE_DIR/mirrors.list"
printf 'updated=old\nsource=old\ncount=1\n' >"$GHACC_CACHE_DIR/mirrors.meta"
subscription_json >"$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
unset GHACC_TEST_CURL_FAIL_MIRRORS
real_mv="$(command -v mv)"
cat >"$TMP/bin/mv" <<EOF
#!/usr/bin/env bash
count_file="\$TMP/mv.calls"
calls="\$(cat "\$count_file")"
calls=\$((calls + 1))
printf '%s\n' "\$calls" > "\$count_file"
if [[ "\$calls" -eq 2 || "\$calls" -eq 3 ]]; then exit 73; fi
exec "$real_mv" "\$@"
EOF
chmod +x "$TMP/bin/mv"
printf '0\n' >"$TMP/mv.calls"
expect_failure_without 'Updated mirrors:' 'failed rollback retains recovery data' env PATH="$TMP/bin:$PATH" "$SCRIPT" update
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" 'updated=old'
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" 'source=old'
assert_contains "$GHACC_CACHE_DIR/mirrors.list" 'https://sub-one.test/'
backup_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.mirrors.list.backup.*' -print -quit)"
[[ -n "$backup_staged" ]] || fail 'failed rollback removed backup'
assert_contains "$backup_staged" 'https://old.test/'

new_case
printf '%s\n' 'https://old.test/' >"$GHACC_CACHE_DIR/mirrors.list"
printf 'updated=old\nsource=old\ncount=1\n' >"$GHACC_CACHE_DIR/mirrors.meta"
printf '%s\n' '{"lastUpdated":"2026-09-23T00:00:00Z"}' >"$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
unset GHACC_TEST_CURL_FAIL_MIRRORS
expect_failure_with 'subscription validation failed' 'missing mirrors preserves cache' env PATH="$TMP/bin:$PATH" "$SCRIPT" update
assert_contains "$GHACC_CACHE_DIR/mirrors.list" 'https://old.test/'
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" 'updated=old'
subscription_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.subscription-*' -print -quit)"
backup_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.mirrors.list.backup.*' -print -quit)"
[[ -z "$subscription_staged" && -z "$backup_staged" ]] || fail 'missing mirrors left staged files'

new_case
printf '%s\n' 'https://old.test/' >"$GHACC_CACHE_DIR/mirrors.list"
printf 'updated=old\nsource=old\ncount=1\n' >"$GHACC_CACHE_DIR/mirrors.meta"
printf '%s\n' '{"lastUpdated":"2026-09-23T00:00:00Z","mirrors":null}' >"$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
unset GHACC_TEST_CURL_FAIL_MIRRORS
expect_failure_with 'subscription validation failed' 'null mirrors preserves cache' env PATH="$TMP/bin:$PATH" "$SCRIPT" update
assert_contains "$GHACC_CACHE_DIR/mirrors.list" 'https://old.test/'
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" 'updated=old'
subscription_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.subscription-*' -print -quit)"
backup_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.mirrors.list.backup.*' -print -quit)"
[[ -z "$subscription_staged" && -z "$backup_staged" ]] || fail 'null mirrors left staged files'

new_case
printf '%s\n' 'https://old.test/' >"$GHACC_CACHE_DIR/mirrors.list"
printf 'updated=old\nsource=old\ncount=1\n' >"$GHACC_CACHE_DIR/mirrors.meta"
cat >"$TMP/subscription.json" <<'EOF'
{
  "lastUpdated": "2026-09-23T00:00:00Z",
  "mirrors": {
    "url": "https://decoy-mirror.test/",
    "type": "file-download",
    "status": "active",
    "features": ["Git Clone"]
  }
}
EOF
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
unset GHACC_TEST_CURL_FAIL_MIRRORS
expect_failure_with 'subscription validation failed' 'non-array mirrors preserves cache' env PATH="$TMP/bin:$PATH" "$SCRIPT" update
assert_contains "$GHACC_CACHE_DIR/mirrors.list" 'https://old.test/'
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" 'updated=old'
subscription_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.subscription-*' -print -quit)"
backup_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.mirrors.list.backup.*' -print -quit)"
[[ -z "$subscription_staged" && -z "$backup_staged" ]] || fail 'non-array mirrors left staged files'

new_case
printf '%s\n' 'https://old.test/' >"$GHACC_CACHE_DIR/mirrors.list"
printf 'updated=old\nsource=old\ncount=1\n' >"$GHACC_CACHE_DIR/mirrors.meta"
cat >"$TMP/subscription.json" <<'EOF'
{
  "lastUpdated": "2026-09-23T00:00:00Z",
  "decoy": {
    "url": "https://decoy-mirror.test/",
    "type": "file-download",
    "status": "active",
    "features": ["Git Clone"]
  },
  "mirrors": [
    {
      "url": "https://array-mirror.test/",
      "type": "file-download",
      "status": "active",
      "features": ["Git Clone"]
    }
  ]
}
EOF
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
unset GHACC_TEST_CURL_FAIL_MIRRORS
expect_success 'update ignores top-level decoy objects' env PATH="$TMP/bin:$PATH" "$SCRIPT" update
assert_contains "$GHACC_CACHE_DIR/mirrors.list" 'https://array-mirror.test/'
assert_line_absent "$GHACC_CACHE_DIR/mirrors.list" 'https://decoy-mirror.test/'
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" 'count=1'

new_case
printf '%s\n' 'https://old.test/' >"$GHACC_CACHE_DIR/mirrors.list"
printf 'updated=old\nsource=old\ncount=1\n' >"$GHACC_CACHE_DIR/mirrors.meta"
printf '%s\n' '{"lastUpdated":"2026-09-23T00:00:00Z","mirrors":[]}' >"$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
unset GHACC_TEST_CURL_FAIL_MIRRORS
expect_failure_with 'subscription validation failed' 'empty mirrors preserves cache' env PATH="$TMP/bin:$PATH" "$SCRIPT" update
assert_contains "$GHACC_CACHE_DIR/mirrors.list" 'https://old.test/'
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" 'updated=old'
subscription_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.subscription-*' -print -quit)"
backup_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.mirrors.list.backup.*' -print -quit)"
[[ -z "$subscription_staged" && -z "$backup_staged" ]] || fail 'empty mirrors left staged files'

new_case
printf '%s\n' 'https://old.test/' >"$GHACC_CACHE_DIR/mirrors.list"
printf 'updated=old\nsource=old\ncount=1\n' >"$GHACC_CACHE_DIR/mirrors.meta"
long_host="$(printf "a%.0s" $(seq 1 2049))"
cat >"$TMP/subscription.json" <<EOF
{
  "lastUpdated": "2026-09-23T00:00:00Z",
  "mirrors": [
    {
      "url": "https://$long_host.test/",
      "type": "file-download",
      "status": "active",
      "features": ["Git Clone"]
    }
  ]
}
EOF
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
unset GHACC_TEST_CURL_FAIL_MIRRORS
expect_failure_with 'subscription validation failed' 'over-length mirror URL preserves cache' env PATH="$TMP/bin:$PATH" "$SCRIPT" update
assert_contains "$GHACC_CACHE_DIR/mirrors.list" 'https://old.test/'
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" 'updated=old'
subscription_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.subscription-*' -print -quit)"
backup_staged="$(find "$GHACC_CACHE_DIR" -maxdepth 1 -name '.mirrors.list.backup.*' -print -quit)"
[[ -z "$subscription_staged" && -z "$backup_staged" ]] || fail 'over-length URL left staged files'

capped_subscription_json() {
  cat <<'EOF'
{
  "lastUpdated": "2026-09-23T00:00:00Z",
  "mirrors": [
    {
      "url": "https://cap-one.test/",
      "type": "file-download",
      "status": "active",
      "features": ["Git Clone"]
    },
    {
      "url": "https://inactive.test/",
      "type": "file-download",
      "status": "inactive",
      "features": ["Git Clone"]
    },
    {
      "url": "https://cap-two.test",
      "type": "file-download",
      "status": "active",
      "features": ["Git Clone"]
    },
    {
      "url": "https://cap-one.test/",
      "type": "file-download",
      "status": "active",
      "features": ["Git Clone"]
    },
    {
      "url": "https://wrong-type.test/",
      "type": "direct-access",
      "status": "active",
      "features": ["Git Clone"]
    },
    {
      "url": "https://cap-three.test/",
      "type": "file-download",
      "status": "active",
      "features": ["Git Clone"]
    },
    {
      "url": "http://insecure.test/",
      "type": "file-download",
      "status": "active",
      "features": ["Git Clone"]
    },
    {
      "url": "https://cap-four.test/",
      "type": "file-download",
      "status": "active",
      "features": ["Git Clone"]
    }
  ]
}
EOF
}
capped_subscription_json >"$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_FILE="$TMP/subscription.json"
export GHACC_TEST_SUBSCRIPTION_MATCH="https://raw.githubusercontent.com/Miao-moe/gh-accelerator-api/main/mirrors.json"
unset GHACC_TEST_CURL_FAIL_MIRRORS
printf '%s\n' \
  "https://cap-one.test/" \
  "https://cap-two.test/" \
  "https://cap-three.test/" >"$TMP/expected.list"
expect_success "update honors subscription mirror cap" env PATH="$TMP/bin:$PATH" GHACC_SUBSCRIPTION_MAX_MIRRORS=3 "$SCRIPT" update
diff -u "$TMP/expected.list" "$GHACC_CACHE_DIR/mirrors.list" || fail "subscription mirror cap output mismatch"
assert_contains "$GHACC_CACHE_DIR/mirrors.meta" "count=3"

new_case
printf '%s\n' "https://cached.test" "https://cached.test/" >"$GHACC_CACHE_DIR/mirrors.list"
printf '%s\n' \
  "https://mirror.test/" \
  "https://fallback.test/" \
  "https://cached.test/" \
  "https://ghfast.top/" >"$TMP/expected-attempts"
expect_success "manual mirrors precede cached and built-in mirrors" env PATH="$TMP/bin:$PATH" GHACC_TEST_GIT_FAIL_MIRRORS="https://mirror.test/,https://fallback.test/,https://cached.test/" "$SCRIPT" pull
assert_git_invocation_count 4
git_attempt_sequence "$GHACC_TEST_GIT_LOG_FILE" >"$TMP/actual-attempts"
diff -u "$TMP/expected-attempts" "$TMP/actual-attempts" || fail "assertion failed: mirror attempt order"

new_case
printf '%s\n' "https://disabled-cache.test" >"$GHACC_CACHE_DIR/mirrors.list"
printf '%s\n' \
  "https://mirror.test/" \
  "https://fallback.test/" \
  "https://ghfast.top/" \
  "https://gh-proxy.com/" >"$TMP/expected-attempts"
expect_success "subscription disabled excludes cache mirrors" env PATH="$TMP/bin:$PATH" GHACC_SUBSCRIPTION=0 GHACC_TEST_GIT_FAIL_MIRRORS="https://mirror.test/,https://fallback.test/,https://ghfast.top/" "$SCRIPT" pull
assert_git_invocation_count 4
git_attempt_sequence "$GHACC_TEST_GIT_LOG_FILE" >"$TMP/actual-attempts"
diff -u "$TMP/expected-attempts" "$TMP/actual-attempts" || fail "assertion failed: disabled-subscription attempt order"
assert_line_absent "$GHACC_TEST_GIT_LOG_FILE" "url.https://disabled-cache.test/https://github.com/.insteadOf="

new_case
mkdir -p "$TMP/systemd"
cat >"$TMP/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GHACC_TEST_SYSTEMCTL_LOG_FILE"
if [[ "$1" == "is-enabled" ]]; then
  exit 1
fi
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
status_output="$(env PATH="$TMP/bin:$PATH" "$SCRIPT" status)"
status_status=$?
[[ "$status_status" == 0 ]] || fail "status returned $status_status when timer was not enabled"
PASS=$((PASS + 1))
echo "ok - status shows subscription cache"
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "is-enabled ghacc-update.timer"
for expected in \
  "Subscription: enabled" \
  "Cache: $GHACC_CACHE_DIR/mirrors.list" \
  "Cached mirrors: 1" \
  "updated=2026-09-23T00:00:00Z" \
  "source=https://subscription.test/mirrors.json" \
  "count=1" \
  "subscription_updated=2026-09-23T00:00:00Z" \
  "Update timer: not enabled"; do
  if ! grep -Fq "$expected" <<<"$status_output"; then
    echo "Expected status output to contain: $expected" >&2
    printf '%s\n' "$status_output" >&2
    fail "assertion failed: status output"
  fi
done

new_case
printf '%s\n' "https://status-cache.test/" >"$GHACC_CACHE_DIR/mirrors.list"
cat >"$GHACC_CACHE_DIR/mirrors.meta" <<'EOF'
updated=2026-09-23T00:00:00Z
source=https://subscription.test/mirrors.json
count=1
subscription_updated=2026-09-23T00:00:00Z
EOF
export GHACC_SYSTEMCTL="$TMP/bin/no-such-systemctl"
status_output="$(env PATH="$TMP/bin:$PATH" "$SCRIPT" status)"
status_status=$?
[[ "$status_status" == 0 ]] || fail "status returned $status_status when systemd was unavailable"
PASS=$((PASS + 1))
echo "ok - status remains successful when systemd unavailable"
if ! grep -Fq 'Update timer: systemd unavailable' <<<"$status_output"; then
  printf '%s\n' "$status_output" >&2
  fail 'unavailable systemctl was not distinguished'
fi

new_case
mkdir -p "$TMP/bin"
cat >"$TMP/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
if [[ "$1" == "is-enabled" ]]; then
  exit 42
fi
exit 0
EOF
chmod +x "$TMP/bin/systemctl"
printf '%s\n' "https://status-cache.test/" >"$GHACC_CACHE_DIR/mirrors.list"
cat >"$GHACC_CACHE_DIR/mirrors.meta" <<'EOF'
updated=2026-09-23T00:00:00Z
source=https://subscription.test/mirrors.json
count=1
subscription_updated=2026-09-23T00:00:00Z
EOF
export GHACC_SYSTEMCTL="$TMP/bin/systemctl"
status_output="$(env PATH="$TMP/bin:$PATH" "$SCRIPT" status)"
status_status=$?
[[ "$status_status" == 0 ]] || fail "status returned $status_status for unknown systemctl failure"
PASS=$((PASS + 1))
echo "ok - status remains successful for unknown systemctl failure"
if ! grep -Fq 'Update timer: unknown' <<<"$status_output"; then
  printf '%s\n' "$status_output" >&2
  fail 'unknown systemctl failure was not distinguished'
fi

new_case
mkdir -p "$TMP/systemd" "$TMP/bin" "$GHACC_CACHE_DIR"
cat >"$TMP/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GHACC_TEST_SYSTEMCTL_LOG_FILE"
exit 0
EOF
chmod +x "$TMP/bin/systemctl"
export GHACC_SYSTEMD_UNIT_DIR="$TMP/systemd"
export GHACC_SYSTEMCTL="$TMP/bin/systemctl"
export GHACC_UPDATE_SCHEDULE='*-*-* 04:30:00'
export GHACC_TEST_SYSTEMCTL_LOG_FILE="$TMP/systemctl.log"
: >"$GHACC_TEST_SYSTEMCTL_LOG_FILE"
expect_success "install-timer writes and enables units" env PATH="$TMP/bin:$PATH" "$SCRIPT" install-timer
[[ -f "$TMP/systemd/ghacc-update.service" ]] || fail "service unit missing"
[[ -f "$TMP/systemd/ghacc-update.timer" ]] || fail "timer unit missing"
for required in \
  "Type=oneshot" \
  "NoNewPrivileges=true" \
  "PrivateTmp=true" \
  "ProtectSystem=strict" \
  "ReadWritePaths=$GHACC_CACHE_DIR"; do
  if ! grep -Fxq "$required" "$TMP/systemd/ghacc-update.service"; then
    echo "Expected service hardening line: $required" >&2
    cat "$TMP/systemd/ghacc-update.service" >&2
    fail "assertion failed: service hardening"
  fi
done
assert_contains "$TMP/systemd/ghacc-update.timer" "$GHACC_UPDATE_SCHEDULE"
assert_contains "$TMP/systemd/ghacc-update.service" "ExecStart=$SCRIPT update"
grep -Fxq "Unit=ghacc-update.service" "$TMP/systemd/ghacc-update.timer" || fail "timer does not target ghacc-update.service"
if grep -Fxq "Unit=ghacc-update.timer" "$TMP/systemd/ghacc-update.timer"; then
  fail "timer incorrectly targets itself"
fi
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "daemon-reload"
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "enable --now ghacc-update.timer"

new_case
mkdir -p "$TMP/systemd" "$TMP/bin"
cat >"$TMP/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GHACC_TEST_SYSTEMCTL_LOG_FILE"
exit 0
EOF
chmod +x "$TMP/bin/systemctl"
export GHACC_SYSTEMD_UNIT_DIR="$TMP/systemd"
export GHACC_SYSTEMCTL="$TMP/bin/systemctl"
export GHACC_UPDATE_SCHEDULE='*-*-* 04:30:00'
export GHACC_TEST_SYSTEMCTL_LOG_FILE="$TMP/systemctl.log"
: >"$GHACC_TEST_SYSTEMCTL_LOG_FILE"
script_canonical="$(cd "$(dirname "$SCRIPT")" && pwd -P)/$(basename "$SCRIPT")"
expect_success "relative install-timer canonicalizes ExecStart" \
  env PATH="$TMP/bin:$PATH" ./linux/ghacc.sh install-timer
grep -Fxq "ExecStart=$script_canonical update" "$TMP/systemd/ghacc-update.service" ||
  fail "relative install-timer did not canonicalize ExecStart"

new_case
mkdir -p "$TMP/systemd" "$TMP/bin" "$TMP/unsafe path"
cat >"$TMP/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GHACC_TEST_SYSTEMCTL_LOG_FILE"
exit 0
EOF
chmod +x "$TMP/bin/systemctl"
cp "$SCRIPT" "$TMP/unsafe path/ghacc"
export GHACC_SYSTEMD_UNIT_DIR="$TMP/systemd"
export GHACC_SYSTEMCTL="$TMP/bin/systemctl"
export GHACC_UPDATE_SCHEDULE='*-*-* 04:30:00'
export GHACC_TEST_SYSTEMCTL_LOG_FILE="$TMP/systemctl.log"
: >"$GHACC_TEST_SYSTEMCTL_LOG_FILE"
expect_failure_without 'Installed update timer' 'install-timer rejects unsafe script path' \
  env PATH="$TMP/bin:$PATH" "$TMP/unsafe path/ghacc" install-timer
[[ ! -e "$TMP/systemd/ghacc-update.service" ]] || fail 'unsafe path created service unit'
[[ ! -e "$TMP/systemd/ghacc-update.timer" ]] || fail 'unsafe path created timer unit'
[[ ! -s "$GHACC_TEST_SYSTEMCTL_LOG_FILE" ]] || fail 'unsafe path invoked systemctl'

expect_success "uninstall-timer disables and removes units" env PATH="$TMP/bin:$PATH" "$SCRIPT" uninstall-timer
[[ ! -e "$TMP/systemd/ghacc-update.service" ]] || fail "service unit still exists"
[[ ! -e "$TMP/systemd/ghacc-update.timer" ]] || fail "timer unit still exists"
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "disable --now ghacc-update.timer"

new_case
mkdir -p "$TMP/systemd" "$TMP/bin" "$TMP/cache" "$TMP/cache-parent"
cat >"$TMP/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GHACC_TEST_SYSTEMCTL_LOG_FILE"
exit 0
EOF
chmod +x "$TMP/bin/systemctl"
printf 'not-a-directory\n' >"$TMP/blocked-cache"
export GHACC_SYSTEMD_UNIT_DIR="$TMP/systemd"
export GHACC_SYSTEMCTL="$TMP/bin/systemctl"
export GHACC_UPDATE_SCHEDULE='*-*-* 04:30:00'
export GHACC_TEST_SYSTEMCTL_LOG_FILE="$TMP/systemctl.log"
: >"$GHACC_TEST_SYSTEMCTL_LOG_FILE"
expect_failure_with "cannot create cache directory" "install-timer rejects an invalid cache path" \
  env PATH="$TMP/bin:$PATH" GHACC_CACHE_DIR="$TMP/blocked-cache" "$SCRIPT" install-timer
[[ ! -e "$TMP/systemd/ghacc-update.service" ]] || fail "service unit created despite invalid cache path"
[[ ! -e "$TMP/systemd/ghacc-update.timer" ]] || fail "timer unit created despite invalid cache path"
[[ ! -s "$GHACC_TEST_SYSTEMCTL_LOG_FILE" ]] || fail "systemctl was invoked despite invalid cache path"

new_case
mkdir -p "$TMP/systemd" "$TMP/bin" "$TMP/cache"
cat >"$TMP/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GHACC_TEST_SYSTEMCTL_LOG_FILE"
exit 0
EOF
chmod +x "$TMP/bin/systemctl"
real_cat="$(command -v cat)"
cat >"$TMP/bin/cat" <<EOF
#!/usr/bin/env bash
count_file="\$TMP/cat.calls"
calls="\$("$real_cat" "\$count_file")"
calls=\$((calls + 1))
printf '%s\n' "\$calls" > "\$count_file"
if [[ "\$calls" -eq "\${GHACC_TEST_CAT_FAIL_CALL:-0}" ]]; then
  exit 42
fi
exec "$real_cat" "\$@"
EOF
chmod +x "$TMP/bin/cat"
printf '0\n' >"$TMP/cat.calls"
export GHACC_SYSTEMD_UNIT_DIR="$TMP/systemd"
export GHACC_SYSTEMCTL="$TMP/bin/systemctl"
export GHACC_UPDATE_SCHEDULE='*-*-* 04:30:00'
export GHACC_TEST_SYSTEMCTL_LOG_FILE="$TMP/systemctl.log"
export GHACC_TEST_CAT_FAIL_CALL=2
: >"$GHACC_TEST_SYSTEMCTL_LOG_FILE"
expect_failure_without "Installed update timer" "failed timer unit write removes both units" \
  env PATH="$TMP/bin:$PATH" "$SCRIPT" install-timer
[[ ! -e "$TMP/systemd/ghacc-update.service" ]] || fail "service unit remains after failed write"
[[ ! -e "$TMP/systemd/ghacc-update.timer" ]] || fail "timer unit remains after failed write"
[[ ! -s "$GHACC_TEST_SYSTEMCTL_LOG_FILE" ]] || fail "systemctl was invoked after failed unit write"

new_case
mkdir -p "$TMP/systemd" "$TMP/bin" "$TMP/cache"
cat >"$TMP/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GHACC_TEST_SYSTEMCTL_LOG_FILE"
if [[ "$1" == "daemon-reload" ]]; then
  exit 42
fi
exit 0
EOF
chmod +x "$TMP/bin/systemctl"
export GHACC_SYSTEMD_UNIT_DIR="$TMP/systemd"
export GHACC_SYSTEMCTL="$TMP/bin/systemctl"
export GHACC_UPDATE_SCHEDULE='*-*-* 04:30:00'
export GHACC_TEST_SYSTEMCTL_LOG_FILE="$TMP/systemctl.log"
: >"$GHACC_TEST_SYSTEMCTL_LOG_FILE"
expect_failure_without "Installed update timer" "failed daemon-reload does not enable timer" \
  env PATH="$TMP/bin:$PATH" "$SCRIPT" install-timer
[[ -f "$TMP/systemd/ghacc-update.service" ]] || fail "service unit missing after daemon-reload failure"
[[ -f "$TMP/systemd/ghacc-update.timer" ]] || fail "timer unit missing after daemon-reload failure"
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "daemon-reload"
if grep -Fq "enable --now ghacc-update.timer" "$GHACC_TEST_SYSTEMCTL_LOG_FILE"; then
  fail "enable was invoked after daemon-reload failure"
fi

new_case
mkdir -p "$TMP/systemd" "$TMP/bin" "$TMP/cache"
cat >"$TMP/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GHACC_TEST_SYSTEMCTL_LOG_FILE"
if [[ "$1" == "enable" ]]; then
  exit 42
fi
exit 0
EOF
chmod +x "$TMP/bin/systemctl"
export GHACC_SYSTEMD_UNIT_DIR="$TMP/systemd"
export GHACC_SYSTEMCTL="$TMP/bin/systemctl"
export GHACC_UPDATE_SCHEDULE='*-*-* 04:30:00'
export GHACC_TEST_SYSTEMCTL_LOG_FILE="$TMP/systemctl.log"
: >"$GHACC_TEST_SYSTEMCTL_LOG_FILE"
expect_failure_without "Installed update timer" "failed enable returns nonzero" \
  env PATH="$TMP/bin:$PATH" "$SCRIPT" install-timer
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "daemon-reload"
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "enable --now ghacc-update.timer"

new_case
mkdir -p "$TMP/systemd" "$TMP/bin" "$TMP/cache"
cat >"$TMP/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$GHACC_TEST_SYSTEMCTL_LOG_FILE"
if [[ "$1" == "disable" ]]; then
  exit 42
fi
exit 0
EOF
chmod +x "$TMP/bin/systemctl"
printf 'old-service\n' >"$TMP/systemd/ghacc-update.service"
printf 'old-timer\n' >"$TMP/systemd/ghacc-update.timer"
export GHACC_SYSTEMD_UNIT_DIR="$TMP/systemd"
export GHACC_SYSTEMCTL="$TMP/bin/systemctl"
export GHACC_TEST_SYSTEMCTL_LOG_FILE="$TMP/systemctl.log"
: >"$GHACC_TEST_SYSTEMCTL_LOG_FILE"
expect_failure_without "Removed update timer" "failed disable preserves units" \
  env PATH="$TMP/bin:$PATH" "$SCRIPT" uninstall-timer
assert_contains "$TMP/systemd/ghacc-update.service" "old-service"
assert_contains "$TMP/systemd/ghacc-update.timer" "old-timer"
assert_contains "$GHACC_TEST_SYSTEMCTL_LOG_FILE" "disable --now ghacc-update.timer"
if grep -Fq "daemon-reload" "$GHACC_TEST_SYSTEMCTL_LOG_FILE"; then
  fail "daemon-reload was invoked after disable failure"
fi
echo "$PASS assertions passed"
