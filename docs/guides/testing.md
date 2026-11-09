<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Testing with the fake gateway

`OpenShell.Net.Testing` provides <xref:OpenShell.Testing.FakeOpenShellClient>, an in-memory gateway that implements every <xref:OpenShell.IOpenShellClient> area with the same error kinds as the real client. It keeps workspace-scoped state, paginates with real page tokens, advances sandbox phases on a `TimeProvider` you control, and streams watch events with cursors. <xref:OpenShell.Testing.FakeGateway> (`fake.Gateway`) scripts command results, injects failures, and records the calls.

```shell
dotnet add package OpenShell.Net.Testing
```

All the examples on this page run in this documentation's build, against the real fake.

## Write code against the interfaces

Depend on <xref:OpenShell.IOpenShellClient>, or on a per-area interface such as <xref:OpenShell.ISandboxClient> or <xref:OpenShell.IExecClient>, so a test can pass the fake:

[!code-csharp[](../snippets/Testing.cs#CodeUnderTest)]

## Script command results

`OnExec` registers a handler that sees each command (<xref:OpenShell.Testing.FakeExecInvocation>: workspace, sandbox, argument list, options, and `CommandLine`) and returns a <xref:OpenShell.Testing.FakeExecScript>, or null to pass. Handlers registered later run first; commands nobody scripts exit with code 0 and no output. `Gateway.Calls` lists every operation in order (<xref:OpenShell.Testing.FakeCall>: sequence, operation name such as `Sandboxes.Create` or `Exec.Run`, workspace, and target).

[!code-csharp[](../snippets/Testing.cs#ScriptExec)]

## Control time

Sandboxes move through their phases on the fake's `TimeProvider`. With the default delays of zero they are `Ready` at once; set `ProvisioningDelay`, `TransitionDelay`, or `DeletionDelay` in <xref:OpenShell.Testing.FakeGatewayOptions> and pass a `FakeTimeProvider` (from the `Microsoft.Extensions.TimeProvider.Testing` package) to test what happens in between. `SetPhase` forces a phase directly.

[!code-csharp[](../snippets/Testing.cs#TimeControl)]

Waits use the same clock: a `WaitReadyAsync` with a `FakeTimeProvider` completes when you advance the clock past the delay, and times out when you advance it past the wait's timeout.

## Simulate failures

`FailNext` makes the next matching calls fail with the exception you give. The operation is an exact name (`Sandboxes.Get`), a prefix pattern (`Sandboxes.*`), or `*`; the call is recorded before it fails. `ClearFailures` removes pending failures.

[!code-csharp[](../snippets/Testing.cs#Failures)]

Other switches in <xref:OpenShell.Testing.FakeGatewayOptions>: `SupportsSuspendResume` and `SandboxUpdatesEnabled` (to test the `Conflict` paths), `AutoCreateWorkspaces`, `DefaultPageSize`, `RootfsMaxBytes`, `GatewayVersion`, and `BuiltinProfiles`.

## Logs, events, and watches

`EmitLog` and `EmitEvent` add log lines and platform events to a sandbox; they reach `GetLogsAsync` and every watcher, with cursors.

[!code-csharp[](../snippets/Testing.cs#Watch)]

## Port forwarding and SSH

`OnForward` plays the sandbox side of TCP forwards and SSH tunnels (<xref:OpenShell.Testing.FakeForwardRequest.IsSsh>): it reads what the client sent and writes the answer. The default handler echoes. `ListenAsync` on the fake records the listener without opening a socket.

[!code-csharp[](../snippets/Testing.cs#Forward)]

`Gateway.InteractiveInput` records what interactive sessions sent: stdin, resizes, and end of input.

## Files

`client.Files` on the fake reads and writes an in-memory file system per sandbox, which `PutFile`, `ReadFile`, and `ListFiles` on the gateway seed and inspect.

[!code-csharp[](../snippets/Testing.cs#Files)]

## Several workspaces

Clients created over one <xref:OpenShell.Testing.FakeGateway> share its state; each is bound to a workspace, as `InWorkspace` views are.

[!code-csharp[](../snippets/Testing.cs#SharedGateway)]

## Endpoint statuses

`ReportEndpointStatuses` sets what the sandbox supervisor reports for its endpoints, for testing token-grant diagnostics:

[!code-csharp[](../snippets/Testing.cs#EndpointStatuses)]

## Differences from a real gateway

The fake auto-creates unknown workspaces by default (`AutoCreateWorkspaces`), lets a global setting override a sandbox value, fails deleting a provider that a sandbox uses with `Conflict`, returns redacted provider credentials from listings, and runs no commands: exec results come from your `OnExec` handlers, and files live in memory instead of being sent with `tar`.
