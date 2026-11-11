<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# .NET SDK feature parity

Target: the union of the Go, Rust, TypeScript, and Python SDK surfaces. Go is the broadest, so the .NET
shape follows it, with additions from the other three. Statuses were checked against the code, not carried
over from the plan.

Legend: ✅ done, 🚧 in progress, ⚠️ implemented with a limitation or not yet verified end to end, ⬜ todo.
Sources: G Go, R Rust, T TypeScript, P Python.

## Gateway API coverage

| Measure | Result |
|---|---|
| User-facing RPCs in the `OpenShell` service | ✅ 67 of 67 called by the curated client |
| `SandboxUpdates` service (`UpdateSandbox`, new in v0.1.7; needs compute-driver support and explicit gateway activation) | ✅ curated as `ISandboxClient.UpdateAsync`; also `OpenShellClient.RawSandboxUpdates` |
| Supervisor and sandbox-internal RPCs (`ConnectSupervisor`, `RelayStream`, `PeerRelay`, `Peer*`, `PushSandboxLogs`, `Report*`, `FinalizeMainProcessExit`, `GetSandboxProviderEnvironment`, `ExchangeProviderSubjectToken`, `IssueSandboxToken`, `RefreshSandboxToken`, `SubmitPolicyAnalysis`) | Excluded by design; no SDK implements them. Reachable through `OpenShellClient.Raw`. |
| Extension services (`ComputeDriver`, `CredentialDriver`, `GatewayInterceptor`, `SupervisorMiddleware`) | Excluded: they are contracts to implement, not call |

"Called" means the RPC is wired and unit tested against an in-memory gRPC invoker. See Testing for what has
and has not run against a real gateway. Counts are against the protos of the NVIDIA/OpenShell release in
`UPSTREAM_VERSION` (v0.1.8).

## Client surface (`IOpenShellClient`)

| Area | Operations | Seen in | Status |
|---|---|---|---|
| Health | `CheckAsync`, `GetGatewayInfoAsync`, `GetCurrentUserAsync` | G R T P | ✅ |
| Workspaces | create, get, list, delete, add/remove/list members, `InWorkspace(name)` scoped view | G R P | ✅ |
| Sandboxes | create, create from template, get, list, list IDs, stop, start, delete (typed `DeletionResult`) | G R T P | ✅ |
| Sandbox suspend and resume | `SuspendAsync`, `ResumeAsync` (`RestartIfRestoreFails`), `WaitSuspendedAsync`; Ready and Stopped waits fail fast on a suspended sandbox (v0.1.7) | G P | ✅ |
| Sandbox configuration update | `UpdateAsync` (`SandboxUpdate` with null-versus-empty fields, compare-and-swap, request id), `ResumePendingUpdateAsync`; fake with pending updates (v0.1.7) | none: upstream SDKs have no curated method | ✅ label updates and compare-and-swap run against a real gateway with updates enabled (Docker driver, Integration workflow); image, command, and environment changes need a driver that supports them (Kubernetes) and are not run |
| Sandbox waits | `WaitReadyAsync`, `WaitStoppedAsync`, `WaitDeletedAsync` with `ExpectedSandboxId` | G R T P | ✅ |
| Endpoint statuses and token-grant failure diagnostics (`Sandbox.EndpointStatuses`, `LastTokenGrantFailure`, `EndpointFailure`) | G | ✅ |
| Sandbox watch and logs | `WatchAsync`, `WatchLogsAsync`, `GetLogsAsync`, cursor resume, reconnect with backoff | G R | ✅ |
| Sandbox providers | attach, detach, list, status, readiness waits (`WaitAsync`, `WaitAllAsync`) | G R T | ✅ attach, update, and detach with readiness waits run against a real sandbox in the Integration workflow |
| Templates | create, get, list, delete | G R T P | ✅ |
| Exec | `RunAsync`, `StreamAsync`, `InteractiveAsync` (stdin, resize, cancel), `ExecPythonAsync` | G R T P | ✅ |
| Files | upload and download for files, streams, and directories (tar over exec) | G | ✅ needs `tar` and `sh` in the sandbox image; files, streams, and directories round-trip against a real sandbox in the Integration workflow |
| Services | expose, get, list, delete, service exposures at create | G R T P | ✅ |
| SSH | create and revoke session, `TunnelAsync` | G T | ✅ |
| TCP | `ForwardAsync` (duplex stream), `ListenAsync` (local listener) | G T | ✅ |
| Config | sandbox and gateway config, policy and setting updates, global scope | G T | ✅ |
| Policy | draft get, approve, reject, approve all, edit, undo, clear, history, status, revisions | G | ✅ the Integration workflow turns denied connections into mechanistic draft chunks and edits, approves (and waits for the revision to load), undoes, rejects, bulk-approves, and clears them |
| Providers | create, get, list (with label selector), update, delete, ensure | G P | ✅ run against a real gateway in the Integration workflow |
| Provider credentials | `GetCredentialsAsync` (operator-only, values redacted in `ToString`) | T P | ✅ the Integration workflow checks only the rejection: its mTLS client certificate is not an operator (`OU=operator`) identity |
| Provider profiles | list, get, import, update, lint, delete, platform view; `StablePlaceholder` with a gateway support check (v0.1.7) | G | ✅ lint, import, update, delete, and the platform view run against a real gateway in the Integration workflow, which ships no built-in profiles |
| Provider refresh | status, configure, rotate, delete | G | ✅ |
| Rootfs staging | `BeginStagingAsync`, `StageAsync`, `RootfsDriverConfig.WithStagingToken` | none | ✅ |
| Raw access | generated gRPC clients on `OpenShellClient.Raw` and `OpenShellClient.RawSandboxUpdates`; `SandboxSpec.RawSpec` hook | R T | ✅ |
| Pagination | `Pager<T>` (items and pages), `ToListAsync` | G R T P | ✅ |
| Errors | `OpenShellException` with kind, gRPC status, and predicates | G R T P | ✅ |
| Error details | `ErrorInfo`, `FieldViolations`, `RetryDelay` decoded from `google.rpc` details, validated against the call status | G R T P | ✅ |

