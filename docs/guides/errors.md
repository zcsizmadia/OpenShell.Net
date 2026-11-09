<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Errors

SDK operations report failures as <xref:OpenShell.OpenShellException>, apart from the .NET exceptions listed after the table. Switch on its `Kind`, an <xref:OpenShell.OpenShellErrorKind>. Gateway errors map from the gRPC status; the last four kinds come from the client itself.

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
| `Tls` | TLS material could not be loaded: <xref:OpenShell.Gateway.GatewayClient> raises it for a registered gateway's mTLS bundle. | (client side) |
| `Connect` | Reserved; the SDK does not currently raise it. | (client side) |
| `Auth` | Credentials could not be obtained or refreshed, for example an OIDC failure. | (client side) |

A gateway that cannot be reached, and a connection or TLS handshake that fails (for example a certificate that does not chain to the trusted roots), surface as `Unavailable`, the status gRPC reports for them; they are not `Tls` or `Connect`. Certificate files named in the [dependency injection](dependency-injection.md) options are checked when the host starts, and a file that cannot be loaded fails that validation.

The shortcuts `IsNotFound`, `IsAlreadyExists`, `IsConflict`, and `IsTransient` (`Unavailable` or `DeadlineExceeded`) cover the common cases, and `GrpcStatusCode` exposes the underlying gRPC status when there is one.

[!code-csharp[](../snippets/ClientBehavior.cs#Errors)]

Cancelling a call through its own `CancellationToken` throws `OperationCanceledException`, not `OpenShellException`. Arguments that are invalid before anything is sent (a null or empty name, an empty command) throw `ArgumentException`. File transfers also throw `InvalidDataException` for an archive they refuse, and `FileNotFoundException` or `DirectoryNotFoundException` for a missing local source; see [Files and rootfs](files-and-rootfs.md).

## Error details

When the gateway attaches structured details (`google.rpc.Status`), the exception exposes them:

- <xref:OpenShell.OpenShellException.ErrorInfo>: a stable `Reason`, a `Domain`, and metadata. Match on `Reason` rather than on the message.
- <xref:OpenShell.OpenShellException.FieldViolations>: the request fields that were rejected, and why.
- <xref:OpenShell.OpenShellException.RetryDelay>: the delay the gateway suggested before trying again. It says nothing about whether repeating a mutation is safe; see [Retries](retries.md).

[!code-csharp[](../snippets/ClientBehavior.cs#ErrorDetails)]

The SDK ignores details whose envelope does not match the call's status, so a mismatched trailer cannot rewrite the error.

## Errors in raw calls

Calls made through `OpenShellClient.Raw` throw the gRPC `RpcException` instead; see [Raw gRPC access](raw-grpc.md).
