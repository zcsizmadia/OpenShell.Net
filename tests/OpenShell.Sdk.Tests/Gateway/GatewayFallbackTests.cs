// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Time.Testing;
using OpenShell.Gateway;
using OpenShell.Oidc;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

/// <summary>
/// The CLI's split between system and per-user configuration: only <c>active_gateway</c> and <c>metadata.json</c> fall
/// back to the system directory, and credentials always live in the user gateway directory.
/// </summary>
public class GatewayFallbackTests
{
    private const string Metadata = """{"gateway_endpoint":"https://gw.example.com"}""";

    private static GatewayLocator Locator(TempDirectory root) =>
        new(new GatewayLocatorOptions
        {
            UserConfigDirectory = root.Combine("user"),
            SystemConfigDirectory = root.Combine("sys"),
            GetEnvironmentVariable = _ => null,
        });

    private static void WriteActive(TempDirectory root, string area, string text) =>
        File.WriteAllText(Path.Combine(root.Make(area), "active_gateway"), text);

    [Test]
    public async Task The_system_active_gateway_is_used_when_the_user_has_none()
    {
        using var root = new TempDirectory();
        GatewayLocatorTests.Register(root, "sys", "corp", Metadata);
        WriteActive(root, "sys", "corp\n");
        var locator = Locator(root);

        await Assert.That(locator.ResolveActiveName()).IsEqualTo("corp");
        await Assert.That(locator.Load().Source).IsEqualTo(GatewayConfigSource.System);
        await Assert.That(locator.List().Single().Active).IsTrue();
    }

    [Test]
    public async Task A_user_selection_wins_over_the_system_default()
    {
        using var root = new TempDirectory();
        WriteActive(root, "sys", "corp");
        WriteActive(root, "user", "mine");

        await Assert.That(Locator(root).ResolveActiveName()).IsEqualTo("mine");
    }

    [Test]
    [Arguments("   \n")]
    [Arguments("../escape")]
    public async Task An_empty_or_invalid_user_selection_falls_through_to_the_system_default(string user)
    {
        using var root = new TempDirectory();
        WriteActive(root, "user", user);
        WriteActive(root, "sys", "corp");

        await Assert.That(Locator(root).ResolveActiveName()).IsEqualTo("corp");
    }

    [Test]
    public async Task Problems_with_both_files_are_reported_together()
    {
        using var root = new TempDirectory();
        WriteActive(root, "user", "");
        WriteActive(root, "sys", "a/b");

        var ex = await Assert.That(() => Locator(root).ResolveActiveName()).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);
        await Assert.That(ex.Message).Contains(Path.Combine(root.Combine("user"), "active_gateway") + " is empty");
        await Assert.That(ex.Message).Contains(Path.Combine(root.Combine("sys"), "active_gateway") + " names an invalid gateway");
    }

    [Test]
    public async Task An_unreadable_active_gateway_file_is_reported()
    {
        using var root = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(root.Make("user"), "active_gateway"));

        var ex = await Assert.That(() => Locator(root).ResolveActiveName()).Throws<OpenShellException>();

        await Assert.That(ex!.Message).Contains("cannot read");
    }

    [Test]
    public async Task A_user_directory_holding_only_credentials_does_not_shadow_the_system_registration()
    {
        using var root = new TempDirectory();
        var system = GatewayLocatorTests.Register(root, "sys", "corp", Metadata);
        var user = root.Make("user", "gateways", "corp");
        OidcTokenFile.Write(user, new OidcTokens("signed-in"), "https://idp.example.com", "cli");

        var config = Locator(root).Load("corp");

        await Assert.That(config.Source).IsEqualTo(GatewayConfigSource.System);
        await Assert.That(config.Directory).IsEqualTo(system);
        await Assert.That(config.StateDirectory).IsEqualTo(user);
    }

    [Test]
    public async Task A_user_registration_keeps_its_own_directory_for_credentials()
    {
        using var root = new TempDirectory();
        GatewayLocatorTests.Register(root, "sys", "corp", Metadata);
        var user = GatewayLocatorTests.Register(root, "user", "corp", Metadata);

        var config = Locator(root).Load("corp");

        await Assert.That(config.Source).IsEqualTo(GatewayConfigSource.User);
        await Assert.That(config.Directory).IsEqualTo(user);
        await Assert.That(config.StateDirectory).IsEqualTo(user);
    }

    [Test]
    public async Task A_system_directory_without_metadata_is_not_a_registration()
    {
        using var root = new TempDirectory();
        root.Make("sys", "gateways", "empty");
        root.Make("user", "gateways", "empty");

        var ex = await Assert.That(() => Locator(root).Load("empty")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);
        await Assert.That(Locator(root).List().Count).IsEqualTo(0);
    }

    [Test]
    public async Task List_follows_the_cli_shadowing_and_skips_unreadable_registrations_in_name_order()
    {
        using var root = new TempDirectory();
        GatewayLocatorTests.Register(root, "sys", "zulu", Metadata);
        GatewayLocatorTests.Register(root, "sys", "corp", Metadata);
        root.Make("user", "gateways", "corp"); // credentials only: the system registration stays visible
        GatewayLocatorTests.Register(root, "user", "alpha", Metadata);
        GatewayLocatorTests.Register(root, "user", "broken", "{nope");
        GatewayLocatorTests.Register(root, "sys", "broken", Metadata); // shadowed by the unreadable user entry, as in the CLI
        GatewayLocatorTests.Register(root, "sys", "bad-system", "{}");

        var list = Locator(root).List();

        await Assert.That(string.Join(", ", list.Select(e => $"{e.Name}:{e.Source}"))).IsEqualTo("alpha:User, corp:System, zulu:System");
    }

    [Test]
    public async Task Gateway_clients_read_credentials_from_the_state_directory()
    {
        using var root = new TempDirectory();
        var system = root.Make("sys");
        var state = root.Make("state");
        await File.WriteAllTextAsync(Path.Combine(state, "edge_token"), "edge.jwt");
        var edge = new GatewayMetadata
        {
            Name = "corp",
            Endpoint = new Uri("https://gw.example.com"),
            Directory = system,
            StateDirectory = state,
            AuthMode = GatewayAuthMode.CloudflareJwt,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gw.example.com/x");
        await GatewayClient.CreateOptions(edge, new GatewayConnectOptions { EdgeTunnel = EdgeTunnelMode.Never }).Auth.ApplyAsync(request, CancellationToken.None);

        await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("edge.jwt");

        OidcTokenFile.Write(state, new OidcTokens("oidc-token", ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)), "https://idp.example.com", "cli");
        var oidc = edge with { AuthMode = GatewayAuthMode.Oidc, OidcIssuer = "https://idp.example.com", OidcClientId = "cli" };
        var auth = GatewayClient.CreateOptions(oidc, new GatewayConnectOptions { TimeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow) }).Auth;
        try
        {
            using var second = new HttpRequestMessage(HttpMethod.Post, "https://gw.example.com/x");
            await auth.ApplyAsync(second, CancellationToken.None);
            await Assert.That(second.Headers.Authorization!.Parameter).IsEqualTo("oidc-token");
        }
        finally
        {
            (auth as IDisposable)?.Dispose();
        }

        await Assert.That(Directory.GetFileSystemEntries(system).Length).IsEqualTo(0);
    }

    [Test]
    public async Task State_directory_defaults_to_the_registration_directory()
    {
        var config = new GatewayMetadata { Name = "gw", Endpoint = new Uri("https://gw.example.com"), Directory = "/some/dir" };

        await Assert.That(config.StateDirectory).IsEqualTo("/some/dir");
    }
}
