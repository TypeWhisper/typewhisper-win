#!/usr/bin/env bash
set -euo pipefail

EVENT=""
REF_NAME=""
SHA=""
INPUT_BASE_VERSION=""

usage() {
  cat >&2 <<'USAGE'
Usage: resolve-release-metadata.sh --event <event> --ref-name <ref> --sha <sha> --base-version <version>
USAGE
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --event)
      EVENT="${2:-}"
      shift 2
      ;;
    --ref-name)
      REF_NAME="${2:-}"
      shift 2
      ;;
    --sha)
      SHA="${2:-}"
      shift 2
      ;;
    --base-version)
      INPUT_BASE_VERSION="${2:-}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      usage
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if [ -z "$EVENT" ]; then
  usage
  echo "--event is required" >&2
  exit 1
fi

if [ -z "$SHA" ]; then
  usage
  echo "--sha is required" >&2
  exit 1
fi

emit() {
  printf '%s=%s\n' "$1" "$2"
}

latest_matching_tag() {
  local pattern="$1"

  git tag --list 'v[0-9]*' --sort=-v:refname |
    grep -E "$pattern" |
    head -1 || true
}

next_patch_version() {
  local version="$1"
  local major minor patch

  IFS='.' read -r major minor patch <<< "$version"
  echo "${major}.${minor}.$((patch + 1))"
}

highest_base_version() {
  local first="$1"
  local second="$2"

  if [ -z "$first" ]; then
    echo "$second"
    return
  fi

  if [ -z "$second" ]; then
    echo "$first"
    return
  fi

  printf '%s\n%s\n' "$first" "$second" | sort -V | tail -1
}

resolve_default_daily_base_version() {
  local latest_stable stable_candidate latest_rc rc_base

  latest_stable="$(latest_matching_tag '^v[0-9]+\.[0-9]+\.[0-9]+$')"
  if [ -n "$latest_stable" ]; then
    stable_candidate="$(next_patch_version "${latest_stable#v}")"
  else
    stable_candidate="0.0.1"
  fi

  latest_rc="$(latest_matching_tag '^v[0-9]+\.[0-9]+\.[0-9]+-rc(\.?[0-9]+)?$')"
  if [ -n "$latest_rc" ]; then
    rc_base="${latest_rc#v}"
    rc_base="${rc_base%%-rc*}"
  else
    rc_base=""
  fi

  highest_base_version "$stable_candidate" "$rc_base"
}

TAG=""
VERSION=""
RELEASE_TARGET=""

if [ "$EVENT" = "push" ]; then
  TAG="$REF_NAME"
  VERSION="${TAG#v}"
else
  BASE_VERSION="${INPUT_BASE_VERSION#v}"

  if [ -z "$BASE_VERSION" ]; then
    BASE_VERSION="$(resolve_default_daily_base_version)"
  fi

  if ! [[ "$BASE_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "base_version must look like 0.7.1, got: $BASE_VERSION" >&2
    exit 1
  fi

  DATE="${TYPEWHISPER_RELEASE_DATE:-$(date -u +%Y%m%d)}"
  if ! [[ "$DATE" =~ ^[0-9]{8}$ ]]; then
    echo "TYPEWHISPER_RELEASE_DATE must look like 20260701, got: $DATE" >&2
    exit 1
  fi

  TAG="v${BASE_VERSION}-daily.${DATE}"
  VERSION="${BASE_VERSION}-daily.${DATE}"
  RELEASE_TARGET="$SHA"

  if git rev-parse "$TAG" >/dev/null 2>&1; then
    echo "Daily tag $TAG already exists; skipping." >&2
    emit "tag" "$TAG"
    emit "version" "$VERSION"
    emit "channel_suffix" "-daily"
    emit "is_prerelease" "true"
    emit "is_stable" "false"
    emit "should_run" "false"
    emit "release_target" "$RELEASE_TARGET"
    exit 0
  fi
fi

CHANNEL_SUFFIX=""
IS_PRERELEASE="false"
IS_STABLE="true"

if [[ "$VERSION" == *"-daily."* ]]; then
  CHANNEL_SUFFIX="-daily"
  IS_PRERELEASE="true"
  IS_STABLE="false"
elif [[ "$VERSION" == *"-rc"* ]]; then
  CHANNEL_SUFFIX="-rc"
  IS_PRERELEASE="true"
  IS_STABLE="false"
elif [[ "$VERSION" == *"-"* ]]; then
  echo "Unsupported prerelease version for Windows app release channels: $VERSION" >&2
  exit 1
fi

emit "tag" "$TAG"
emit "version" "$VERSION"
emit "channel_suffix" "$CHANNEL_SUFFIX"
emit "is_prerelease" "$IS_PRERELEASE"
emit "is_stable" "$IS_STABLE"
emit "should_run" "true"
emit "release_target" "$RELEASE_TARGET"

echo "Release tag: $TAG" >&2
echo "Package version: $VERSION" >&2
echo "Velopack channel suffix: ${CHANNEL_SUFFIX:-<stable>}" >&2
