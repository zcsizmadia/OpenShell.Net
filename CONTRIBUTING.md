<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Contributing to OpenShell.Net

Work is tracked in [GitHub issues](https://github.com/zcsizmadia/OpenShell.Net/issues). Each change goes on its own branch, named `<issue number>-<short-name>`, and lands through a pull request whose checks (CI on Linux and Windows, integration tests against a real gateway, docs, CodeQL) must pass. Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/).

## Build from source

Protobuf bindings are generated at build time from the repository `proto/` directory and are never committed. You need the .NET 11 SDK and the .NET 8 and .NET 10 runtimes. From the repository root, in Bash (Git Bash on Windows):

```shell
scripts/ci.sh            # format check, build, unit tests on every framework, coverage summary, pack, smoke
scripts/ci.sh test       # or one step: format, build, test, coverage, integration, pack, smoke, aot
```

`smoke` restores the freshly packed packages into a small consumer app under `tests/package-smoke/` and runs it on every framework. `aot` publishes another consumer with Native AOT, failing on any trim or AOT warning, and runs it; it needs the platform's native toolchain (clang on Linux, the Visual Studio C++ tools on Windows). Packing runs NuGet package validation.

The integration tests run against a real gateway and skip unless `OPENSHELL_GATEWAY_ADDRESS` is set (see `tests/OpenShell.Sdk.IntegrationTests/GatewayFixture.cs` for the token, CA, and mTLS variables). On Linux with Docker, `scripts/integration-gateway.sh` starts the upstream gateway release from `UPSTREAM_VERSION` with the Docker compute driver and mTLS, runs a command against it, and stops it; the Integration workflow runs it on every pull request and nightly:

```shell
scripts/ci.sh build
scripts/integration-gateway.sh scripts/ci.sh integration   # or no command: print the variables and keep the gateway running
```

## Coding guidelines

The build treats warnings as errors and enforces the analyzers in `.editorconfig`; CodeQL (`security-and-quality`) runs on every pull request, tests included. Beyond what the tools enforce:

- **Dispose everything you create, on every path.** Prefer `using` / `await using` declarations to a manual `Dispose()` in `finally`. Dispose in `catch` blocks and before early returns too, for example a `TcpListener` whose `Start()` threw. Call `Dispose()` rather than only `Stop()` or `Close()` when the type is disposable. When ownership moves elsewhere (a response handed to `HttpClient`, a timer kept in a collection), say so in a comment and make sure the new owner disposes it.
- **In tests, too.** A test that calls `Dispose()` on purpose to assert disposal behaviour still declares the object with `using`, so a failed assertion before the call doesn't leak it.
- **No fixed sleeps in tests.** Wait on a signal, or use the bounded helpers in `tests/OpenShell.Sdk.Tests/Support/Waits.cs` with a `FakeTimeProvider`.

## Staying in sync with NVIDIA OpenShell

The gRPC protos (`proto/`) and the shared SDK conformance fixtures (`conformance/`) are vendored unmodified from an NVIDIA/OpenShell release, recorded in `UPSTREAM_VERSION`:

```shell
scripts/sync-upstream.sh --check   # is UPSTREAM_VERSION the latest release?
scripts/sync-upstream.sh           # sync to the latest release
scripts/sync-upstream.sh v0.1.7    # or to a specific tag or commit
```

A weekly workflow does this automatically. When upstream publishes a release, it opens a pull request with the synced files and the result of the full check against them.

## Releasing

Pushing a `v*` tag runs the Release workflow (`.github/workflows/release.yml`). An unprivileged job checks, packs (packages and `.snupkg` symbols), smoke-tests and AOT-publishes with the tag's version. A second job in the `nuget` environment, which only `v*` tags can deploy to, then pushes those packages to nuget.org through Trusted Publishing and creates the GitHub release. The version must start with `UPSTREAM_VERSION`: `v0.1.7`, `v0.1.7.1` for an SDK-only release, or `v0.1.7-preview.1`.

To dry-run a release without publishing, run the workflow manually (Actions → Release → Run workflow), optionally with the version to check.

```shell
git tag v0.1.7 && git push origin v0.1.7
```

After a release, set `PackageValidationBaselineVersion` in `src/Directory.Build.props` to it, so a breaking public API change fails the build.

## Mutation testing

[Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/) (a local tool in `dotnet-tools.json`, configured in `tests/OpenShell.Sdk.Tests/stryker-config.json`) mutates `OpenShell.Sdk` and reports the changes no test catches. It runs weekly in the "Mutation testing" workflow, which uploads the HTML report; the score is a report, not a gate. To run it locally (about 7 minutes for the whole SDK):

```shell
dotnet tool restore
cd tests/OpenShell.Sdk.Tests
dotnet stryker                  # everything
dotnet stryker --since main     # only code changed since main
```

## Documentation

The documentation site is built with DocFX from `docs/`. The C# snippets live in `docs/snippets/` and are compiled, and mostly run, before the site is built:

```shell
dotnet tool restore
scripts/docs.sh          # check, build, and run the snippets, then build the site into artifacts/docs/
scripts/docs.sh serve    # build and serve the site locally
```
