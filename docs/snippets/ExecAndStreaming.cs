// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using OpenShell;

namespace Snippets;

/// <summary>Snippets for docs/guides/exec-and-streaming.md.</summary>
internal static class ExecAndStreaming
{
    internal static async Task<ExecResult> RunAsync(IOpenShellClient client)
    {
        #region Run
        var result = await client.Exec.RunAsync(
            "agent-1",
            ["python", "-c", "import sys; print(sys.stdin.read().upper())"],
            new ExecOptions
            {
                WorkingDirectory = "/workspace",
                Environment = new Dictionary<string, string> { ["LANG"] = "C.UTF-8" },
                Stdin = Encoding.UTF8.GetBytes("hello"),
                Timeout = TimeSpan.FromSeconds(30),
            });

        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"exit {result.ExitCode}: {result.StderrText}");
        }

        Console.WriteLine(result.StdoutText);
        #endregion
        return result;
    }

    internal static async Task PythonAsync(IOpenShellClient client)
    {
        #region Python
        // Runs the source with `python -c`; arguments arrive in sys.argv[1:].
        var result = await client.Exec.ExecPythonAsync(
            "agent-1",
            "import sys; print(sum(int(a) for a in sys.argv[1:]))",
            new PythonExecOptions { Interpreter = "python3", Arguments = ["1", "2", "3"] });
        #endregion
        Console.WriteLine(result.StdoutText);
    }

    internal static async Task<int> StreamAsync(IOpenShellClient client)
    {
        #region Stream
        var exitCode = -1;
        await foreach (var e in client.Exec.StreamAsync("agent-1", ["sh", "-c", "echo out; echo err >&2; exit 3"]))
        {
            switch (e)
            {
                case ExecOutput { IsStdErr: false } o:
                    Console.Write(o.Text);
                    break;
                case ExecOutput o:
                    Console.Error.Write(o.Text);
                    break;
                case ExecExit x:
                    exitCode = x.ExitCode;   // always the last event
                    break;
            }
        }
        #endregion
        return exitCode;
    }

    internal static async Task<int> InteractiveAsync(IOpenShellClient client)
    {
        #region Interactive
        await using var session = await client.Exec.InteractiveAsync(
            "agent-1",
            ["bash", "-l"],
            columns: 120,
            rows: 40);

        // Read the output (once) while writing input.
        var output = Task.Run(async () =>
        {
            await foreach (var e in session.ReadEventsAsync())
            {
                if (e is ExecOutput o)
                {
                    Console.Write(o.Text);
                }
            }
        });

        await session.WriteStdinAsync(Encoding.UTF8.GetBytes("ls /workspace\n"));
        await session.ResizeAsync(columns: 160, rows: 50);
        await session.WriteStdinAsync(Encoding.UTF8.GetBytes("exit\n"));
        await session.CompleteStdinAsync();   // EOF; output keeps flowing

        var exitCode = await session.WaitForExitAsync();
        await output;
        #endregion
        return exitCode;
    }

    internal static async Task WatchAsync(IOpenShellClient client, CancellationToken cancellationToken)
    {
        #region Watch
        var options = new WatchOptions
        {
            FollowStatus = true,      // SandboxUpdated (the default)
            FollowLogs = true,        // LogReceived
            FollowEvents = true,      // PlatformEventReceived
            LogTailLines = 50,        // start with the last 50 lines
            StopOnTerminal = true,    // end when the sandbox reaches a terminal phase
        };

        await foreach (var e in client.Watch.WatchAsync("agent-1", options, cancellationToken))
        {
            switch (e)
            {
                case SandboxUpdated u:
                    Console.WriteLine($"phase {u.Sandbox.Phase}");
                    break;
                case LogReceived l:
                    Console.WriteLine($"[{l.Line.Source}] {l.Line.Level} {l.Line.Message}");
                    break;
                case PlatformEventReceived p:
                    Console.WriteLine($"{p.Event.Type} {p.Event.Reason}: {p.Event.Message}");
                    break;
                case DraftPolicyUpdated d:
                    Console.WriteLine($"{d.Update.TotalPending} policy chunks pending review");
                    break;
                case WatchWarning w:
                    Console.WriteLine($"warning: {w.Message}");
                    break;
            }
        }
        #endregion
    }

    internal static async Task ResumeWatchAsync(IOpenShellClient client, Func<string?> loadCursor, Action<string> saveCursor)
    {
        #region ResumeWatch
        // Store the cursor of the last event you handled; pass it back to continue after a process restart.
        var options = new WatchOptions { FollowLogs = true, ResumeAfterCursor = loadCursor() };
        await foreach (var e in client.Watch.WatchAsync("agent-1", options))
        {
            if (e is LogReceived log)
            {
                Console.WriteLine(log.Line.Message);
            }

            if (!string.IsNullOrEmpty(e.Cursor))
            {
                saveCursor(e.Cursor);
            }
        }
        #endregion
    }

    internal static async Task LogsAsync(IOpenShellClient client)
    {
        #region Logs
        // A one-off read of the buffered logs.
        var logs = await client.Watch.GetLogsAsync("agent-1", new LogOptions
        {
            Lines = 200,
            Sources = ["sandbox"],
            MinLevel = "WARN",
        });

        foreach (var line in logs.Lines)
        {
            Console.WriteLine($"{line.Time:O} {line.Level} {line.Message}");
        }

        // Or follow them: WatchLogsAsync never follows status, only the logs and events you ask for.
        var follow = new WatchOptions { FollowLogs = true, LogTailLines = 20 };
        await foreach (var e in client.Watch.WatchLogsAsync("agent-1", follow))
        {
            if (e is LogReceived log)
            {
                Console.WriteLine(log.Line.Message);
            }
        }
        #endregion
    }
}
