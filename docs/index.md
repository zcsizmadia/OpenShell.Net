<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# OpenShell.Net

OpenShell.Net is a .NET (C#) client for the [NVIDIA OpenShell](https://github.com/NVIDIA/OpenShell) gateway. It exposes a curated, async API over the gateway's gRPC contract: sandboxes, exec, file transfer, port forwarding, providers, policy, workspaces, and more. It targets `net8.0` and `net10.0` (apps on .NET 11 use the `net10.0` build) and is trimming and AOT compatible.

> [!NOTE]
> This is a community project. It is not affiliated with, endorsed by, or supported by NVIDIA. Report problems in the repository's [issues](https://github.com/zcsizmadia/OpenShell.Net/issues), not upstream.

[!code-csharp[](snippets/GettingStarted.cs#Overview)]

## Where to start

- **[Getting started](getting-started/installation.md)**: install the packages, connect to a gateway, and run a command in a new sandbox.
- **[Guides](guides/authentication.md)**: task-oriented pages with code for every area of the SDK.
- **[API reference](api/index.md)**: every public type of the three packages, generated from the XML documentation.

## Packages

| Package | Contents |
| --- | --- |
| [`OpenShell.Net`](https://www.nuget.org/packages/OpenShell.Net) | The client: every user-facing RPC, authentication, OIDC, gateway discovery, edge tunnel, retries, telemetry. |
| [`OpenShell.Net.DependencyInjection`](https://www.nuget.org/packages/OpenShell.Net.DependencyInjection) | `AddOpenShell`, the `AddOpenShellClient` builder, and an ASP.NET Core health check. |
| [`OpenShell.Net.Testing`](https://www.nuget.org/packages/OpenShell.Net.Testing) | `FakeOpenShellClient`, an in-memory gateway for unit tests. |

The public API lives in the `OpenShell` namespace (with `OpenShell.Gateway`, `OpenShell.Oidc`, `OpenShell.Edge`, `OpenShell.Testing`, and `OpenShell.DependencyInjection` for the specialized parts); the assemblies are named `OpenShell.Sdk*`.

## Which gateway version

The package version is the NVIDIA OpenShell release the SDK is built from, so `OpenShell.Net 0.1.8.x` pairs with an OpenShell 0.1.8 gateway. See [Versioning](guides/versioning.md). [Feature parity](guides/parity.md) summarizes which gateway features the SDK covers and how each was tested; [`PARITY.md`](https://github.com/zcsizmadia/OpenShell.Net/blob/main/PARITY.md) has the full detail.
