// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using OpenShell;
using OpenShell.Gateway;

namespace Snippets;

/// <summary>Snippets for docs/guides/gateway-discovery.md.</summary>
internal static class Discovery
{
    internal static void ListGateways()
    {
        #region ListGateways
        var locator = new GatewayLocator();
        Console.WriteLine($"user config:   {locator.UserConfigDirectory}");
        Console.WriteLine($"system config: {locator.SystemConfigDirectory ?? "(none)"}");

        foreach (var entry in locator.List())
        {
            Console.WriteLine($"{(entry.Active ? "*" : " ")} {entry.Name} ({entry.Source})");
        }

        var gateway = locator.Load(locator.ResolveActiveName());
        Console.WriteLine($"{gateway.Name}: {gateway.Endpoint}, auth {gateway.AuthMode}");
        Console.WriteLine($"registration in {gateway.Directory}, credentials in {gateway.StateDirectory}");
        #endregion
    }

    internal static void ConnectOptions()
    {
        #region ConnectOptions
        using var client = GatewayClient.Connect("prod", new GatewayConnectOptions
        {
            DefaultWorkspace = "team-a",
            DefaultTimeout = TimeSpan.FromSeconds(30),
            WriteBack = false,   // keep renewed tokens in memory; leave oidc_token.json untouched
        });
        #endregion
    }

    internal static void CustomOptions()
    {
        #region CustomOptions
        // Build the options yourself to set what GatewayConnectOptions does not expose.
        // CreateOptions does not start an edge tunnel: use Connect for cloudflare_jwt gateways.
        var gateway = new GatewayLocator().Load("prod");
        var options = GatewayClient.CreateOptions(gateway) with
        {
            Retry = RetryPolicy.Default with { MaxAttempts = 6 },
        };

        using var client = new OpenShellClient(options);
        #endregion
    }

    internal static void CustomLocator(string root)
    {
        #region CustomLocator
        // Read registrations from other directories, for example in tests.
        var locator = new GatewayLocator(new GatewayLocatorOptions
        {
            UserConfigDirectory = Path.Combine(root, "user"),
            SystemConfigDirectory = Path.Combine(root, "system"),
            GetEnvironmentVariable = _ => null,   // ignore OPENSHELL_GATEWAY and the other variables
        });

        using var client = GatewayClient.Connect("local", new GatewayConnectOptions { Locator = locator });
        #endregion
    }
}
