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

    public CallOptions CallOptions(CancellationToken cancellationToken, TimeSpan? timeout = null, Metadata? headers = null)
    {
        var effective = timeout ?? Options.DefaultTimeout;
        return new CallOptions(
            headers: headers,
            deadline: effective is { } t ? DateTime.UtcNow + t : null,
            cancellationToken: cancellationToken);
    }

    /// <summary>Runs a unary call, retrying once when the auth provider can recover from <c>Unauthenticated</c>.</summary>
    public async ValueTask<TResponse> UnaryAsync<TResponse>(
        Func<CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null) =>
        (await UnaryCoreAsync(call, null, readHeaders: false, timeout, cancellationToken).ConfigureAwait(false)).Response;

    /// <summary>Like <see cref="UnaryAsync{TResponse}"/>, sending request headers and returning the response headers too.</summary>
    public ValueTask<(TResponse Response, Metadata Headers)> UnaryWithHeadersAsync<TResponse>(
        Func<CallOptions, AsyncUnaryCall<TResponse>> call,
        Metadata headers,
        CancellationToken cancellationToken) =>
        UnaryCoreAsync(call, headers, readHeaders: true, null, cancellationToken);

    private async ValueTask<(TResponse Response, Metadata Headers)> UnaryCoreAsync<TResponse>(
        Func<CallOptions, AsyncUnaryCall<TResponse>> call,
        Metadata? headers,
        bool readHeaders,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var active = call(CallOptions(cancellationToken, timeout, headers));
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
    /// </summary>
    public async IAsyncEnumerable<TResponse> StreamAsync<TResponse>(
        Func<CallOptions, AsyncServerStreamingCall<TResponse>> call,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string? replica = null,
        Action? onUnrouted = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var active = await OpenStreamAsync(call, replica, onUnrouted, cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = active.ResponseStream;
            while (true)
            {
                bool moved;
                try
                {
                    moved = await stream.MoveNext(cancellationToken).ConfigureAwait(false);
                }
                catch (RpcException e)
                {
                    throw Map(e, cancellationToken);
                }

                if (!moved)
                {
                    yield break;
                }

                yield return stream.Current;
            }
        }
        finally
        {
            active.Dispose();
        }
    }

    /// <summary>
    /// Opens a duplex call and sends its first frame. With a <paramref name="replica"/>, the call is routed to it and
    /// waits for the gateway to answer; when the replica cannot be reached it is reopened unrouted once, resending
    /// the first frame. Failures surface as <see cref="RpcException"/> for the caller to map.
    /// </summary>
    public static async ValueTask<AsyncDuplexStreamingCall<TRequest, TResponse>> OpenDuplexAsync<TRequest, TResponse>(
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

    private async ValueTask<AsyncServerStreamingCall<TResponse>> OpenStreamAsync<TResponse>(
        Func<CallOptions, AsyncServerStreamingCall<TResponse>> call,
        string? replica,
        Action? onUnrouted,
        CancellationToken cancellationToken)
    {
        if (replica is null)
        {
            return call(CallOptions(cancellationToken));
        }

        var routed = call(CallOptions(cancellationToken, headers: Transport.OwnerRouting.Route(replica)));
        if (await Transport.OwnerRouting.UnansweredAsync(routed.ResponseHeadersAsync).ConfigureAwait(false) is { } failure
            && Transport.OwnerRouting.RetryUnrouted(failure))
        {
            routed.Dispose();
            onUnrouted?.Invoke();
            return call(CallOptions(cancellationToken));
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
