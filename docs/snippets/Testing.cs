// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Microsoft.Extensions.Time.Testing;
using OpenShell;
using OpenShell.Testing;

namespace Snippets;

/// <summary>Snippets for docs/guides/testing.md. Program.cs runs every one of them.</summary>
internal static class Testing
{
    #region CodeUnderTest
    /// <summary>Application code depends on the interface, so a test can pass the fake.</summary>
    internal sealed class BuildRunner(IOpenShellClient client)
    {
        public async Task<bool> RunTestsAsync(string sandbox, CancellationToken cancellationToken = default)
        {
            await client.Sandboxes.WaitReadyAsync(sandbox, cancellationToken: cancellationToken);
            var result = await client.Exec.RunAsync(sandbox, ["make", "test"], cancellationToken: cancellationToken);
            return result.Succeeded;
        }
    }
    #endregion

    internal static async Task ScriptExecAsync()
    {
        #region ScriptExec
        using var fake = new FakeOpenShellClient();

        // Handlers run newest first; return null to let the next one (or the default, exit 0) answer.
        fake.Gateway.OnExec(call => call.CommandLine == "make test"
            ? FakeExecScript.Failure(2, "1 test failed\n")
            : null);

        await fake.Sandboxes.CreateAsync("ci", new SandboxSpec { Image = "img" });
        var passed = await new BuildRunner(fake).RunTestsAsync("ci");

        Check(!passed, "the scripted failure was not reported");
        Check(fake.Gateway.Calls.Any(c => c.Operation == "Exec.Run" && c.Target == "ci"), "the exec was not recorded");
        #endregion
    }

    internal static async Task TimeControlAsync()
    {
        #region TimeControl
        var time = new FakeTimeProvider();
        using var fake = new FakeOpenShellClient(new FakeGatewayOptions
        {
            TimeProvider = time,
            ProvisioningDelay = TimeSpan.FromSeconds(30),
        });

        await fake.Sandboxes.CreateAsync("slow", new SandboxSpec { Image = "img" });
        Check((await fake.Sandboxes.GetAsync("slow")).Phase == SandboxPhase.Provisioning, "expected Provisioning");

        // Moving the clock fires the scheduled transition; no real time passes.
        time.Advance(TimeSpan.FromSeconds(30));
        Check((await fake.Sandboxes.GetAsync("slow")).Phase == SandboxPhase.Ready, "expected Ready");

        // Or set a phase directly, for example to test error handling.
        fake.Gateway.SetPhase("slow", SandboxPhase.Error);
        #endregion
    }

