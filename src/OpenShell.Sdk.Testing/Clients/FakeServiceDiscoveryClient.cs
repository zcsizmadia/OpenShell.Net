// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenShell.Testing;

public sealed partial class FakeGateway
{
    /// <summary>The host every fake route's supervisor endpoint names.</summary>
    internal const string FakeSupervisorHost = "supervisor.fake.gateway.local";

    /// <summary>The port every fake route's supervisor endpoint names.</summary>
    internal const uint FakeSupervisorPort = 8443;

    /// <summary>
    /// The routes the gateway would discover in <paramref name="workspace"/>: one per domain service of a Ready sandbox,
    /// as a supervisor installs them. Leases are not modelled, so <see cref="DiscoveredServiceRoute.ExpiresAt"/> is null.
    /// The caller holds <see cref="Sync"/>.
    /// </summary>
    internal ServiceRouteSnapshot RouteSnapshotLocked(string workspace)
    {
        var routes = Services.Values
            .Where(s => s.Workspace == workspace && s.Domain
                && Sandboxes.TryGetValue(new Key(s.Workspace, s.Sandbox), out var sandbox) && sandbox.Phase == SandboxPhase.Ready)
            .OrderBy(s => s.Sandbox, StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new DiscoveredServiceRoute
            {
                Workspace = s.Workspace,
                Sandbox = s.Sandbox,
                Name = s.Name,
                Hostname = RouteHostname(s),
                Endpoint = new ServiceRouteEndpoint(FakeSupervisorHost, FakeSupervisorPort),
                InstallationVersion = string.Create(CultureInfo.InvariantCulture, $"{s.SandboxId}:{s.ResourceVersion}"),
                DeclarationRevision = s.ResourceVersion,
            })
            .ToArray();
        var identity = string.Join('\n', routes.Select(r => $"{r.Sandbox}\0{r.Name}\0{r.Hostname}\0{r.InstallationVersion}"));
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        return new ServiceRouteSnapshot(routes, revision);
    }

    /// <summary>A discovery call's checks and snapshot. The caller holds <see cref="Sync"/>.</summary>
    internal ServiceRouteSnapshot DiscoverLocked(string workspace)
    {
        if (!Options.ServiceDiscoveryEnabled)
        {
            throw Conflict("service discovery requires the supervisor app listener to be enabled");
        }

        _ = RequireWorkspace(workspace);
        return RouteSnapshotLocked(workspace);
    }

    /// <summary>Reads the route snapshot of <paramref name="workspace"/> without recording a call, for open watches.</summary>
    internal ServiceRouteSnapshot ReadRoutes(string workspace)
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SettleLocked();
            return RouteSnapshotLocked(workspace);
        }
    }

    /// <summary>The gateway's readable route label (<c>workspace--sandbox[--service]</c>) under a fake service domain.</summary>
    internal static string RouteHostname(ServiceEntry s) =>
        (s.Name.Length > 0 ? $"{s.Workspace}--{s.Sandbox}--{s.Name}" : $"{s.Workspace}--{s.Sandbox}") + ".services.fake.example.com";
}

internal sealed class FakeServiceDiscoveryClient(FakeGateway gateway, string workspace) : IServiceDiscoveryClient
{
    public ValueTask<ServiceRouteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(gateway.Run("ServiceDiscovery.GetSnapshot", workspace, null, cancellationToken, () =>
        {
            return gateway.DiscoverLocked(workspace);
        }));

    public IAsyncEnumerable<ServiceRouteSnapshot> WatchAsync(ServiceRouteWatchOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamAsync(cancellationToken);

    // The fake watch has no lifetime bound, so it never ends on its own and needs no reconnect.
    private async IAsyncEnumerable<ServiceRouteSnapshot> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var snapshot = gateway.Run("ServiceDiscovery.Watch", workspace, null, cancellationToken, () =>
        {
            return gateway.DiscoverLocked(workspace);
        });
        yield return snapshot;
        var revision = snapshot.Revision;
        while (true)
        {
            var changed = gateway.Changed;
            snapshot = gateway.ReadRoutes(workspace);
            if (snapshot.Revision != revision)
            {
                revision = snapshot.Revision;
                yield return snapshot;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
