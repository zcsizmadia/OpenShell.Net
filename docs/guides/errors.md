<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Errors

Every SDK operation throws <xref:OpenShell.OpenShellException>. Switch on its `Kind`, an <xref:OpenShell.OpenShellErrorKind>. Gateway errors map from the gRPC status; the last four kinds come from the client itself.

| Kind | Meaning | gRPC status |
| --- | --- | --- |
| `NotFound` | The resource does not exist. | `NOT_FOUND` |
| `AlreadyExists` | The resource already exists. | `ALREADY_EXISTS` |
| `PermissionDenied` | The caller is authenticated but not allowed. | `PERMISSION_DENIED` |
| `Unauthenticated` | Credentials are missing or invalid. | `UNAUTHENTICATED` |
| `InvalidArgument` | A request argument was rejected. | `INVALID_ARGUMENT`, `OUT_OF_RANGE` |
| `Conflict` | Optimistic concurrency or an invalid state transition. | `ABORTED`, `FAILED_PRECONDITION` |
| `Unavailable` | The gateway is temporarily unavailable. | `UNAVAILABLE` |
| `DeadlineExceeded` | The call or a wait exceeded its deadline. | `DEADLINE_EXCEEDED` |
| `Cancelled` | The call was cancelled by the gateway. | `CANCELLED` |
| `Unimplemented` | The gateway does not implement the operation (or a field it needs). | `UNIMPLEMENTED` |
| `Internal` | The gateway reported an internal error. | `INTERNAL`, `DATA_LOSS` |
| `Unknown` | Anything else. | `UNKNOWN`, `RESOURCE_EXHAUSTED`, and other codes |
| `InvalidConfig` | The client was configured inconsistently or unsafely. | (client side) |
| `Tls` | TLS material could not be loaded, for example a registered gateway's mTLS bundle. | (client side) |
| `Connect` | The gateway could not be reached. A connection failure during a call surfaces as `Unavailable`. | (client side) |
| `Auth` | Credentials could not be obtained or refreshed, for example an OIDC failure. | (client side) |

The shortcuts `IsNotFound`, `IsAlreadyExists`, `IsConflict`, and `IsTransient` (`Unavailable` or `DeadlineExceeded`) cover the common cases, and `GrpcStatusCode` exposes the underlying gRPC status when there is one.

[!code-csharp[](../snippets/ClientBehavior.cs#Errors)]

Cancelling a call through its own `CancellationToken` throws `OperationCanceledException`, not `OpenShellException`. Arguments that are invalid before anything is sent (a null or empty name, an empty command) throw `ArgumentException`.

## Error details

When the gateway attaches structured details (`google.rpc.Status`), the exception exposes them:

- <xref:OpenShell.OpenShellException.ErrorInfo>: a stable `Reason`, a `Domain`, and metadata. Match on `Reason` rather than on the message.
- <xref:OpenShell.OpenShellException.FieldViolations>: the request fields that were rejected, and why.
- <xref:OpenShell.OpenShellException.RetryDelay>: the delay the gateway suggested before trying again. It says nothing about whether repeating a mutation is safe; see [Retries](retries.md).

[!code-csharp[](../snippets/ClientBehavior.cs#ErrorDetails)]

The SDK ignores details whose envelope does not match the call's status, so a mismatched trailer cannot rewrite the error.

## Errors in raw calls

Calls made through `OpenShellClient.Raw` throw the gRPC `RpcException` instead; see [Raw gRPC access](raw-grpc.md).
