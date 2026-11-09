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
| `SandboxUpdates` service (`UpdateSandbox`, new in v0.1.7; needs compute-driver support and explicit gateway activation) | ⚠️ raw only: `OpenShellClient.RawSandboxUpdates` |
| Supervisor and sandbox-internal RPCs (`ConnectSupervisor`, `RelayStream`, `PeerRelay`, `Peer*`, `PushSandboxLogs`, `Report*`, `FinalizeMainProcessExit`, `GetSandboxProviderEnvironment`, `ExchangeProviderSubjectToken`, `IssueSandboxToken`, `RefreshSandboxToken`, `SubmitPolicyAnalysis`) | Excluded by design; no SDK implements them. Reachable through `OpenShellClient.Raw`. |
| Extension services (`ComputeDriver`, `CredentialDriver`, `GatewayInterceptor`, `SupervisorMiddleware`) | Excluded: they are contracts to implement, not call |

"Called" means the RPC is wired and unit tested against an in-memory gRPC invoker. See Testing for what has
and has not run against a real gateway. Counts are against the protos of the NVIDIA/OpenShell release in
`UPSTREAM_VERSION` (v0.1.7).

## Client surface (`IOpenShellClient`)

| Area | Operations | Seen in | Status |
|---|---|---|---|
| Health | `CheckAsync`, `GetGatewayInfoAsync`, `GetCurrentUserAsync` | G R T P | ✅ |
| Workspaces | create, get, list, delete, add/remove/list members, `InWorkspace(name)` scoped view | G R P | ✅ |
| Sandboxes | create, create from template, get, list, list IDs, stop, start, delete (typed `DeletionResult`) | G R T P | ✅ |
| Sandbox suspend and resume | `SuspendAsync`, `ResumeAsync` (`RestartIfRestoreFails`), `WaitSuspendedAsync`; Ready and Stopped waits fail fast on a suspended sandbox (v0.1.7) | G P | ✅ |
| Sandbox configuration update | `SandboxUpdates.UpdateSandbox` (v0.1.7) | | ⬜ raw only |
| Sandbox waits | `WaitReadyAsync`, `WaitStoppedAsync`, `WaitDeletedAsync` with `ExpectedSandboxId` | G R T P | ✅ |
| Endpoint statuses and token-grant failure diagnostics (`Sandbox.EndpointStatuses`, `LastTokenGrantFailure`, `EndpointFailure`) | G | ✅ |
| Sandbox watch and logs | `WatchAsync`, `WatchLogsAsync`, `GetLogsAsync`, cursor resume, reconnect with backoff | G R | ✅ |
| Sandbox providers | attach, detach, list, status, readiness waits (`WaitAsync`, `WaitAllAsync`) | G R T | ✅ |
| Templates | create, get, list, delete | G R T P | ✅ |
| Exec | `RunAsync`, `StreamAsync`, `InteractiveAsync` (stdin, resize, cancel), `ExecPythonAsync` | G R T P | ✅ |
| Files | upload and download for files, streams, and directories (tar over exec) | G | ⚠️ needs `tar` and `sh` in the sandbox image; not run against a real sandbox |
| Services | expose, get, list, delete, service exposures at create | G R T P | ✅ |
| SSH | create and revoke session, `TunnelAsync` | G T | ✅ |
| TCP | `ForwardAsync` (duplex stream), `ListenAsync` (local listener) | G T | ✅ |
| Config | sandbox and gateway config, policy and setting updates, global scope | G T | ✅ |
| Policy | draft get, approve, reject, approve all, edit, undo, clear, history, status, revisions | G | ✅ |
| Providers | create, get, list (with label selector), update, delete, ensure | G P | ✅ |
| Provider credentials | `GetCredentialsAsync` (operator-only, values redacted in `ToString`) | T P | ✅ |
| Provider profiles | list, get, import, update, lint, delete, platform view; `StablePlaceholder` with a gateway support check (v0.1.7) | G | ✅ |
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
| Token cache, in-memory or on disk | G | ✅ |

## Gateway configuration (`OpenShell.Gateway`)

| Feature | Seen in | Status |
|---|---|---|
| Resolve the on-disk gateway config and active gateway | G P | ✅ |
| List gateways and load metadata | G | ✅ |
| CLI discovery rules from v0.1.7: `OPENSHELL_SYSTEM_GATEWAY_DIR` system root, FQDN and IPv4 gateway names, Windows device names rejected on every platform | G | ✅ |
| mTLS bundle and token loading | G P | ✅ |
| `GatewayClient.Connect` convenience | P | ✅ |

## Testing support

| Feature | Seen in | Status |
|---|---|---|
| Interfaces for every client area, for mocking | G | ✅ |
| In-memory fake client (`OpenShell.Sdk.Testing`; 16 contract scenarios run against both the fake and the real client) | G | ✅ |
| `AddOpenShellFake()` to swap the fake into a DI container | none | ⬜ |
| Shared conformance fixtures consumed by tests (`sdk/conformance/*.json`) | G T P | ✅ |
| Unit tests on an in-memory gRPC invoker, net8.0, net10.0 and net11.0 | G T P | ✅ |
| Integration tests against a real gateway (`OPENSHELL_GATEWAY_ADDRESS`) | G | ⚠️ written, skipped without a gateway, never run against a real one |
| Testcontainers fixture for a one-command local gateway | none | ⬜ |

## Packaging, docs, and CI

| Feature | Status |
|---|---|
| NuGet packages `OpenShell.Net`, `OpenShell.Net.DependencyInjection`, and `OpenShell.Net.Testing`, with shared metadata, README, and NOTICE | ✅ all three pack locally; not yet published |
| `OpenShell.Sdk.DependencyInjection` (`AddOpenShell`, options binding, startup validation, keyed clients, factory, health check) | ✅ |
| Fluent builder for the DI registration (`AddOpenShellClient`) | ✅ |
| OpenTelemetry `ActivitySource` and `Meter` (`OpenShell.Sdk`, one client span and `rpc.client.duration` per RPC) | ✅ |
| README | ✅ |
| `scripts/ci.sh`, CI workflow (Linux and Windows), nuget.org release workflow (Trusted Publishing) | ⚠️ written; no workflow has run yet |
| Vendored upstream protos and conformance fixtures, `scripts/sync-upstream.sh`, weekly sync pull request, drift check in CI | ⚠️ the script runs locally; the workflows have not run yet |
| Editorconfig with analyzer severities and SPDX header enforcement | ✅ |

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

- Targets `net8.0` and `net10.0`; `net11.0` is listed temporarily to surface new analyzer warnings and is
  removed before the pull request if .NET 11 is not generally available. The list lives in
  `OpenShellSdkTargetFrameworks` in `Directory.Build.props`.
- Async only, with `CancellationToken` everywhere and `IAsyncEnumerable<T>` for streams and pages.
- Records and `required` or `init` members for domain types; the generated protobuf types stay reachable
  through `Raw`.
- Proto bindings are generated at build time from the repository `proto/` directory and are never committed, so
  there is no proto-check step.