    internal static async Task FailuresAsync()
    {
        #region Failures
        using var fake = new FakeOpenShellClient();
        await fake.Sandboxes.CreateAsync("demo", new SandboxSpec { Image = "img" });

        // The next two Sandboxes.Get calls fail; the third succeeds.
        fake.Gateway.FailNext("Sandboxes.Get", new OpenShellException(OpenShellErrorKind.Unavailable, "gateway down"), times: 2);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await fake.Sandboxes.GetAsync("demo");
                Check(attempt == 3, $"succeeded on attempt {attempt}");
                break;
            }
            catch (OpenShellException e) when (e.IsTransient)
            {
                // Your retry logic under test.
            }
        }

        // The fake reports the same error kinds as the real client.
        try
        {
            await fake.Sandboxes.GetAsync("missing");
            Check(false, "expected NotFound");
        }
        catch (OpenShellException e) when (e.IsNotFound)
        {
        }
        #endregion
    }

    internal static async Task WatchAsync()
    {
        #region Watch
        using var fake = new FakeOpenShellClient();
        await fake.Sandboxes.CreateAsync("demo", new SandboxSpec { Image = "img" });

        fake.Gateway.EmitLog("demo", "starting");
        fake.Gateway.EmitLog("demo", "disk almost full", level: "WARN");

        var logs = await fake.Watch.GetLogsAsync("demo", new LogOptions { MinLevel = "WARN" });
        Check(logs.Lines.Single().Message == "disk almost full", "expected only the warning");

        // Watches replay and follow, with cursors, until you cancel.
        using var cts = new CancellationTokenSource();
        var messages = new List<string>();
        await foreach (var e in fake.Watch.WatchLogsAsync("demo", new WatchOptions { FollowLogs = true, LogTailLines = 10 }, cts.Token))
        {
            if (e is LogReceived log)
            {
                messages.Add(log.Line.Message);
                if (messages.Count == 2)
                {
                    break;
                }
            }
        }

        Check(messages.SequenceEqual(["starting", "disk almost full"]), string.Join(", ", messages));
        #endregion
    }

    internal static async Task ForwardAsync()
    {
        #region Forward
        using var fake = new FakeOpenShellClient();
        await fake.Sandboxes.CreateAsync("demo", new SandboxSpec { Image = "img" });

        // Play the sandbox side of TCP forwards: read what the client sent, write the answer.
        fake.Gateway.OnForward(async (request, sandboxSide, cancellationToken) =>
        {
            using var reader = new StreamReader(sandboxSide, Encoding.UTF8, leaveOpen: true);
            var question = await reader.ReadToEndAsync(cancellationToken);
            await sandboxSide.WriteAsync(Encoding.UTF8.GetBytes($"port {request.Port}: {question.ToUpperInvariant()}"), cancellationToken);
        });

        await using var stream = await fake.Tcp.ForwardAsync("demo", 8080);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("ping"));
        await stream.CompleteWriteAsync();

        using var answer = new StreamReader(stream, Encoding.UTF8);
        Check(await answer.ReadToEndAsync() == "port 8080: PING", "unexpected forward answer");
        #endregion
    }

    internal static async Task FilesAsync()
    {
        #region Files
        using var fake = new FakeOpenShellClient();
        await fake.Sandboxes.CreateAsync("demo", new SandboxSpec { Image = "img" });

        // Seed the sandbox's in-memory file system, run the code under test, and inspect the result.
        fake.Gateway.PutFile("demo", "/workspace/input.txt", Encoding.UTF8.GetBytes("hello"));

        using var input = new MemoryStream();
        await fake.Files.DownloadAsync("demo", "/workspace/input.txt", input);
        using var output = new MemoryStream(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input.ToArray()).ToUpperInvariant()));
        await fake.Files.UploadAsync("demo", output, "/workspace/output.txt");

        Check(Encoding.UTF8.GetString(fake.Gateway.ReadFile("demo", "/workspace/output.txt")!) == "HELLO", "unexpected file content");
        Check(fake.Gateway.ListFiles("demo").Contains("/workspace/output.txt"), "the uploaded file is not listed");
        #endregion
    }

    internal static async Task SharedGatewayAsync()
    {
        #region SharedGateway
        // One in-memory gateway, two clients bound to different workspaces.
        using var gateway = new FakeGateway();
        using var teamA = new FakeOpenShellClient(gateway, "team-a");
        using var teamB = new FakeOpenShellClient(gateway, "team-b");

        await teamA.Sandboxes.CreateAsync("shared-name", new SandboxSpec { Image = "img" });
        await teamB.Sandboxes.CreateAsync("shared-name", new SandboxSpec { Image = "img" });   // no conflict

        var all = await teamA.Sandboxes.List(new ListOptions { AllWorkspaces = true }).ToListAsync();
        Check(all.Count == 2, $"expected 2 sandboxes, found {all.Count}");
        #endregion
    }

    internal static async Task EndpointStatusesAsync()
    {
        #region EndpointStatuses
        using var fake = new FakeOpenShellClient();
        await fake.Sandboxes.CreateAsync("demo", new SandboxSpec { Image = "img" });

        fake.Gateway.ReportEndpointStatuses("demo",
        [
            new EndpointStatus
            {
                Host = "api.example.com",
                Ports = [443],
                LastResult = EndpointResult.CredentialUnavailable,
                LastFailure = new EndpointFailure { Code = "token_exchange_failed", Stage = "token_grant" },
            },
        ]);

        var sandbox = await fake.Sandboxes.GetAsync("demo");
        Check(sandbox.EndpointStatuses.Single().LastFailure?.Code == "token_exchange_failed", "status not reported");
        #endregion
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("snippet check failed: " + message);
        }
    }
}
