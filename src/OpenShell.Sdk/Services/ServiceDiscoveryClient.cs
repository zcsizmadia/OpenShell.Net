// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>
/// The address an ingress proxy dials for a discovered route: a supervisor TLS endpoint, never a workload port. Send
/// <see cref="DiscoveredServiceRoute.Hostname"/> as the TLS server name and HTTP authority.
/// </summary>
/// <param name="Host">Supervisor host.</param>
/// <param name="Port">Supervisor TLS port.</param>
public sealed record ServiceRouteEndpoint(string Host, uint Port);

/// <summary>
/// An application route a sandbox supervisor installed and the gateway accepted. It is evidence of installation, not
/// of application health or ingress readiness, and it stops being valid at <see cref="ExpiresAt"/>.
/// </summary>
public sealed record DiscoveredServiceRoute
{
    /// <summary>Workspace the route belongs to.</summary>
    public required string Workspace { get; init; }

    /// <summary>Sandbox name.</summary>
    public required string Sandbox { get; init; }

    /// <summary>Canonical service name; empty for the unnamed service.</summary>
    public string Name { get; init; } = "";

    /// <summary>Public hostname; send it as the TLS server name and HTTP authority.</summary>
    public string Hostname { get; init; } = "";

    /// <summary>The supervisor endpoint to dial; null when the gateway omitted it.</summary>
    public ServiceRouteEndpoint? Endpoint { get; init; }

    /// <summary>Opaque identity of the installation; changes when the owner, backend, or declaration is replaced, not on lease renewal.</summary>
    public string InstallationVersion { get; init; } = "";

    /// <summary>Resource version of the service declaration.</summary>
    public ulong DeclarationRevision { get; init; }

    /// <summary>When the installation lease expires; do not use the route after this time.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>A complete view of a workspace's discovered routes. Each snapshot replaces the previous one.</summary>
/// <param name="Routes">The installed, unexpired routes.</param>
/// <param name="Revision">Opaque equality token for this view, lease expiries included. It implies no order across gateway replicas or restarts.</param>
public sealed record ServiceRouteSnapshot(IReadOnlyList<DiscoveredServiceRoute> Routes, string Revision);

/// <summary>Options for <see cref="IServiceDiscoveryClient.WatchAsync"/>.</summary>
public sealed record ServiceRouteWatchOptions
{
    /// <summary>
    /// Reopen the watch when the gateway ends it. The gateway bounds each watch (one minute in v0.1.9), so with
    /// <see langword="false"/> the enumeration ends at that bound. Defaults to <see langword="true"/>.
    /// </summary>
    public bool Reconnect { get; init; } = true;

    /// <summary>
    /// Maximum consecutive reconnect attempts after the connection drops. Null retries until cancelled. The counter
    /// resets whenever a snapshot arrives.
    /// </summary>
    public int? MaxReconnectAttempts { get; init; }
}

/// <summary>
/// Service route discovery for ingress proxies and controllers (the <c>openshell.v1.ServiceDiscovery</c> service, new in
/// OpenShell v0.1.9), scoped to one workspace. The gateway serves it only when its supervisor app listener is enabled
/// and otherwise fails with <c>FailedPrecondition</c>. The caller needs the workspace <c>user</c> role.
/// </summary>
public interface IServiceDiscoveryClient
{
    /// <summary>Reads the installed, unexpired routes of the workspace once.</summary>
    ValueTask<ServiceRouteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams full replacement snapshots: the current view first, then each changed view, including lease-only
    /// renewals. The watch reopens when the gateway ends it and reconnects with exponential backoff when the gateway
    /// becomes unavailable or rejects credentials that expired during the watch; a snapshot whose revision matches the
    /// last one yielded is skipped. Other failures end the
    /// enumeration with an <see cref="OpenShellException"/>. Errors are never empty snapshots: while the watch fails,
    /// drop cached routes at their <see cref="DiscoveredServiceRoute.ExpiresAt"/>.
    /// </summary>
    IAsyncEnumerable<ServiceRouteSnapshot> WatchAsync(ServiceRouteWatchOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed partial class ServiceDiscoveryClient(ClientCore core, string workspace) : IServiceDiscoveryClient
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(2);

    [LoggerMessage(Level = LogLevel.Information, Message = "Service route watch for workspace {Workspace} lost its connection; reconnecting in {DelayMs} ms (attempt {Attempt})")]
    private static partial void LogReconnect(ILogger logger, string workspace, double delayMs, int attempt);

    public async ValueTask<ServiceRouteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var request = new Wire.GetServiceRouteSnapshotRequest { WorkspaceScope = ClientCore.Scope(workspace) };
        var response = await core.UnaryAsync(o => core.ServiceDiscovery.GetServiceRouteSnapshotAsync(request, o), cancellationToken).ConfigureAwait(false);
        return ToDomain(response.Snapshot);
    }

