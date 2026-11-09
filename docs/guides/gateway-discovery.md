<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Gateway discovery

The OpenShell CLI keeps a registration for every gateway it knows: the endpoint, the authentication mode, and the credentials. The `OpenShell.Gateway` namespace reads the same files with the same rules as the CLI, so an application can connect to whatever gateway the user selected, without its own configuration.

## Connect

<xref:OpenShell.Gateway.GatewayClient.Connect*?displayProperty=nameWithType> loads a registration, builds the authentication and TLS settings from it, and returns an <xref:OpenShell.OpenShellClient>. Pass no name for the active gateway.

[!code-csharp[](../snippets/GettingStarted.cs#ConnectToRegisteredGateway)]

<xref:OpenShell.Gateway.GatewayConnectOptions> adjusts the result: the default workspace and timeout, your own `Auth` or `Tls` in place of the registered ones, whether expired OIDC tokens are renewed (`AutoRefresh`) and written back (`WriteBack`), and how a Cloudflare Access gateway is reached (`EdgeTunnel`).

[!code-csharp[](../snippets/Discovery.cs#ConnectOptions)]

By authentication mode (`auth_mode` in `metadata.json`), the client gets:

| `auth_mode` | <xref:OpenShell.Gateway.GatewayAuthMode> | Authentication |
| --- | --- | --- |
| missing or `none` | `None` | No credentials. |
| `plaintext` | `Plaintext` | No credentials, plaintext HTTP. |
| `mtls` | `Mtls` | The client certificate in `mtls/tls.crt` and `mtls/tls.key`; fails when they are missing. |
| `oidc` | `Oidc` | The access token in `oidc_token.json`, renewed with its refresh token and written back. |
| `cloudflare_jwt` | `CloudflareJwt` | The Access token in `edge_token` (or the legacy `cf_token`), through a local edge tunnel. |

For an `https` endpoint, the `mtls` directory also supplies the trust: the CA in `mtls/ca.crt` when present, otherwise the system roots, and the client certificate when present.

## Where registrations live

| Location | Default | Holds |
| --- | --- | --- |
| User configuration directory | `$XDG_CONFIG_HOME/openshell`; otherwise `%APPDATA%\openshell` on Windows and `~/.config/openshell` elsewhere | `active_gateway`, and `gateways/<name>/` with `metadata.json` and the credentials |
| System configuration directory | `$OPENSHELL_SYSTEM_GATEWAY_DIR` when it is an absolute path; otherwise `/etc/openshell` on Linux and macOS, and none on Windows | `active_gateway`, and `gateways/<name>/metadata.json` |

The rules, as in the CLI:

- **Active gateway.** `$OPENSHELL_GATEWAY`, then the user `active_gateway` file, then the system `active_gateway` file. A missing, empty, or invalid file falls through to the next source.
- **Registrations.** A user entry shadows a system registration of the same name only when it has its own `metadata.json`. Only `active_gateway` and `metadata.json` fall back to the system directory.
- **Credentials.** The `mtls` bundle, `oidc_token.json`, and `edge_token` are always read from and written to the user gateway directory, <xref:OpenShell.Gateway.GatewayMetadata.StateDirectory?displayProperty=nameWithType>, also for a system registration. <xref:OpenShell.Gateway.GatewayMetadata.Directory?displayProperty=nameWithType> is where `metadata.json` was found.
- **Names.** ASCII letters, digits, `-`, `_`, and `.`, so host-derived names such as `gw.example.com` and `192.0.2.1` work. `.` and `..`, a leading or trailing dot, and Windows device names such as `CON` or `COM1.example.com` are rejected on every platform.

## Inspect registrations

<xref:OpenShell.Gateway.GatewayLocator> exposes the same lookups:

[!code-csharp[](../snippets/Discovery.cs#ListGateways)]

`List()` sorts by name and skips registrations whose metadata cannot be read; `Load(name)` throws `OpenShellException` with `NotFound` or `InvalidConfig` when it cannot load one.

## The token file

`oidc_token.json` uses the CLI format, so one `openshell gateway login` serves the CLI and the SDK:

```json
{
  "access_token": "<access token>",
  "refresh_token": "<refresh token>",
  "expires_at": 1793000000,
  "issuer": "https://idp.example.com/realms/openshell",
  "client_id": "openshell-cli"
}
```

`expires_at` is in Unix seconds. A token issued for another issuer or client id than the registration names is never sent. To sign in from .NET, see [Signing in to a registered gateway](authentication.md#signing-in-to-a-registered-gateway).

## Build the options yourself

<xref:OpenShell.Gateway.GatewayClient.CreateOptions*?displayProperty=nameWithType> returns the <xref:OpenShell.OpenShellClientOptions> that `Connect` would use, for settings `GatewayConnectOptions` does not cover. It does not start an edge tunnel.

[!code-csharp[](../snippets/Discovery.cs#CustomOptions)]

## Other directories

<xref:OpenShell.Gateway.GatewayLocatorOptions> points the locator at other directories, and replaces the environment lookup, for example in tests:

[!code-csharp[](../snippets/Discovery.cs#CustomLocator)]