## Transport and auth

| Feature | Seen in | Status |
|---|---|---|
| Plaintext h2c (loopback dev) | G R T P | ✅ |
| TLS with system roots or a pinned CA | G R T P | ✅ |
| mTLS client certificate | G T P | ✅ |
| Insecure skip-verify (development only) | G R T P | ✅ |
| Static bearer token, no auth | G R T P | ✅ |
| Refreshable token (single flight, proactive, and on `Unauthenticated`) | G R P | ✅ |
| Extra headers wrapper | G | ✅ |
| Cloudflare Access header and cookie auth | G R T | ✅ |
| Edge WebSocket tunnel proxy (`EdgeTunnelProxy`) | G R | ✅ |
| Edge tunnel started automatically for `cloudflare_jwt` gateways (`EdgeTunnelMode.Auto`, owned by the client) | R | ⚠️ tested against a fake WebSocket edge, not a real Cloudflare Access edge |
| Refusal to send credentials over plaintext to a non-loopback host | T | ✅ |
| Retry policy (reads only by default, backoff with jitter, honours the gateway retry delay, deadline aware) | G | ✅ |
| Owner-replica routing for exec, interactive exec, port forwarding, and SSH (`x-openshell-want-owner`, `x-openshell-replica`, one unrouted retry, 30 s owner cache) | G T P | ⚠️ unit tested with scripted headers; not run against a multi-replica gateway |
| Logger option (`ILoggerFactory`) | G | ⚠️ the gRPC channel, edge tunnel, retries, and watch reconnects log; waits and token refresh do not |
| Per-call deadline and cancellation | all | ✅ |

## OIDC (`OpenShell.Oidc`)

| Flow | Seen in | Status |
|---|---|---|
| Discovery and issuer validation | G R T P | ✅ |
| Client credentials | G T P | ✅ |
| Refresh-token exchange | G R P | ✅ |
| Authorization code with PKCE, loopback callback, browser launch | G | ⚠️ tested against a stub identity provider |
| Device-code flow | G | ⚠️ tested against a stub identity provider |
| Keyboard (paste) flow | G | ✅ |
| Token cache, in-memory or on disk, in the CLI's `oidc_token.json` format (`expires_at`, `issuer`, `client_id`); a cached token is reused only for the same issuer and client | G R | ✅ checked against the shared `oidc-token-bundle.json` fixture |

