// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using OpenShell.Gateway;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

/// <summary>The NVIDIA/OpenShell v0.1.7 discovery rules: the system root override and FQDN or IP gateway names.</summary>
public class GatewayDiscoveryTests
{
    private const string Metadata = """{"gateway_endpoint":"https://gw.example.com"}""";

    private static GatewayLocator Locator(TempDirectory root, string? systemOverride = null, Dictionary<string, string>? env = null) =>
        new(new GatewayLocatorOptions
        {
            UserConfigDirectory = root.Combine("user"),
            SystemConfigDirectory = systemOverride,
            GetEnvironmentVariable = name => env is not null && env.TryGetValue(name, out var v) ? v : null,
        });

    [Test]
    public async Task The_system_root_comes_from_the_environment_when_it_is_absolute()
    {
        using var root = new TempDirectory();
        var system = root.Combine("etc-openshell");
        GatewayLocatorTests.Register(root, "etc-openshell", "shared.example.com", Metadata);
        var locator = Locator(root, env: new() { ["OPENSHELL_SYSTEM_GATEWAY_DIR"] = system });

        await Assert.That(locator.SystemConfigDirectory).IsEqualTo(system);
        var config = locator.Load("shared.example.com");
        await Assert.That(config.Source).IsEqualTo(GatewayConfigSource.System);
        await Assert.That(locator.List().Select(e => e.Name)).IsEquivalentTo(["shared.example.com"]);
    }

    [Test]
    [Arguments("")]
    [Arguments("relative/dir")]
    [Arguments("etc")]
    public async Task Empty_or_relative_system_roots_are_ignored(string value)
    {
        using var root = new TempDirectory();
        var locator = Locator(root, env: new() { ["OPENSHELL_SYSTEM_GATEWAY_DIR"] = value });

        await Assert.That(locator.SystemConfigDirectory).IsEqualTo(OperatingSystem.IsWindows() ? null : "/etc/openshell");
    }

    [Test]
    public async Task The_explicit_option_wins_over_the_environment()
    {
        using var root = new TempDirectory();
        var locator = Locator(root, systemOverride: root.Combine("option"), env: new() { ["OPENSHELL_SYSTEM_GATEWAY_DIR"] = root.Combine("env") });

        await Assert.That(locator.SystemConfigDirectory).IsEqualTo(root.Combine("option"));
    }

    [Test]
    public async Task Host_derived_names_work_for_loading_listing_and_the_active_gateway()
    {
        using var root = new TempDirectory();
        GatewayLocatorTests.Register(root, "user", "gw.example.com", Metadata);
        GatewayLocatorTests.Register(root, "user", "192.0.2.1", Metadata);
        GatewayLocatorTests.Register(root, "user", "with space", Metadata);
        GatewayLocatorTests.Register(root, "user", "foo..bar", Metadata);
        File.WriteAllText(Path.Join(root.Make("user"), "active_gateway"), "gw.example.com\n");
        var locator = Locator(root, systemOverride: root.Combine("none"));

        await Assert.That(locator.ResolveActiveName()).IsEqualTo("gw.example.com");
        await Assert.That(locator.Load().Name).IsEqualTo("gw.example.com");
        await Assert.That(locator.Load("192.0.2.1").Name).IsEqualTo("192.0.2.1");

        // Directories with names the CLI would reject are skipped, not listed.
        var listed = locator.List();
        await Assert.That(listed.Select(e => e.Name)).IsEquivalentTo(["192.0.2.1", "gw.example.com"]);
        await Assert.That(listed.Single(e => e.Active).Name).IsEqualTo("gw.example.com");
    }

    [Test]
    public async Task An_active_gateway_with_a_device_name_is_rejected()
    {
        using var root = new TempDirectory();
        File.WriteAllText(Path.Join(root.Make("user"), "active_gateway"), "COM1.example.com");

        var ex = await Assert.That(() => Locator(root).ResolveActiveName()).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);
        await Assert.That(ex.Message).Contains("reserved Windows device name");
    }
}
