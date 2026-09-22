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

log() { printf '[ghacc] %s\n' "$*" >&2; }
die() { log "ERROR: $*"; exit 2; }
have() { command -v "$1" >/dev/null 2>&1; }

load_config() {
  # Startup initialization preserves empty scalar values as deliberate overrides.
  local timeout="$GHACC_TIMEOUT" download_dir="$GHACC_DOWNLOAD_DIR"
  local auto_select="$GHACC_AUTO_SELECT" fallback_direct="$GHACC_FALLBACK_DIRECT"

  if [[ -n "${GHACC_CONFIG:-}" && -f "$GHACC_CONFIG" ]]; then
    # shellcheck disable=SC1090
    source "$GHACC_CONFIG"
  fi

  GHACC_TIMEOUT="$timeout"
  GHACC_DOWNLOAD_DIR="$download_dir"
  GHACC_AUTO_SELECT="$auto_select"
  GHACC_FALLBACK_DIRECT="$fallback_direct"
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
  *)
    usage
    exit 2
    ;;
esac
