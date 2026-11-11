<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Connect to a gateway

Everything goes through one <xref:OpenShell.OpenShellClient> (or the <xref:OpenShell.IOpenShellClient> interface). Create one per gateway and reuse it: it holds the HTTP/2 connection. The client connects lazily, so creating it never fails because the gateway is down; call `Health.CheckAsync()` when startup must verify that the gateway is reachable.

There are three ways to get a client.

## A registered CLI gateway

If you use the OpenShell CLI, the gateways it registered are on disk with their endpoint, authentication mode, mTLS bundle, and cached tokens. <xref:OpenShell.Gateway.GatewayClient.Connect*?displayProperty=nameWithType> builds a client from one:

[!code-csharp[](../snippets/GettingStarted.cs#ConnectToRegisteredGateway)]

With no name it uses the active gateway: `$OPENSHELL_GATEWAY`, then the user's `active_gateway` file, then the system one. An expired OIDC token is renewed with the stored refresh token, or by repeating the client-credentials grant for a CI token, and written back, so the CLI stays signed in too. [Gateway discovery](../guides/gateway-discovery.md) describes where registrations are read from.

## An explicit endpoint

Pass the address and the credentials in <xref:OpenShell.OpenShellClientOptions>:

[!code-csharp[](../snippets/GettingStarted.cs#ConnectToEndpoint)]

`Auth` takes any <xref:OpenShell.IAuthProvider>: a static token as here, a refreshable token, OIDC client credentials, Cloudflare Access, or none. `Tls` takes a private CA and a client certificate for mTLS. See [Authentication](../guides/authentication.md).

> [!IMPORTANT]
> The SDK refuses to send credentials over plaintext `http://` to a host other than loopback, and throws `OpenShellException` with `OpenShellErrorKind.InvalidConfig`.

## An ASP.NET Core or Generic Host app

The `OpenShell.Net.DependencyInjection` package registers a singleton <xref:OpenShell.IOpenShellClient>, and its per-area clients such as <xref:OpenShell.ISandboxClient>, in the service collection:

[!code-csharp[](../snippets/GettingStarted.cs#ConnectWithDependencyInjection)]

Options are validated when the host starts, so a bad address, an unreadable certificate, or a token headed for plaintext HTTP fails at startup instead of on the first call. Configuration binding, named gateways, and health checks are in [Dependency injection](../guides/dependency-injection.md).

## Next

[Write your first program](first-program.md).
