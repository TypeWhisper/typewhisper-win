#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESOLVER="$SCRIPT_DIR/resolve-release-metadata.sh"

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

assert_eq() {
  local name="$1"
  local expected="$2"
  local actual="$3"

  if [ "$actual" != "$expected" ]; then
    fail "$name: expected '$expected', got '$actual'"
  fi
}

metadata_value() {
  local output="$1"
  local key="$2"

  echo "$output" | awk -F= -v wanted="$key" '$1 == wanted { print substr($0, length($1) + 2) }'
}

with_repo() {
  local tmp
  tmp="$(mktemp -d)"

  (
    cd "$tmp"
    git init -q
    git config user.email "release-test@example.invalid"
    git config user.name "Release Metadata Test"
    git commit --allow-empty -m initial >/dev/null
    "$@"
  )

  rm -rf "$tmp"
}

resolve_daily_with_tags() {
  local sha
  for tag in "$@"; do
    git tag "$tag"
  done

  sha="$(git rev-parse HEAD)"
  TYPEWHISPER_RELEASE_DATE=20260701 bash "$RESOLVER" \
    --event schedule \
    --ref-name main \
    --sha "$sha" \
    --base-version ""
}

test_daily_uses_release_candidate_base_when_it_is_newer_than_stable() {
  local output
  output="$(resolve_daily_with_tags v0.8.4 v1.0.0-rc1)"

  assert_eq "tag" "v1.0.0-daily.20260701" "$(metadata_value "$output" tag)"
  assert_eq "version" "1.0.0-daily.20260701" "$(metadata_value "$output" version)"
  assert_eq "channel_suffix" "-daily" "$(metadata_value "$output" channel_suffix)"
  assert_eq "is_prerelease" "true" "$(metadata_value "$output" is_prerelease)"
  assert_eq "is_stable" "false" "$(metadata_value "$output" is_stable)"
  assert_eq "should_run" "true" "$(metadata_value "$output" should_run)"
}

test_daily_uses_next_stable_patch_without_release_candidate() {
  local output
  output="$(resolve_daily_with_tags v0.8.4)"

  assert_eq "tag" "v0.8.5-daily.20260701" "$(metadata_value "$output" tag)"
  assert_eq "version" "0.8.5-daily.20260701" "$(metadata_value "$output" version)"
}

test_daily_uses_next_patch_after_stable_catches_up_to_release_candidate() {
  local output
  output="$(resolve_daily_with_tags v0.8.4 v1.0.0-rc1 v1.0.0)"

  assert_eq "tag" "v1.0.1-daily.20260701" "$(metadata_value "$output" tag)"
  assert_eq "version" "1.0.1-daily.20260701" "$(metadata_value "$output" version)"
}

test_explicit_base_version_wins() {
  local output

  git tag v0.8.4
  git tag v1.0.0-rc1
  git tag v1.0.0-daily.20260701

  output="$(TYPEWHISPER_RELEASE_DATE=20260701 bash "$RESOLVER" \
    --event workflow_dispatch \
    --ref-name main \
    --sha "$(git rev-parse HEAD)" \
    --base-version v2.0.0)"

  assert_eq "tag" "v2.0.0-daily.20260701" "$(metadata_value "$output" tag)"
  assert_eq "version" "2.0.0-daily.20260701" "$(metadata_value "$output" version)"
}

test_existing_daily_tag_skips_build() {
  local output
  output="$(resolve_daily_with_tags v0.8.4 v1.0.0-rc1 v1.0.0-daily.20260701)"

  assert_eq "tag" "v1.0.0-daily.20260701" "$(metadata_value "$output" tag)"
  assert_eq "should_run" "false" "$(metadata_value "$output" should_run)"
}

test_push_release_candidate_uses_rc_channel() {
  local output
  output="$(TYPEWHISPER_RELEASE_DATE=20260701 bash "$RESOLVER" \
    --event push \
    --ref-name v1.0.0-rc1 \
    --sha "$(git rev-parse HEAD)" \
    --base-version "")"

  assert_eq "tag" "v1.0.0-rc1" "$(metadata_value "$output" tag)"
  assert_eq "version" "1.0.0-rc1" "$(metadata_value "$output" version)"
  assert_eq "channel_suffix" "-rc" "$(metadata_value "$output" channel_suffix)"
  assert_eq "is_prerelease" "true" "$(metadata_value "$output" is_prerelease)"
  assert_eq "is_stable" "false" "$(metadata_value "$output" is_stable)"
  assert_eq "should_run" "true" "$(metadata_value "$output" should_run)"
}

with_repo test_daily_uses_release_candidate_base_when_it_is_newer_than_stable
with_repo test_daily_uses_next_stable_patch_without_release_candidate
with_repo test_daily_uses_next_patch_after_stable_catches_up_to_release_candidate
with_repo test_explicit_base_version_wins
with_repo test_existing_daily_tag_skips_build
with_repo test_push_release_candidate_uses_rc_channel

echo "release metadata resolver tests passed"
