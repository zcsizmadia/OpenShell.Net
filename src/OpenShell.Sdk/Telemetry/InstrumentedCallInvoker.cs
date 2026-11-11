// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Grpc.Core;

namespace OpenShell;

/// <summary>
/// Wraps a <see cref="CallInvoker"/> so every RPC emits a client span and a duration measurement. When no listener
/// is subscribed the wrapper hands the call straight to the inner invoker.
/// </summary>
internal sealed class InstrumentedCallInvoker(CallInvoker inner, EnvironmentTraceContext? trace = null) : CallInvoker
{
    private static bool Enabled => OpenShellTelemetry.Source.HasListeners() || OpenShellTelemetry.Duration.Enabled;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        if (!Enabled)
        {
            return inner.AsyncUnaryCall(method, host, WithTrace(options), request);
        }

        var scope = CallScope.Start(method.ServiceName, method.Name, trace);
        options = WithTrace(options, scope);
        var call = scope.Invoke(() => inner.AsyncUnaryCall(method, host, options, request));
        return new AsyncUnaryCall<TResponse>(
            Observe(call.ResponseAsync, scope), call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, call.Dispose);
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        if (!Enabled)
        {
            return inner.BlockingUnaryCall(method, host, WithTrace(options), request);
        }

        // Invoke records a failure itself.
        var scope = CallScope.Start(method.ServiceName, method.Name, trace);
        options = WithTrace(options, scope);
        var response = scope.Invoke(() => inner.BlockingUnaryCall(method, host, options, request));
        scope.Complete(null);
        return response;
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        if (!Enabled)
        {
            return inner.AsyncServerStreamingCall(method, host, WithTrace(options), request);
        }

