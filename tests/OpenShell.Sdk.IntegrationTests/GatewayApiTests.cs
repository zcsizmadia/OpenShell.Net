// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

namespace OpenShell.IntegrationTests;

/// <summary>Error mapping, pagination, services, and configuration reads against a real gateway.</summary>
public class GatewayApiTests
{
    [Test]
    [Timeout(600_000)]
    public async Task Gateway_errors_map_to_error_kinds(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();

        var invalid = await Assert.That(async () => await client.Sandboxes.CreateAsync(
            "this-name-is-far-too-long-for-the-gateway", new SandboxSpec { Image = GatewayFixture.Image }, cancellationToken: cancellationToken))
            .Throws<OpenShellException>();
        await Assert.That(invalid!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);

        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-dup", cancellationToken);
        var duplicate = await Assert.That(async () => await client.Sandboxes.CreateAsync(
            sandbox.Name, new SandboxSpec { Image = GatewayFixture.Image }, cancellationToken: cancellationToken))
            .Throws<OpenShellException>();
        await Assert.That(duplicate!.IsAlreadyExists).IsTrue();
    }

    [Test]
    [Timeout(600_000)]
    public async Task Sandbox_listing_pages_with_a_label_selector(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        var run = GatewayFixture.UniqueName("page");
        var labels = new CreateSandboxOptions { Labels = new Dictionary<string, string> { ["dotnet-it-run"] = run } };
        var names = new[] { GatewayFixture.UniqueName("dn-page"), GatewayFixture.UniqueName("dn-page") };
        try
        {
            foreach (var name in names)
            {
                await client.Sandboxes.CreateAsync(name, new SandboxSpec { Image = GatewayFixture.Image }, labels, cancellationToken);
            }

            var pages = new List<Page<Sandbox>>();
            await foreach (var page in client.Sandboxes.List(new ListOptions { PageSize = 1, LabelSelector = $"dotnet-it-run={run}" }).PagesAsync(cancellationToken))
            {
                pages.Add(page);
            }

            await Assert.That(pages.Count).IsGreaterThanOrEqualTo(2);
            await Assert.That(pages.All(p => p.Items.Count <= 1)).IsTrue();
            await Assert.That(pages.SelectMany(p => p.Items).Select(s => s.Name).Order().ToArray()).IsEquivalentTo(names.Order().ToArray());

            var ids = await client.Sandboxes.ListIdsAsync(new ListOptions { LabelSelector = $"dotnet-it-run={run}" }, cancellationToken);
            await Assert.That(ids.Count).IsEqualTo(2);
        }
        finally
        {
            foreach (var name in names)
            {
                var deletion = await client.Sandboxes.DeleteAsync(name, new DeleteOptions { AllowMissing = true }, CancellationToken.None);
                if (deletion.Outcome == DeletionOutcome.Accepted)
                {
                    await client.Sandboxes.WaitDeletedAsync(name, new WaitOptions { Timeout = TimeSpan.FromMinutes(2) }, CancellationToken.None);
                }
            }
        }
    }

    [Test]
    [Timeout(600_000)]
    public async Task Services_expose_get_list_and_delete(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-svc", cancellationToken);

        var exposed = await client.Services.ExposeAsync(sandbox.Name, "web", 18080, cancellationToken: cancellationToken);
        await Assert.That(exposed.Name).IsEqualTo("web");
        await Assert.That(exposed.TargetPort).IsEqualTo(18080u);

        var fetched = await client.Services.GetAsync(sandbox.Name, "web", cancellationToken);
        await Assert.That(fetched.TargetPort).IsEqualTo(18080u);
        var listed = await client.Services.List(sandbox.Name).ToListAsync(cancellationToken);
        await Assert.That(listed.Select(s => s.Name)).Contains("web");

        var deletion = await client.Services.DeleteAsync(sandbox.Name, "web", cancellationToken: cancellationToken);
        await Assert.That(deletion.Outcome).IsNotEqualTo(DeletionOutcome.Unspecified);
        var missing = await Assert.That(async () => await client.Services.GetAsync(sandbox.Name, "web", cancellationToken)).Throws<OpenShellException>();
        await Assert.That(missing!.IsNotFound).IsTrue();
    }

    [Test]
    [Timeout(600_000)]
    public async Task Config_and_policy_reads_succeed(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-config", cancellationToken);

        var gateway = await client.Config.GetGatewayAsync(cancellationToken);
        await Assert.That(gateway.Settings).IsNotNull();

        var config = await client.Config.GetSandboxAsync(sandbox.Name, cancellationToken);
        var status = await client.Policy.GetStatusAsync(sandbox.Name, cancellationToken: cancellationToken);
        await Assert.That(status.ActiveVersion).IsEqualTo(config.PolicyVersion);

        var revisions = await client.Policy.ListRevisions(sandbox.Name).ToListAsync(cancellationToken);
        await Assert.That(revisions).IsNotNull();
        var draft = await client.Policy.GetDraftAsync(sandbox.Name, cancellationToken: cancellationToken);
        await Assert.That(draft).IsNotNull();
    }
}
