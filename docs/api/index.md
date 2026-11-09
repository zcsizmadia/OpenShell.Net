<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# API reference

Generated from the XML documentation of the three packages, for the `net10.0` build (the `net8.0` build has the same API).

| Namespace | Package | Contents |
| --- | --- | --- |
| <xref:OpenShell> | `OpenShell.Net` | The client (<xref:OpenShell.OpenShellClient>, <xref:OpenShell.IOpenShellClient>), the per-area clients, models, options, authentication, retries, errors, telemetry. |
| <xref:OpenShell.Gateway> | `OpenShell.Net` | Gateways registered by the OpenShell CLI: <xref:OpenShell.Gateway.GatewayClient>, <xref:OpenShell.Gateway.GatewayLocator>. |
| <xref:OpenShell.Oidc> | `OpenShell.Net` | OIDC login, device code, refresh, and client credentials. |
| <xref:OpenShell.Edge> | `OpenShell.Net` | The Cloudflare Access edge tunnel. |
| <xref:Microsoft.Extensions.DependencyInjection> | `OpenShell.Net.DependencyInjection` | `AddOpenShell`, `AddOpenShellClient`, and the health check registration. |
| <xref:OpenShell.DependencyInjection> | `OpenShell.Net.DependencyInjection` | Options and the client factory. |
| <xref:OpenShell.Testing> | `OpenShell.Net.Testing` | <xref:OpenShell.Testing.FakeOpenShellClient> and <xref:OpenShell.Testing.FakeGateway>. |
| `Openshell.V1`, `Openshell.Sandbox.V1`, `Openshell.Datamodel.V1`, `Openshell.Options.V1` | `OpenShell.Net` | The generated protobuf messages and gRPC clients, used by policies and [raw access](../guides/raw-grpc.md). Their documentation comes from the `.proto` files. |
