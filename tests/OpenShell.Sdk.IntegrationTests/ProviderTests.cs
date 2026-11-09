// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.IntegrationTests;

/// <summary>Providers, provider profiles, and sandbox provider attachment against a real gateway.</summary>
public class ProviderTests
{
    private const string PlaceholderPrefix = "openshell:resolve:env:";
    private const string TokenKey = "DN_IT_TOKEN";

    [Test]
    [Timeout(300_000)]
    public async Task Providers_support_create_get_list_update_and_delete(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        var run = GatewayFixture.UniqueName("run");
        var name = GatewayFixture.UniqueName("dn-prov");
        const string Secret = "sk-dotnet-it-original";
        var type = await ImportProfileAsync(client, withEndpoint: false, cancellationToken);
        try
        {
            var created = await client.Providers.CreateAsync(
                new Provider
                {
                    Name = name,
                    Type = type,
                    ProfileWorkspace = GatewayFixture.Workspace,
                    Credentials = new Dictionary<string, string> { [TokenKey] = Secret },
                    Config = new Dictionary<string, string> { ["DN_IT_SETTING"] = "one" },
                    Labels = new Dictionary<string, string> { ["dotnet-it-run"] = run },
                },
                cancellationToken: cancellationToken);
            await Assert.That(created.Name).IsEqualTo(name);
            await Assert.That(created.Type).IsEqualTo(type);
            await Assert.That(created.Id).IsNotEmpty();
            await Assert.That(created.Workspace).IsEqualTo(GatewayFixture.Workspace);

            // Reads keep the credential keys but never return the secret.
            var fetched = await client.Providers.GetAsync(name, cancellationToken);
            await Assert.That(fetched.Id).IsEqualTo(created.Id);
            await Assert.That(fetched.Credentials.Keys).Contains(TokenKey);
            await Assert.That(fetched.Credentials[TokenKey]).IsNotEqualTo(Secret);
            await Assert.That(fetched.Config["DN_IT_SETTING"]).IsEqualTo("one");
            await Assert.That(fetched.Labels["dotnet-it-run"]).IsEqualTo(run);
            await Assert.That(fetched.ResourceVersion).IsGreaterThan(0ul);
            await Assert.That(fetched.ToString()).DoesNotContain(Secret);

            var listed = await client.Providers.List(new ListOptions { LabelSelector = $"dotnet-it-run={run}" }).ToListAsync(cancellationToken);
            await Assert.That(listed.Select(p => p.Name).ToArray()).IsEquivalentTo(new[] { name });

            // An update merges: config changes, and the credential it does not mention stays.
            var updated = await client.Providers.UpdateAsync(
                new Provider { Name = name, Type = type, Config = new Dictionary<string, string> { ["DN_IT_SETTING"] = "two" } },
                cancellationToken: cancellationToken);
            await Assert.That(updated.MutationId).IsNotEmpty();
            await Assert.That(updated.TargetReceipts).IsEmpty();
            await Assert.That(updated.Provider.Config["DN_IT_SETTING"]).IsEqualTo("two");
            await Assert.That(updated.Provider.Credentials.Keys).Contains(TokenKey);
            await Assert.That(updated.Provider.ResourceVersion).IsGreaterThan(fetched.ResourceVersion);

            // EnsureAsync updates the existing provider in place.
            var ensured = await client.Providers.EnsureAsync(
                new Provider { Name = name, Type = type, Config = new Dictionary<string, string> { ["DN_IT_SETTING"] = "three" } },
                cancellationToken);
            await Assert.That(ensured.Id).IsEqualTo(created.Id);
            await Assert.That(ensured.Config["DN_IT_SETTING"]).IsEqualTo("three");

            var typeChange = await Assert.That(async () => await client.Providers.UpdateAsync(
                new Provider { Name = name, Type = "dn-it-other-type" }, cancellationToken: cancellationToken)).Throws<OpenShellException>();
            await Assert.That(typeChange!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);

            var duplicate = await Assert.That(async () => await client.Providers.CreateAsync(
                new Provider { Name = name, Type = type, Credentials = new Dictionary<string, string> { [TokenKey] = Secret } },
                cancellationToken: cancellationToken)).Throws<OpenShellException>();
            await Assert.That(duplicate!.IsAlreadyExists).IsTrue();

            // A provider needs a built-in or imported profile.
            var profileless = await Assert.That(async () => await client.Providers.CreateAsync(
                new Provider { Name = GatewayFixture.UniqueName("dn-prov"), Type = "generic" }, cancellationToken: cancellationToken)).Throws<OpenShellException>();
            await Assert.That(profileless!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);

            // Exporting credentials needs an operator client certificate (OU=operator); the test PKI issues a user one.
            try
            {
                var exported = await client.Providers.GetCredentialsAsync(
                    name, [TokenKey], new ProviderCredentialsOptions { Workspace = GatewayFixture.Workspace }, cancellationToken);
                await Assert.That(exported[TokenKey].Value).IsEqualTo(Secret);
            }
            catch (OpenShellException e) when (e.Kind is OpenShellErrorKind.PermissionDenied or OpenShellErrorKind.Unauthenticated)
            {
                // Expected without an operator identity.
            }

            var deletion = await client.Providers.DeleteAsync(name, cancellationToken: cancellationToken);
            await Assert.That(deletion.Outcome).IsEqualTo(DeletionOutcome.Completed);
            var missing = await Assert.That(async () => await client.Providers.GetAsync(name, cancellationToken)).Throws<OpenShellException>();
            await Assert.That(missing!.IsNotFound).IsTrue();
            var again = await client.Providers.DeleteAsync(name, new DeleteOptions { AllowMissing = true }, cancellationToken);
            await Assert.That(again.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
        }
        finally
        {
            await DeleteProviderAsync(client, name);
            await DeleteProfileAsync(client, type);
        }
    }

    [Test]
    [Timeout(300_000)]
    public async Task Provider_profiles_lint_import_update_and_delete(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        var profiles = client.Providers.Profiles;
        var id = GatewayFixture.UniqueName("dn-it-profile");
        var providerName = GatewayFixture.UniqueName("dn-prof");
        var item = new ProfileImportItem(
            new ProviderProfile
            {
                Id = id,
                DisplayName = "dotnet integration profile",
                Description = "Imported by the OpenShell.Net integration tests",
                Category = ProviderProfileCategory.Other,
                Credentials =
                [
                    new ProfileCredential { Name = "token", Description = "Test token", EnvVars = ["DN_IT_TOKEN"], Required = true },
                ],
            },
            $"{id}.yaml");
        try
        {
            // The gateway release ships no built-in profiles, so the catalog starts without this one.
            var catalog = await profiles.List(new ListOptions { PageSize = 200 }).ToListAsync(cancellationToken);
            await Assert.That(catalog.Select(p => p.Id)).DoesNotContain(id);

            var lint = await profiles.LintAsync([item], cancellationToken);
            await Assert.That(lint.Valid).IsTrue();
            var invalid = await profiles.LintAsync([item with { Profile = item.Profile with { Id = "Not_Kebab_Case" } }], cancellationToken);
            await Assert.That(invalid.Valid).IsFalse();
            await Assert.That(invalid.Diagnostics.Any(d => d.Severity == "error")).IsTrue();

            var imported = await profiles.ImportAsync([item], cancellationToken: cancellationToken);
            await Assert.That(imported.Imported).IsTrue();
            var stored = imported.Profiles.Single();
            await Assert.That(stored.Id).IsEqualTo(id);
            await Assert.That(stored.ResourceVersion).IsGreaterThan(0ul);

            var fetched = await profiles.GetAsync(id, cancellationToken);
            await Assert.That(fetched.DisplayName).IsEqualTo("dotnet integration profile");
            await Assert.That(fetched.Credentials.Single().EnvVars).IsEquivalentTo(new[] { "DN_IT_TOKEN" });

            // Workspace profiles do not leak into the platform scope.
            var workspaceIds = (await profiles.List(new ListOptions { PageSize = 200 }).ToListAsync(cancellationToken)).Select(p => p.Id).ToArray();
            await Assert.That(workspaceIds).Contains(id);
            var platformIds = (await profiles.Platform.List(new ListOptions { PageSize = 200 }).ToListAsync(cancellationToken)).Select(p => p.Id).ToArray();
            await Assert.That(platformIds).DoesNotContain(id);

            // A second import of the same id is refused with diagnostics, not stored.
            var reimport = await profiles.ImportAsync([item], cancellationToken: cancellationToken);
            await Assert.That(reimport.Imported).IsFalse();
            await Assert.That(reimport.Diagnostics).IsNotEmpty();

            var renamed = item with { Profile = item.Profile with { DisplayName = "dotnet integration profile v2" } };
            var update = await profiles.UpdateAsync(id, stored.ResourceVersion, renamed, cancellationToken: cancellationToken);
            await Assert.That(update.Updated).IsTrue();
            await Assert.That(update.Profile!.DisplayName).IsEqualTo("dotnet integration profile v2");
            await Assert.That(update.Profile.ResourceVersion).IsGreaterThan(stored.ResourceVersion);

            var stale = await Assert.That(async () => await profiles.UpdateAsync(id, stored.ResourceVersion, item, cancellationToken: cancellationToken))
                .Throws<OpenShellException>();
            await Assert.That(stale!.IsConflict).IsTrue();

            // A provider can use the imported profile, which then cannot be deleted.
            var provider = await client.Providers.CreateAsync(
                new Provider
                {
                    Name = providerName,
                    Type = id,
                    ProfileWorkspace = GatewayFixture.Workspace,
                    Credentials = new Dictionary<string, string> { ["DN_IT_TOKEN"] = "dotnet-it-token" },
                },
                cancellationToken: cancellationToken);
            await Assert.That(provider.Type).IsEqualTo(id);
            var inUse = await Assert.That(async () => await profiles.DeleteAsync(id, cancellationToken: cancellationToken)).Throws<OpenShellException>();
            await Assert.That(inUse!.IsConflict).IsTrue();

            await client.Providers.DeleteAsync(providerName, cancellationToken: cancellationToken);
            var deletion = await profiles.DeleteAsync(id, cancellationToken: cancellationToken);
            await Assert.That(deletion.Outcome).IsEqualTo(DeletionOutcome.Completed);
            var missing = await Assert.That(async () => await profiles.GetAsync(id, cancellationToken)).Throws<OpenShellException>();
            await Assert.That(missing!.IsNotFound).IsTrue();
            var again = await profiles.DeleteAsync(id, new DeleteOptions { AllowMissing = true }, cancellationToken);
            await Assert.That(again.Outcome).IsEqualTo(DeletionOutcome.AlreadyAbsent);
        }
        finally
        {
            await DeleteProviderAsync(client, providerName);
            await profiles.DeleteAsync(id, new DeleteOptions { AllowMissing = true }, CancellationToken.None);
        }
    }

    [Test]
    [Timeout(600_000)]
    public async Task Providers_attach_update_and_detach_on_a_running_sandbox(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        var providerName = GatewayFixture.UniqueName("dn-attach");
        var readiness = TimeSpan.FromMinutes(3);
        var type = await ImportProfileAsync(client, withEndpoint: true, cancellationToken);
        await client.Providers.CreateAsync(
            new Provider { Name = providerName, Type = type, ProfileWorkspace = GatewayFixture.Workspace, Credentials = new Dictionary<string, string> { [TokenKey] = "nvapi-dotnet-it" } },
            cancellationToken: cancellationToken);
        try
        {
            await using var sandbox = await TestSandbox.CreateAsync(client, "dn-attach", cancellationToken);
            await Assert.That(await ReadKeyAsync(sandbox, cancellationToken)).IsEqualTo("NOT_SET");

            var attach = await client.SandboxProviders.AttachAsync(sandbox.Name, providerName, cancellationToken: cancellationToken);
            await Assert.That(attach.Attached).IsTrue();
            await Assert.That(attach.Receipt).IsNotNull();
            await Assert.That(attach.Receipt!.Kind).IsEqualTo(ProviderMutationKind.Attach);
            var attached = await client.SandboxProviders.WaitAsync(attach.Receipt, readiness, cancellationToken);
            await Assert.That(attached.Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
            await Assert.That(attached.Status.State).IsEqualTo(ProviderReadinessState.Ready);

            var status = await client.SandboxProviders.GetStatusAsync(sandbox.Name, providerName, cancellationToken: cancellationToken);
            await Assert.That(status.State).IsEqualTo(ProviderReadinessState.Ready);
            var listed = await client.SandboxProviders.List(sandbox.Name).ToListAsync(cancellationToken);
            await Assert.That(listed.Select(p => p.Name)).Contains(providerName);

            // Later exec launches see a placeholder, never the secret.
            var placeholder = await Poll.UntilAsync(
                async () =>
                {
                    var value = await ReadKeyAsync(sandbox, cancellationToken);
                    return (value.StartsWith(PlaceholderPrefix, StringComparison.Ordinal) ? value : null, value);
                },
                TimeSpan.FromMinutes(1),
                "the attached provider's placeholder in a new exec",
                cancellationToken);
            await Assert.That(placeholder).DoesNotContain("nvapi-dotnet-it");

            // Updating an attached provider returns one receipt per sandbox.
            var update = await client.Providers.UpdateAsync(
                new Provider { Name = providerName, Type = type, Credentials = new Dictionary<string, string> { [TokenKey] = "nvapi-dotnet-it-rotated" } },
                cancellationToken: cancellationToken);
            await Assert.That(update.TargetReceipts.Count).IsEqualTo(1);
            await Assert.That(update.TargetReceipts[0].Desired!.Sandbox).IsEqualTo(sandbox.Name);
            var updated = await client.SandboxProviders.WaitAllAsync(update.TargetReceipts, readiness, cancellationToken);
            await Assert.That(updated.Single().Outcome).IsEqualTo(ProviderWaitOutcome.Complete);

            var detach = await client.SandboxProviders.DetachAsync(sandbox.Name, providerName, cancellationToken: cancellationToken);
            await Assert.That(detach.Detached).IsTrue();
            await Assert.That(detach.Receipt).IsNotNull();
            var detached = await client.SandboxProviders.WaitAsync(detach.Receipt!, readiness, cancellationToken);
            await Assert.That(detached.Outcome).IsEqualTo(ProviderWaitOutcome.Complete);
            await Assert.That(detached.Status.State).IsEqualTo(ProviderReadinessState.Revoked);

            await Poll.UntilAsync(
                async () =>
                {
                    var value = await ReadKeyAsync(sandbox, cancellationToken);
                    return (value == "NOT_SET" ? value : null, value);
                },
                TimeSpan.FromMinutes(1),
                "the detached provider to disappear from a new exec",
                cancellationToken);
            var remaining = await client.SandboxProviders.List(sandbox.Name).ToListAsync(cancellationToken);
            await Assert.That(remaining.Select(p => p.Name)).DoesNotContain(providerName);
        }
        finally
        {
            await DeleteProviderAsync(client, providerName);
            await DeleteProfileAsync(client, type);
        }
    }

    private static async Task<string> ReadKeyAsync(TestSandbox sandbox, CancellationToken cancellationToken) =>
        (await sandbox.ShAsync("printf '%s' \"${DN_IT_TOKEN:-NOT_SET}\"", cancellationToken)).Trim();

    /// <summary>
    /// Imports a workspace profile that declares <see cref="TokenKey"/>; the gateway release ships no built-in profiles.
    /// Credentials of a profile without endpoints are withheld from sandboxes unless policy binds them, so a test that
    /// injects credentials asks for an endpoint.
    /// </summary>
    private static async Task<string> ImportProfileAsync(OpenShellClient client, bool withEndpoint, CancellationToken cancellationToken)
    {
        var id = GatewayFixture.UniqueName("dn-it-type");
        var profile = new ProviderProfile
        {
            Id = id,
            DisplayName = "dotnet integration provider type",
            Category = ProviderProfileCategory.Other,
            Credentials = [new ProfileCredential { Name = "token", EnvVars = [TokenKey], Required = true }],
            Endpoints = withEndpoint ? [new Openshell.Sandbox.V1.NetworkEndpoint { Host = "dn-it.example.com", Port = 443 }] : [],
            Binaries = withEndpoint ? [new Openshell.Sandbox.V1.NetworkBinary { Path = "/usr/bin/curl" }] : [],
        };
        var imported = await client.Providers.Profiles.ImportAsync([new ProfileImportItem(profile, $"{id}.yaml")], cancellationToken: cancellationToken);
        if (!imported.Imported)
        {
            throw new InvalidOperationException($"profile import failed: {string.Join("; ", imported.Diagnostics.Select(d => $"{d.Field}: {d.Message}"))}");
        }

        return id;
    }

    private static async Task DeleteProfileAsync(OpenShellClient client, string id) =>
        await client.Providers.Profiles.DeleteAsync(id, new DeleteOptions { AllowMissing = true }, CancellationToken.None);

    private static async Task DeleteProviderAsync(OpenShellClient client, string name) =>
        await client.Providers.DeleteAsync(name, new DeleteOptions { AllowMissing = true }, CancellationToken.None);
}
