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

    /// <summary>
    /// Deletes a sandbox and waits until it is gone. The v0.1.7 Docker driver can fail a delete that comes right after
    /// a create with Internal ("volume is in use") while the container still starts, so a cleanup retries that error.
    /// </summary>
    public static async Task DeleteAsync(OpenShellClient client, string name, string? expectedId = null)
    {
        DeletionResult deletion;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                deletion = await client.Sandboxes.DeleteAsync(name, new DeleteOptions { AllowMissing = true }, CancellationToken.None);
                break;
            }
            catch (OpenShellException e) when (e.Kind == OpenShellErrorKind.Internal && attempt < 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
        }

        if (deletion.Outcome == DeletionOutcome.Accepted)
        {
            await client.Sandboxes.WaitDeletedAsync(
                name, new WaitOptions { ExpectedSandboxId = expectedId, Timeout = TimeSpan.FromMinutes(2) }, CancellationToken.None);
        }
    }

    /// <summary>Creates a sandbox from <see cref="GatewayFixture.Image"/> and waits until it is ready.</summary>
    public static Task<TestSandbox> CreateAsync(OpenShellClient client, string prefix, CancellationToken cancellationToken) =>
        CreateAsync(client, prefix, new SandboxSpec { Image = GatewayFixture.Image }, cancellationToken);

    /// <summary>Creates a sandbox from <paramref name="spec"/> and waits until it is ready.</summary>
    public static async Task<TestSandbox> CreateAsync(OpenShellClient client, string prefix, SandboxSpec spec, CancellationToken cancellationToken)
    {
        var name = GatewayFixture.UniqueName(prefix);
        var created = await client.Sandboxes.CreateAsync(
            name,
            spec,
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

    public async ValueTask DisposeAsync() => await DeleteAsync(Client, Name, Sandbox.Id);
}
