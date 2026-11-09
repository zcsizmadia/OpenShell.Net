<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Raw gRPC access

The curated client calls every user-facing RPC of the `OpenShell` service. For anything it does not expose, such as a field it does not surface, the generated gRPC clients are one property away:

| Property | Generated client |
| --- | --- |
| <xref:OpenShell.OpenShellClient.Raw?displayProperty=nameWithType> | `Openshell.V1.OpenShell.OpenShellClient`, the `OpenShell` service |
| <xref:OpenShell.OpenShellClient.RawSandboxUpdates?displayProperty=nameWithType> | `Openshell.V1.SandboxUpdates.SandboxUpdatesClient`, the separate `SandboxUpdates` service |

Both share the connection, authentication, retries, and telemetry of the curated client. They are on the concrete `OpenShellClient`, not on the `IOpenShellClient` interface, so the fake does not offer them.

[!code-csharp[](../snippets/Raw.cs#RawCall)]

Raw calls use the protobuf wire types (in the `Openshell.V1`, `Openshell.Sandbox.V1`, and `Openshell.Datamodel.V1` namespaces) and do not map errors to `OpenShellException`: they throw the gRPC `RpcException`. Requests also do not get the client's workspace: set their `WorkspaceScope` yourself, as above.

## Adjust a curated request

`SandboxSpec.RawSpec` changes the wire `SandboxSpec` after the SDK has mapped the curated fields, for fields that `SandboxSpec` does not surface:

[!code-csharp[](../snippets/Raw.cs#RawSpec)]

The generated types are listed in the [API reference](../api/index.md) with the rest of the package.
