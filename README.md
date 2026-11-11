<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# OpenShell.Net: .NET (C#) SDK for NVIDIA OpenShell

[![CI](https://github.com/zcsizmadia/OpenShell.Net/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/zcsizmadia/OpenShell.Net/actions/workflows/ci.yml)
[![Integration](https://github.com/zcsizmadia/OpenShell.Net/actions/workflows/integration.yml/badge.svg?branch=main)](https://github.com/zcsizmadia/OpenShell.Net/actions/workflows/integration.yml)
[![NuGet](https://img.shields.io/nuget/v/OpenShell.Net?logo=nuget&label=NuGet)](https://www.nuget.org/packages/OpenShell.Net)
[![Release](https://img.shields.io/github/v/release/zcsizmadia/OpenShell.Net?include_prereleases&sort=semver&label=release)](https://github.com/zcsizmadia/OpenShell.Net/releases)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![NVIDIA OpenShell](https://img.shields.io/badge/NVIDIA%20OpenShell-v0.1.8-76B900?logo=nvidia)](https://github.com/NVIDIA/OpenShell/releases/tag/v0.1.8)
[![License](https://img.shields.io/github/license/zcsizmadia/OpenShell.Net)](https://github.com/zcsizmadia/OpenShell.Net/blob/main/LICENSE)

.NET client for the [NVIDIA OpenShell](https://github.com/NVIDIA/OpenShell) gateway. It targets `net8.0` and `net10.0` (apps on .NET 11 use the `net10.0` build, and the tests also run on .NET 11), is trimming and AOT compatible, and exposes a curated, async API over the gateway's gRPC contract.

> This is a community project. It is not affiliated with, endorsed by, or supported by NVIDIA. Report problems in this repository's [issues](https://github.com/zcsizmadia/OpenShell.Net/issues), not upstream.

The generated protobuf bindings come from the `proto/` files of the OpenShell release recorded in [`UPSTREAM_VERSION`](https://github.com/zcsizmadia/OpenShell.Net/blob/main/UPSTREAM_VERSION). Use a gateway from that release or a compatible one: curated methods stay compatible while the underlying RPC contracts do. [`PARITY.md`](https://github.com/zcsizmadia/OpenShell.Net/blob/main/PARITY.md) tracks which gateway features the SDK covers.

## Versions

The package version is the NVIDIA OpenShell release the SDK is built from, with a fourth number for SDK-only releases in between:

| Package version | Built from OpenShell |
| --- | --- |
| `0.1.7` | `v0.1.7` |
| `0.1.8` | `v0.1.8` |
| `0.1.8.1`, `0.1.8.2`, ... | `v0.1.8`, with SDK fixes and additions |
| `0.1.9` | `v0.1.9` |

So `OpenShell.Net 0.1.8.x` pairs with an OpenShell 0.1.8 gateway. While OpenShell is at 0.x, an SDK-only release can also change the SDK's API; the release notes say so. The assembly metadata `OpenShellUpstreamRelease` records the release at run time.

## Parity

Built from NVIDIA OpenShell v0.1.8, the client covers all 67 user-facing gateway RPCs plus the `SandboxUpdates` service, and targets the union of the Go, Rust, TypeScript, and Python SDKs. It is integration-tested against a real v0.1.8 gateway in CI. See the [feature parity summary](https://zcsizmadia.github.io/OpenShell.Net/guides/parity.html) and [`PARITY.md`](https://github.com/zcsizmadia/OpenShell.Net/blob/main/PARITY.md) for details.

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

try
{
    await client.Sandboxes.WaitReadyAsync(sandbox.Name, new WaitOptions { Timeout = TimeSpan.FromMinutes(2) });

    var result = await client.Exec.RunAsync(
        sandbox.Name,
        ["python", "-c", "print('hello from OpenShell')"]);
    Console.WriteLine(result.StdoutText);
}
finally
{
    // Delete the sandbox even when the wait or the command throws.
    var deletion = await client.Sandboxes.DeleteAsync(sandbox.Name);
    if (deletion.Outcome == DeletionOutcome.Accepted)
    {
        await client.Sandboxes.WaitDeletedAsync(
            sandbox.Name,
            new WaitOptions { ExpectedSandboxId = deletion.ResourceId });
    }
}
```

`DeleteAsync` returns a typed `DeletionResult`, not a boolean. `Accepted` means cleanup is still pending, `Unspecified` does not establish completion, and `IsComplete` is true only for `Completed` and `AlreadyAbsent`. A missing target is an error unless you pass `new DeleteOptions { AllowMissing = true }`.

## Documentation

The full documentation is at **[zcsizmadia.github.io/OpenShell.Net](https://zcsizmadia.github.io/OpenShell.Net/)**:

- **Getting started:** [installation](https://zcsizmadia.github.io/OpenShell.Net/getting-started/installation.html), [connecting to a gateway](https://zcsizmadia.github.io/OpenShell.Net/getting-started/connecting.html) (explicit endpoint, a gateway registered by the OpenShell CLI, or a DI host), and [a first program](https://zcsizmadia.github.io/OpenShell.Net/getting-started/first-program.html).
- **Guides:**
  - Connecting: [authentication](https://zcsizmadia.github.io/OpenShell.Net/guides/authentication.html) (tokens, OIDC, mTLS, Cloudflare Access), [gateway discovery](https://zcsizmadia.github.io/OpenShell.Net/guides/gateway-discovery.html), [dependency injection](https://zcsizmadia.github.io/OpenShell.Net/guides/dependency-injection.html), [multi-replica gateways](https://zcsizmadia.github.io/OpenShell.Net/guides/multi-replica.html).
  - Sandboxes and their contents: [sandboxes](https://zcsizmadia.github.io/OpenShell.Net/guides/sandboxes.html) (lifecycle, suspend and resume, updates, endpoint status), [exec and streaming](https://zcsizmadia.github.io/OpenShell.Net/guides/exec-and-streaming.html), [networking](https://zcsizmadia.github.io/OpenShell.Net/guides/networking.html) (TCP forwarding, SSH, services), [files and rootfs](https://zcsizmadia.github.io/OpenShell.Net/guides/files-and-rootfs.html).
  - Configuration: [providers](https://zcsizmadia.github.io/OpenShell.Net/guides/providers.html), [policy](https://zcsizmadia.github.io/OpenShell.Net/guides/policy.html), [workspaces and pagination](https://zcsizmadia.github.io/OpenShell.Net/guides/workspaces-and-pagination.html).
  - Behaviour and diagnostics: [retries](https://zcsizmadia.github.io/OpenShell.Net/guides/retries.html), [errors](https://zcsizmadia.github.io/OpenShell.Net/guides/errors.html), [observability](https://zcsizmadia.github.io/OpenShell.Net/guides/observability.html).
  - Other: [testing with the fake gateway](https://zcsizmadia.github.io/OpenShell.Net/guides/testing.html), [raw gRPC access](https://zcsizmadia.github.io/OpenShell.Net/guides/raw-grpc.html), [versioning](https://zcsizmadia.github.io/OpenShell.Net/guides/versioning.html).
- **[API reference](https://zcsizmadia.github.io/OpenShell.Net/api/index.html)** for all three packages.

Every C# snippet in the guides is compiled, and most are run against the fake gateway, by the Docs workflow. The sources are in [`docs/`](https://github.com/zcsizmadia/OpenShell.Net/tree/main/docs).

## Contributing

Building from source, the CI script, integration tests against a real gateway, syncing with NVIDIA OpenShell, and mutation testing are described in [CONTRIBUTING.md](https://github.com/zcsizmadia/OpenShell.Net/blob/main/CONTRIBUTING.md).

## License

Apache-2.0. See [LICENSE](https://github.com/zcsizmadia/OpenShell.Net/blob/main/LICENSE) and [NOTICE](https://github.com/zcsizmadia/OpenShell.Net/blob/main/NOTICE). The vendored protos and fixtures are Copyright NVIDIA Corporation & Affiliates, also under Apache-2.0.
