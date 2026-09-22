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
  mkdir -p "$TMP/bin" "$TMP/work" "$TMP/download"
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
  if [[ "${GHACC_TEST_GIT_WORKTREE:-1}" == 1 ]]; then
    printf 'true\n'
  else
    printf 'false\n'
  fi
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
EOF
mkdir -p "$TMP/config-download"
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
assert_git_invocation_count 3

new_case
expect_failure_with "direct fallback is disabled" "git honors disabled direct fallback" env PATH="$TMP/bin:$PATH" GHACC_TEST_GIT_FAIL_ACCELERATED=1 GHACC_FALLBACK_DIRECT=0 "$SCRIPT" pull
assert_git_invocation_count 2

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
expect_success "download falls back to direct after transfer failure" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_TEST_CURL_FAIL_MIRRORS="https://mirror.test/,https://fallback.test/" "$SCRIPT" download https://raw.githubusercontent.com/owner/repo/main/README.md
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
expect_success "pull works from a repository subdirectory" env PATH="$TMP/bin:$PATH" "$SCRIPT" pull
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
expect_failure_with "direct fallback is disabled" "environment overrides sourced direct fallback" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_FALLBACK_DIRECT=0 GHACC_TEST_CURL_FAIL_MIRRORS="https://mirror.test/,https://fallback.test/" "$SCRIPT" download https://raw.githubusercontent.com/owner/repo/main/README.md
assert_line_absent "$GHACC_TEST_CURL_LOG_FILE" "https://raw.githubusercontent.com/owner/repo/main/README.md"

new_case
expect_success "empty environment download dir overrides config" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="" bash -c 'cd "$1" && exec "$2" archive owner/repo v1.2.3' bash "$TMP/work" "$SCRIPT"
[[ -f "$TMP/work/repo-v1.2.3.tar.gz" ]] || fail "empty GHACC_DOWNLOAD_DIR did not select current directory"
[[ -z "$(find "$TMP/config-download" -type f -print -quit)" ]] || fail "config download dir was used despite empty environment override"

new_case
expect_success "download falls back to direct GitHub" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_TEST_CURL_FAIL_MIRRORS="https://mirror.test/,https://fallback.test/" "$SCRIPT" download https://raw.githubusercontent.com/owner/repo/main/README.md
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
expect_success "release metadata falls back to direct GitHub" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_TEST_CURL_FAIL_MIRRORS="https://mirror.test/,https://fallback.test/" "$SCRIPT" release owner/repo v1.2.3 app-linux.tar.gz
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://api.github.com/repos/owner/repo/releases/tags/v1.2.3"
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://github.com/owner/repo/releases/download/v1.2.3/app-linux.tar.gz"

echo "$PASS assertions passed"
