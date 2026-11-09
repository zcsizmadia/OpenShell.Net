// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using OpenShell;

namespace Snippets;

/// <summary>Snippets for docs/guides/providers.md.</summary>
internal static class Providers
{
    internal static async Task CreateAsync(IOpenShellClient client)
    {
        #region CreateProvider
        // Creates the provider, or updates it in place when one with the same name exists.
        var provider = await client.Providers.EnsureAsync(new Provider
        {
            Name = "github",
            Type = "github",
            Credentials = new Dictionary<string, string>
            {
                ["GITHUB_TOKEN"] = Environment.GetEnvironmentVariable("GITHUB_TOKEN")!,
            },
            Labels = new Dictionary<string, string> { ["team"] = "platform" },
        });

        // Provider.ToString() never prints credential values.
        Console.WriteLine(provider);

        await foreach (var p in client.Providers.List(new ListOptions { LabelSelector = "team=platform" }))
        {
            Console.WriteLine($"{p.Name} ({p.Type})");
        }
        #endregion
    }

    internal static async Task AttachAsync(IOpenShellClient client)
    {
        #region Attach
        var attached = await client.SandboxProviders.AttachAsync("agent-1", "github");
        if (attached.Receipt is { } receipt)
        {
            // Wait until the sandbox supervisor has installed exactly this authority.
            var wait = await client.SandboxProviders.WaitAsync(receipt, TimeSpan.FromMinutes(1));
            switch (wait.Outcome)
            {
                case ProviderWaitOutcome.Complete:
                    break;
                case ProviderWaitOutcome.TimedOut:
                    Console.WriteLine($"still {wait.Status.State}: {wait.Status.Reason}");
                    break;
                case ProviderWaitOutcome.Terminal:
                    throw new InvalidOperationException($"provider not installed: {wait.Status.Reason}");
            }
        }

        await foreach (var p in client.SandboxProviders.List("agent-1"))
        {
            Console.WriteLine(p.Name);
        }
        #endregion
    }

    internal static async Task UpdateAsync(IOpenShellClient client)
    {
        #region Update
        var current = await client.Providers.GetAsync("github");
        var update = await client.Providers.UpdateAsync(current with
        {
            Credentials = new Dictionary<string, string>
            {
                ["GITHUB_TOKEN"] = Environment.GetEnvironmentVariable("GITHUB_TOKEN_ROTATED")!,
            },
        });

        // One receipt per sandbox the provider is attached to, awaited under one deadline.
        var results = await client.SandboxProviders.WaitAllAsync(update.TargetReceipts, TimeSpan.FromMinutes(2));
        #endregion
        Console.WriteLine(results.Count);
    }

    internal static async Task CredentialsAsync(IOpenShellClient operatorClient)
    {
        #region Credentials
        // Requires a client authenticated with an operator client certificate (OU=operator) and no bearer token.
        var credentials = await operatorClient.Providers.GetCredentialsAsync(
            "my-graph",
            ["MS_GRAPH_ACCESS_TOKEN"],
            new ProviderCredentialsOptions
            {
                Workspace = "production",
                MinimumRemainingLifetime = TimeSpan.FromMinutes(5),
            });

        var token = credentials["MS_GRAPH_ACCESS_TOKEN"];
        Console.WriteLine($"expires {token.ExpirationTime}");   // never log token.Value
        #endregion
    }

    internal static async Task ProfilesAsync(IOpenShellClient client)
    {
        #region Profiles
        var profile = new ProviderProfile
        {
            Id = "internal-api",
            DisplayName = "Internal API",
            Category = ProviderProfileCategory.Other,
            Credentials =
            [
                new ProfileCredential
                {
                    Name = "api_key",
                    EnvVars = ["INTERNAL_API_KEY"],
                    Required = true,
                    StablePlaceholder = true,   // one placeholder value across value-only revisions
                },
            ],
        };

        // Lint reports diagnostics without importing anything.
        var lint = await client.Providers.Profiles.LintAsync([new ProfileImportItem(profile)]);
        foreach (var d in lint.Diagnostics)
        {
            Console.WriteLine($"{d.Severity} {d.ProfileId}.{d.Field}: {d.Message}");
        }

        if (lint.Valid)
        {
            var imported = await client.Providers.Profiles.ImportAsync([new ProfileImportItem(profile)]);
            Console.WriteLine($"imported: {imported.Imported}");
        }

        // Platform (global) profiles instead of the ones in this workspace.
        await foreach (var builtin in client.Providers.Profiles.Platform.List())
        {
            Console.WriteLine($"{builtin.Id}: {builtin.DisplayName}");
        }
        #endregion
    }

    internal static async Task RefreshAsync(IOpenShellClient client)
    {
        #region Refresh
        foreach (var status in await client.Providers.Refresh.GetStatusAsync("my-graph"))
        {
            Console.WriteLine(status);
        }

        // Rotate a gateway-refreshed credential now.
        await client.Providers.Refresh.RotateAsync("my-graph", "MS_GRAPH_ACCESS_TOKEN");
        #endregion
    }
}
