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

## Trace context from CI

The gateway parents its spans on the W3C trace context of each call. Within a traced process, that is the current `Activity`: HttpClient propagates it, and the SDK's span sits between it and the gateway when you subscribe to the source.

A short-lived tool on a CI runner usually has no tracing set up, but a pipeline instrumented with an OpenTelemetry integration (for example the Jenkins OpenTelemetry plugin or an OpenTelemetry GitHub Action) exports its trace context as `TRACEPARENT` and `TRACESTATE`. Set `TraceContextFromEnvironment` to forward it, as the OpenShell CLI does, so the gateway's spans join the pipeline's trace with no collector or provider in the tool:

```csharp
using var client = GatewayClient.Connect(options: new GatewayConnectOptions { TraceContextFromEnvironment = true });
```

The option is on <xref:OpenShell.OpenShellClientOptions>, <xref:OpenShell.Gateway.GatewayConnectOptions>, and the dependency injection options (`OpenShell:TraceContextFromEnvironment`). The variables are read once, when the client is created; lower-case names are a fallback, and an invalid `TRACEPARENT` is ignored (logged at debug level) together with its `TRACESTATE`. A call made while an `Activity` is current propagates that activity instead. When you subscribe to the `OpenShell.Sdk` source, the SDK's spans take the environment context as their parent.

## Logging

Set `OpenShellClientOptions.LoggerFactory` to receive the SDK's own log events: retries, watch reconnects, the gRPC channel, and edge tunnel activity. The dependency injection package supplies the container's logger factory automatically, and <xref:OpenShell.Gateway.GatewayConnectOptions> takes one too.

[!code-csharp[](../snippets/Observability.cs#Logging)]

A standalone <xref:OpenShell.Edge.EdgeTunnelProxy> logs through `EdgeTunnelOptions.Logger`. Sandbox waits and token refresh do not log.
