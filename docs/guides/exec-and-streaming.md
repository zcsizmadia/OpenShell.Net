<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Exec and streaming

`client.Exec` (<xref:OpenShell.IExecClient>) runs commands in a ready sandbox, and `client.Watch` (<xref:OpenShell.ISandboxWatchClient>) streams its status, logs, and events. Commands are argument lists: nothing is parsed by a shell unless you run one (`["sh", "-c", "..."]`).

## Run a command

`RunAsync` buffers the output and returns an <xref:OpenShell.ExecResult> once the command exits. A non-zero exit code is a result (`Succeeded` is false), not an exception; failures to reach the sandbox throw <xref:OpenShell.OpenShellException>.

[!code-csharp[](../snippets/ExecAndStreaming.cs#Run)]

<xref:OpenShell.ExecOptions> sets the working directory, extra environment variables, standard input, a server-side execution timeout, a TTY, `NoLoginShell`, and an idempotency key (`RequestId`).

Exec, including `RunAsync`, is a streaming call, so `OpenShellClientOptions.DefaultTimeout` does not bound it. Bound a command with `ExecOptions.Timeout` (enforced by the gateway) or a `CancellationToken`; the same holds for watches. See [Retries and timeouts](retries.md#timeouts).

### Python

<xref:OpenShell.ExecPythonExtensions.ExecPythonAsync*> runs a Python source string with `python -c` (other SDKs send a pickled callable; .NET code cannot be pickled). `ExecPythonStreamAsync` streams instead.

[!code-csharp[](../snippets/ExecAndStreaming.cs#Python)]

## Stream output

`StreamAsync` yields <xref:OpenShell.ExecOutput> events as output arrives, and always ends with an <xref:OpenShell.ExecExit> event, so a failing command cannot be missed. A stream that ends without an exit event throws `OpenShellException` (`Unavailable`).

[!code-csharp[](../snippets/ExecAndStreaming.cs#Stream)]

`ExecOutput.Data` holds the raw bytes; `Text` decodes them as UTF-8.

## Interactive sessions

`InteractiveAsync` starts a command with a bidirectional stream and returns an <xref:OpenShell.ExecInteractiveSession>: write standard input, resize the terminal, close the input, and read output, concurrently. Read `ReadEventsAsync` once; `WaitForExitAsync` (or the `ExitCode` task) gives the exit code.

[!code-csharp[](../snippets/ExecAndStreaming.cs#Interactive)]

Pass `allocateTty: false` for a pipe without a terminal. `CancelAsync` cancels the command and releases the stream, as disposing the session does. Disposing does not wait for a stdin write that is blocked because the remote side is not reading: it cancels the write, which then throws `ObjectDisposedException` (or `OperationCanceledException` when the caller's own token cancelled it).

## Watch a sandbox

`Watch.WatchAsync` streams a sandbox's status changes, log lines, platform events (for example Kubernetes events), and draft-policy updates, as subclasses of <xref:OpenShell.WatchEvent>:

| Event | Carries | Enabled by |
| --- | --- | --- |
| <xref:OpenShell.SandboxUpdated> | the new <xref:OpenShell.Sandbox> | `FollowStatus` (on by default) |
| <xref:OpenShell.LogReceived> | a <xref:OpenShell.LogLine> | `FollowLogs` |
| <xref:OpenShell.PlatformEventReceived> | a <xref:OpenShell.PlatformEvent> | `FollowEvents` |
| <xref:OpenShell.DraftPolicyUpdated> | a <xref:OpenShell.DraftPolicyUpdate> | `WatchAsync` only, whenever the draft changes |
| <xref:OpenShell.WatchWarning> | a recoverable loss, such as dropped lines; the stream continues | always |

[!code-csharp[](../snippets/ExecAndStreaming.cs#Watch)]

The stream reconnects with exponential backoff when the gateway becomes unavailable and resumes from the last cursor it saw; `WatchOptions.MaxReconnectAttempts` bounds the consecutive attempts (null retries until cancelled). Each event carries an opaque `Cursor`. Save it to continue after a process restart:

[!code-csharp[](../snippets/ExecAndStreaming.cs#ResumeWatch)]

A cursor the gateway has already trimmed is terminal and throws; start a new watch without a cursor to recover.

## Logs

`GetLogsAsync` reads the buffered log lines once; `WatchLogsAsync` follows logs and platform events without status updates. Filter by source (`gateway`, `sandbox`), minimum level, and time.

[!code-csharp[](../snippets/ExecAndStreaming.cs#Logs)]

## Owner routing

On a multi-replica gateway, exec, interactive exec, port forwarding, and SSH streams go straight to the replica that holds the sandbox's supervisor session. See [Multi-replica gateways](multi-replica.md).
