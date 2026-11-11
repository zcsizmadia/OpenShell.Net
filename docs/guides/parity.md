<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Feature parity

OpenShell.Net is built from the protos of NVIDIA OpenShell v0.1.8 (the release in `UPSTREAM_VERSION`). This page summarizes what it covers and how that was tested; [`PARITY.md`](https://github.com/zcsizmadia/OpenShell.Net/blob/main/PARITY.md) has the full, per-feature detail.

## Coverage

- The curated client calls all 67 user-facing RPCs of the gateway's `OpenShell` service, and the `SandboxUpdates` service (new in v0.1.7) as `ISandboxClient.UpdateAsync`.
- The surface targets the union of the Go, Rust, TypeScript, and Python SDKs. Go is the broadest, so the .NET shape follows it, with additions from the other three.
- Anything the curated API does not cover is reachable through the generated gRPC clients; see [Raw gRPC access](raw-grpc.md).

## Areas

Every area is unit tested against an in-memory gRPC invoker. "Fake" means the area is also modelled by `FakeOpenShellClient` (see [Testing](testing.md)); "gateway" means the Integration workflow runs it against a real v0.1.8 gateway (Docker driver, mTLS) on pull requests, `main`, and nightly.

| Area | Status | Tested with |
| --- | --- | --- |
| [Sandboxes](sandboxes.md): lifecycle, waits, stop and start, suspend and resume, updates, templates | Done | unit, fake, gateway (lifecycle, stop, label updates, start; suspend only as the Docker driver's rejection) |
| [Exec and streaming](exec-and-streaming.md): run, stream, interactive, Python | Done | unit, fake, gateway (Python exec is skipped: the default image has no `python3`) |
| [Files and rootfs](files-and-rootfs.md): file, stream, and directory transfer; rootfs staging | Done | unit, fake, gateway (file transfer only) |
| [Networking](networking.md): TCP forward and listen, SSH sessions and tunnels, services | Done | unit, fake, gateway |
| [Providers and profiles](providers.md): providers, sandbox attachment, profiles, credentials, refresh | Done | unit, fake, gateway (credential export only as a rejection; refresh not run) |
| [Policy and configuration](policy.md): draft review, revisions, sandbox and gateway settings | Done | unit, fake, gateway |
| [Workspaces and pagination](workspaces-and-pagination.md) | Done | unit, fake, gateway (listing) |
| Watch and logs (in [Exec and streaming](exec-and-streaming.md#watch-a-sandbox)) | Done | unit, fake, gateway |
| [Authentication](authentication.md): tokens, refreshable tokens, OIDC flows | Done | unit; OIDC against a stub identity provider |
| [Gateway discovery](gateway-discovery.md), mTLS, Cloudflare Access edge tunnel | Done | unit; mTLS also against the gateway; the edge tunnel against a fake WebSocket edge |
| [Dependency injection](dependency-injection.md) | Done | unit |
| [Testing](testing.md) with `FakeOpenShellClient` | Done | unit; 16 contract scenarios run against both the fake and the real client |
| [Observability](observability.md): `ActivitySource`, `Meter`, logging | Done | unit |

## Not yet verified against real infrastructure

These work in unit tests but have not run against the infrastructure they target:

- [Owner routing](multi-replica.md) on a multi-replica gateway (tested with scripted headers).
- The edge tunnel through a real Cloudflare Access edge.
- The browser and device-code OIDC flows against a real identity provider.
- Provider credential export with an operator certificate, and provider credential refresh (it needs a token endpoint).
- Suspend and resume, and image, command, and environment updates: the Docker driver used in CI does not support them (they need Kubernetes).

## Deliberate differences from the other SDKs

- `ExecPythonAsync` sends Python source (`python -c`), not a pickled callable, because .NET code cannot be pickled.
- File transfer is tar over exec, so the sandbox image needs `tar` and `sh`.
- Policy types are the generated wire types, as in TypeScript; there is no policy builder.
- `TunnelAsync` has no port argument, because the relay ignores it.
- For `cloudflare_jwt` gateways the edge token is sent as a bearer token and as the Access header and cookie, the union of what Go, Rust, and TypeScript send; `mtls` gateways load their certificates from the `mtls/` directory.
- Go's added, modified, and deleted watch event types are not modelled; `Sandbox.Phase` carries that state.
- The retry policy does not retry streaming calls; `WatchAsync` reconnects on its own.

## Out of scope

- Supervisor and sandbox-internal RPCs (such as `ConnectSupervisor`, `RelayStream`, and `PushSandboxLogs`); no SDK implements them, and they remain reachable through `OpenShellClient.Raw`.
- The extension services (`ComputeDriver`, `CredentialDriver`, `GatewayInterceptor`, `SupervisorMiddleware`), which are contracts to implement rather than call.
- `netstandard2.0`: it needs polyfills and a different transport, and gRPC over HTTP/2 on .NET Framework is limited.
