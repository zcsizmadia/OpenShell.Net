<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Dependency injection

The `OpenShell.Net.DependencyInjection` package registers OpenShell clients with `Microsoft.Extensions.DependencyInjection`, for ASP.NET Core and Generic Host apps.

```shell
dotnet add package OpenShell.Net.DependencyInjection
```

## Register from configuration

`AddOpenShell` binds <xref:OpenShell.DependencyInjection.OpenShellOptions> from a configuration section and registers a singleton <xref:OpenShell.IOpenShellClient>, plus its per-area clients (`IHealthClient`, `ISandboxClient`, `IExecClient`, `IWorkspaceClient`, `ISandboxTemplateClient`, `IProviderClient`, `IConfigClient`, `IPolicyClient`, `ISandboxWatchClient`). The options are validated when the host starts, so a bad address, an unreadable certificate, or a token headed for plaintext HTTP fails at startup instead of on the first call.

[!code-csharp[](../snippets/DependencyInjection.cs#WebApp)]

[!code-json[](../snippets/appsettings.json)]

| Key | Meaning |
| --- | --- |
| `Address` | The gateway URL. Required. |
| `Workspace` | The default workspace (`default`). |
| `Token` | A static bearer token. Keep it in user secrets, environment variables, or a vault, not in the JSON file. |
| `CaCertificatePath` | A PEM file of CA certificates that the gateway certificate must chain to, instead of the system roots. |
| `ClientCertificatePath`, `ClientKeyPath` | A PEM client certificate and key for mTLS. |
| `InsecureSkipVerify` | Skip server certificate validation (development only). |
| `AllowInsecureAuth` | Allow credentials over plaintext HTTP to a non-loopback host. |
| `DefaultTimeout` | Deadline for unary calls, as a `TimeSpan` (`00:00:30`). Streaming calls such as exec, file transfers, watches, and forwards are not bounded by it; see [Retries and timeouts](retries.md#timeouts). |
| `Retry` | `MaxAttempts`, `InitialBackoff`, `MaxBackoff`, `BackoffMultiplier`, `Jitter`, `RetryableStatusCodes`; unset values keep the defaults. |
| `RouteToOwnerReplica` | [Owner routing](multi-replica.md) (`true`). |

The client is created on first use and disposed with the container. Only a client that was built successfully is kept: when the build fails, for example because a certificate file is not there yet, that caller gets the error and the next resolution tries again. Concurrent first resolutions share one client per name. `services.AddHealthChecks().AddOpenShellHealthCheck()` adds a health check that calls the gateway health RPC; pass `gatewayName` to check a named registration.

## Configure in code

[!code-csharp[](../snippets/DependencyInjection.cs#InCode)]

`OpenShellOptions.Auth` and `OpenShellOptions.Tls` take any <xref:OpenShell.IAuthProvider> or <xref:OpenShell.TlsOptions> and win over `Token` and the certificate paths. The `AddOpenShell(name, (provider, options) => ...)` overload configures them from other services. The client does not dispose an auth provider you supply, so register a disposable one, such as <xref:OpenShell.Oidc.ClientCredentialsAuth>, as a container singleton, or dispose it yourself.

## The fluent builder

`AddOpenShellClient` returns an <xref:Microsoft.Extensions.DependencyInjection.IOpenShellClientBuilder> with a method for each setting. A name registers a separate, keyed client for each gateway:

[!code-csharp[](../snippets/DependencyInjection.cs#FluentBuilder)]

`WithClientCredentials` registers an OIDC <xref:OpenShell.Oidc.ClientCredentialsAuth> as a container singleton, so the container disposes it. Other methods: `WithBearerToken`, `WithAuth`, `WithCaCertificate`, `WithClientCertificate`, `WithTls`, `WithInsecureTls`, `AllowInsecureAuth`, `WithTimeout`, `WithRetry`, `WithoutRetry`, and `BindConfiguration`.

[!code-csharp[](../snippets/DependencyInjection.cs#AuthFromServices)]

## Named clients

A named client is a keyed `IOpenShellClient`. Inject it with `[FromKeyedServices("name")]`, or resolve it at run time from <xref:OpenShell.DependencyInjection.IOpenShellClientFactory>. The per-area clients are registered for the default (unnamed) gateway only.

[!code-csharp[](../snippets/DependencyInjection.cs#Keyed)]

## Logging

The registered clients use the container's `ILoggerFactory`; see [Observability](observability.md).