## Gateway configuration (`OpenShell.Gateway`)

| Feature | Seen in | Status |
|---|---|---|
| Resolve the on-disk gateway config and active gateway, falling back to the system `active_gateway` when the user one is missing, empty or invalid | G P | ✅ |
| User entries shadow system registrations only when they hold `metadata.json`; credentials (`mtls/`, `oidc_token.json`, `edge_token`) always live in the user gateway directory (`GatewayMetadata.StateDirectory`) | G R | ✅ |
| List gateways and load metadata (sorted by name, unreadable registrations skipped, system default marked active) | G | ✅ |
| CLI discovery rules from v0.1.7: `OPENSHELL_SYSTEM_GATEWAY_DIR` system root, FQDN and IPv4 gateway names, Windows device names rejected on every platform | G | ✅ |
| mTLS bundle and token loading; renewed OIDC tokens are written back so the CLI stays signed in; a token the CLI marked `client_credentials` is renewed by repeating the grant with `$OPENSHELL_OIDC_CLIENT_SECRET` or `OidcClientSecretProvider` (v0.1.8) | G P | ✅ |
| `GatewayClient.Connect` convenience | P | ✅ |

## Testing support

| Feature | Seen in | Status |
|---|---|---|
| Interfaces for every client area, for mocking | G | ✅ |
| In-memory fake client (`OpenShell.Net.Testing`; 16 contract scenarios run against both the fake and the real client) | G | ✅ |
| `AddOpenShellFake()` to swap the fake into a DI container | none | ⬜ |
| Shared conformance fixtures consumed by tests (`conformance/*.json`) | G T P | ✅ |
| Unit tests on an in-memory gRPC invoker, on the net8.0, net10.0 and net11.0 runtimes (Linux and Windows CI) | G T P | ✅ |
| Integration tests against a real gateway (`OPENSHELL_GATEWAY_ADDRESS`) | G | ✅ the Integration workflow (`scripts/integration-gateway.sh`) runs them on pull requests, `main`, and nightly against the upstream gateway release in `UPSTREAM_VERSION` with the Docker driver and mTLS: lifecycle, stop, update and start, exec (one-shot, streaming, interactive), files, watch and logs, TCP forward and listen, SSH session and tunnel, services, config and policy reads, policy draft review, providers and provider profiles, sandbox provider attach and detach, pagination, and error kinds. Python exec skips because the default image has no `python3`; suspend is checked only as the Docker driver's rejection; provider credential export is checked only as a rejection (no operator certificate); provider refresh is not run (it needs a token endpoint) |
| Testcontainers fixture for a one-command local gateway | none | ⬜ |

## Packaging, docs, and CI

| Feature | Status |
|---|---|
| NuGet packages `OpenShell.Net`, `OpenShell.Net.DependencyInjection`, and `OpenShell.Net.Testing`, with shared metadata, README, and NOTICE; `net8.0` and `net10.0` builds (apps on .NET 11 use `net10.0`) | ✅ packed in CI with package validation; every pull request restores the packages into a consumer app on each framework and publishes a Native AOT consumer (trim and AOT warnings fail). Published to nuget.org (0.1.7); `PackageValidationBaselineVersion` checks API compatibility against the last release |
| `OpenShell.Net.DependencyInjection` (`AddOpenShell`, options binding, startup validation, keyed clients, factory, health check) | ✅ |
| Fluent builder for the DI registration (`AddOpenShellClient`) | ✅ |
| OpenTelemetry `ActivitySource` and `Meter` (`OpenShell.Sdk`, one client span and `rpc.client.duration` per RPC) | ✅ |
| Opt-in forwarding of `TRACEPARENT`/`TRACESTATE` from the environment (`TraceContextFromEnvironment`), as the OpenShell CLI does since v0.1.8 (upstream PR 2931) | ✅ |
| README (overview, install, quick start) and documentation site: getting started, 18 guides (including a feature parity summary), API reference (DocFX, GitHub Pages); every snippet compiled and most run against the fake in CI | ✅ |
| `scripts/ci.sh`, CI workflow (Linux and Windows) | ✅ runs on every pull request and on `main` |
| nuget.org release workflow (Trusted Publishing, tag validated against `UPSTREAM_VERSION`) | ✅ published v0.1.7 (packages, symbols, GitHub release) |
| Vendored upstream protos and conformance fixtures, `scripts/sync-upstream.sh` (optional files for fixtures newer than the release), drift check in CI | ✅ the drift check runs in CI and fails on script errors |
| Weekly upstream sync pull request (`upstream-sync.yml`) | ⚠️ enabled; has not run yet (opens a pull request when upstream releases) |
| Editorconfig with analyzer severities and SPDX header enforcement | ✅ |

