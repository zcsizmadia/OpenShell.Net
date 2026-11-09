// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Google.Protobuf;
using Grpc.Core;
using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>
/// A running interactive command. Read <see cref="ReadEventsAsync"/> (once) for output, write stdin and resize
/// the terminal concurrently, and await <see cref="WaitForExitAsync"/> for the exit code.
/// </summary>
public sealed class ExecInteractiveSession : IAsyncDisposable
{
    private const int MaxFrameBytes = 256 * 1024;

    private readonly AsyncDuplexStreamingCall<Wire.ExecSandboxInput, Wire.ExecSandboxEvent> _call;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _eventsStarted;
    private int _inputCompleted;
    private int _disposed;

    private ExecInteractiveSession(AsyncDuplexStreamingCall<Wire.ExecSandboxInput, Wire.ExecSandboxEvent> call, CancellationTokenSource cts)
    {
        _call = call;
        _cts = cts;
    }

    internal static async ValueTask<ExecInteractiveSession> StartAsync(
        ClientCore core,
        Wire.ExecSandboxRequest start,
        CancellationToken cancellationToken,
        string? replica = null,
        Action? onUnrouted = null)
    {
        ObjectDisposedException.ThrowIf(core.IsDisposed, core);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var call = await ClientCore.OpenDuplexAsync(
                headers => core.Grpc.ExecSandboxInteractive(new CallOptions(headers: headers, cancellationToken: cts.Token)),
                new Wire.ExecSandboxInput { Start = start },
                replica,
                onUnrouted,
                cts.Token).ConfigureAwait(false);
            return new ExecInteractiveSession(call, cts);
        }
        catch (RpcException e)
        {
            cts.Dispose();
            throw ClientCore.Map(e, cancellationToken);
        }
        catch
        {
            cts.Dispose();
            throw;
        }
    }

    /// <summary>Completes with the exit code once the command finished; faults if the stream failed first.</summary>
    public Task<int> ExitCode => _exit.Task;

    /// <summary>Sends bytes to the command's standard input.</summary>
    public async ValueTask WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        while (!data.IsEmpty)
        {
            var chunk = data[..Math.Min(data.Length, MaxFrameBytes)];
            data = data[chunk.Length..];

            // The gRPC writer serializes the frame before the awaited write completes, so wrapping is safe.
            await SendAsync(new Wire.ExecSandboxInput { Stdin = UnsafeByteOperations.UnsafeWrap(chunk) }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Tells the command's terminal about a new window size.</summary>
    public ValueTask ResizeAsync(uint columns, uint rows, CancellationToken cancellationToken = default) =>
        SendAsync(new Wire.ExecSandboxInput { Resize = new Wire.ExecSandboxWindowResize { Cols = columns, Rows = rows } }, cancellationToken);

    /// <summary>Closes standard input (EOF) while output keeps flowing.</summary>
    public async ValueTask CompleteStdinAsync()
    {
        if (Interlocked.Exchange(ref _inputCompleted, 1) != 0)
        {
            return;
        }

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Disposal cancels the call instead of waiting for a blocked send, so there is nothing left to complete.
            if (Volatile.Read(ref _disposed) == 0)
            {
                await CompleteRequestStreamAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task CompleteRequestStreamAsync()
    {
        try
        {
            await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (RpcException)
        {
            // The stream is already torn down.
        }
    }

    /// <summary>
    /// Streams output and finally the exit event. May be enumerated once. If the gateway closes the stream without
    /// an exit event the enumeration throws, so a failing command cannot be missed.
    /// </summary>
    public IAsyncEnumerable<ExecEvent> ReadEventsAsync(CancellationToken cancellationToken = default)
    {
        return Interlocked.Exchange(ref _eventsStarted, 1) != 0
            ? throw new InvalidOperationException("the event stream can only be enumerated once")
            : ReadEventsCoreAsync(cancellationToken);
    }

    private async IAsyncEnumerable<ExecEvent> ReadEventsCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = LinkToSession(cancellationToken);
        var token = linked.Token;
        var sawExit = false;
        while (await MoveNextAsync(token, cancellationToken).ConfigureAwait(false))
        {
            var ev = _call.ResponseStream.Current;
            switch (ev.PayloadCase)
            {
                case Wire.ExecSandboxEvent.PayloadOneofCase.Stdout:
                    yield return new ExecOutput(false, ev.Stdout.Data.Memory);
                    break;
                case Wire.ExecSandboxEvent.PayloadOneofCase.Stderr:
                    yield return new ExecOutput(true, ev.Stderr.Data.Memory);
                    break;
                case Wire.ExecSandboxEvent.PayloadOneofCase.Exit:
                    sawExit = true;
                    _exit.TrySetResult(ev.Exit.ExitCode);
                    yield return new ExecExit(ev.Exit.ExitCode);
                    break;
            }
        }

        if (!sawExit)
        {
            var error = new OpenShellException(OpenShellErrorKind.Unavailable, "exec stream ended without an exit event");
            _exit.TrySetException(error);
            throw error;
        }
    }

    /// <summary>Advances the response stream; a failure also settles <see cref="ExitCode"/>.</summary>
    private async Task<bool> MoveNextAsync(CancellationToken token, CancellationToken cancellationToken)
    {
        try
        {
            return await _call.ResponseStream.MoveNext(token).ConfigureAwait(false);
        }
        catch (RpcException e)
        {
            var mapped = ClientCore.Map(e, token);
            if (mapped is OperationCanceledException canceled)
            {
                _exit.TrySetCanceled(canceled.CancellationToken);
            }
            else
            {
                _exit.TrySetException(mapped);
            }

            throw mapped;
        }
        catch (OperationCanceledException e)
        {
            _exit.TrySetCanceled(e.CancellationToken);
            throw;
        }
        catch (ObjectDisposedException)
        {
            _exit.TrySetCanceled(cancellationToken);
            throw new OperationCanceledException("the interactive session was closed");
        }
    }

    /// <summary>
    /// Waits for the exit code. When nobody is reading <see cref="ReadEventsAsync"/>, output is drained and discarded.
    /// </summary>
    public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _eventsStarted, 1) == 0)
        {
            await foreach (var _ in ReadEventsCoreAsync(cancellationToken).ConfigureAwait(false))
            {
                // Drain: reading the events to the end is what records the exit code.
            }
        }

        return await _exit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels the command and releases the stream.</summary>
    public async ValueTask CancelAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            // Close stdin cleanly when no send is in flight. A send holding the lock may be blocked on flow control
            // indefinitely, so never wait for it: cancelling below ends it instead.
            if (_sendLock.Wait(0))
            {
                try
                {
                    if (Interlocked.Exchange(ref _inputCompleted, 1) == 0)
                    {
                        await CompleteRequestStreamAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    _sendLock.Release();
                }
            }
        }
        finally
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _call.Dispose();
            _cts.Dispose();
            _exit.TrySetException(new OpenShellException(OpenShellErrorKind.Cancelled, "the interactive session was closed before the command exited"));
            _ = _exit.Task.Exception; // Observed so an unawaited ExitCode never surfaces as an unobserved exception.
        }
    }

    private CancellationTokenSource LinkToSession(CancellationToken cancellationToken)
    {
        try
        {
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        }
        catch (ObjectDisposedException)
        {
            // Only DisposeAsync disposes the source, and it settles the exit code itself.
            throw new OperationCanceledException("the interactive session was closed");
        }
    }

    private async ValueTask SendAsync(Wire.ExecSandboxInput message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _inputCompleted) != 0)
        {
            throw new InvalidOperationException("standard input is already closed");
        }

        // Linked to the session's own source so disposing the session ends a send blocked on flow control.
        using var linked = LinkToSession(cancellationToken);
        var token = linked.Token;
        try
        {
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        try
        {
            await _call.RequestStream.WriteAsync(message, token).ConfigureAwait(false);
        }
        catch (RpcException e)
        {
            throw ClientCore.Map(e, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
