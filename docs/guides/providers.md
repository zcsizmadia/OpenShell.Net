<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Providers and profiles

A provider holds the credentials and configuration for an external service (a source-control host, an inference API, a data source). Attached to a sandbox, it makes those credentials available to the workload under the gateway's control. A provider profile describes a provider type: its credentials, how they are injected, the endpoints and binaries they are for, and how they are refreshed.

| Client | Area |
| --- | --- |
| `client.Providers` (<xref:OpenShell.IProviderClient>) | Create, get, list, update, delete, and ensure providers; export runtime credentials. |
| `client.SandboxProviders` (<xref:OpenShell.ISandboxProviderClient>) | Attach and detach providers, and wait until a sandbox has installed them. |
| `client.Providers.Profiles` (<xref:OpenShell.IProviderProfileClient>) | List, get, import, update, lint, and delete profiles. |
| `client.Providers.Refresh` (<xref:OpenShell.IProviderRefreshClient>) | Gateway-owned credential refresh: status, configure, rotate, delete. |

## Create and list providers

[!code-csharp[](../snippets/Providers.cs#CreateProvider)]

`EnsureAsync` creates the provider or updates it in place; `CreateAsync` fails with `AlreadyExists` when it exists. <xref:OpenShell.Provider>, <xref:OpenShell.ProviderCredentialValue>, and <xref:OpenShell.RefreshConfig> override `ToString` so secret values never print.

## Attach to a sandbox

Attaching returns a <xref:OpenShell.ProviderMutationReceipt> that names the exact authority the sandbox should install. `WaitAsync` waits, within a timeout of at most one hour, until the sandbox supervisor reports it:

[!code-csharp[](../snippets/Providers.cs#Attach)]

| <xref:OpenShell.ProviderWaitOutcome> | Meaning |
| --- | --- |
| `Complete` | The authority was installed (attach, update) or revoked (detach). |
| `TimedOut` | The deadline passed; `Status` is the last state observed. |
| `Terminal` | Installation failed or was withheld, or a later mutation superseded this one. It never turns into success. |

Detaching returns a receipt too; revocation is complete only when it reports `Revoked`. Providers can also be attached at creation through `SandboxSpec.Providers`.

## Update credentials

`UpdateAsync` returns one receipt per sandbox the provider is attached to; `WaitAllAsync` waits for all of them under one deadline.

[!code-csharp[](../snippets/Providers.cs#Update)]

## Export runtime credentials

<xref:OpenShell.IProviderClient.GetCredentialsAsync*> exports exactly the credential keys you name, or fails without a result. It requires a client authenticated with a trusted operator client certificate (`OU=operator`) and no bearer or edge token, and an explicit workspace. The gateway refreshes eligible missing or short-lived values first. Keep the values out of logs.

[!code-csharp[](../snippets/Providers.cs#Credentials)]

## Profiles

Profiles live in a workspace; `Profiles.Platform` is a view of the platform (global) profiles. `LintAsync` reports diagnostics without changing anything, `ImportAsync` adds profiles, and `UpdateAsync` replaces one by id, with a compare-and-swap on its resource version.

[!code-csharp[](../snippets/Providers.cs#Profiles)]

### Stable placeholders

Provider credentials reach the sandbox environment as opaque placeholders. By default each provider revision gets a new placeholder. A profile credential with `StablePlaceholder = true` keeps one identity-bound placeholder across value-only revisions, so the sandbox environment does not change when only the secret value does.

A stable placeholder needs at least one environment variable (`EnvVars`), and cannot be combined with gateway-managed refresh or a token grant. Because older gateways drop unknown fields silently, `ImportAsync`, `UpdateAsync`, and `LintAsync` first check that the gateway supports it and fail with `OpenShellErrorKind.Unimplemented` when it does not; <xref:OpenShell.LintProfilesResult.SupportsStablePlaceholder?displayProperty=nameWithType> reports support.

## Credential refresh

For credentials the gateway refreshes itself (for example OAuth2 tokens), `Refresh` reads the state of each credential, configures the strategy, forces a rotation, and removes the configuration.

[!code-csharp[](../snippets/Providers.cs#Refresh)]
