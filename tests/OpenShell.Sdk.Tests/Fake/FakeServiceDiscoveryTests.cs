// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using OpenShell.Testing;
using OpenShell.Tests.Support;

namespace OpenShell.Tests;

public class FakeServiceDiscoveryTests
{
    [Test]
    public async Task Snapshot_lists_the_domain_services_of_ready_sandboxes()
    {
        using var client = FakeCoreTests.New();
        await client.Sandboxes.CreateAsync("a", FakeCoreTests.Spec());
        await client.Services.ExposeAsync("a", "web", 8080, new ExposeServiceOptions { Domain = true });
        await client.Services.ExposeAsync("a", "", 3000, new ExposeServiceOptions { Domain = true });
        await client.Services.ExposeAsync("a", "path-only", 9000);
        await client.InWorkspace("other").Sandboxes.CreateAsync("b", FakeCoreTests.Spec());
        await client.InWorkspace("other").Services.ExposeAsync("b", "web", 80, new ExposeServiceOptions { Domain = true });

        var snapshot = await client.ServiceDiscovery.GetSnapshotAsync();
        var again = await client.ServiceDiscovery.GetSnapshotAsync();

        await Assert.That(snapshot.Routes.Select(r => r.Hostname)).IsEquivalentTo(new[]
        {
            "default--a.services.fake.example.com",
            "default--a--web.services.fake.example.com",
        });
        var web = snapshot.Routes.Single(r => r.Name == "web");
        await Assert.That(web.Workspace).IsEqualTo("default");
        await Assert.That(web.Sandbox).IsEqualTo("a");
        await Assert.That(web.Endpoint).IsEqualTo(new ServiceRouteEndpoint("supervisor.fake.gateway.local", 8443));
        await Assert.That(web.InstallationVersion.Length).IsGreaterThan(0);
        await Assert.That(web.DeclarationRevision).IsGreaterThan(0ul);
        await Assert.That(web.ExpiresAt).IsNull();
        await Assert.That(snapshot.Revision.Length).IsEqualTo(16);
        await Assert.That(again.Revision).IsEqualTo(snapshot.Revision);
        await Assert.That(client.Gateway.Calls.Count(c => c.Operation == "ServiceDiscovery.GetSnapshot")).IsEqualTo(2);
    }

    [Test]
    public async Task Sandboxes_that_are_not_ready_have_no_routes()
    {
        using var client = FakeCoreTests.New(new FakeGatewayOptions { ProvisioningDelay = TimeSpan.FromHours(1) });
        await client.Sandboxes.CreateAsync("a", FakeCoreTests.Spec());
        await client.Services.ExposeAsync("a", "web", 8080, new ExposeServiceOptions { Domain = true });

        var snapshot = await client.ServiceDiscovery.GetSnapshotAsync();

        await Assert.That(snapshot.Routes).IsEmpty();
    }

    [Test]
    public async Task Watch_sends_the_current_view_then_each_change()
    {
        using var client = FakeCoreTests.New();
        await client.Sandboxes.CreateAsync("a", FakeCoreTests.Spec());
        using var cts = new CancellationTokenSource();
        await using var watch = client.ServiceDiscovery.WatchAsync(cancellationToken: cts.Token).GetAsyncEnumerator(cts.Token);

        await Assert.That(await watch.MoveNextAsync().AsTask().WithinBudget()).IsTrue();
        var initial = watch.Current;
        var next = watch.MoveNextAsync().AsTask();

        // A path-only service changes no route, so only the domain service is seen.
        await client.Services.ExposeAsync("a", "path-only", 9000);
        await client.Services.ExposeAsync("a", "web", 8080, new ExposeServiceOptions { Domain = true });
        await Assert.That(await next.WithinBudget()).IsTrue();
        var exposed = watch.Current;

        next = watch.MoveNextAsync().AsTask();
        await client.Services.DeleteAsync("a", "web");
        await Assert.That(await next.WithinBudget()).IsTrue();
        var removed = watch.Current;

        await Assert.That(initial.Routes).IsEmpty();
        await Assert.That(exposed.Routes.Single().Name).IsEqualTo("web");
        await Assert.That(removed.Routes).IsEmpty();
        await Assert.That(removed.Revision).IsEqualTo(initial.Revision);
        await Assert.That(client.Gateway.Calls.Count(c => c.Operation == "ServiceDiscovery.Watch")).IsEqualTo(1);
        await cts.CancelAsync();
    }

    [Test]
    public async Task Disabled_discovery_and_injected_failures_are_typed()
    {
        using var disabled = FakeCoreTests.New(new FakeGatewayOptions { ServiceDiscoveryEnabled = false });
        using var client = FakeCoreTests.New();
        client.Gateway.FailNext("ServiceDiscovery.*", new OpenShellException(OpenShellErrorKind.Unavailable, "down"));

        var snapshot = await Assert.That(async () => await disabled.ServiceDiscovery.GetSnapshotAsync()).Throws<OpenShellException>();
        var watch = await Assert.That(async () =>
        {
            await foreach (var _ in disabled.ServiceDiscovery.WatchAsync())
            {
                // The disabled gateway rejects the watch before sending a snapshot.
            }
        }).Throws<OpenShellException>();
        var injected = await Assert.That(async () => await client.ServiceDiscovery.GetSnapshotAsync()).Throws<OpenShellException>();

        await Assert.That(snapshot!.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(watch!.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(injected!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
    }

    [Test]
    public async Task Unknown_workspaces_are_rejected_when_not_auto_created()
    {
        using var client = FakeCoreTests.New(new FakeGatewayOptions { AutoCreateWorkspaces = false });

        var ex = await Assert.That(async () => await client.InWorkspace("missing").ServiceDiscovery.GetSnapshotAsync()).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.NotFound);
    }
}
