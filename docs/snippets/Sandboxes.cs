// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using OpenShell;

namespace Snippets;

/// <summary>Snippets for docs/guides/sandboxes.md.</summary>
internal static class Sandboxes
{
    internal static async Task<Sandbox> CreateAsync(IOpenShellClient client)
    {
        #region Create
        var sandbox = await client.Sandboxes.CreateAsync(
            "agent-1",
            new SandboxSpec
            {
                Image = "registry.example.com/team/python-agent:1.0",
                Environment = new Dictionary<string, string> { ["MODE"] = "dev" },
                RestartPolicy = SandboxRestartPolicy.OnFailure,
                ServiceExposures = [new ServiceExposure("web", 8080)],
            },
            new CreateSandboxOptions
            {
                Labels = new Dictionary<string, string> { ["team"] = "platform" },
                RequestId = "create-agent-1",   // a retry with the same id returns the original result
            });

        Console.WriteLine($"{sandbox.Name} ({sandbox.Id}) is {sandbox.Phase}");
        #endregion
        return sandbox;
    }

    internal static async Task WaitAsync(IOpenShellClient client)
    {
        #region Wait
        // Polls with backoff until the sandbox is Ready (or Completed). Error, Stopped, and Suspending or Suspended
        // fail the wait at once; running out of time throws OpenShellException with DeadlineExceeded.
        var ready = await client.Sandboxes.WaitReadyAsync("agent-1", new WaitOptions
        {
            Timeout = TimeSpan.FromMinutes(5),
            PollInterval = TimeSpan.FromMilliseconds(500),
            MaxPollInterval = TimeSpan.FromSeconds(5),
        });
        #endregion
        Console.WriteLine(ready.Phase);
    }

    internal static async Task GetAndListAsync(IOpenShellClient client)
    {
        #region GetAndList
        var sandbox = await client.Sandboxes.GetAsync("agent-1");
        foreach (var condition in sandbox.Conditions)
        {
            Console.WriteLine($"{condition.Type}={condition.Status}: {condition.Reason} {condition.Message}");
        }

        await foreach (var s in client.Sandboxes.List(new ListOptions { LabelSelector = "team=platform" }))
        {
            Console.WriteLine($"{s.Name}: {s.Phase}");
        }

        IReadOnlyList<string> ids = await client.Sandboxes.ListIdsAsync();
        #endregion
        Console.WriteLine(ids.Count);
    }

    internal static async Task StopStartAsync(IOpenShellClient client)
    {
        #region StopStart
        await client.Sandboxes.StopAsync("agent-1");          // Ready -> Stopping
        await client.Sandboxes.WaitStoppedAsync("agent-1");   // -> Stopped

        await client.Sandboxes.StartAsync("agent-1");         // -> Starting
        await client.Sandboxes.WaitReadyAsync("agent-1");     // -> Ready
        #endregion
    }

    internal static async Task SuspendResumeAsync(IOpenShellClient client)
    {
        #region SuspendResume
        await client.Sandboxes.SuspendAsync("agent-1");         // Ready -> Suspending
        await client.Sandboxes.WaitSuspendedAsync("agent-1");   // -> Suspended, memory retained

        await client.Sandboxes.ResumeAsync(
            "agent-1",
            new ResumeSandboxOptions { RestartIfRestoreFails = true });   // -> Starting, with the retained state
        await client.Sandboxes.WaitReadyAsync("agent-1");       // -> Ready
        #endregion
    }

    internal static async Task UpdateAsync(IOpenShellClient client)
    {
        #region Update
        var sandbox = await client.Sandboxes.GetAsync("agent-1");   // must be Stopped

        // Compare-and-swap on the version you read. Null properties stay as they are; an empty collection
        // replaces the value with nothing.
        sandbox = await client.Sandboxes.UpdateAsync(
            "agent-1",
            sandbox.ResourceVersion,
            new SandboxUpdate
            {
                Image = "registry.example.com/team/python-agent:1.1",
                Environment = new Dictionary<string, string> { ["MODE"] = "prod" },
            },
            new UpdateSandboxOptions { RequestId = "update-agent-1-to-1.1" });

        await client.Sandboxes.StartAsync("agent-1");   // the update does not start the sandbox
        #endregion
    }

    internal static async Task ResumePendingUpdateAsync(IOpenShellClient client)
    {
        #region ResumePendingUpdate
        // An update that failed part-way stays pending and blocks start and stop until it is finished.
        var current = await client.Sandboxes.GetAsync("agent-1");
        await client.Sandboxes.ResumePendingUpdateAsync("agent-1", current.ResourceVersion);
        #endregion
    }

    internal static async Task EndpointStatusAsync(IOpenShellClient client)
    {
        #region EndpointStatus
        var sandbox = await client.Sandboxes.GetAsync("agent-1");
        foreach (var endpoint in sandbox.EndpointStatuses)
        {
            Console.WriteLine($"{endpoint.Host}:{string.Join(',', endpoint.Ports)}{endpoint.Path} {endpoint.LastResult} at {endpoint.LastReportedAt}");
            if (endpoint.LastFailure is { } failure)
            {
                Console.WriteLine(
                    $"  {failure.Stage}/{failure.Code}: {failure.RecoveryAction} " +
                    $"(provider {failure.Provider}, key {failure.CredentialKey}, HTTP {failure.UpstreamHttpStatus}, request {failure.RequestId})");
            }
        }

        if (sandbox.LastTokenGrantFailure is { } grant)
        {
            Console.WriteLine($"last token grant failure for {grant.DestinationHost}:{grant.DestinationPort}: {grant.Failure?.Code}");
        }
        #endregion
    }

    internal static async Task DeleteAsync(IOpenShellClient client)
    {
        #region Delete
        var deletion = await client.Sandboxes.DeleteAsync("agent-1", new DeleteOptions { AllowMissing = true });
        switch (deletion.Outcome)
        {
            case DeletionOutcome.Completed:
            case DeletionOutcome.AlreadyAbsent:
                break;   // deletion.IsComplete is true

            case DeletionOutcome.Accepted:
                // Cleanup is still running. ExpectedSandboxId makes sure a new sandbox created under the
                // same name does not keep the wait going.
                await client.Sandboxes.WaitDeletedAsync(
                    "agent-1",
                    new WaitOptions { ExpectedSandboxId = deletion.ResourceId });
                break;

            default:
                // Unspecified: the gateway did not say; it does not establish completion.
                await client.Sandboxes.WaitDeletedAsync("agent-1");
                break;
        }
        #endregion
    }

    internal static async Task TemplatesAsync(IOpenShellClient client)
    {
        #region Templates
        await client.Templates.CreateAsync(new SandboxWorkloadTemplate
        {
            Name = "python-small",
            Image = "registry.example.com/team/python-agent:1.0",
            Resources = new WorkloadResources { Cpu = "1", Memory = "2Gi" },
            Environment = new Dictionary<string, string> { ["PYTHONUNBUFFERED"] = "1" },
        });

        // The optional spec overrides fields of the template.
        var sandbox = await client.Sandboxes.CreateFromTemplateAsync(
            "agent-2",
            "python-small",
            new SandboxSpec { Environment = new Dictionary<string, string> { ["MODE"] = "dev" } });
        #endregion
        Console.WriteLine(sandbox.Name);
    }
}
