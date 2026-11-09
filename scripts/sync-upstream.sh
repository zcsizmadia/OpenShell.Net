#!/usr/bin/env bash
# SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
# SPDX-License-Identifier: Apache-2.0
#
# Vendors the files this SDK shares with NVIDIA/OpenShell (gRPC protos and SDK conformance
# fixtures) from an upstream release, and records the release tag in UPSTREAM_VERSION.
#
#   scripts/sync-upstream.sh            sync to the latest NVIDIA/OpenShell release
#   scripts/sync-upstream.sh v0.1.7     sync to a specific release tag (or any ref)
#   scripts/sync-upstream.sh --check    exit 1 if UPSTREAM_VERSION is behind the latest release
#   scripts/sync-upstream.sh --latest   print the latest release tag
#
# Set GITHUB_TOKEN to avoid the anonymous GitHub API rate limit.
set -euo pipefail

repo="NVIDIA/OpenShell"
# upstream path -> local path, relative to each repository root. A leading "?" marks a file that may not exist
# at the requested tag yet (it was vendored from a later upstream commit); the local copy is then kept.
files=(
  "proto/openshell.proto:proto/openshell.proto"
  "proto/sandbox.proto:proto/sandbox.proto"
  "proto/datamodel.proto:proto/datamodel.proto"
  "proto/options.proto:proto/options.proto"
  "sdk/conformance/oauth-client-credentials.json:conformance/oauth-client-credentials.json"
  "sdk/conformance/oauth-client-credentials.json.license:conformance/oauth-client-credentials.json.license"
  "?sdk/conformance/oidc-token-bundle.json:conformance/oidc-token-bundle.json"
  "?sdk/conformance/oidc-token-bundle.json.license:conformance/oidc-token-bundle.json.license"
)
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version_file="$root/UPSTREAM_VERSION"

auth=()
if [[ -n "${GITHUB_TOKEN:-}" ]]; then
  auth=(-H "Authorization: Bearer $GITHUB_TOKEN")
fi

latest_release() {
  local tag
  tag="$(curl -fsSL "${auth[@]}" "https://api.github.com/repos/$repo/releases/latest" |
    sed -n 's/^  "tag_name": *"\([^"]*\)".*/\1/p')"
  if [[ -z "$tag" ]]; then
    echo "error: could not determine the latest $repo release" >&2
    exit 2
  fi
  printf '%s\n' "$tag"
}

current="$(tr -d '[:space:]' < "$version_file")"

case "${1:-}" in
  --latest)
    latest_release
    exit 0
    ;;
  --check)
    latest="$(latest_release)"
    if [[ "$current" == "$latest" ]]; then
      echo "Vendored files are at $current, the latest $repo release."
      exit 0
    fi
    echo "Vendored files are at $current; the latest $repo release is $latest. Run scripts/sync-upstream.sh $latest." >&2
    exit 1
    ;;
esac

tag="${1:-$(latest_release)}"

# Download everything first so a failed fetch leaves the working tree untouched.
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
for entry in "${files[@]}"; do
  src="${entry%%:*}"
  dst="${entry#*:}"
  optional=0
  if [[ "$src" == "?"* ]]; then
    optional=1
    src="${src:1}"
  fi
  mkdir -p "$tmp/$(dirname "$dst")"
  status="$(curl -sSL "${auth[@]}" -o "$tmp/$dst" -w '%{http_code}' "https://raw.githubusercontent.com/$repo/$tag/$src")"
  if [[ "$status" == 404 && $optional == 1 ]]; then
    rm -f "$tmp/$dst"
    echo "$src is not in $tag; keeping the local copy." >&2
  elif [[ "$status" != 200 ]]; then
    echo "error: fetching $src at $tag returned HTTP $status" >&2
    exit 2
  fi
done
for entry in "${files[@]}"; do
  dst="${entry#*:}"
  [[ -f "$tmp/$dst" ]] || continue
  mkdir -p "$root/$(dirname "$dst")"
  cp "$tmp/$dst" "$root/$dst"
done
printf '%s\n' "$tag" > "$version_file"
# The README's NVIDIA OpenShell badge and its link name the release; shields.io escapes "-" as "--" in badge text.
badge_tag="${tag//-/--}"
sed -i -E \
  -e "s#(badge/NVIDIA%20OpenShell-)[^?]*(-76B900\?)#\1${badge_tag}\2#" \
  -e "s#(github\.com/NVIDIA/OpenShell/releases/tag/)[^)]*\)#\1${tag})#" \
  "$root/README.md"
echo "Synced vendored files from $repo $tag (was $current)."
