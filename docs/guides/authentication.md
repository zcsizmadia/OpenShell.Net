<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Authentication

A gateway authenticates callers with a bearer token (usually an OIDC access token), a client certificate (mTLS), Cloudflare Access in front of it, or nothing at all for local development. <xref:OpenShell.OpenShellClientOptions> carries the first through `Auth` (an <xref:OpenShell.IAuthProvider>) and the second through `Tls` (<xref:OpenShell.TlsOptions>); they combine freely.

If the OpenShell CLI has registered the gateway, <xref:OpenShell.Gateway.GatewayClient.Connect*?displayProperty=nameWithType> derives all of this from the registration, and you can skip most of this page. See [Gateway discovery](gateway-discovery.md).

## Providers at a glance

| Provider | Use |
| --- | --- |
| <xref:OpenShell.Auth.None?displayProperty=nameWithType> | No credentials, for example a local plaintext gateway. The default. |
| <xref:OpenShell.Auth.StaticToken*?displayProperty=nameWithType> | A fixed bearer token. |
| <xref:OpenShell.Auth.Bearer*?displayProperty=nameWithType> | A token fetched per request from a cache you manage. |
| <xref:OpenShell.RefreshableToken.AsAuthProvider*?displayProperty=nameWithType> | Single-flight renewal before expiry and after an `Unauthenticated` response. |
| <xref:OpenShell.Oidc.ClientCredentialsAuth> | OAuth2 client credentials for services, renewed automatically. |
| <xref:OpenShell.Auth.CloudflareAccess*?displayProperty=nameWithType> | Cloudflare Access header and cookie, optionally on top of another provider. |
| <xref:OpenShell.Auth.WithHeaders*?displayProperty=nameWithType> | Extra fixed headers on top of another provider. |

## Static and refreshable tokens

A token you already have:

[!code-csharp[](../snippets/Authentication.cs#StaticToken)]

A token your application caches and renews elsewhere: <xref:OpenShell.Auth.Bearer*> calls your function before each request.

[!code-csharp[](../snippets/Authentication.cs#BearerTokenSource)]

<xref:OpenShell.RefreshableToken> does the caching for you. It calls your <xref:OpenShell.TokenRefresher> shortly before the token expires (the `leeway`, one minute here), lets only one renewal run at a time, and renews once more when the gateway rejects a call with `Unauthenticated`; the client then repeats that call once. A failed renewal surfaces its error instead of serving an expired token.

[!code-csharp[](../snippets/Authentication.cs#RefreshableToken)]

## OIDC

The `OpenShell.Oidc` namespace implements the flows the OpenShell CLI uses. Every flow discovers the issuer (`/.well-known/openid-configuration`) and validates it, keeps secrets and tokens in memory, and never logs them: <xref:OpenShell.Oidc.OidcOptions> and <xref:OpenShell.Oidc.OidcTokens> print no secrets from `ToString`.

### Services: client credentials

For long-running automation, <xref:OpenShell.Oidc.ClientCredentialsAuth> exchanges a client id and secret for a token and renews it before expiry. `ClientSecretProvider` reads the secret on every renewal, so it can come from a vault and rotate; `ClientSecret` takes a fixed value instead.

[!code-csharp[](../snippets/Authentication.cs#ClientCredentials)]

<xref:OpenShell.Oidc.OidcLogin.ClientCredentialsAsync*?displayProperty=nameWithType> performs a single exchange when you manage renewal yourself.

### Users: browser login

<xref:OpenShell.Oidc.OidcLogin.LoginAsync*?displayProperty=nameWithType> runs the authorization-code flow, with PKCE when the issuer advertises it. It opens the system browser and receives the code on a loopback port (`CallbackPort`, or 8000 then 18000 when it is 0), and falls back to a paste-the-code flow when no port is free; set `UseKeyboardFlow` to use the paste flow directly. Interactive flows request `openid profile email` unless you set `Scopes`.

[!code-csharp[](../snippets/Authentication.cs#BrowserLogin)]

With a token cache directory (`TokenCacheDirectory`, or the registered gateway's directory when `Gateway` is set), a still-valid cached token issued for the same issuer and client id is returned without a prompt, and new tokens are written to `oidc_token.json` there. Set `InMemory` to skip the cache.

### Users without a browser: device code

<xref:OpenShell.Oidc.OidcLogin.DeviceLoginAsync*?displayProperty=nameWithType> runs the RFC 8628 device flow: the user opens a URL on another device and enters a code. `DisplayDeviceCode` receives both; the default prints them to the console.

[!code-csharp[](../snippets/Authentication.cs#DeviceLogin)]

### Signing in to a registered gateway

Set `OidcOptions.Gateway` to a registration loaded with <xref:OpenShell.Gateway.GatewayLocator>. The issuer, client id, audience, and scopes come from its `metadata.json`, and the tokens are cached in its `oidc_token.json` in the CLI's format, so the CLI and the SDK share one sign-in.

[!code-csharp[](../snippets/Authentication.cs#GatewayLogin)]

`oidc_token.json` holds `access_token`, `refresh_token`, `expires_at` (Unix seconds), `issuer`, and `client_id`. A token issued for another issuer or client id is never sent.

### Errors

OIDC failures throw <xref:OpenShell.Oidc.OidcException>, an `OpenShellException` whose <xref:OpenShell.Oidc.OidcException.Failure> names the step that failed (`Discovery`, `AuthCode`, `DeviceCode`, `ClientCredentials`, `Refresh`, `Timeout`, ...). Its `Kind` is `InvalidConfig` for missing or malformed settings, `DeadlineExceeded` for a flow that did not finish within `OidcOptions.Timeout`, and `Auth` otherwise.

## Mutual TLS

The default local gateway requires a client certificate. Pass it, and a private CA when the gateway certificate is not signed by a public one, through <xref:OpenShell.TlsOptions>:

[!code-csharp[](../snippets/Authentication.cs#MutualTls)]

A registered `mtls` gateway loads `mtls/ca.crt`, `mtls/tls.crt`, and `mtls/tls.key` from its credentials directory automatically.

## Cloudflare Access and the edge tunnel

A gateway behind Cloudflare Access needs the Access token on every request, but the edge authenticates only GET requests, so gRPC calls go through a WebSocket tunnel. <xref:OpenShell.Edge.EdgeTunnelProxy> starts a loopback proxy that carries the gRPC traffic over that tunnel:

[!code-csharp[](../snippets/Authentication.cs#EdgeTunnel)]

For a registered `cloudflare_jwt` gateway, <xref:OpenShell.Gateway.GatewayClient> starts the proxy, points the client at it, and disposes it with the client. `EdgeTunnelMode.Never` sends the Access header and cookie on direct gRPC calls instead, and supplying your own `Auth` in <xref:OpenShell.Gateway.GatewayConnectOptions> opts out as well.

[!code-csharp[](../snippets/Authentication.cs#EdgeRegistered)]

Edge tokens must use cookie-safe JWT characters.

## Extra headers

[!code-csharp[](../snippets/Authentication.cs#ExtraHeaders)]

## Plaintext and development settings

The SDK refuses to send credentials over plaintext `http://` to a host other than loopback: the client throws <xref:OpenShell.OpenShellException> with `OpenShellErrorKind.InvalidConfig` when it is created. Loopback addresses are allowed, which covers a local development gateway.

[!code-csharp[](../snippets/Authentication.cs#InsecureLocal)]

When you accept that the credential crosses the network in clear text, opt in with `AllowInsecureAuth`:

[!code-csharp[](../snippets/Authentication.cs#PlaintextOverride)]

Other checks made when the client is created: a client certificate must include its private key, and `TlsOptions.InsecureSkipVerify` disables server certificate validation, so use it only for local development. <xref:OpenShell.OpenShellClientOptions.Validate*> runs the same checks without creating a client.
