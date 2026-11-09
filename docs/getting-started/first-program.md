<!--
SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
SPDX-License-Identifier: Apache-2.0
-->

# Your first program

This program creates a sandbox, waits until it is ready, runs a command in it, prints the output, and deletes the sandbox again.

## 1. Create the project

```shell
dotnet new console -n HelloOpenShell
cd HelloOpenShell
dotnet add package OpenShell.Net
```

## 2. Write the program

Replace the contents of `Program.cs`. Start with the namespaces and the client; this connects to the active gateway registered by the OpenShell CLI. To use an explicit address and token instead, create the client as shown in [Connect to a gateway](connecting.md#an-explicit-endpoint).

```csharp
using OpenShell;
using OpenShell.Gateway;
```

[!code-csharp[](../snippets/GettingStarted.cs#FirstProgramConnect)]

Then add the program itself below it:

[!code-csharp[](../snippets/GettingStarted.cs#FirstProgram)]

Use an image your gateway can pull.

## 3. Run it

```shell
dotnet run
```

```text
exit 0: hello from OpenShell
```

## What happened

1. <xref:OpenShell.ISandboxClient.CreateAsync*> asked the gateway for a sandbox and returned as soon as the gateway accepted it, in the `Provisioning` phase.
2. <xref:OpenShell.ISandboxClient.WaitReadyAsync*> polled the sandbox, with backoff, until it reached `Ready`. A sandbox that enters `Error`, stops, or is suspended fails the wait at once with an `OpenShellException` whose `Kind` is `Conflict`; running out of time throws one whose `Kind` is `DeadlineExceeded`. Either way the `finally` block still deletes the sandbox.
3. <xref:OpenShell.IExecClient.RunAsync*> ran the command (an argument list, not a shell string) and buffered its output into an <xref:OpenShell.ExecResult>: `ExitCode`, `StdoutText`, `StderrText`. A non-zero exit code is a result, not an exception.
4. <xref:OpenShell.ISandboxClient.DeleteAsync*> returned a typed <xref:OpenShell.DeletionResult>. `Accepted` means the gateway is still cleaning up, so the program waits with <xref:OpenShell.ISandboxClient.WaitDeletedAsync*>; `ExpectedSandboxId` keeps a new sandbox with the same name from extending the wait.

Every failure surfaces as an <xref:OpenShell.OpenShellException> with a `Kind` you can switch on; see [Errors](../guides/errors.md).

> [!TIP]
> The same code runs unchanged against `FakeOpenShellClient` from `OpenShell.Net.Testing`, which is how this documentation tests it. See [Testing](../guides/testing.md).

## Next steps

- [Sandboxes](../guides/sandboxes.md): images, environment, labels, templates, stop and start, suspend and resume, updates.
- [Exec and streaming](../guides/exec-and-streaming.md): streaming output, interactive sessions, logs, and watches.
- [Authentication](../guides/authentication.md): OIDC, client credentials, mTLS, and Cloudflare Access.
