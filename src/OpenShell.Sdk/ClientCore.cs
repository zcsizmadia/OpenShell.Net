// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Openshell.Datamodel.V1;
using GrpcService = Openshell.V1.OpenShell;

namespace OpenShell;

/// <summary>State shared by an <see cref="OpenShellClient"/> and every workspace-scoped view of it.</summary>
internal sealed class ClientCore : IDisposable
{
    private readonly GrpcChannel? _channel;
    private int _disposed;

    public ClientCore(OpenShellClientOptions options, GrpcChannel? channel, CallInvoker invoker)
    {
        Options = options;
        _channel = channel;
        Grpc = new GrpcService.OpenShellClient(invoker);
        SandboxUpdates = new Openshell.V1.SandboxUpdates.SandboxUpdatesClient(invoker);
        Routing = new Transport.OwnerRouting(this);
    }

    /// <summary>Owner-replica routing for sandbox streams, shared by every workspace view of the client.</summary>
    public Transport.OwnerRouting Routing { get; }

    public OpenShellClientOptions Options { get; }

    public GrpcService.OpenShellClient Grpc { get; }

    /// <summary>The separate <c>openshell.v1.SandboxUpdates</c> service, on the same connection and call pipeline.</summary>
    public Openshell.V1.SandboxUpdates.SandboxUpdatesClient SandboxUpdates { get; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Logger for SDK-level events such as retries and watch reconnects; discards everything when no factory is configured.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>A resource whose lifetime follows the client, such as an edge tunnel proxy.</summary>
    public IDisposable? OwnedResource { get; init; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Options for a unary call: <see cref="OpenShellClientOptions.DefaultTimeout"/> becomes its deadline.</summary>
    private CallOptions UnaryCallOptions(Metadata? headers, CancellationToken cancellationToken) =>
        new(
            headers: headers,
            deadline: Options.DefaultTimeout is { } t ? DateTime.UtcNow + t : null,
            cancellationToken: cancellationToken);

    /// <summary>Runs a unary call, retrying once when the auth provider can recover from <c>Unauthenticated</c>.</summary>
    public async ValueTask<TResponse> UnaryAsync<TResponse>(
        Func<CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken) =>
        (await UnaryCoreAsync(call, null, readHeaders: false, cancellationToken).ConfigureAwait(false)).Response;

    /// <summary>Like <see cref="UnaryAsync{TResponse}"/>, sending request headers and returning the response headers too.</summary>
    public ValueTask<(TResponse Response, Metadata Headers)> UnaryWithHeadersAsync<TResponse>(
        Func<CallOptions, AsyncUnaryCall<TResponse>> call,
        Metadata headers,
        CancellationToken cancellationToken) =>
        UnaryCoreAsync(call, headers, readHeaders: true, cancellationToken);

    private async ValueTask<(TResponse Response, Metadata Headers)> UnaryCoreAsync<TResponse>(
        Func<CallOptions, AsyncUnaryCall<TResponse>> call,
        Metadata? headers,
        bool readHeaders,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var active = call(UnaryCallOptions(headers, cancellationToken));
                var response = await active.ResponseAsync.ConfigureAwait(false);
                return (response, readHeaders ? await active.ResponseHeadersAsync.ConfigureAwait(false) : Metadata.Empty);
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.Unauthenticated && attempt == 0)
            {
                if (!await TryRecoverAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw Map(e, cancellationToken);
                }
            }
            catch (RpcException e)
            {
                throw Map(e, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Runs a server-streaming call, mapping gRPC failures to <see cref="OpenShellException"/>. With a
    /// <paramref name="replica"/>, the call is routed to it and reopened once unrouted when the replica cannot be
    /// reached (see <see cref="Transport.OwnerRouting"/>); <paramref name="onUnrouted"/> runs before that retry.
    /// A call the gateway rejects as <c>Unauthenticated</c> before any message arrived is reopened once when the auth
    /// provider renews its credentials, as for unary calls.
    /// </summary>
    public async IAsyncEnumerable<TResponse> StreamAsync<TResponse>(
        Func<CallOptions, AsyncServerStreamingCall<TResponse>> call,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string? replica = null,
        Action? onUnrouted = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var opened = await OpenFirstAsync(call, replica, onUnrouted, cancellationToken).ConfigureAwait(false);
        using var active = opened.Call;
        if (!opened.HasFirst)
        {
            yield break;
        }

        var stream = active.ResponseStream;
        yield return stream.Current;
        while (await MoveNextAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            yield return stream.Current;
        }
    }

    /// <summary>
    /// Opens a server stream and waits for its first message or its end. Only here, before anything was received, is an
    /// <c>Unauthenticated</c> rejection retried: once, and only when the auth provider renewed its credentials.
    /// </summary>
    private async ValueTask<(AsyncServerStreamingCall<TResponse> Call, bool HasFirst)> OpenFirstAsync<TResponse>(
        Func<CallOptions, AsyncServerStreamingCall<TResponse>> call,
        string? replica,
        Action? onUnrouted,
        CancellationToken cancellationToken)
    {
        // A replica found unreachable is not routed to again on the retry.
        var route = replica;
        void Unrouted()
        {
            route = null;
            onUnrouted?.Invoke();
        }

        for (var attempt = 0; ; attempt++)
        {
            var active = await OpenStreamAsync(call, route, Unrouted, cancellationToken).ConfigureAwait(false);
            try
            {
                return (active, await active.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false));
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.Unauthenticated && attempt == 0)
            {
                active.Dispose();
                if (!await TryRecoverAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw Map(e, cancellationToken);
                }
            }
            catch (RpcException e)
            {
                active.Dispose();
                throw Map(e, cancellationToken);
            }
            catch
            {
                active.Dispose();
                throw;
            }
        }
    }

    private static async Task<bool> MoveNextAsync<TResponse>(IAsyncStreamReader<TResponse> stream, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.MoveNext(cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException e)
        {
            throw Map(e, cancellationToken);
        }
    }

    /// <summary>
    /// Opens a duplex call and sends its first frame. With a <paramref name="replica"/>, the call is routed to it and
    /// waits for the gateway to answer; when the replica cannot be reached it is reopened unrouted once, resending
    /// the first frame. Failures surface as <see cref="RpcException"/> for the caller to map.
    /// </summary>
    /// <remarks>
    /// When the auth provider sends credentials, the open also waits for the gateway to answer. A call it rejects as
    /// <c>Unauthenticated</c> never reached the handler and nothing was received, so it is reopened once, resending
    /// the first frame, when the provider renews its credentials. Nothing is retried once the gateway answered.
    /// </remarks>
    public async ValueTask<AsyncDuplexStreamingCall<TRequest, TResponse>> OpenDuplexAsync<TRequest, TResponse>(
        Func<Metadata?, AsyncDuplexStreamingCall<TRequest, TResponse>> open,
        TRequest first,
        string? replica,
        Action? onUnrouted,
        CancellationToken cancellationToken)
    {
        if (!Options.Auth.SendsCredentials)
        {
            return await OpenDuplexOnceAsync(open, first, replica, onUnrouted, cancellationToken).ConfigureAwait(false);
        }

        // A replica found unreachable is not routed to again on the retry.
        var route = replica;
        void Unrouted()
        {
            route = null;
            onUnrouted?.Invoke();
        }

        for (var attempt = 0; ; attempt++)
        {
            RpcException rejected;
            try
            {
                var call = await OpenDuplexOnceAsync(open, first, route, Unrouted, cancellationToken).ConfigureAwait(false);
                if (attempt > 0)
                {
                    return call;
                }

                // A routed open already waited for this answer; an unrouted one waits here.
                if (await Transport.OwnerRouting.UnansweredAsync(call.ResponseHeadersAsync).ConfigureAwait(false)
                    is not { StatusCode: StatusCode.Unauthenticated } failure)
                {
                    // Answered, or failed for another reason that reading the call reports.
                    return call;
                }

                call.Dispose();
                rejected = failure;
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.Unauthenticated && attempt == 0)
            {
                rejected = e;
            }

            if (!await TryRecoverAsync(cancellationToken).ConfigureAwait(false))
            {
                throw rejected;
            }
        }
    }

    private static async ValueTask<AsyncDuplexStreamingCall<TRequest, TResponse>> OpenDuplexOnceAsync<TRequest, TResponse>(
        Func<Metadata?, AsyncDuplexStreamingCall<TRequest, TResponse>> open,
        TRequest first,
        string? replica,
        Action? onUnrouted,
        CancellationToken cancellationToken)
    {
        if (replica is null)
        {
            return await OpenAndSendAsync(open, null, first, cancellationToken).ConfigureAwait(false);
        }

        var routed = open(Transport.OwnerRouting.Route(replica));
        RpcException? writeFailure = null;
        try
        {
            await routed.RequestStream.WriteAsync(first, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException e)
        {
            // The call already ended; its status says whether the replica was reachable.
            writeFailure = e;
        }
        catch
        {
            routed.Dispose();
            throw;
        }

        var failure = await Transport.OwnerRouting.UnansweredAsync(routed.ResponseHeadersAsync).ConfigureAwait(false);
        if (failure is not null && Transport.OwnerRouting.RetryUnrouted(failure))
        {
            routed.Dispose();
            onUnrouted?.Invoke();
            return await OpenAndSendAsync(open, null, first, cancellationToken).ConfigureAwait(false);
        }

        if (writeFailure is not null)
        {
            routed.Dispose();
            throw failure ?? writeFailure;
        }

        return routed;
    }

    private static async ValueTask<AsyncDuplexStreamingCall<TRequest, TResponse>> OpenAndSendAsync<TRequest, TResponse>(
        Func<Metadata?, AsyncDuplexStreamingCall<TRequest, TResponse>> open,
        Metadata? headers,
        TRequest first,
        CancellationToken cancellationToken)
    {
        var call = open(headers);
        try
        {
            await call.RequestStream.WriteAsync(first, cancellationToken).ConfigureAwait(false);
            return call;
        }
        catch
        {
            call.Dispose();
            throw;
        }
    }

    private static async ValueTask<AsyncServerStreamingCall<TResponse>> OpenStreamAsync<TResponse>(
        Func<CallOptions, AsyncServerStreamingCall<TResponse>> call,
        string? replica,
        Action? onUnrouted,
        CancellationToken cancellationToken)
    {
        // Streams have no deadline: DefaultTimeout applies to unary calls only.
        if (replica is null)
        {
            return call(new CallOptions(cancellationToken: cancellationToken));
        }

        var routed = call(new CallOptions(headers: Transport.OwnerRouting.Route(replica), cancellationToken: cancellationToken));
        if (await Transport.OwnerRouting.UnansweredAsync(routed.ResponseHeadersAsync).ConfigureAwait(false) is { } failure
            && Transport.OwnerRouting.RetryUnrouted(failure))
        {
            routed.Dispose();
            onUnrouted?.Invoke();
            return call(new CallOptions(cancellationToken: cancellationToken));
        }

        // Answered, or failed for a reason the gateway owns: reading the stream reports it.
        return routed;
    }

    public static Exception Map(RpcException e, CancellationToken cancellationToken) =>
        e.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(cancellationToken)
            : OpenShellException.FromRpc(e);

    public static WorkspaceSelector Scope(string workspace) => new() { Workspace = workspace };

    public static WorkspaceSelector ListScope(string workspace, bool all) =>
        all ? new WorkspaceSelector { AllWorkspaces = new AllWorkspaces() } : Scope(workspace);

    private async ValueTask<bool> TryRecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Options.Auth.TryRecoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OpenShellException)
        {
            throw new OpenShellException(OpenShellErrorKind.Auth, "credential recovery failed", e);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel?.Dispose();
            OwnedResource?.Dispose();
        }
    }
}
