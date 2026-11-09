<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Installation

## Requirements

- .NET 8 or later. The packages ship `net8.0` and `net10.0` builds; apps on .NET 9 use the `net8.0` build, and apps on .NET 11 use the `net10.0` build.
- An NVIDIA OpenShell gateway from the release the package was built from, or a compatible one. See [Versioning](../guides/versioning.md).
- Optional: the [OpenShell CLI](https://github.com/NVIDIA/OpenShell). When it has registered a gateway (`openshell gateway ...`), the SDK can reuse that registration and its credentials.

The packages are trimming and Native AOT compatible.

## Add the packages

```shell
dotnet add package OpenShell.Net
```

Add the others when you need them:

```shell
# AddOpenShell / AddOpenShellClient for Microsoft.Extensions.DependencyInjection, and a health check
dotnet add package OpenShell.Net.DependencyInjection

# FakeOpenShellClient for unit tests (add it to the test project)
dotnet add package OpenShell.Net.Testing
```

| Package | Assembly | Namespaces |
| --- | --- | --- |
| `OpenShell.Net` | `OpenShell.Sdk` | `OpenShell`, `OpenShell.Gateway`, `OpenShell.Oidc`, `OpenShell.Edge`, and the generated `Openshell.*.V1` wire types |
| `OpenShell.Net.DependencyInjection` | `OpenShell.Sdk.DependencyInjection` | `Microsoft.Extensions.DependencyInjection` (registration), `OpenShell.DependencyInjection` (options, factory) |
| `OpenShell.Net.Testing` | `OpenShell.Sdk.Testing` | `OpenShell.Testing` |

Most code needs a single `using OpenShell;`.

## Next

[Connect to a gateway](connecting.md).
