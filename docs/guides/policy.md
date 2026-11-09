<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Policy and configuration

A sandbox policy sets what the workload may do: filesystem and process rules fixed at creation, and network rules that can change while it runs. When the workload is denied network access, the gateway can propose rules as a *draft*, in chunks, for a person (or your code) to review.

The policy types are the generated wire types from `openshell.sandbox.v1` (`Openshell.Sandbox.V1.SandboxPolicy`, `NetworkPolicyRule`, ...), as in the TypeScript SDK; there is no policy builder.

## Review draft chunks

`client.Policy` (<xref:OpenShell.IPolicyClient>) reads the draft and approves, rejects, edits, or undoes its chunks. Each <xref:OpenShell.DraftChunk> carries the proposed rule, the rationale, security notes, a confidence, and a `ReviewToken` that pins the inputs you reviewed: approving fails with `Conflict` when they changed since.

[!code-csharp[](../snippets/Policy.cs#ReviewDraft)]

A rejection reason is surfaced back to the agent in the sandbox. `EditChunkAsync` replaces the proposed rule of a pending chunk before approval, `UndoChunkAsync` reverses an approval (removing the merged rule from the active policy), and `ClearChunksAsync` drops every pending chunk. A [watch](exec-and-streaming.md#watch-a-sandbox) reports draft changes as `DraftPolicyUpdated` events.

## Approve in bulk

[!code-csharp[](../snippets/Policy.cs#ApproveAll)]

## Revisions

Every policy change creates a revision. `GetStatusAsync` reports the active version and the load status of a revision (`PolicyStatusOptions.Version`, or the latest); `ListRevisions` pages through them, newest first.

[!code-csharp[](../snippets/Policy.cs#Revisions)]

## Set the policy

`client.Config` (<xref:OpenShell.IConfigClient>) writes the policy directly. At sandbox scope only the network policies may change; the static parts (filesystem, landlock, process) must match the create-time policy. `SetPolicyOptions.Wait` polls until the sandbox reports the new policy hash.

[!code-csharp[](../snippets/Policy.cs#SetPolicy)]

`MergePolicyAsync` applies incremental merge operations (`Openshell.V1.PolicyMergeOperation`) instead of a whole policy, and `SetGlobalPolicyAsync` replaces the gateway-global policy. Updates accept annotations, stored with the revision (never put secrets in them), and a request id for idempotency.

## Settings

Settings are typed key-value pairs at sandbox or gateway-global scope. <xref:OpenShell.SettingValue> converts implicitly from `string`, `bool`, and `long`; `SettingValue.FromBytes` takes bytes.

[!code-csharp[](../snippets/Policy.cs#Settings)]

`GetSandboxAsync` returns the effective settings with the scope each value comes from, plus the policy, its version and hash, and the revisions of the sandbox configuration. `GetGatewayAsync` returns the global settings. Only global settings can be deleted.
