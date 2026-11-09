// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.IntegrationTests;

/// <summary>A ready sandbox with a unique name, deleted (and waited for) on dispose.</summary>
internal sealed class TestSandbox : IAsyncDisposable
{
    private TestSandbox(OpenShellClient client, Sandbox sandbox)
    {
        Client = client;
        Sandbox = sandbox;
    }

    public OpenShellClient Client { get; }

    public Sandbox Sandbox { get; }

    public string Name => Sandbox.Name;

    /// <summary>Creates a sandbox from <see cref="GatewayFixture.Image"/> and waits until it is ready.</summary>
    public static async Task<TestSandbox> CreateAsync(OpenShellClient client, string prefix, CancellationToken cancellationToken)
    {
        var name = GatewayFixture.UniqueName(prefix);
        var created = await client.Sandboxes.CreateAsync(
            name,
            new SandboxSpec { Image = GatewayFixture.Image },
            new CreateSandboxOptions { Labels = new Dictionary<string, string> { ["sdk"] = "dotnet" } },
            cancellationToken);
        var sandbox = new TestSandbox(client, created);
        try
        {
            await client.Sandboxes.WaitReadyAsync(name, new WaitOptions { Timeout = TimeSpan.FromMinutes(5) }, cancellationToken);
        }
        catch
        {
            await sandbox.DisposeAsync();
            throw;
        }

        return sandbox;
    }

    /// <summary>Runs a shell command and fails the test with its output when it exits non-zero.</summary>
    public async Task<string> ShAsync(string script, CancellationToken cancellationToken)
    {
        var result = await Client.Exec.RunAsync(Name, ["/bin/sh", "-c", script], cancellationToken: cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"`{script}` exited {result.ExitCode}: {result.StdoutText}{result.StderrText}");
        }

        return result.StdoutText;
    }

    public async ValueTask DisposeAsync()
    {
        var deletion = await Client.Sandboxes.DeleteAsync(Name, new DeleteOptions { AllowMissing = true }, CancellationToken.None);
        if (deletion.Outcome == DeletionOutcome.Accepted)
        {
            await Client.Sandboxes.WaitDeletedAsync(
                Name, new WaitOptions { ExpectedSandboxId = Sandbox.Id, Timeout = TimeSpan.FromMinutes(2) }, CancellationToken.None);
        }
    }
}
