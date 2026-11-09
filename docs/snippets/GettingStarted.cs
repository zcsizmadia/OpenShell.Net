// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using OpenShell;
using OpenShell.Gateway;

namespace Snippets;

/// <summary>Snippets for docs/getting-started.</summary>
internal static class GettingStarted
{
    internal static async Task OverviewAsync()
    {
        #region Overview
        using var client = GatewayClient.Connect();   // the gateway the OpenShell CLI made active

        var result = await client.Exec.RunAsync("my-sandbox", ["uname", "-a"]);
        Console.WriteLine(result.StdoutText);
        #endregion
    }

    internal static async Task ConnectToEndpointAsync()
    {
        #region ConnectToEndpoint
        using var client = new OpenShellClient(new OpenShellClientOptions
        {
            Address = new Uri("https://gateway.example.com"),
            Auth = Auth.StaticToken(Environment.GetEnvironmentVariable("OPENSHELL_TOKEN")!),
        });

        // The client connects lazily; a health check verifies that the gateway is reachable.
        var health = await client.Health.CheckAsync();
        Console.WriteLine($"{health.Status}: {health.Version}");
        #endregion
    }

    internal static async Task ConnectToRegisteredGatewayAsync()
    {
        #region ConnectToRegisteredGateway
        // The gateway the OpenShell CLI made active, with its stored credentials.
        using var client = GatewayClient.Connect();

        // Or a registered gateway by name.
        using var staging = GatewayClient.Connect("staging");

        var info = await client.Health.GetGatewayInfoAsync();
        Console.WriteLine(info);
        #endregion
    }

    internal static void ConnectWithDependencyInjection(IServiceCollection services)
    {
        #region ConnectWithDependencyInjection
        services.AddOpenShellClient()
            .WithAddress("https://gateway.example.com")
            .WithBearerToken(Environment.GetEnvironmentVariable("OPENSHELL_TOKEN")!);

        // Inject IOpenShellClient (or ISandboxClient, IExecClient, ...) wherever you need it.
        #endregion
    }

    /// <summary>
    /// The body of the first program. In the guide it follows <c>using var client = GatewayClient.Connect();</c> in a
    /// top-level Program.cs; here it takes the client so that Program.cs can run it against the fake.
    /// </summary>
    internal static async Task RunFirstProgramAsync(IOpenShellClient client)
    {
        #region FirstProgram
        // 1. Create the sandbox. The name is optional: pass null to let the gateway generate one.
        var sandbox = await client.Sandboxes.CreateAsync(
            "hello-openshell",
            new SandboxSpec { Image = "registry.example.com/team/python-agent:1.0" });

        try
        {
            // 2. Wait until it is Ready. A sandbox that fails, or a timeout, throws OpenShellException.
            await client.Sandboxes.WaitReadyAsync(sandbox.Name, new WaitOptions { Timeout = TimeSpan.FromMinutes(2) });

            // 3. Run a command and read its output.
            var result = await client.Exec.RunAsync(sandbox.Name, ["echo", "hello from OpenShell"]);
            Console.WriteLine($"exit {result.ExitCode}: {result.StdoutText}");
        }
        finally
        {
            // 4. Delete it, and wait while the gateway finishes the cleanup in the background.
            var deletion = await client.Sandboxes.DeleteAsync(sandbox.Name);
            if (deletion.Outcome == DeletionOutcome.Accepted)
            {
                await client.Sandboxes.WaitDeletedAsync(
                    sandbox.Name,
                    new WaitOptions { ExpectedSandboxId = deletion.ResourceId });
            }
        }
        #endregion
    }

    internal static void Connect()
    {
        #region FirstProgramConnect
        using var client = GatewayClient.Connect();
        #endregion
    }
}
