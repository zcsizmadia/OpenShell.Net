// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

/// <summary>The <c>openshell.v1.ServiceDiscovery</c> service, new in OpenShell v0.1.9.</summary>
public class ServiceDiscoveryClientTests
{
    private static readonly DateTimeOffset Expiry = DateTimeOffset.UnixEpoch.AddDays(3);

    private static Wire.ServiceRouteSnapshot Snapshot(string revision, params string[] sandboxes)
    {
        var snapshot = new Wire.ServiceRouteSnapshot { Revision = revision };
        foreach (var sandbox in sandboxes)
        {
            snapshot.Routes.Add(new Wire.DiscoveredServiceRoute
            {
                Workspace = "ws",
                Sandbox = sandbox,
                Name = "web",
                Hostname = $"ws--{sandbox}--web.services.example.com",
                Endpoint = new Wire.ServiceRouteEndpoint { Host = "10.0.0.5", Port = 8443 },
                InstallationVersion = "iv-" + sandbox,
                DeclarationRevision = 7,
                ExpiresAt = Timestamp.FromDateTimeOffset(Expiry),
            });
        }

        return snapshot;
    }

    private static Wire.WatchServiceRoutesResponse Watched(string revision, params string[] sandboxes) =>
        new() { Snapshot = Snapshot(revision, sandboxes) };

    private static IEnumerable<Wire.WatchServiceRoutesResponse> Fail(StatusCode code, params Wire.WatchServiceRoutesResponse[] before)
    {
        foreach (var response in before)
        {
            yield return response;
        }

        throw new RpcException(new Status(code, "stream failure"));
    }

    /// <summary>Takes <paramref name="count"/> snapshots on a background thread, advancing <paramref name="time"/> past any backoff.</summary>
    private static async Task<List<ServiceRouteSnapshot>> TakeAsync(IAsyncEnumerable<ServiceRouteSnapshot> source, int count, FakeTimeProvider? time = null)
    {
        var task = Task.Run(async () =>
        {
            var all = new List<ServiceRouteSnapshot>();
            await foreach (var snapshot in source)
            {
                all.Add(snapshot);
                if (all.Count == count)
                {
                    break;
                }
            }

            return all;
        });
        if (time is not null)
        {
            await time.AdvanceUntilDoneAsync(task, TimeSpan.FromSeconds(1), pauseBetweenSteps: true);
        }

        return await task.WithinBudget();
    }

