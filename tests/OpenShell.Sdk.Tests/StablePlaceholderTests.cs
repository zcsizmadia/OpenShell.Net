// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using OpenShell.Testing;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class StablePlaceholderTests
{
    private static ProfileImportItem Item(bool stable, string id = "p") => new(new ProviderProfile
    {
        Id = id,
        DisplayName = "P",
        Credentials = [new ProfileCredential { Name = "token", EnvVars = ["TOKEN"], StablePlaceholder = stable }],
    });

    /// <summary>A gateway that records the order of profile calls and reports stable-placeholder support as given.</summary>
    private static FakeCallInvoker Gateway(bool supports, List<string> calls, List<Wire.ProviderProfileImportItem>? sent = null) =>
        new FakeCallInvoker()
            .Unary<Wire.LintProviderProfilesRequest, Wire.LintProviderProfilesResponse>("LintProviderProfiles", _ =>
            {
                calls.Add("lint");
                return new Wire.LintProviderProfilesResponse { Valid = true, SupportsStablePlaceholder = supports };
            })
            .Unary<Wire.ImportProviderProfilesRequest, Wire.ImportProviderProfilesResponse>("ImportProviderProfiles", r =>
            {
                calls.Add("import");
                sent?.AddRange(r.Profiles);
                return new Wire.ImportProviderProfilesResponse { Imported = true };
            })
            .Unary<Wire.UpdateProviderProfilesRequest, Wire.UpdateProviderProfilesResponse>("UpdateProviderProfiles", r =>
            {
                calls.Add("update");
                sent?.Add(r.Profile);
                return new Wire.UpdateProviderProfilesResponse { Updated = true, Profile = r.Profile.Profile };
            });

    [Test]
    public async Task Opting_in_checks_gateway_support_before_import_and_update()
    {
        var calls = new List<string>();
        var sent = new List<Wire.ProviderProfileImportItem>();
        using var client = ClientTests.NewClient(Gateway(supports: true, calls, sent));

        await client.Providers.Profiles.ImportAsync([Item(stable: true)]);
        var updated = await client.Providers.Profiles.UpdateAsync("p", 1, Item(stable: true));

        await Assert.That(string.Join(",", calls)).IsEqualTo("lint,import,lint,update");
        await Assert.That(sent.All(i => i.Profile.Credentials.Single().StablePlaceholder)).IsTrue();
        await Assert.That(updated.Profile!.Credentials.Single().StablePlaceholder).IsTrue();
    }

    [Test]
    public async Task Without_an_opt_in_nothing_extra_is_sent()
    {
        var calls = new List<string>();
        using var client = ClientTests.NewClient(Gateway(supports: false, calls));

        await client.Providers.Profiles.ImportAsync([Item(stable: false)]);
        await client.Providers.Profiles.UpdateAsync("p", 1, Item(stable: false));

        await Assert.That(string.Join(",", calls)).IsEqualTo("import,update");
    }

    [Test]
    public async Task An_older_gateway_fails_client_side_before_anything_is_persisted()
    {
        var calls = new List<string>();
        using var client = ClientTests.NewClient(Gateway(supports: false, calls));

        var import = await Assert.That(async () => await client.Providers.Profiles.ImportAsync([Item(stable: false, id: "a"), Item(stable: true, id: "b")])).Throws<OpenShellException>();
        var update = await Assert.That(async () => await client.Providers.Profiles.UpdateAsync("p", 1, Item(stable: true))).Throws<OpenShellException>();
        var lint = await Assert.That(async () => await client.Providers.Profiles.LintAsync([Item(stable: true)])).Throws<OpenShellException>();

        await Assert.That(import!.Kind).IsEqualTo(OpenShellErrorKind.Unimplemented);
        await Assert.That(import.Message).IsEqualTo("gateway does not support stable_placeholder; upgrade the gateway before enabling it");
        await Assert.That(update!.Kind).IsEqualTo(OpenShellErrorKind.Unimplemented);
        await Assert.That(lint!.Kind).IsEqualTo(OpenShellErrorKind.Unimplemented);
        await Assert.That(string.Join(",", calls)).IsEqualTo("lint,lint,lint");
    }

    [Test]
    public async Task Lint_reports_support_without_an_opt_in()
    {
        var calls = new List<string>();
        using var older = ClientTests.NewClient(Gateway(supports: false, calls));
        using var newer = ClientTests.NewClient(Gateway(supports: true, calls));

        var before = await older.Providers.Profiles.LintAsync([Item(stable: false)]);
        var after = await newer.Providers.Profiles.LintAsync([Item(stable: true)]);

        await Assert.That(before.SupportsStablePlaceholder).IsFalse();
        await Assert.That(after.SupportsStablePlaceholder).IsTrue();
        await Assert.That(after.Valid).IsTrue();
    }

    [Test]
    public async Task The_fake_supports_stable_placeholders_and_stores_the_opt_in()
    {
        using var client = new FakeOpenShellClient();

        var lint = await client.Providers.Profiles.LintAsync([Item(stable: true)]);
        var import = await client.Providers.Profiles.ImportAsync([Item(stable: true)]);

        await Assert.That(lint.SupportsStablePlaceholder).IsTrue();
        await Assert.That(lint.Valid).IsTrue();
        await Assert.That(import.Imported).IsTrue();
        await Assert.That((await client.Providers.Profiles.GetAsync("p")).Credentials.Single().StablePlaceholder).IsTrue();
    }

    [Test]
    public async Task The_fake_applies_the_gateway_rules()
    {
        using var client = new FakeOpenShellClient();
        var profile = new ProviderProfile
        {
            Id = "p",
            DisplayName = "P",
            Credentials =
            [
                new ProfileCredential
                {
                    Name = "access",
                    EnvVars = ["ACCESS"],
                    StablePlaceholder = true,
                    Refresh = new ProfileCredentialRefresh
                    {
                        Strategy = RefreshStrategy.OAuth2RefreshToken,
                        AdditionalOutputs = [new ProfileCredentialRefreshOutput("id_token", "id")],
                    },
                },
                new ProfileCredential { Name = "id", EnvVars = ["ID"], StablePlaceholder = true },
                new ProfileCredential { Name = "nameonly", StablePlaceholder = true },
                new ProfileCredential { Name = "grant", EnvVars = ["GRANT"], StablePlaceholder = true, TokenGrant = new CredentialTokenGrant() },
                new ProfileCredential { Name = "static", EnvVars = ["STATIC"], StablePlaceholder = true, Refresh = new ProfileCredentialRefresh { Strategy = RefreshStrategy.Static } },
            ],
        };

        var lint = await client.Providers.Profiles.LintAsync([new ProfileImportItem(profile, "f.yaml")]);

        await Assert.That(lint.Valid).IsFalse();
        await Assert.That(lint.Diagnostics.Where(d => d.Field == "credentials.stable_placeholder").Select(d => d.Message)).IsEquivalentTo(
        [
            "stable_placeholder is incompatible with gateway-managed credential refresh",
            "stable_placeholder is incompatible with gateway-managed credential refresh",
            "stable_placeholder requires at least one environment variable",
            "stable_placeholder is incompatible with token_grant credentials",
        ]);
    }
}
