#!/usr/bin/env bash
# SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
# SPDX-License-Identifier: Apache-2.0
#
# Build, test, and pack the SDK. CI runs `scripts/ci.sh all`; each step also runs on its own.
#
#   scripts/ci.sh format        verify formatting and analyzer rules (read-only)
#   scripts/ci.sh build         build every project for every target framework (Release)
#   scripts/ci.sh test          run the unit tests on every target framework (after build)
#   scripts/ci.sh coverage      run the unit tests with coverage on the newest framework (after build)
#   scripts/ci.sh integration   run the integration tests against OPENSHELL_GATEWAY_ADDRESS (after build)
#   scripts/ci.sh pack          pack the NuGet packages into artifacts/ (VERSION=x.y.z stamps the version)
#   scripts/ci.sh all           format, build, test, coverage, pack
#
# Tests run with `dotnet run` per framework rather than `dotnet test`: the TUnit projects are executables,
# `dotnet test` reports zero tests on some hosts, and a framework that discovers no tests still fails here.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

solution="OpenShell.Sdk.slnx"
unit_tests=(OpenShell.Sdk.Tests OpenShell.Sdk.DependencyInjection.Tests)
packages=(OpenShell.Sdk OpenShell.Sdk.DependencyInjection OpenShell.Sdk.Testing)

# The framework list lives in Directory.Build.props (OpenShellSdkTargetFrameworks).
target_frameworks() {
  local tfms
  tfms="$(dotnet msbuild src/OpenShell.Sdk -getProperty:OpenShellSdkTargetFrameworks -nologo | tr -d '\r')"
  IFS=';' read -r -a TFMS <<< "$tfms"
  LATEST="${TFMS[${#TFMS[@]}-1]}"
}

step_format() {
  echo "== format"
  dotnet format "$solution" --verify-no-changes
}

step_build() {
  echo "== build"
  dotnet build "$solution" --nologo -c Release
}

step_test() {
  target_frameworks
  for project in "${unit_tests[@]}"; do
    for tfm in "${TFMS[@]}"; do
      echo "== test $project on $tfm"
      dotnet run --project "tests/$project" --no-build -c Release -f "$tfm"
    done
  done
}

step_coverage() {
  target_frameworks
  local root out file
  root="$(pwd -P)/TestResults/coverage"
  rm -rf "$root"
  for project in "${unit_tests[@]}"; do
    echo "== coverage $project on $LATEST"
    out="$root/$project"
    dotnet run --project "tests/$project" --no-build -c Release -f "$LATEST" -- \
      --coverage --coverage-output-format cobertura \
      --coverage-settings "$(pwd -P)/coverage.settings.xml" \
      --coverage-output "$out/coverage.cobertura.xml"
    file="$(find "$out" -name '*.xml' | head -1)"
    # One line per measured assembly: a test project also loads assemblies it barely exercises
    # (the DI tests load OpenShell.Sdk), so the file-wide rate would understate its own target.
    grep -oE '<package [^>]*' "$file" | while read -r package; do
      name="$(grep -oE 'name="[^"]*"' <<< "$package" | cut -d'"' -f2)"
      line="$(grep -oE 'line-rate="[0-9.]*"' <<< "$package" | cut -d'"' -f2)"
      branch="$(grep -oE 'branch-rate="[0-9.]*"' <<< "$package" | cut -d'"' -f2)"
      printf '%s: %s line %.1f%%, branch %.1f%%\n' "$project" "$name" \
        "$(awk "BEGIN { print $line * 100 }")" "$(awk "BEGIN { print $branch * 100 }")"
    done
  done
}

step_integration() {
  : "${OPENSHELL_GATEWAY_ADDRESS:?OPENSHELL_GATEWAY_ADDRESS must be set}"
  target_frameworks
  echo "== integration tests on $LATEST"
  dotnet run --project tests/OpenShell.Sdk.IntegrationTests --no-build -c Release -f "$LATEST"
}

step_pack() {
  echo "== pack ${VERSION:-(project default version)}"
  rm -rf artifacts
  local version_args=()
  if [[ -n "${VERSION:-}" ]]; then
    version_args=("-p:Version=$VERSION")
  fi
  for project in "${packages[@]}"; do
    dotnet pack "src/$project" --nologo -c Release -o artifacts ${version_args[@]+"${version_args[@]}"}
  done
  ls -1 artifacts
}

case "${1:-all}" in
  format) step_format ;;
  build) step_build ;;
  test) step_test ;;
  coverage) step_coverage ;;
  integration) step_integration ;;
  pack) step_pack ;;
  all)
    step_format
    step_build
    step_test
    step_coverage
    step_pack
    ;;
  *)
    echo "usage: $0 [format|build|test|coverage|integration|pack|all]" >&2
    exit 2
    ;;
esac
