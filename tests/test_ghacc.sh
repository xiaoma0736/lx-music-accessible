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
if [[ "${GHACC_TEST_CURL_FAIL:-0}" == 1 && "$url" == https://mirror.test/https://* ]]; then exit 22; fi
if [[ "$3" == *GHACC_TEST_HEADER ]]; then exit 22; fi
for ((i=1; i<=$#; i++)); do
  if [[ "${!i}" == "-o" ]]; then
    j=$((i + 1))
    if [[ "$url" == *"https://api.github.com/"* ]]; then
      printf '%s\n' '{"assets":[{"browser_download_url":"https://github.com/owner/repo/releases/download/v1.2.3/app-linux.tar.gz"}]}' > "${!j}"
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
    fail "assertion failed: $text"
  fi
}

fail() {
  echo "not ok - $*" >&2
  exit 1
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
expect_success "git falls back to direct after accelerated failure" env PATH="$TMP/bin:$PATH" GHACC_TEST_GIT_FAIL_ACCELERATED=1 "$SCRIPT" pull
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "https://github.com/"
assert_contains "$GHACC_TEST_GIT_LOG_FILE" "insteadOf"

new_case
expect_success "archive downloads codeload tarball" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" "$SCRIPT" archive owner/repo v1.2.3
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://codeload.github.com/owner/repo/tar.gz/v1.2.3"

new_case
expect_success "release downloads an exact asset" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" "$SCRIPT" release owner/repo v1.2.3 app-linux.tar.gz
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://mirror.test/https://github.com/owner/repo/releases/download/v1.2.3/app-linux.tar.gz"

new_case
GHACC_TEST_CURL_FAIL=1
expect_success "download falls back to direct GitHub" env PATH="$TMP/bin:$PATH" GHACC_DOWNLOAD_DIR="$TMP/download" GHACC_TEST_CURL_FAIL=1 "$SCRIPT" download https://raw.githubusercontent.com/owner/repo/main/README.md
assert_contains "$GHACC_TEST_CURL_LOG_FILE" "https://raw.githubusercontent.com/owner/repo/main/README.md"

echo "$PASS assertions passed"
