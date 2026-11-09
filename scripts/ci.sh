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
#   scripts/ci.sh pack          pack the NuGet packages into artifacts/
#   scripts/ci.sh smoke         restore the packed packages into a consumer app and run it on every test framework
#   scripts/ci.sh aot           publish a consumer app with Native AOT (trim and AOT warnings fail) and run it
#   scripts/ci.sh all           format, build, test, coverage, pack, smoke
#
# Versions (see src/Directory.Build.props): builds default to <UPSTREAM_VERSION>-dev. Set VERSION to the full
# release version (0.1.7, 0.1.7.1, 0.1.7-preview.1), or VERSION_SUFFIX to replace "dev" (ci.42).
#
# Tests run with `dotnet run` per framework rather than `dotnet test`: the TUnit projects are executables,
# `dotnet test` reports zero tests on some hosts, and a framework that discovers no tests still fails here.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

solution="OpenShell.Sdk.slnx"
unit_tests=(OpenShell.Sdk.Tests OpenShell.Sdk.DependencyInjection.Tests)
packages=(OpenShell.Sdk OpenShell.Sdk.DependencyInjection OpenShell.Sdk.Testing)

# Passed to build and pack, so the tested binaries carry the version that ships.
version_args=()
if [[ -n "${VERSION:-}" ]]; then
  version_args=("-p:Version=$VERSION")
elif [[ -n "${VERSION_SUFFIX:-}" ]]; then
  version_args=("-p:VersionSuffix=$VERSION_SUFFIX")
fi

# The test framework list lives in Directory.Build.props (OpenShellTestTargetFrameworks); it covers every
# shipped framework plus newer runtimes. Coverage runs on the newest one.
target_frameworks() {
  local tfms
  tfms="$(dotnet msbuild tests/OpenShell.Sdk.Tests -getProperty:OpenShellTestTargetFrameworks -nologo | tr -d '\r')"
  IFS=';' read -r -a TFMS <<< "$tfms"
  LATEST="${TFMS[${#TFMS[@]}-1]}"
}

step_format() {
  echo "== format"
  dotnet format "$solution" --verify-no-changes
}

step_build() {
  echo "== build"
  dotnet build "$solution" --nologo -c Release ${version_args[@]+"${version_args[@]}"}
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

# Reads the version that pack produced, for example 0.1.7-dev, from the OpenShell.Net package file name.
package_version() {
  local nupkg
  nupkg="$(ls artifacts/OpenShell.Net.[0-9]*.nupkg 2>/dev/null | head -n 1)"
  if [[ -z "$nupkg" ]]; then
    echo "error: no OpenShell.Net package in artifacts/; run scripts/ci.sh pack first" >&2
    exit 2
  fi
  nupkg="${nupkg##*/OpenShell.Net.}"
  PACKAGE_VERSION="${nupkg%.nupkg}"
  # The version is reused between packs, so drop previously restored copies of our packages.
  rm -rf tests/package-smoke/.packages/openshell.net*
}

step_smoke() {
  package_version
  target_frameworks
  rm -rf tests/package-smoke/Consumer/bin tests/package-smoke/Consumer/obj
  for tfm in "${TFMS[@]}"; do
    echo "== smoke OpenShell.Net $PACKAGE_VERSION on $tfm"
    dotnet run --project tests/package-smoke/Consumer -c Release -f "$tfm" -p:OpenShellPackageVersion="$PACKAGE_VERSION"
  done
}

step_aot() {
  package_version
  local out=tests/package-smoke/.aot
  rm -rf "$out" tests/package-smoke/Aot/bin tests/package-smoke/Aot/obj
  echo "== Native AOT publish of OpenShell.Net $PACKAGE_VERSION"
  dotnet publish tests/package-smoke/Aot -c Release -o "$out" -p:OpenShellPackageVersion="$PACKAGE_VERSION"
  echo "== run the Native AOT binary"
  "$out/Aot"
}

step_pack() {
  echo "== pack"
  rm -rf artifacts
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
  smoke) step_smoke ;;
  aot) step_aot ;;
  all)
    step_format
    step_build
    step_test
    step_coverage
    step_pack
    step_smoke
    ;;
  *)
    echo "usage: $0 [format|build|test|coverage|integration|pack|smoke|aot|all]" >&2
    exit 2
    ;;
esac
