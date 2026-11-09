// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Grpc.Core;
using Wire = Openshell.V1;

namespace OpenShell.Transport;

/// <summary>
/// Owner-replica routing for long-lived sandbox streams, as in the Go, TypeScript, and Python SDKs.
/// </summary>
/// <remarks>
/// <para>
/// A multi-replica gateway behind Envoy Gateway with replica routing reports the replica that holds a sandbox's
/// supervisor session in the <c>x-openshell-owner</c> response header of a <c>GetSandbox</c> that asked for it with
/// <c>x-openshell-want-owner: 1</c>. A stream opened with <c>x-openshell-replica: &lt;owner&gt;</c> then goes straight
/// to that replica instead of being relayed between replicas.
/// </para>
/// <para>
/// A stale owner is harmless, because any replica relays. A routed stream that fails before the gateway answers with
/// an Envoy-level <c>UNAVAILABLE</c> (empty message, <c>no healthy upstream</c>, or <c>connection failure</c>) is
/// reopened once without the header. Gateway errors such as "supervisor not connected" are never retried, and
/// nothing is retried after response headers arrived.
/// </para>
/// <para>
/// Owners, including "no owner", are cached per workspace and sandbox for <see cref="CacheTtl"/>, so a
/// single-replica gateway costs one extra lookup per sandbox and period, not one per call.
/// </para>
/// </remarks>
internal sealed class OwnerRouting(ClientCore core)
{
    public const string WantOwnerHeader = "x-openshell-want-owner";
    public const string OwnerHeader = "x-openshell-owner";
    public const string ReplicaHeader = "x-openshell-replica";

    internal const int MaxCacheEntries = 1024;
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly Dictionary<(string Workspace, string Sandbox), (string? Owner, DateTimeOffset Expires)> _cache = [];
    private readonly object _lock = new();

    private bool Enabled => core.Options.RouteToOwnerReplica;

    /// <summary>
    /// <c>GetSandbox</c> that also learns the owner replica. Errors propagate, so this can replace an existence check.
    /// </summary>
    public async ValueTask<(Wire.SandboxResponse Response, string? Owner)> GetSandboxAsync(string workspace, string sandbox, CancellationToken cancellationToken)
    {
        var request = new Wire.GetSandboxRequest { WorkspaceScope = ClientCore.Scope(workspace), Name = sandbox };
        if (!Enabled)
        {
            return (await core.UnaryAsync(o => core.Grpc.GetSandboxAsync(request, o), cancellationToken).ConfigureAwait(false), null);
        }

        var (response, headers) = await core.UnaryWithHeadersAsync(
            o => core.Grpc.GetSandboxAsync(request, o),
            new Metadata { { WantOwnerHeader, "1" } },
            cancellationToken).ConfigureAwait(false);
        var owner = headers.GetValue(OwnerHeader) is { Length: > 0 } value ? value : null;
        Remember(workspace, sandbox, owner);
        return (response, owner);
    }

    /// <summary>
    /// The owner for a call without its own existence check: cached, or one best-effort lookup. A failed lookup leaves
    /// the call unrouted; the call itself then reports the problem.
    /// </summary>
    public async ValueTask<string?> OwnerAsync(string workspace, string sandbox, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return null;
        }

        lock (_lock)
        {
            if (_cache.TryGetValue((workspace, sandbox), out var entry) && entry.Expires > core.Time.GetUtcNow())
            {
                return entry.Owner;
            }
        }

        try
        {
            return (await GetSandboxAsync(workspace, sandbox, cancellationToken).ConfigureAwait(false)).Owner;
        }
        catch (OpenShellException)
        {
            return null;
        }
    }

    /// <summary>Drops a cached owner after its replica could not be reached.</summary>
    public void Forget(string workspace, string sandbox)
    {
        lock (_lock)
        {
            _cache.Remove((workspace, sandbox));
        }
    }

    public static Metadata Route(string owner) => new() { { ReplicaHeader, owner } };

    /// <summary>
    /// True for the Envoy-level <c>UNAVAILABLE</c> failures that mean the routed replica could not be reached.
    /// </summary>
    public static bool RetryUnrouted(RpcException e) =>
        e.StatusCode == StatusCode.Unavailable
        && (e.Status.Detail.Length == 0
            || e.Status.Detail.Contains("no healthy upstream", StringComparison.OrdinalIgnoreCase)
            || e.Status.Detail.Contains("connection failure", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Waits for the gateway to answer a stream. Returns <see langword="null"/> when response headers arrived, or
    /// the failure when the call ended first (including a trailers-only response, whose status rides in the headers).
    /// </summary>
    public static async Task<RpcException?> UnansweredAsync(Task<Metadata> responseHeaders)
    {
        Metadata headers;
        try
        {
            headers = await responseHeaders.ConfigureAwait(false);
        }
        catch (RpcException e)
        {
            return e;
        }

        if (headers.GetValue("grpc-status") is not { } code
            || !int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            || status == 0)
        {
            return null;
        }

        var message = headers.GetValue("grpc-message") is { } encoded ? Uri.UnescapeDataString(encoded) : "";
        return new RpcException(new Status((StatusCode)status, message));
    }

    private void Remember(string workspace, string sandbox, string? owner)
    {
        lock (_lock)
        {
            if (_cache.Count >= MaxCacheEntries && !_cache.ContainsKey((workspace, sandbox)))
            {
                _cache.Clear();
            }

            _cache[(workspace, sandbox)] = (owner, core.Time.GetUtcNow() + CacheTtl);
        }
    }
}