        var scope = CallScope.Start(method.ServiceName, method.Name, trace);
        options = WithTrace(options, scope);
        var call = scope.Invoke(() => inner.AsyncServerStreamingCall(method, host, options, request));
        return new AsyncServerStreamingCall<TResponse>(
            new ObservedReader<TResponse>(call.ResponseStream, scope),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            () =>
            {
                call.Dispose();
                scope.Complete(new OperationCanceledException());
            });
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
    {
        if (!Enabled)
        {
            return inner.AsyncClientStreamingCall(method, host, WithTrace(options));
        }

        var scope = CallScope.Start(method.ServiceName, method.Name, trace);
        options = WithTrace(options, scope);
        var call = scope.Invoke(() => inner.AsyncClientStreamingCall(method, host, options));
        return new AsyncClientStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            Observe(call.ResponseAsync, scope),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            () =>
            {
                call.Dispose();
                scope.Complete(new OperationCanceledException());
            });
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
    {
        if (!Enabled)
        {
            return inner.AsyncDuplexStreamingCall(method, host, WithTrace(options));
        }

        var scope = CallScope.Start(method.ServiceName, method.Name, trace);
        options = WithTrace(options, scope);
        var call = scope.Invoke(() => inner.AsyncDuplexStreamingCall(method, host, options));
        return new AsyncDuplexStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            new ObservedReader<TResponse>(call.ResponseStream, scope),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            () =>
            {
                call.Dispose();
                scope.Complete(new OperationCanceledException());
            });
    }

    /// <summary>
    /// Forwards the environment trace context as <c>traceparent</c> and <c>tracestate</c> metadata: the SDK span when
    /// it was parented on that context, else the context itself when no activity is current. A call made under the
    /// caller's own activity is left to HttpClient, which propagates that activity. HttpClient never replaces a
    /// <c>traceparent</c> the request already carries, so the gateway sees this one even when other instrumentation
    /// (such as grpc-dotnet's own activity) starts a span below it.
    /// </summary>
    private CallOptions WithTrace(CallOptions options, CallScope? scope = null)
    {
        if (trace is null || options.Headers?.Get(EnvironmentTraceContext.TraceParentHeader) is not null)
        {
            return options;
        }

        string parent;
        string? state;
        if (scope?.EnvironmentChild is { } span)
        {
            parent = span.Id!;
            state = span.TraceStateString;
        }
        else if (Activity.Current is null)
        {
            parent = trace.TraceParent;
            state = trace.TraceState;
        }
        else
        {
            return options;
        }

        var headers = new Metadata();
        foreach (var entry in options.Headers ?? [])
        {
            headers.Add(entry);
        }

        headers.Add(EnvironmentTraceContext.TraceParentHeader, parent);
        if (state is not null)
        {
            headers.Add(EnvironmentTraceContext.TraceStateHeader, state);
        }

        return options.WithHeaders(headers);
    }

    private static async Task<T> Observe<T>(Task<T> response, CallScope scope)
    {
        try
        {
            var result = await response.ConfigureAwait(false);
            scope.Complete(null);
            return result;
        }
        catch (Exception e)
        {
            scope.Complete(e);
            throw;
        }
    }

    private sealed class ObservedReader<T>(IAsyncStreamReader<T> reader, CallScope scope) : IAsyncStreamReader<T>
    {
        public T Current => reader.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            try
            {
                var moved = await reader.MoveNext(cancellationToken).ConfigureAwait(false);
                if (!moved)
                {
                    scope.Complete(null);
                }

                return moved;
            }
            catch (Exception e)
            {
                scope.Complete(e);
                throw;
            }
        }
    }

    /// <summary>One RPC's span and timer; completing it twice is harmless.</summary>
    private sealed class CallScope
    {
        private readonly Activity? _activity;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly string _service;
        private readonly string _method;
        private int _completed;

        private CallScope(Activity? activity, string service, string method, bool environmentParent)
        {
            _activity = activity;
            EnvironmentChild = environmentParent ? activity : null;
            _service = service;
            _method = method;
        }

        /// <summary>The span, when it was started under the environment trace context rather than a current activity.</summary>
        public Activity? EnvironmentChild { get; }

        /// <summary>Starts the span under the current activity, or under the environment trace context when none is current.</summary>
        public static CallScope Start(string service, string method, EnvironmentTraceContext? trace)
        {
            var name = $"{service}/{method}";
            var environmentParent = trace is not null && Activity.Current is null;
            var activity = environmentParent
                ? OpenShellTelemetry.Source.StartActivity(name, ActivityKind.Client, trace!.Context)
                : OpenShellTelemetry.Source.StartActivity(name, ActivityKind.Client);
            activity?.SetTag("rpc.system", "grpc");
            activity?.SetTag("rpc.service", service);
            activity?.SetTag("rpc.method", method);
            return new CallScope(activity, service, method, environmentParent);
        }

        /// <summary>
        /// Runs the inner call while the span is current, so instrumentation below it nests correctly, then restores
        /// the caller's current activity: the span ends on another continuation and must not leak into the caller's flow.
        /// </summary>
        public T Invoke<T>(Func<T> call)
        {
            var parent = _activity?.Parent;
            try
            {
                return call();
            }
            catch (Exception e)
            {
                Complete(e);
                throw;
            }
            finally
            {
                if (_activity is not null)
                {
                    Activity.Current = parent;
                }
            }
        }

        public void Complete(Exception? error)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            var code = error switch
            {
                null => StatusCode.OK,
                RpcException rpc => rpc.StatusCode,
                OperationCanceledException => StatusCode.Cancelled,
                _ => StatusCode.Unknown,
            };

            var elapsed = Stopwatch.GetElapsedTime(_started).TotalSeconds;
            OpenShellTelemetry.Duration.Record(
                elapsed,
                new KeyValuePair<string, object?>("rpc.system", "grpc"),
                new KeyValuePair<string, object?>("rpc.service", _service),
                new KeyValuePair<string, object?>("rpc.method", _method),
                new KeyValuePair<string, object?>("rpc.grpc.status_code", (int)code));

            if (_activity is null)
            {
                return;
            }

            _activity.SetTag("rpc.grpc.status_code", (int)code);
            if (code != StatusCode.OK)
            {
                _activity.SetStatus(ActivityStatusCode.Error, code.ToString());
            }

            _activity.Dispose();
        }
    }
}