## Upstream changes in v0.1.8

Reviewed on 2026-11-11 against the NVIDIA/OpenShell v0.1.8 release (80 commits after v0.1.7):

- CLI-compatible `oidc_token.json` (upstream PR 4830) and the system `active_gateway` fallback (upstream PR 4917):
  implemented here (#10, #11). The shared `oidc-token-bundle.json` fixture is part of v0.1.8.
- Go SDK: Listen tunnels are set up in parallel (upstream PR 4995). The .NET forwarder already opens each
  accepted connection independently.
- Go SDK: `RefreshableToken` no longer serves an expired cached token after a failed refresh. The .NET
  `RefreshableToken` already surfaces the refresh error.
- CLI renewal of client-credentials OIDC tokens (upstream PR 4389): `oidc_token.json` gains an optional
  `client_credentials` marker, read and preserved here, and `GatewayClient` renews a marked token by repeating the
  grant (#90).
- CLI forwarding of `TRACEPARENT`/`TRACESTATE` from the environment to the gateway (upstream PR 2931):
  implemented as the opt-in `TraceContextFromEnvironment` (#92).
- TypeScript SDK: forward tokens are revoked on close (upstream PR 5079). The .NET forwarder uses `ForwardTcp`
  and creates no SSH session tokens.
- Proto changes (`ServiceRouteLease`, `SupervisorServiceAdvertisement`, `supports_service_advertisements`) are
  supervisor-session messages; no SDK-facing change.

## Known differences from the other SDKs

- Policy types in `SandboxSpec` and the policy client are the generated wire types, as in TypeScript. There is
  no policy builder because neither Go nor TypeScript has one.
- `ExecPythonAsync` sends script source (`python -c`), not a pickled callable, because .NET code cannot be pickled.
- File transfer uses tar over exec, so the sandbox image needs `tar` and `sh`. Go's file transfer is a stub, so
  there is nothing to compare against.
- `TunnelAsync` has no `port` argument; the relay ignores it.
- For `cloudflare_jwt` gateways the edge token is sent as a bearer token and as the Access header and cookie,
  which is the union of what Go, Rust, and TypeScript send. `mtls` gateways load certificates from the `mtls/`
  directory, where Go rejects them.
- Go's added, modified, and deleted watch event types are not modelled; `Sandbox.Phase` carries that state.
- Streaming calls are not retried by the retry policy; `WatchAsync` reconnects on its own.
- The fake auto-creates unknown workspaces by default (`AutoCreateWorkspaces`), lets a global setting override a
  sandbox value, fails deleting a provider that a sandbox uses with `Conflict`, returns redacted provider credentials
  from listings, and records `ListenAsync` listeners without opening a socket.

## Out of scope

- `netstandard2.0`: needs polyfills and a different transport, and gRPC over HTTP/2 on .NET Framework is limited.
  Revisit if there is a concrete consumer.
- A .NET framework for implementing the extension services.

## .NET specifics

- Targets `net8.0` and `net10.0`; the tests also run on `net11.0`, loading the `net10.0` build. A newer target
  is added only when the code needs its APIs. Building needs the .NET 11 SDK for C# 15. The lists live in
  `OpenShellSdkTargetFrameworks` and `OpenShellTestTargetFrameworks` in `Directory.Build.props`.
- Async only, with `CancellationToken` everywhere and `IAsyncEnumerable<T>` for streams and pages.
- Records and `required` or `init` members for domain types; the generated protobuf types stay reachable
  through `Raw`.
- Proto bindings are generated at build time from the repository `proto/` directory and are never committed, so
  there is no proto-check step.