    [Test]
    public async Task Snapshot_sends_the_workspace_and_maps_every_field()
    {
        Wire.GetServiceRouteSnapshotRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetServiceRouteSnapshotRequest, Wire.GetServiceRouteSnapshotResponse>("GetServiceRouteSnapshot", r =>
        {
            seen = r;
            return new Wire.GetServiceRouteSnapshotResponse { Snapshot = Snapshot("rev-1", "sb") };
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var snapshot = await client.ServiceDiscovery.GetSnapshotAsync();

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(invoker.UnaryFullNames.Single()).IsEqualTo("/openshell.v1.ServiceDiscovery/GetServiceRouteSnapshot");
        await Assert.That(snapshot.Revision).IsEqualTo("rev-1");
        var route = snapshot.Routes.Single();
        await Assert.That(route.Workspace).IsEqualTo("ws");
        await Assert.That(route.Sandbox).IsEqualTo("sb");
        await Assert.That(route.Name).IsEqualTo("web");
        await Assert.That(route.Hostname).IsEqualTo("ws--sb--web.services.example.com");
        await Assert.That(route.Endpoint).IsEqualTo(new ServiceRouteEndpoint("10.0.0.5", 8443));
        await Assert.That(route.InstallationVersion).IsEqualTo("iv-sb");
        await Assert.That(route.DeclarationRevision).IsEqualTo(7ul);
        await Assert.That(route.ExpiresAt).IsEqualTo(Expiry);
    }

    [Test]
    public async Task Snapshot_tolerates_missing_messages()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetServiceRouteSnapshotRequest, Wire.GetServiceRouteSnapshotResponse>("GetServiceRouteSnapshot", _ =>
            new Wire.GetServiceRouteSnapshotResponse());
        using var client = ClientTests.NewClient(invoker);

        var empty = await client.ServiceDiscovery.GetSnapshotAsync();
        var partial = ServiceDiscoveryClient.ToDomain(new Wire.ServiceRouteSnapshot { Routes = { new Wire.DiscoveredServiceRoute { Sandbox = "sb" } } });

        await Assert.That(empty.Routes).IsEmpty();
        await Assert.That(empty.Revision).IsEqualTo("");
        await Assert.That(partial.Routes.Single().Endpoint).IsNull();
        await Assert.That(partial.Routes.Single().ExpiresAt).IsNull();
    }

    [Test]
    public async Task Snapshot_failure_is_typed()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.GetServiceRouteSnapshotRequest, Wire.GetServiceRouteSnapshotResponse>(
            "GetServiceRouteSnapshot", StatusCode.FailedPrecondition, "service discovery requires the supervisor app listener to be enabled");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.ServiceDiscovery.GetSnapshotAsync()).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
    }

    [Test]
    public async Task Watch_reopens_when_the_gateway_ends_it_and_skips_a_repeated_revision()
    {
        var opened = 0;
        var invoker = new FakeCallInvoker().Stream<Wire.WatchServiceRoutesRequest, Wire.WatchServiceRoutesResponse>("WatchServiceRoutes", r =>
            Interlocked.Increment(ref opened) == 1
                ? [Watched("a"), Watched("b", "sb")]
                : [Watched("b", "sb"), Watched("c", "sb", "other")]);
        using var client = ClientTests.NewClient(invoker, "ws");

        var snapshots = await TakeAsync(client.ServiceDiscovery.WatchAsync(), 3);

        await Assert.That(snapshots.Select(s => s.Revision)).IsEquivalentTo(new[] { "a", "b", "c" });
        await Assert.That(snapshots[2].Routes.Count).IsEqualTo(2);
        await Assert.That(invoker.CountOf("WatchServiceRoutes")).IsEqualTo(2);
        var request = (Wire.WatchServiceRoutesRequest)invoker.Calls.First(c => c.Method == "WatchServiceRoutes").Request;
        await Assert.That(request.WorkspaceScope.Workspace).IsEqualTo("ws");
    }

    [Test]
    public async Task Watch_without_reconnect_ends_with_the_gateway_stream()
    {
        var invoker = new FakeCallInvoker().Stream<Wire.WatchServiceRoutesRequest, Wire.WatchServiceRoutesResponse>("WatchServiceRoutes", _ =>
            [Watched("a"), Watched("a"), Watched("b")]);
        using var client = ClientTests.NewClient(invoker);

        var snapshots = await TakeAsync(client.ServiceDiscovery.WatchAsync(new ServiceRouteWatchOptions { Reconnect = false }), int.MaxValue);

        await Assert.That(snapshots.Select(s => s.Revision)).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(invoker.CountOf("WatchServiceRoutes")).IsEqualTo(1);
    }

    [Test]
    public async Task Watch_reconnects_after_the_gateway_becomes_unavailable()
    {
        var time = new FakeTimeProvider();
        var opened = 0;
        var invoker = new FakeCallInvoker().Stream<Wire.WatchServiceRoutesRequest, Wire.WatchServiceRoutesResponse>("WatchServiceRoutes", _ =>
            Interlocked.Increment(ref opened) switch
            {
                1 => Fail(StatusCode.Unavailable, Watched("a")),
                2 => Fail(StatusCode.Unavailable),
                _ => [Watched("a"), Watched("b")],
            });
        using var client = ClientTests.NewClient(invoker, time: time);

        var snapshots = await TakeAsync(client.ServiceDiscovery.WatchAsync(), 2, time);

        await Assert.That(snapshots.Select(s => s.Revision)).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(invoker.CountOf("WatchServiceRoutes")).IsEqualTo(3);
    }

    [Test]
    public async Task Watch_backs_off_when_a_stream_ends_without_a_snapshot()
    {
        var time = new FakeTimeProvider();
        var opened = 0;
        var invoker = new FakeCallInvoker().Stream<Wire.WatchServiceRoutesRequest, Wire.WatchServiceRoutesResponse>("WatchServiceRoutes", _ =>
            Interlocked.Increment(ref opened) == 1 ? [] : [Watched("a")]);
        using var client = ClientTests.NewClient(invoker, time: time);

        var snapshots = await TakeAsync(client.ServiceDiscovery.WatchAsync(), 1, time);

        await Assert.That(snapshots.Single().Revision).IsEqualTo("a");
        await Assert.That(time.GetUtcNow() - time.Start).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100));
    }

    [Test]
    public async Task Watch_gives_up_after_the_reconnect_limit()
    {
        var time = new FakeTimeProvider();
        var invoker = new FakeCallInvoker().Stream<Wire.WatchServiceRoutesRequest, Wire.WatchServiceRoutesResponse>("WatchServiceRoutes", _ =>
            Fail(StatusCode.Unavailable));
        using var client = ClientTests.NewClient(invoker, time: time);

        var watch = TakeAsync(client.ServiceDiscovery.WatchAsync(new ServiceRouteWatchOptions { MaxReconnectAttempts = 2 }), 1, time);
        var ex = await Assert.That(async () => await watch).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
        await Assert.That(invoker.CountOf("WatchServiceRoutes")).IsEqualTo(3);
    }

    [Test]
    public async Task Watch_reopens_once_credentials_expire_mid_stream()
    {
        var opened = 0;
        var invoker = new FakeCallInvoker().Stream<Wire.WatchServiceRoutesRequest, Wire.WatchServiceRoutesResponse>("WatchServiceRoutes", _ =>
            Interlocked.Increment(ref opened) == 1 ? Fail(StatusCode.Unauthenticated, Watched("a")) : [Watched("a"), Watched("b")]);
        using var client = ClientTests.NewClient(invoker);

        var snapshots = await TakeAsync(client.ServiceDiscovery.WatchAsync(), 2);

        await Assert.That(snapshots.Select(s => s.Revision)).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(invoker.CountOf("WatchServiceRoutes")).IsEqualTo(2);
    }

    [Test]
    [Arguments(StatusCode.Unauthenticated, true, OpenShellErrorKind.Unauthenticated)]
    [Arguments(StatusCode.PermissionDenied, false, OpenShellErrorKind.PermissionDenied)]
    [Arguments(StatusCode.ResourceExhausted, false, null)]
    public async Task Watch_surfaces_failures_it_does_not_recover(StatusCode code, bool afterSnapshot, OpenShellErrorKind? kind)
    {
        var invoker = new FakeCallInvoker().Stream<Wire.WatchServiceRoutesRequest, Wire.WatchServiceRoutesResponse>("WatchServiceRoutes", _ =>
            afterSnapshot ? Fail(code, Watched("a")) : Fail(code));
        using var client = ClientTests.NewClient(invoker);

        // Without reconnect, credentials that expire mid-stream end the watch instead of reopening it.
        var watch = TakeAsync(client.ServiceDiscovery.WatchAsync(new ServiceRouteWatchOptions { Reconnect = false }), int.MaxValue);
        var ex = await Assert.That(async () => await watch).Throws<OpenShellException>();

        if (kind is { } expected)
        {
            await Assert.That(ex!.Kind).IsEqualTo(expected);
        }

        await Assert.That(invoker.CountOf("WatchServiceRoutes")).IsEqualTo(1);
    }

    [Test]
    public async Task Raw_service_discovery_client_calls_the_separate_service()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetServiceRouteSnapshotRequest, Wire.GetServiceRouteSnapshotResponse>("GetServiceRouteSnapshot", _ =>
            new Wire.GetServiceRouteSnapshotResponse { Snapshot = Snapshot("r") });
        using var client = ClientTests.NewClient(invoker);

        var response = await client.RawServiceDiscovery.GetServiceRouteSnapshotAsync(new Wire.GetServiceRouteSnapshotRequest());

        await Assert.That(response.Snapshot.Revision).IsEqualTo("r");
        await Assert.That(invoker.UnaryFullNames.Single()).IsEqualTo("/openshell.v1.ServiceDiscovery/GetServiceRouteSnapshot");
    }

    [Test]
    public async Task Workspace_views_scope_discovery()
    {
        Wire.GetServiceRouteSnapshotRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.GetServiceRouteSnapshotRequest, Wire.GetServiceRouteSnapshotResponse>("GetServiceRouteSnapshot", r =>
        {
            seen = r;
            return new Wire.GetServiceRouteSnapshotResponse();
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        await client.InWorkspace("other").ServiceDiscovery.GetSnapshotAsync();

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("other");
    }
}
