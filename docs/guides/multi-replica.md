<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Multi-replica gateways

A Kubernetes gateway can run several replicas. Each sandbox's supervisor keeps its session with one of them, the *owner*, and any other replica relays streams for that sandbox to the owner. With replica routing enabled on the gateway (`grpcRoute.replicaRouting.enabled`), the SDK sends exec, interactive exec, port forwarding, and SSH streams straight to the owner instead, saving the relay hop.

How it works:

1. Before opening one of those streams, the client asks `GetSandbox` for the owner, with the `x-openshell-want-owner` header; the gateway answers in `x-openshell-owner`.
2. It tags the stream with `x-openshell-replica`, and the gateway's route sends it to that replica.
3. The answer is cached for 30 seconds per sandbox, so a single-replica gateway costs one extra `GetSandbox` per sandbox and period, and ignores the header.
4. If the owner replica cannot be reached, the stream is reopened once without routing. A stale owner is harmless, because every replica relays.

Routing is gRPC metadata only and is on by default. Turn it off with `RouteToOwnerReplica`:

[!code-csharp[](../snippets/ClientBehavior.cs#OwnerRouting)]

With dependency injection, set `RouteToOwnerReplica` in the `OpenShell` configuration section or on <xref:OpenShell.DependencyInjection.OpenShellOptions>.

> [!NOTE]
> Owner routing is unit tested with scripted headers; it has not yet been run against a multi-replica gateway. See [`PARITY.md`](https://github.com/zcsizmadia/OpenShell.Net/blob/main/PARITY.md).
