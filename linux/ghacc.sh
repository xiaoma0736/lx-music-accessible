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
  status)
    shift
    if (($#)); then
      log "ERROR: status accepts no arguments"
      printf 'Usage: ghacc status\n' >&2
      exit 2
    fi
    status_command "$@"
    ;;
  help|--help|-h) usage ;;
  clone|pull|fetch|git|download|archive|release)
    log "ERROR: $1 is not implemented yet"
    exit 127
    ;;
  *)
    usage
    exit 2
    ;;
esac
