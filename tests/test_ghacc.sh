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
expect_failure "listed unimplemented transfer command fails" env PATH="$TMP/bin:$PATH" "$SCRIPT" clone https://github.com/owner/repo.git

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
