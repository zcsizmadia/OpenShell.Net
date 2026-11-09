<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Versioning

The package version is the NVIDIA OpenShell release the SDK is built from, with a fourth number for SDK-only releases in between:

| Package version | Built from OpenShell |
| --- | --- |
| `0.1.7` | `v0.1.7` |
| `0.1.7.1`, `0.1.7.2`, ... | `v0.1.7`, with SDK fixes and additions |
| `0.1.8` | `v0.1.8` |

So `OpenShell.Net 0.1.7.x` pairs with an OpenShell 0.1.7 gateway. While OpenShell is at 0.x, an SDK-only release can also change the SDK's API; the [release notes](https://github.com/zcsizmadia/OpenShell.Net/releases) say so.

The generated protobuf bindings come from the `proto/` files of the release recorded in [`UPSTREAM_VERSION`](https://github.com/zcsizmadia/OpenShell.Net/blob/main/UPSTREAM_VERSION). Use a gateway from that release or a compatible one: curated methods stay compatible while the underlying RPC contracts do. Features that need gateway-side support, such as sandbox updates or stable placeholders, fail with a typed error (`Conflict` or `Unimplemented`) on a gateway that lacks them.

## At run time

The assembly metadata `OpenShellUpstreamRelease` records the release, and `Health.CheckAsync()` reports the gateway's version:

[!code-csharp[](../snippets/Observability.cs#UpstreamRelease)]

## Feature coverage

[Feature parity](parity.md) summarizes which gateway features the SDK covers, how each was tested, and how the SDK differs from the Go, Rust, TypeScript, and Python SDKs; [`PARITY.md`](https://github.com/zcsizmadia/OpenShell.Net/blob/main/PARITY.md) has the full detail.
