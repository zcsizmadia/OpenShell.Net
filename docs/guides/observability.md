<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Observability

## Traces and metrics

Every RPC emits one client span and one `rpc.client.duration` measurement, in seconds, following the OpenTelemetry gRPC conventions, with the attributes `rpc.system`, `rpc.service`, `rpc.method`, and `rpc.grpc.status_code`. A retried call is a single span with its final outcome. Nothing is recorded or allocated until a listener subscribes, and request payloads, metadata, and credentials are never recorded.

The source and the meter are both named `OpenShell.Sdk`, available as <xref:OpenShell.OpenShellTelemetry.ActivitySourceName?displayProperty=nameWithType> and <xref:OpenShell.OpenShellTelemetry.MeterName?displayProperty=nameWithType>. With the [OpenTelemetry .NET SDK](https://opentelemetry.io/docs/languages/dotnet/), subscribe by name: `tracing.AddSource(OpenShellTelemetry.ActivitySourceName)` and `metrics.AddMeter(OpenShellTelemetry.MeterName)`.

Without OpenTelemetry, the .NET listeners receive the same data:

[!code-csharp[](../snippets/Observability.cs#Listeners)]

## Logging

Set `OpenShellClientOptions.LoggerFactory` to receive the SDK's own log events: retries, watch reconnects, the gRPC channel, and edge tunnel activity. The dependency injection package supplies the container's logger factory automatically, and <xref:OpenShell.Gateway.GatewayConnectOptions> takes one too.

[!code-csharp[](../snippets/Observability.cs#Logging)]

A standalone <xref:OpenShell.Edge.EdgeTunnelProxy> logs through `EdgeTunnelOptions.Logger`. Sandbox waits and token refresh do not log.
