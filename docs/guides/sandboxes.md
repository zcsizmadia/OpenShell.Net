<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Sandboxes

`client.Sandboxes` (<xref:OpenShell.ISandboxClient>) creates and manages sandboxes in the client's workspace. A <xref:OpenShell.Sandbox> is an immutable snapshot: read the current state with `GetAsync`, a wait, or a [watch](exec-and-streaming.md#watch-a-sandbox).

## Lifecycle

<xref:OpenShell.Sandbox.Phase?displayProperty=nameWithType> is a <xref:OpenShell.SandboxPhase>:

| Phase | Meaning | Reached by |
| --- | --- | --- |
| `Provisioning` | Resources are being created. | `CreateAsync` |
| `Ready` | Running and usable. | provisioning, `StartAsync`, `ResumeAsync` |
| `Stopping`, `Stopped` | Being stopped; stopped and can be started again. | `StopAsync` |
| `Starting` | Being started. | `StartAsync`, `ResumeAsync` |
| `Suspending`, `Suspended` | Being frozen; frozen with its memory retained. | `SuspendAsync` |
| `Completed` | The main process finished and will not restart. | the workload |
| `Error` | Failed. | the workload or the driver |
| `Deleting` | Being deleted. | `DeleteAsync` |
| `Unknown`, `Unspecified` | The gateway cannot tell, or did not report. | |

## Create

`CreateAsync` returns as soon as the gateway accepts the sandbox, usually in `Provisioning`. <xref:OpenShell.SandboxSpec> describes the workload (image, command, environment, providers, policy, restart policy, services to expose); <xref:OpenShell.CreateSandboxOptions> adds labels, annotations, and an idempotency key.

[!code-csharp[](../snippets/Sandboxes.cs#Create)]

Pass `null` as the name to let the gateway generate one, and read it from the returned `Sandbox.Name`. `SandboxSpec.Template` (<xref:OpenShell.SandboxTemplateSpec>) sets driver-level details such as the runtime class, resources, and driver configuration; `SandboxSpec.Policy` is the generated `SandboxPolicy` wire type (see [Policy](policy.md)); `SandboxSpec.RawSpec` adjusts the wire request for fields the curated shape does not cover (see [Raw gRPC access](raw-grpc.md)).

## Wait

The wait helpers poll `GetAsync` with backoff until the sandbox reaches a phase, and throw <xref:OpenShell.OpenShellException> with `DeadlineExceeded` when the <xref:OpenShell.WaitOptions> timeout (two minutes by default) runs out.

[!code-csharp[](../snippets/Sandboxes.cs#Wait)]

| Wait | Ends when | Fails at once when |
| --- | --- | --- |
| `WaitReadyAsync` | `Ready` or `Completed` | `Error`, `Stopped`, `Suspending`, or `Suspended` |
| `WaitStoppedAsync` | `Stopped` | `Error`, `Suspending`, or `Suspended` |
| `WaitSuspendedAsync` | `Suspended` | `Error`, or any phase other than `Suspending` (the suspension did not happen or was undone) |
| `WaitDeletedAsync` | the sandbox is gone, or its id differs from `ExpectedSandboxId` | |

Cancelling a wait through its `CancellationToken` throws `OperationCanceledException`.

## Read and list

[!code-csharp[](../snippets/Sandboxes.cs#GetAndList)]

`List` returns a lazy <xref:OpenShell.Pager`1>; see [Workspaces and pagination](workspaces-and-pagination.md).

## Stop and start

[!code-csharp[](../snippets/Sandboxes.cs#StopStart)]

## Suspend and resume

A ready sandbox can be suspended: it is frozen with its memory retained and resumed later from that state. The compute driver must support it; otherwise both calls fail with `OpenShellErrorKind.Conflict` (`FAILED_PRECONDITION`).

[!code-csharp[](../snippets/Sandboxes.cs#SuspendResume)]

Repeating a suspend while suspending or suspended, or a resume while it is starting, is safe. `ResumeSandboxOptions.RestartIfRestoreFails` boots the workload cleanly when the memory state cannot be restored, instead of failing. `StopAsync` and `StartAsync` also work on a suspended sandbox, but they discard the retained state. `WaitReadyAsync` and `WaitStoppedAsync` fail fast on a suspended sandbox instead of waiting for a phase it will not reach on its own.

## Update a stopped sandbox

A gateway with `[openshell.gateway] enable_sandbox_updates = true` can change a stopped sandbox in place. Pass the resource version you read (compare-and-swap), and set only what changes: a null property in <xref:OpenShell.SandboxUpdate> is left alone, while an empty collection replaces the value with nothing. The update does not start the sandbox.

[!code-csharp[](../snippets/Sandboxes.cs#Update)]

| Failure | Kind |
| --- | --- |
| The resource version is stale | `Conflict` (`ABORTED`) |
| The sandbox is running, or the gateway has the feature off | `Conflict` (`FAILED_PRECONDITION`) |
| Image, command, or environment change on a compute driver that does not support it (only Kubernetes at first) | `Unimplemented` |

Label and annotation changes work on every driver; gateway-owned keys are preserved and system keys are rejected. An empty `Command` runs the image's default shell, and an empty `Environment` clears the sandbox-level overrides (template defaults remain). With `UpdateSandboxOptions.RequestId` the gateway admits a retried update at most once. If an update fails part-way, it stays pending and blocks start and stop; finish it with `ResumePendingUpdateAsync`:

[!code-csharp[](../snippets/Sandboxes.cs#ResumePendingUpdate)]

## Endpoint statuses

`Sandbox.EndpointStatuses` reports what the sandbox supervisor last observed for each declared endpoint: host, ports, path, the latest <xref:OpenShell.EndpointResult>, and when it was reported. When a gateway-managed token grant fails, `EndpointStatus.LastFailure` and `Sandbox.LastTokenGrantFailure` carry the classified cause (<xref:OpenShell.EndpointFailure>): a stable `Code` and `Stage` (lower_snake_case), the `RecoveryAction` to take, the provider and credential key, the upstream HTTP status, and the `x-openshell-request-id` the workload saw, for correlating with gateway logs.

[!code-csharp[](../snippets/Sandboxes.cs#EndpointStatus)]

## Delete

`DeleteAsync` returns a typed <xref:OpenShell.DeletionResult>, not a boolean:

| `Outcome` | Meaning | `IsComplete` |
| --- | --- | --- |
| `Completed` | Deleted. | true |
| `AlreadyAbsent` | It did not exist, and `DeleteOptions.AllowMissing` was set. | true |
| `Accepted` | Cleanup is still pending; wait with `WaitDeletedAsync`. | false |
| `Unspecified` | The gateway did not say; it does not establish completion. | false |

A missing sandbox is a `NotFound` error unless you pass `AllowMissing = true`. The same result type is returned by every delete in the SDK.

[!code-csharp[](../snippets/Sandboxes.cs#Delete)]

## Templates

`client.Templates` (<xref:OpenShell.ISandboxTemplateClient>) stores reusable workload templates (<xref:OpenShell.SandboxWorkloadTemplate>: image, environment, resources, driver configuration, startup service level). `CreateFromTemplateAsync` creates a sandbox from one, and an optional spec overrides its fields.

[!code-csharp[](../snippets/Sandboxes.cs#Templates)]
