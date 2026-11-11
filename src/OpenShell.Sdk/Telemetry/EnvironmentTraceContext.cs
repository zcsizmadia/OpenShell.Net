// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OpenShell;

/// <summary>
/// A W3C trace context captured from <c>TRACEPARENT</c> and <c>TRACESTATE</c>, as the OpenShell CLI forwards it so a CI
/// pipeline's trace parents the gateway's spans. Only a valid <c>traceparent</c> is kept; <c>tracestate</c> is kept
/// only alongside one.
/// </summary>
/// <param name="Context">The parsed, remote parent context.</param>
/// <param name="TraceParent">The normalized <c>traceparent</c> header value.</param>
/// <param name="TraceState">The <c>tracestate</c> header value, or null.</param>
internal sealed record EnvironmentTraceContext(ActivityContext Context, string TraceParent, string? TraceState)
{
    public const string TraceParentVariable = "TRACEPARENT";
    public const string TraceStateVariable = "TRACESTATE";
    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";

    // W3C Trace Context caps tracestate at 32 list members of at most 256 characters each, so no valid value comes
    // near this; a longer one is dropped rather than sent.
    private const int MaxTraceStateLength = 8192;

    /// <summary>
    /// Reads the context from the environment. Each variable is looked up upper-case first, then lower-case, as the
    /// CLI does; values are trimmed and a blank value counts as unset. Null when <c>TRACEPARENT</c> is unset or invalid.
    /// </summary>
    public static EnvironmentTraceContext? FromEnvironment(Func<string, string?>? getEnvironmentVariable = null)
    {
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        return Parse(Read(env, TraceParentVariable), Read(env, TraceStateVariable));
    }

    /// <summary>
    /// <see cref="FromEnvironment"/>, logging at debug level when <c>TRACEPARENT</c> is set but invalid: the context is
    /// then not forwarded, as in the CLI.
    /// </summary>
    public static EnvironmentTraceContext? Capture(ILogger logger, Func<string, string?>? getEnvironmentVariable = null)
    {
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var context = FromEnvironment(env);
        if (context is null && Read(env, TraceParentVariable) is not null)
        {
            EnvironmentTraceContextLog.InvalidTraceParent(logger);
        }

        return context;
    }

    /// <summary>Parses a <c>traceparent</c> and optional <c>tracestate</c>; null when the <c>traceparent</c> is invalid.</summary>
    public static EnvironmentTraceContext? Parse(string? traceParent, string? traceState)
    {
        traceParent = Normalize(traceParent);
        if (traceParent is null
            || !ActivityContext.TryParse(traceParent, null, isRemote: true, out var parsed)
            || parsed.TraceId == default
            || parsed.SpanId == default)
        {
            return null;
        }

        var state = Normalize(traceState) is { } value && IsHeaderSafe(value) ? value : null;
        var context = new ActivityContext(parsed.TraceId, parsed.SpanId, parsed.TraceFlags, state, isRemote: true);
        var sampled = (parsed.TraceFlags & ActivityTraceFlags.Recorded) != 0 ? "01" : "00";
        return new EnvironmentTraceContext(context, $"00-{parsed.TraceId.ToHexString()}-{parsed.SpanId.ToHexString()}-{sampled}", state);
    }

    private static string? Read(Func<string, string?> env, string upper) =>
        Normalize(env(upper)) ?? Normalize(env(upper.ToLowerInvariant()));

    private static string? Normalize(string? value) => value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    private static bool IsHeaderSafe(string value) =>
        value.Length <= MaxTraceStateLength && value.All(static c => c is >= ' ' and <= '~');
}

internal static partial class EnvironmentTraceContextLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "TRACEPARENT is set but invalid; not forwarding trace context")]
    public static partial void InvalidTraceParent(ILogger logger);
}
