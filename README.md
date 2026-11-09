<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# OpenShell.Net: unofficial .NET (C#) SDK for NVIDIA OpenShell

.NET client for the [NVIDIA OpenShell](https://github.com/NVIDIA/OpenShell) gateway. It targets `net8.0` and `net10.0` (apps on .NET 11 use the `net10.0` build, and the tests also run on .NET 11), is trimming and AOT compatible, and exposes a curated, async API over the gateway's gRPC contract.

> This is a community project. It is not affiliated with, endorsed by, or supported by NVIDIA. Report problems in this repository's [issues](https://github.com/zcsizmadia/OpenShell.Net/issues), not upstream.

The generated protobuf bindings come from the `proto/` files of the OpenShell release recorded in [`UPSTREAM_VERSION`](https://github.com/zcsizmadia/OpenShell.Net/blob/main/UPSTREAM_VERSION). Use a gateway from that release or a compatible one: curated methods stay compatible while the underlying RPC contracts do. [`PARITY.md`](https://github.com/zcsizmadia/OpenShell.Net/blob/main/PARITY.md) tracks which gateway features the SDK covers.

## Versions

The package version is the NVIDIA OpenShell release the SDK is built from, with a fourth number for SDK-only releases in between:

| Package version | Built from OpenShell |
| --- | --- |
| `0.1.7` | `v0.1.7` |
| `0.1.7.1`, `0.1.7.2`, ... | `v0.1.7`, with SDK fixes and additions |
| `0.1.8` | `v0.1.8` |

So `OpenShell.Net 0.1.7.x` pairs with an OpenShell 0.1.7 gateway. While OpenShell is at 0.x, an SDK-only release can also change the SDK's API; the release notes say so. The assembly metadata `OpenShellUpstreamRelease` records the release at run time.

## Install

The packages are published to [nuget.org](https://www.nuget.org/packages/OpenShell.Net):

```shell
dotnet add package OpenShell.Net
```

| Package | Contents |
| --- | --- |
| `OpenShell.Net` | The client: every user-facing RPC, authentication, OIDC, gateway discovery, edge tunnel, retries, telemetry |
| `OpenShell.Net.DependencyInjection` | `AddOpenShell`, the `AddOpenShellClient` builder, and an ASP.NET health check |
| `OpenShell.Net.Testing` | `FakeOpenShellClient`, an in-memory gateway for unit tests |

The public API lives in the `OpenShell` namespace, and the assemblies are named `OpenShell.Sdk*`.

## Quick start

Create one `OpenShellClient` and reuse it. The client connects lazily, so call `Health.CheckAsync()` when startup must verify that the gateway is reachable.

```csharp
using OpenShell;

using var client = new OpenShellClient(new OpenShellClientOptions
{
    Address = new Uri("https://gateway.example.com"),
    Auth = Auth.StaticToken(Environment.GetEnvironmentVariable("OPENSHELL_TOKEN")!),
});

var health = await client.Health.CheckAsync();
Console.WriteLine($"{health.Status}: {health.Version}");

var sandbox = await client.Sandboxes.CreateAsync(
    "sdk-example",
    new SandboxSpec { Image = "registry.example.com/team/python-agent:1.0" });

await client.Sandboxes.WaitReadyAsync(sandbox.Name, new WaitOptions { Timeout = TimeSpan.FromMinutes(2) });

var result = await client.Exec.RunAsync(
    sandbox.Name,
    ["python", "-c", "print('hello from OpenShell')"]);
Console.WriteLine(result.StdoutText);

var deletion = await client.Sandboxes.DeleteAsync(sandbox.Name);
if (deletion.Outcome == DeletionOutcome.Accepted)
{
    await client.Sandboxes.WaitDeletedAsync(
        sandbox.Name,
        new WaitOptions { ExpectedSandboxId = deletion.ResourceId });
}
```

`DeleteAsync` returns a typed `DeletionResult`, not a boolean. `Accepted` means cleanup is still pending, `Unspecified` does not establish completion, and `IsComplete` is true only for `Completed` and `AlreadyAbsent`. A missing target is an error unless you pass `new DeleteOptions { AllowMissing = true }`.

### Suspend and resume

A ready sandbox can be suspended: it is frozen with its memory retained and resumed later from that state. The compute driver must support it; otherwise both calls fail with `OpenShellErrorKind.Conflict` (`FAILED_PRECONDITION`).

```csharp
await client.Sandboxes.SuspendAsync("sdk-example");        // Ready -> Suspending
await client.Sandboxes.WaitSuspendedAsync("sdk-example");  // -> Suspended

await client.Sandboxes.ResumeAsync("sdk-example");         // -> Starting, with the retained state
await client.Sandboxes.WaitReadyAsync("sdk-example");      // -> Ready
```

Repeating a suspend while suspending or suspended, or a resume while it is starting, is safe. `ResumeSandboxOptions.RestartIfRestoreFails` boots the workload cleanly when the memory state cannot be restored, instead of failing. `StopAsync` and `StartAsync` also work on a suspended sandbox, but they discard the retained state. `WaitReadyAsync` and `WaitStoppedAsync` fail fast on a suspended sandbox instead of waiting for a phase it will not reach on its own.

### Update a stopped sandbox

A gateway with `[openshell.gateway] enable_sandbox_updates = true` can change a stopped sandbox in place. Pass the resource version you read (compare-and-swap); set only what changes, because a null property is left alone while an empty collection replaces the value with nothing. The update does not start the sandbox.

```csharp
var sandbox = await client.Sandboxes.GetAsync("sdk-example");   // must be Stopped
sandbox = await client.Sandboxes.UpdateAsync("sdk-example", sandbox.ResourceVersion, new SandboxUpdate
{
    Image = "registry.example.com/team/python-agent:1.1",
    Environment = new Dictionary<string, string> { ["MODE"] = "prod" },
});
await client.Sandboxes.StartAsync("sdk-example");
```

A stale version fails with `Conflict` (`ABORTED`); a running sandbox, or a gateway with the feature off, with `Conflict` (`FAILED_PRECONDITION`). Image, command, and environment changes need compute-driver support (Kubernetes at first) and otherwise fail with `Unimplemented`; label and annotation changes work everywhere, and system keys are rejected. If an update fails part-way, it stays pending and blocks start and stop: call `ResumePendingUpdateAsync(name, currentResourceVersion)` to finish it. With `UpdateSandboxOptions.RequestId` the gateway admits a retried update at most once.

### Endpoint status

`Sandbox.EndpointStatuses` reports what the sandbox supervisor last observed for each declared endpoint: host, ports, path, the latest `EndpointResult`, and when it was reported. When a gateway-managed token grant fails, `EndpointStatus.LastFailure` and `Sandbox.LastTokenGrantFailure` carry the classified cause: a stable `Code` and `Stage` (lower_snake_case), the `RecoveryAction` to take, the provider and credential key, the upstream HTTP status, and the `x-openshell-request-id` the workload saw, for correlating with gateway logs. In tests, `FakeGateway.ReportEndpointStatuses` sets them.

## Connect and authenticate

`Auth` builds the authentication providers:

| Provider | Use |
| --- | --- |
| `Auth.None` | No credentials, for example a local plaintext gateway. |
| `Auth.StaticToken(token)` | A fixed OIDC bearer token. |
| `Auth.Bearer(tokenSource)` | A token fetched per request from a cache you manage. |
| `Auth.CloudflareAccess(edgeToken, inner)` | Cloudflare Access header and cookie, optionally on top of another provider. |
| `Auth.WithHeaders(inner, headers)` | Extra fixed headers on top of another provider. |
| `RefreshableToken.AsAuthProvider()` | Single-flight renewal before expiry and after an `Unauthenticated` response. |

For long-running service automation, use the OIDC client-credentials provider from `OpenShell.Oidc`. It discovers and validates the issuer, keeps secrets and tokens in memory, and renews the token before expiry:

```csharp
using OpenShell;
using OpenShell.Oidc;

using var auth = ClientCredentialsAuth.Create(new OidcOptions
{
    Issuer = "https://idp.example.com/realms/openshell",
    ClientId = "openshell-service",
    ClientSecretProvider = ct => ValueTask.FromResult(
        Environment.GetEnvironmentVariable("OPENSHELL_OIDC_CLIENT_SECRET")!),
    Audience = "openshell-gateway",
});

using var client = new OpenShellClient(new OpenShellClientOptions
{
    Address = new Uri("https://gateway.example.com"),
    Auth = auth,
});
```

`OidcLogin` also provides the interactive authorization-code flow with PKCE, the device-code flow, a paste-the-code keyboard flow, and refresh-token exchange.

### Mutual TLS

The default local gateway requires a client certificate. Pass it, and optionally a private CA, through `TlsOptions`:

```csharp
using System.Security.Cryptography.X509Certificates;
using OpenShell;

var client = new OpenShellClient(new OpenShellClientOptions
{
    Address = new Uri("https://localhost:8080"),
    Tls = new TlsOptions
    {
        CaCertificates = new X509Certificate2Collection(X509Certificate2.CreateFromPemFile("ca.crt")),
        ClientCertificate = X509Certificate2.CreateFromPemFile("client.crt", "client.key"),
    },
});
```

`TlsOptions.InsecureSkipVerify` disables server certificate validation. Use it only for local development.

### Use a registered gateway

`GatewayClient.Connect(name)` builds a client from a gateway the OpenShell CLI registered on disk, including its authentication mode, mTLS bundle, and cached tokens. Pass no name to use the active gateway. It renews an expired OIDC token with the stored refresh token and writes the rotation back so the CLI stays in sync. `oidc_token.json` uses the CLI format (`access_token`, `refresh_token`, `expires_at` in Unix seconds, `issuer`, `client_id`), so one `openshell gateway login` serves both, and a token issued for another issuer or client is never sent.

```csharp
using OpenShell.Gateway;

using var client = GatewayClient.Connect();
```

Registrations are read from the user configuration directory (`$XDG_CONFIG_HOME/openshell`, or the OS default) and then the system directory, `/etc/openshell` on Linux and macOS, or the absolute path in `OPENSHELL_SYSTEM_GATEWAY_DIR` on any OS. As in the CLI, only `active_gateway` and `metadata.json` fall back to the system directory: the active gateway comes from `$OPENSHELL_GATEWAY`, then the user `active_gateway`, then the system one, and a user entry shadows a system registration only when it has its own `metadata.json`. Credentials (the `mtls` bundle, `oidc_token.json`, `edge_token`) are always read from and written to the user gateway directory, `GatewayMetadata.StateDirectory`. Gateway names follow the CLI rules: ASCII letters, digits, `-`, `_`, and `.`, so host-derived names such as `gw.example.com` and `192.0.2.1` work, while `.` and `..` components and Windows device names such as `CON` or `COM1.example.com` are rejected on every platform.

### Token transport safety

The SDK refuses to send credentials over plaintext `http://` to a non-loopback host and throws an `OpenShellException` with `OpenShellErrorKind.InvalidConfig`. Set `AllowInsecureAuth` only when you accept that the credential crosses the network in clear text. Edge tokens must use cookie-safe JWT characters. A client certificate must include its private key.

### Gateways behind Cloudflare Access

The edge authenticates only GET requests, so gRPC calls reach such a gateway through a WebSocket tunnel. `GatewayClient.Connect` does this for you when the registered gateway uses `cloudflare_jwt`: it starts a loopback proxy, points the client at it, and disposes the proxy with the client. Pass `EdgeTunnel = EdgeTunnelMode.Never` in `GatewayConnectOptions` to send the Access headers on direct gRPC calls instead, or supply your own `Auth` to opt out.

Outside `GatewayClient`, start the proxy yourself and point the client at it:

```csharp
using OpenShell;
using OpenShell.Edge;

await using var proxy = EdgeTunnelProxy.Start("https://gateway.example.com", edgeToken);
using var client = new OpenShellClient(new OpenShellClientOptions
{
    Address = proxy.LocalUri,
    Auth = Auth.CloudflareAccess(edgeToken),
});
```

### Multi-replica gateways

On a Kubernetes gateway with several replicas and replica routing enabled (`grpcRoute.replicaRouting.enabled`), exec, interactive exec, port forwarding, and SSH tunnels go straight to the replica that holds the sandbox's supervisor session instead of being relayed between replicas. The client asks `GetSandbox` for the owner (`x-openshell-want-owner`) and tags the stream with `x-openshell-replica`. It caches the answer for 30 seconds per sandbox, so a single-replica gateway costs one extra `GetSandbox` per sandbox and period. If the owner replica cannot be reached, the stream is reopened once without routing; a stale owner is harmless because every replica relays. Set `RouteToOwnerReplica = false` to turn it off.

## Retries

Reads (`Get*`, `List*`, and `Health`) that fail with `Unavailable` are retried up to four times with exponential backoff and jitter. Writes are never retried by default, because a lost response could repeat a side effect. Retries stop at the call's deadline and when the caller cancels. Streaming calls are not retried; `Watch.WatchAsync` reconnects on its own. When the gateway attaches a retry delay (`RetryInfo`, for example with the `MUTATION_LOCK_TIMEOUT` reason after a 10 s wait for a sandbox or workspace lock), the next attempt waits at least that long; a delay longer than `MaxBackoff` ends the retries and surfaces the error with its `RetryDelay`.

```csharp
using OpenShell;

var client = new OpenShellClient(new OpenShellClientOptions
{
    Address = new Uri("https://gateway.example.com"),
    Retry = RetryPolicy.Default with { MaxAttempts = 6, MaxBackoff = TimeSpan.FromSeconds(10) },
});
```

Use `RetryPolicy.None` to turn retries off. To retry a write you know is safe, for example one that carries a request id, widen `RetryPolicy.IsIdempotent`.

## Workspaces

Operations run in `OpenShellClientOptions.DefaultWorkspace`, which defaults to `default`. `InWorkspace` returns a view bound to another workspace that shares the same connection. Disposing a view does not close the shared connection.

```csharp
var team = client.InWorkspace("team-a");
var sandboxes = await team.Sandboxes.List().ToListAsync();

var workspace = await client.Workspaces.GetAsync("team-a");
```

## Pagination

List methods return a lazy `Pager<T>`. Enumerate it for items, or call `PagesAsync()` for pages. Set `ListOptions.AllWorkspaces` to list across workspaces where the resource supports it.

```csharp
await foreach (var sandbox in client.Sandboxes.List(new ListOptions { PageSize = 50 }))
{
    Console.WriteLine($"{sandbox.Name}: {sandbox.Phase}");
}
```

## Errors

Every operation throws `OpenShellException`. Switch on `Kind`, which covers `NotFound`, `AlreadyExists`, `PermissionDenied`, `Unauthenticated`, `InvalidArgument`, `Unavailable`, `DeadlineExceeded`, `Cancelled`, `Unimplemented`, `Conflict`, `Internal`, `Auth`, `Connect`, `Tls`, and `InvalidConfig`. The `IsNotFound`, `IsAlreadyExists`, `IsConflict`, and `IsTransient` shortcuts cover the common cases, and `GrpcStatusCode` exposes the underlying gRPC status. Cancelling a call through its `CancellationToken` throws `OperationCanceledException`.

```csharp
try
{
    await client.Sandboxes.GetAsync("missing");
}
catch (OpenShellException e) when (e.IsNotFound)
{
    // Handle the missing sandbox.
}
```

Waits that exceed their timeout throw `OpenShellErrorKind.DeadlineExceeded`.

When the gateway attaches structured details, the exception exposes them. `ErrorInfo` carries a stable `Reason`, a `Domain`, and metadata. `FieldViolations` lists the request fields that were rejected. `RetryDelay` is the delay the gateway suggested before trying again; it says nothing about whether repeating a mutation is safe. The SDK ignores details whose envelope does not match the call status, so a mismatched trailer cannot rewrite the error.

```csharp
try
{
    await client.Sandboxes.CreateAsync("Bad Name!", new SandboxSpec { Image = "img" });
}
catch (OpenShellException e) when (e.Kind == OpenShellErrorKind.InvalidArgument)
{
    foreach (var violation in e.FieldViolations)
    {
        Console.WriteLine($"{violation.Field}: {violation.Description}");
    }
}
```

## Streaming exec, watch, and logs

`Exec.StreamAsync` yields output as it arrives and always ends with an `ExecExit` event, so a failing command cannot be missed. `Exec.InteractiveAsync` opens an interactive session with stdin, resize, and cancel.

```csharp
await foreach (var e in client.Exec.StreamAsync("sdk-example", ["sh", "-c", "echo hi; exit 3"]))
{
    switch (e)
    {
        case ExecOutput o: Console.Write(o.Text); break;
        case ExecExit x: Console.WriteLine($"exit {x.ExitCode}"); break;
    }
}
```

`Watch.WatchAsync` streams sandbox status, log, platform, and draft-policy events. It reconnects with backoff on `Unavailable` and resumes from the last cursor. Pass `WatchOptions.ResumeAfterCursor` to resume across process restarts.

```csharp
var options = new WatchOptions { FollowLogs = true };
await foreach (var e in client.Watch.WatchAsync("sdk-example", options))
{
    if (e is LogReceived log)
    {
        Console.WriteLine(log.Line.Message);
    }
}
```

## Providers

`client.Providers` creates, updates, lists, and deletes providers, ensures one exists, and retrieves runtime credentials. `client.Providers.Profiles` and `Refresh` manage provider profiles and credential refresh. `client.SandboxProviders` attaches and detaches providers and waits for readiness.

`GetCredentialsAsync` requires an operator client certificate and an explicit workspace. Keep the returned values out of logs. `Provider`, `ProviderCredentialValue`, and `RefreshConfig` override `ToString` so secret values never print.

```csharp
var credentials = await operatorClient.Providers.GetCredentialsAsync(
    "my-graph",
    ["MS_GRAPH_ACCESS_TOKEN"],
    new ProviderCredentialsOptions { Workspace = "production" });
```

A profile credential with `StablePlaceholder = true` keeps one opaque placeholder in the sandbox environment across value-only provider revisions. It needs at least one environment variable and cannot use gateway-managed refresh or a token grant. Because older gateways drop unknown fields silently, `ImportAsync`, `UpdateAsync`, and `LintAsync` first lint the profiles and fail with `OpenShellErrorKind.Unimplemented` when the gateway does not support it; `LintProfilesResult.SupportsStablePlaceholder` reports support.

## Other APIs

| Property | Operations |
| --- | --- |
| `Services` | Expose, get, list, and delete sandbox services. |
| `Ssh` | Create and revoke SSH sessions, and open a tunnel. |
| `Tcp` | `ForwardAsync` returns a duplex `Stream`, and `ListenAsync` binds a local port that forwards to the sandbox. |
| `Files` | Upload and download files and directories using tar over exec. The sandbox image needs `tar` and `sh`. |
| `Config` | Read and update sandbox and gateway configuration, policy, and settings. |
| `Policy` | Review draft policy chunks and list policy revisions. |
| `Templates` | Create, get, list, and delete reusable workload templates. |
| `Workspaces` | Manage workspaces and their members. |
| `Rootfs` | Stage a local rootfs tar for drivers that boot from one. It works only when the client shares the gateway's filesystem. |

## Raw client

`OpenShellClient.Raw` exposes the generated gRPC client for RPCs the curated surface does not cover, and `OpenShellClient.RawSandboxUpdates` the client for the separate `SandboxUpdates` service. Both share the connection, authentication, retries, and telemetry of the curated client. Raw calls use protobuf wire types and do not map errors to `OpenShellException`.

## Dependency injection

The `OpenShell.Net.DependencyInjection` package registers a singleton `IOpenShellClient`, plus its per-area clients, with `Microsoft.Extensions.DependencyInjection`. Options are validated at startup, so a bad address, an unreadable certificate, or a token headed for plaintext HTTP fails when the host starts instead of on the first call.

```csharp
using Microsoft.Extensions.DependencyInjection;

services.AddOpenShell(configuration.GetSection("OpenShell"));
```

```json
{
  "OpenShell": {
    "Address": "https://gateway.example.com",
    "Workspace": "team-a",
    "CaCertificatePath": "/etc/openshell/ca.crt",
    "ClientCertificatePath": "/etc/openshell/client.crt",
    "ClientKeyPath": "/etc/openshell/client.key",
    "DefaultTimeout": "00:00:30",
    "Retry": { "MaxAttempts": 6 }
  }
}
```

Keep secrets such as `Token` in user secrets, environment variables, or a vault, not in the JSON file. `AddOpenShellClient` offers a fluent alternative, and a name registers a keyed client for each gateway:

```csharp
services.AddOpenShellClient("prod")
    .WithAddress("https://gateway.example.com")
    .WithClientCredentials(
        "https://idp.example.com/realms/openshell",
        "openshell-service",
        ct => ValueTask.FromResult(Environment.GetEnvironmentVariable("OPENSHELL_OIDC_CLIENT_SECRET")!),
        "sandbox:read",
        "sandbox:write")
    .WithRetry(retry => retry.MaxAttempts = 6);

// Resolve it with [FromKeyedServices("prod")] IOpenShellClient client, or from the factory:
var prod = provider.GetRequiredService<IOpenShellClientFactory>().Create("prod");
```

The builder also sets the workspace, bearer token, auth providers, CA and client certificates, timeout, and insecure-TLS and insecure-auth overrides. `services.AddHealthChecks().AddOpenShellHealthCheck()` adds an ASP.NET health check that calls the gateway health RPC. The OIDC provider is a container singleton, so the container disposes it.

## Observability

Every RPC emits one client span and one `rpc.client.duration` measurement, in seconds, using the OpenTelemetry gRPC conventions: the attributes `rpc.system`, `rpc.service`, `rpc.method`, and `rpc.grpc.status_code`. A retried call is a single span with its final outcome. Nothing is recorded or allocated until a listener subscribes, and request payloads, metadata, and credentials are never recorded. Subscribe by name:

```csharp
using OpenShell;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(OpenShellTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(OpenShellTelemetry.MeterName));
```

Set `OpenShellClientOptions.LoggerFactory` to receive the SDK's own log events: retries, watch reconnects, and edge tunnel activity. The dependency injection package supplies the container's logger factory automatically.

## Testing

`OpenShell.Net.Testing` provides `FakeOpenShellClient`, an in-memory gateway that implements every `IOpenShellClient` area with the same error kinds as the real client. It keeps workspace-scoped state, paginates with real page tokens, advances sandbox phases on a `TimeProvider` you control, and streams watch events with cursors. Script command results, inject failures, and inspect the recorded calls through `Gateway`:

```csharp
using OpenShell;
using OpenShell.Testing;

using var fake = new FakeOpenShellClient();
fake.Gateway.OnExec(call => call.CommandLine == "echo hi" ? FakeExecScript.Success("hi\n") : null);

await fake.Sandboxes.CreateAsync("demo", new SandboxSpec { Image = "img" });
var result = await fake.Exec.RunAsync("demo", ["echo", "hi"]);
// result.StdoutText == "hi\n"

fake.Gateway.FailNext("Sandboxes.Get", new OpenShellException(OpenShellErrorKind.Unavailable, "down"));
```

Write your own code against `IOpenShellClient` and its per-area interfaces, such as `ISandboxClient`, so tests can substitute the fake. `ListenAsync` on the fake records the listener without opening a socket, and provider credentials come back redacted from listings.

## Build from source

Protobuf bindings are generated at build time from the repository `proto/` directory and are never committed. You need the .NET 11 SDK and the .NET 8 and .NET 10 runtimes. From the repository root, in Bash (Git Bash on Windows):

```shell
scripts/ci.sh            # format check, build, unit tests on every framework, coverage summary, pack
scripts/ci.sh test       # or one step: format, build, test, coverage, integration, pack
```

The integration tests run against a real gateway and skip unless `OPENSHELL_GATEWAY_ADDRESS` is set (see `tests/OpenShell.Sdk.IntegrationTests/GatewayFixture.cs` for the token, CA, and mTLS variables).

## Staying in sync with NVIDIA OpenShell

The gRPC protos (`proto/`) and the shared SDK conformance fixtures (`conformance/`) are vendored unmodified from an NVIDIA/OpenShell release, recorded in `UPSTREAM_VERSION`:

```shell
scripts/sync-upstream.sh --check   # is UPSTREAM_VERSION the latest release?
scripts/sync-upstream.sh           # sync to the latest release
scripts/sync-upstream.sh v0.1.7    # or to a specific tag or commit
```

A weekly workflow does this automatically. When upstream publishes a release, it opens a pull request with the synced files and the result of the full check against them.

## License

Apache-2.0. See [LICENSE](https://github.com/zcsizmadia/OpenShell.Net/blob/main/LICENSE) and [NOTICE](https://github.com/zcsizmadia/OpenShell.Net/blob/main/NOTICE). The vendored protos and fixtures are Copyright NVIDIA Corporation & Affiliates, also under Apache-2.0.
