<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Networking

Three ways to reach a port inside a sandbox: a TCP forward through the gateway (`client.Tcp`), an SSH relay (`client.Ssh`), and a service the gateway publishes (`client.Services`).

## TCP forwarding

<xref:OpenShell.ITcpClient.ForwardAsync*> opens a duplex <xref:OpenShell.ForwardStream> to a loopback port inside the sandbox. One reader and one writer may be active at the same time; `CompleteWriteAsync` half-closes the stream so the remote side sees end-of-file while the response keeps flowing. Disposing the stream does not wait for a write that is blocked because the remote side is not reading: it cancels the write, which then throws `ObjectDisposedException` (or `OperationCanceledException` when the caller's own token cancelled it).

Forwards, listeners, and SSH tunnels are streaming calls, so `OpenShellClientOptions.DefaultTimeout` does not bound them; end them by disposing them or with a `CancellationToken`. See [Retries and timeouts](retries.md#timeouts).

[!code-csharp[](../snippets/Networking.cs#Forward)]

<xref:OpenShell.ITcpClient.ListenAsync*> binds a local port (0 picks a free one) and forwards each accepted connection to the sandbox port, so ordinary clients such as `HttpClient` or a browser can connect. Dispose the listener to stop accepting and tear down the active connections.

[!code-csharp[](../snippets/Networking.cs#Listen)]

<xref:OpenShell.ListenOptions> sets the bind address (loopback by default), a service id for audit and correlation, and `UseSshTunnel` to carry the connections over SSH relays instead.

## SSH

<xref:OpenShell.ISshClient.TunnelAsync*> opens the raw SSH byte stream to the sandbox, for an SSH client library to drive. It creates a session for the tunnel and revokes it when the stream is disposed.

[!code-csharp[](../snippets/Networking.cs#Ssh)]

For an external SSH client, create a session yourself. <xref:OpenShell.SshSession> carries the gateway host, port and scheme, the host key fingerprint to pin, the expiry, and the token; the token is a credential and `ToString` redacts it. The response is validated before it is returned, because its fields end up in a `ProxyCommand` that a local shell runs.

[!code-csharp[](../snippets/Networking.cs#SshSession)]

## Services

A service publishes a sandbox port through the gateway under a URL. Declare them when you create the sandbox (`SandboxSpec.ServiceExposures`), or expose them later:

[!code-csharp[](../snippets/Networking.cs#Services)]

`ExposeServiceOptions.Domain` publishes the service on its own domain. `AuthorizationMode` decides what happens to the caller's `Authorization` header: `Strip` (the default) removes it before forwarding, and `BearerPassthrough` forwards one valid bearer header unchanged. `Sandbox.ServiceUrls` lists the URLs of a sandbox's services.

### Service route discovery

Ingress proxies and controllers can discover the routes that sandbox supervisors installed for a workspace through `client.ServiceDiscovery` (the `openshell.v1.ServiceDiscovery` service, new in OpenShell v0.1.9). `GetSnapshotAsync` reads them once; `WatchAsync` streams a full replacement snapshot first and again whenever the view changes, including lease renewals:

[!code-csharp[](../snippets/Networking.cs#ServiceDiscovery)]

The gateway ends each watch after a bounded lifetime (one minute); `WatchAsync` reopens it, skips a snapshot whose `Revision` it already returned, and reconnects with backoff when the gateway is unavailable. Set `ServiceRouteWatchOptions.Reconnect` to `false` to end at the gateway's bound instead. A route proves installation, not application health, and is valid only until `ExpiresAt`: an error is never an empty snapshot, so while the watch fails, drop cached routes as they expire.

The gateway serves discovery only when its supervisor app listener is enabled and otherwise fails with `Conflict` (`FailedPrecondition`). The caller needs the workspace `user` role, and the gateway rejects all-workspace selectors, so each `InWorkspace` view watches one workspace.

## Multi-replica gateways

Forwards and SSH tunnels are routed to the replica that holds the sandbox's supervisor session. See [Multi-replica gateways](multi-replica.md).
