#!/usr/bin/env bash
# SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
# SPDX-License-Identifier: Apache-2.0
#
# Build the documentation site (docs/) into artifacts/docs/. The docs workflow runs `scripts/docs.sh`.
#
#   scripts/docs.sh           check, build, and run the snippets, then build the site
#   scripts/docs.sh snippets  only the snippets: format check, build, and run (no gateway needed)
#   scripts/docs.sh site      only the site: API metadata from src/ (net10.0) plus the pages; warnings fail
#   scripts/docs.sh serve     build everything, then serve the site at http://localhost:8080
#
# The guides include #region blocks from docs/snippets/, so the snippets build first: a snippet that no longer
# compiles fails here instead of shipping in the site. DocFX is a local tool pinned in dotnet-tools.json.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

snippets="docs/snippets/Snippets.csproj"

step_snippets() {
  echo "== snippets: format"
  dotnet format "$snippets" --verify-no-changes
  echo "== snippets: build"
  dotnet build "$snippets" --nologo -c Release
  echo "== snippets: run"
  dotnet run --project "$snippets" --no-build -c Release
}

# DocFX renders an empty block, without a warning, for a #region that does not exist, so check every
# [!code-...[](file#Region)] reference in the pages first.
check_snippet_references() {
  echo "== site: snippet references"
  local failed=0 page ref target file region
  while IFS= read -r page; do
    while IFS= read -r ref; do
      target="${ref#*](}"
      target="${target%)]}"
      file="$(dirname "$page")/${target%%#*}"
      region=""
      [[ "$target" == *"#"* ]] && region="${target#*#}"
      if [[ ! -f "$file" ]]; then
        echo "$page: $target: file not found" >&2
        failed=1
      elif [[ -n "$region" ]] && ! grep -Eq "^[[:space:]]*#region[[:space:]]+${region}[[:space:]]*$" "$file"; then
        echo "$page: $target: no '#region $region'" >&2
        failed=1
      fi
    done < <(grep -o '\[!code-[a-z]*\[[^]]*\]([^)]*)\]' "$page" || true)
  done < <(find docs -name '*.md' -not -path 'docs/api/*' | sort)
  return "$failed"
}

step_site() {
  check_snippet_references
  echo "== site"
  dotnet tool restore
  rm -rf artifacts/docs
  # Remove stale API metadata (everything in docs/api/ except the hand-written index.md).
  find docs/api -mindepth 1 ! -name index.md -exec rm -rf {} +
  dotnet docfx docs/docfx.json --warningsAsErrors
  echo "Site written to artifacts/docs/"
}

case "${1:-all}" in
  all) step_snippets; step_site ;;
  snippets) step_snippets ;;
  site) step_site ;;
  serve) step_snippets; step_site; dotnet docfx serve artifacts/docs --port 8080 ;;
  *) echo "usage: $0 [all|snippets|site|serve]" >&2; exit 2 ;;
esac
