// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using OpenShell;

namespace Snippets;

/// <summary>Snippets for the workspaces, pagination, retries, errors and multi-replica guides.</summary>
internal static class ClientBehavior
{
    internal static async Task WorkspacesAsync(IOpenShellClient client)
    {
        #region Workspaces
        // Operations run in OpenShellClientOptions.DefaultWorkspace ("default" unless set).
        Console.WriteLine(client.Workspace);

        await client.Workspaces.CreateAsync("team-a", new Dictionary<string, string> { ["cost-center"] = "42" });
        await client.Workspaces.AddMemberAsync("team-a", "user:alice@example.com", WorkspaceRole.Admin);

        // A view bound to another workspace that shares the connection. Disposing it does not close the connection.
        var teamA = client.InWorkspace("team-a");
        var sandboxes = await teamA.Sandboxes.List().ToListAsync();

        await foreach (var member in client.Workspaces.ListMembers("team-a"))
        {
            Console.WriteLine($"{member.PrincipalSubject}: {member.Role}");
        }
        #endregion
        Console.WriteLine(sandboxes.Count);
    }

    internal static async Task PaginationAsync(IOpenShellClient client)
    {
        #region Pagination
        // Items: the pager fetches the next page as you enumerate.
        await foreach (var sandbox in client.Sandboxes.List(new ListOptions { PageSize = 50 }))
        {
            Console.WriteLine($"{sandbox.Workspace}/{sandbox.Name}: {sandbox.Phase}");
        }

        // Pages: for example to show one page at a time and resume from its token later.
        await foreach (var page in client.Sandboxes.List(new ListOptions { PageSize = 50 }).PagesAsync())
        {
            Console.WriteLine($"{page.Items.Count} sandboxes, more: {page.HasMore}, next token: {page.NextPageToken}");
        }

        // Everything at once, across workspaces where the resource supports it.
        var all = await client.Sandboxes.List(new ListOptions { AllWorkspaces = true }).ToListAsync();
        #endregion
        Console.WriteLine(all.Count);
    }

    internal static OpenShellClient Retries()
    {
        #region Retries
        var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            Retry = RetryPolicy.Default with
            {
                MaxAttempts = 6,                          // the first attempt plus five retries
                MaxBackoff = TimeSpan.FromSeconds(10),    // a gateway RetryInfo delay above this ends the retries
            },
            DefaultTimeout = TimeSpan.FromSeconds(30),    // deadline for each unary call, retries included
        });
        #endregion
        return client;
    }

    internal static OpenShellClient RetryWrites()
    {
        #region RetryWrites
        // Writes are not retried by default. Widen IsIdempotent for methods you make safe to repeat, for
        // example by always sending a request id. It receives the gRPC method name.
        var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            Retry = RetryPolicy.Default with
            {
                IsIdempotent = method => method.StartsWith("Get", StringComparison.Ordinal)
                    || method.StartsWith("List", StringComparison.Ordinal)
                    || method is "Health" or "CreateSandbox",
            },
        });

        // Or turn retries off.
        var noRetries = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            Retry = RetryPolicy.None,
        });
        #endregion
        noRetries.Dispose();
        return client;
    }

    internal static async Task MutationLockAsync(IOpenShellClient client)
    {
        #region MutationLock
        try
        {
            await client.Sandboxes.StopAsync("agent-1");
        }
        catch (OpenShellException e) when (e.ErrorInfo?.Reason == "MUTATION_LOCK_TIMEOUT")
        {
            // Another mutation held the sandbox lock for longer than the gateway waits (10 s). The gateway
            // suggests when to try again; whether repeating this write is safe is your decision.
            await Task.Delay(e.RetryDelay ?? TimeSpan.FromSeconds(1));
            await client.Sandboxes.StopAsync("agent-1");
        }
        #endregion
    }

    internal static async Task ErrorsAsync(IOpenShellClient client)
    {
        #region Errors
        try
        {
            await client.Sandboxes.GetAsync("missing");
        }
        catch (OpenShellException e) when (e.IsNotFound)
        {
            Console.WriteLine("no such sandbox");
        }
        catch (OpenShellException e) when (e.IsTransient)
        {
            // Unavailable or DeadlineExceeded, after the retries ran out.
            Console.WriteLine($"gateway unavailable: {e.Message}");
        }
        catch (OpenShellException e)
        {
            Console.WriteLine($"{e.Kind} (gRPC {e.GrpcStatusCode}): {e.Message}");
        }
        #endregion
    }

    internal static async Task ErrorDetailsAsync(IOpenShellClient client)
    {
        #region ErrorDetails
        try
        {
            await client.Sandboxes.CreateAsync("Bad Name!", new SandboxSpec { Image = "img" });
        }
        catch (OpenShellException e) when (e.Kind == OpenShellErrorKind.InvalidArgument)
        {
            foreach (var violation in e.FieldViolations)
            {
                Console.WriteLine($"{violation.Field}: {violation.Description}");
            }

            if (e.ErrorInfo is { } info)
            {
                Console.WriteLine($"{info.Domain}/{info.Reason}");
            }
        }
        #endregion
    }

    internal static async Task CancellationAsync(IOpenShellClient client)
    {
        #region Cancellation
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await client.Exec.RunAsync("agent-1", ["sleep", "60"], cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Your own cancellation surfaces as OperationCanceledException, not OpenShellException.
        }
        #endregion
    }

    internal static OpenShellClient OwnerRouting()
    {
        #region OwnerRouting
        var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            RouteToOwnerReplica = false,   // relay every stream through whichever replica answers
        });
        #endregion
        return client;
    }
}
