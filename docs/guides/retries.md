<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Retries and timeouts

## What is retried

Unary reads (`Get*`, `List*`, and `Health`) that fail with `Unavailable` are retried, up to four attempts in all, with exponential backoff and jitter. Writes are never retried by default, because a lost response could repeat a side effect. Retries stop at the call's deadline and when the caller cancels. Streaming calls are not retried: `Watch.WatchAsync` reconnects on its own, and a broken exec or forward stream surfaces its error.

<xref:OpenShell.RetryPolicy> controls this through `OpenShellClientOptions.Retry`:

[!code-csharp[](../snippets/ClientBehavior.cs#Retries)]

| Setting | Default |
| --- | --- |
| `MaxAttempts` | 4 (the first attempt included; 1 disables retries) |
| `InitialBackoff` | 100 ms |
| `MaxBackoff` | 5 s |
| `BackoffMultiplier` | 2 |
| `Jitter` | 0.2 (the fraction of each delay that is randomized) |
| `RetryableStatusCodes` | `Unavailable` |
| `IsIdempotent` | reads: `Get*`, `List*`, `Health` |

`OpenShellClientOptions.DefaultTimeout` sets a deadline for unary calls that do not set their own; null (the default) means none.

## Server-suggested delays and `MUTATION_LOCK_TIMEOUT`

When the gateway attaches a retry delay (`google.rpc.RetryInfo`), the next attempt waits at least that long. A delay longer than `MaxBackoff` ends the retries and surfaces the error, with the delay in <xref:OpenShell.OpenShellException.RetryDelay?displayProperty=nameWithType>.

The gateway does this, for example, when a mutation waited 10 seconds for the lock of a sandbox or workspace that another mutation held: the call fails with `Unavailable`, an <xref:OpenShell.ErrorInfo> whose `Reason` is `MUTATION_LOCK_TIMEOUT`, and a retry delay. Because the failed call is a write, the SDK does not repeat it; decide in your code whether repeating it is safe:

[!code-csharp[](../snippets/ClientBehavior.cs#MutationLock)]

`RetryDelay` says when to try again, not whether repeating a mutation is safe.

## Retrying writes

To retry a write you have made safe to repeat, for example one that always carries a request id, widen `IsIdempotent`. It receives the gRPC method name, such as `GetSandbox` or `CreateSandbox`. Use <xref:OpenShell.RetryPolicy.None?displayProperty=nameWithType> to turn retries off.

[!code-csharp[](../snippets/ClientBehavior.cs#RetryWrites)]

Most mutations accept a request id (`RequestId` on the options records, or a `requestId` parameter): the gateway answers a repeated request with the original result, which makes a retry safe.

## Cancellation

Every method takes a `CancellationToken`. Cancelling it throws `OperationCanceledException` (not `OpenShellException`), also in the middle of retries and waits.

[!code-csharp[](../snippets/ClientBehavior.cs#Cancellation)]