    public IAsyncEnumerable<ServiceRouteSnapshot> WatchAsync(ServiceRouteWatchOptions? options = null, CancellationToken cancellationToken = default) =>
        RunAsync(options ?? new ServiceRouteWatchOptions(), cancellationToken);

    private async IAsyncEnumerable<ServiceRouteSnapshot> RunAsync(ServiceRouteWatchOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? revision = null;
        var backoff = InitialBackoff;
        var attempts = 0;
        while (true)
        {
            var request = new Wire.WatchServiceRoutesRequest { WorkspaceScope = ClientCore.Scope(workspace) };
            var received = false;
            var lost = false;
            await using (var stream = core.StreamAsync(o => core.ServiceDiscovery.WatchServiceRoutes(request, o), cancellationToken)
                .GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    Wire.WatchServiceRoutesResponse current;
                    try
                    {
                        if (!await stream.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        current = stream.Current;
                    }
                    catch (OpenShellException e) when (e.Kind == OpenShellErrorKind.Unavailable && attempts < (options.MaxReconnectAttempts ?? int.MaxValue))
                    {
                        attempts++;
                        lost = true;
                        break;
                    }
                    catch (OpenShellException e) when (e.Kind == OpenShellErrorKind.Unauthenticated && received && options.Reconnect)
                    {
                        // The gateway rechecks credentials during a watch and ends it once they expire. Reopening
                        // presents the provider's current credentials; a rejection at open is retried once after
                        // the provider renews them, and otherwise surfaces.
                        break;
                    }

                    received = true;
                    attempts = 0;
                    backoff = InitialBackoff;
                    var snapshot = ToDomain(current.Snapshot);
                    if (snapshot.Revision.Length > 0 && snapshot.Revision == revision)
                    {
                        continue;
                    }

                    revision = snapshot.Revision;
                    yield return snapshot;
                }
            }

            if (!lost && !options.Reconnect)
            {
                yield break;
            }

            // A watch the gateway ended at its lifetime reopens at once; one that failed or ended empty backs off.
            if (lost || !received)
            {
                LogReconnect(core.Logger, workspace, backoff.TotalMilliseconds, attempts);
                await Task.Delay(backoff, core.Time, cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    internal static ServiceRouteSnapshot ToDomain(Wire.ServiceRouteSnapshot? snapshot) =>
        snapshot is null
            ? new ServiceRouteSnapshot([], "")
            : new ServiceRouteSnapshot(snapshot.Routes.Select(ToDomain).ToArray(), snapshot.Revision);

    private static DiscoveredServiceRoute ToDomain(Wire.DiscoveredServiceRoute r) => new()
    {
        Workspace = r.Workspace,
        Sandbox = r.Sandbox,
        Name = r.Name,
        Hostname = r.Hostname,
        Endpoint = r.Endpoint is { } e ? new ServiceRouteEndpoint(e.Host, e.Port) : null,
        InstallationVersion = r.InstallationVersion,
        DeclarationRevision = r.DeclarationRevision,
        ExpiresAt = r.ExpiresAt?.ToDateTimeOffset(),
    };
}
