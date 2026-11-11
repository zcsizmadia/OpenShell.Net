// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using OpenShell.Gateway;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class EnvironmentTraceContextTests
{
    internal const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    internal const string SpanId = "00f067aa0ba902b7";
    internal const string TraceParent = $"00-{TraceId}-{SpanId}-01";

    private static Func<string, string?> Env(params (string Name, string? Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;

    [Test]
    public async Task A_valid_traceparent_and_tracestate_are_captured()
    {
        var trace = EnvironmentTraceContext.FromEnvironment(Env(("TRACEPARENT", TraceParent), ("TRACESTATE", "vendor=a,other=b")))!;

        await Assert.That(trace.TraceParent).IsEqualTo(TraceParent);
        await Assert.That(trace.TraceState).IsEqualTo("vendor=a,other=b");
        await Assert.That(trace.Context.TraceId.ToHexString()).IsEqualTo(TraceId);
        await Assert.That(trace.Context.SpanId.ToHexString()).IsEqualTo(SpanId);
        await Assert.That(trace.Context.TraceFlags).IsEqualTo(ActivityTraceFlags.Recorded);
        await Assert.That(trace.Context.TraceState).IsEqualTo("vendor=a,other=b");
        await Assert.That(trace.Context.IsRemote).IsTrue();
    }

    [Test]
    public async Task An_unsampled_traceparent_stays_unsampled()
    {
        var trace = EnvironmentTraceContext.Parse($"00-{TraceId}-{SpanId}-00", null)!;

        await Assert.That(trace.TraceParent).IsEqualTo($"00-{TraceId}-{SpanId}-00");
        await Assert.That(trace.TraceState).IsNull();
    }

    [Test]
    public async Task Upper_case_names_win_and_lower_case_names_are_the_fallback()
    {
        const string other = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        var upper = EnvironmentTraceContext.FromEnvironment(Env(("TRACEPARENT", TraceParent), ("traceparent", other)))!;
        var lower = EnvironmentTraceContext.FromEnvironment(Env(("traceparent", other), ("tracestate", "k=v")))!;

        await Assert.That(upper.TraceParent).IsEqualTo(TraceParent);
        await Assert.That(lower.TraceParent).IsEqualTo(other);
        await Assert.That(lower.TraceState).IsEqualTo("k=v");
    }

    [Test]
    public async Task Values_are_trimmed_and_a_blank_upper_case_value_does_not_shadow_the_lower_case_one()
    {
        var trace = EnvironmentTraceContext.FromEnvironment(
            Env(("TRACEPARENT", "   "), ("traceparent", $"  {TraceParent}\n"), ("TRACESTATE", "\t"), ("tracestate", " k=v ")))!;

        await Assert.That(trace.TraceParent).IsEqualTo(TraceParent);
        await Assert.That(trace.TraceState).IsEqualTo("k=v");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not-a-traceparent")]
    [Arguments("00-00000000000000000000000000000000-00f067aa0ba902b7-01")]
    [Arguments("00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01")]
    [Arguments("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7")]
    public async Task An_unset_or_invalid_traceparent_captures_nothing_and_drops_the_tracestate(string? traceParent)
    {
        await Assert.That(EnvironmentTraceContext.FromEnvironment(Env(("TRACEPARENT", traceParent), ("TRACESTATE", "k=v")))).IsNull();
    }

    [Test]
    [Arguments("k=v\r\nx-injected: 1")]
    [Arguments("k=é")]
    public async Task A_tracestate_that_is_not_a_safe_header_value_is_dropped(string traceState)
    {
        var trace = EnvironmentTraceContext.Parse(TraceParent, traceState)!;

        await Assert.That(trace.TraceParent).IsEqualTo(TraceParent);
        await Assert.That(trace.TraceState).IsNull();
        await Assert.That(trace.Context.TraceState).IsNull();
    }

    [Test]
    public async Task An_overlong_tracestate_is_dropped()
    {
        await Assert.That(EnvironmentTraceContext.Parse(TraceParent, new string('a', 8193))!.TraceState).IsNull();
        await Assert.That(EnvironmentTraceContext.Parse(TraceParent, new string('a', 8192))!.TraceState).IsNotNull();
    }

    [Test]
    public async Task Capture_logs_a_set_but_invalid_traceparent_at_debug_level()
    {
        using var logs = new CapturingLoggerFactory();
        var logger = logs.CreateLogger("OpenShell.Sdk");

        await Assert.That(EnvironmentTraceContext.Capture(logger, Env(("TRACEPARENT", "garbage")))).IsNull();
        await Assert.That(EnvironmentTraceContext.Capture(logger, Env())).IsNull();
        await Assert.That(EnvironmentTraceContext.Capture(logger, Env(("TRACEPARENT", TraceParent)))).IsNotNull();

        var entry = logs.Entries.Single();
        await Assert.That(entry.Level).IsEqualTo(LogLevel.Debug);
        await Assert.That(entry.Message).Contains("TRACEPARENT is set but invalid");
    }

    [Test]
    public async Task The_option_is_off_by_default_and_reaches_the_client_options_from_a_gateway_registration()
    {
        using var dir = new TempDirectory();
        var config = new GatewayMetadata { Name = "gw", Endpoint = new Uri("http://127.0.0.1:1"), Directory = dir.Path, AuthMode = GatewayAuthMode.None };

        await Assert.That(new OpenShellClientOptions { Address = new Uri("http://localhost") }.TraceContextFromEnvironment).IsFalse();
        await Assert.That(GatewayClient.CreateOptions(config).TraceContextFromEnvironment).IsFalse();
        await Assert.That(GatewayClient.CreateOptions(config, new GatewayConnectOptions { TraceContextFromEnvironment = true }).TraceContextFromEnvironment).IsTrue();
    }
}

/// <summary>How the instrumented invoker forwards the environment trace context; not parallel because of the listeners.</summary>
[NotInParallel]
public class EnvironmentTracePropagationTests
{
    private static readonly EnvironmentTraceContext Trace = EnvironmentTraceContext.Parse(EnvironmentTraceContextTests.TraceParent, "vendor=a")!;

    private static FakeCallInvoker NewInvoker() => new FakeCallInvoker()
        .Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ => new Wire.HealthResponse { Version = "1" })
        .Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => [new Wire.ExecSandboxEvent()])
        .ClientStream<Wire.PushSandboxLogsRequest, Wire.PushSandboxLogsResponse>("PushSandboxLogs", _ => new Wire.PushSandboxLogsResponse())
        .Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", _ => Task.CompletedTask);

    private static string? Header(CallOptions options, string key) => options.Headers?.GetValue(key);

    private static async Task MakeEveryKindOfCall(CallInvoker invoker)
    {
        var client = new Wire.OpenShell.OpenShellClient(invoker);
        await client.HealthAsync(new Wire.HealthRequest(), new Metadata { { "x-keep", "1" } });
        using var stream = client.ExecSandbox(new Wire.ExecSandboxRequest());
        await stream.ResponseStream.MoveNext(CancellationToken.None);
        using var upload = client.PushSandboxLogs();
        using var duplex = client.ForwardTcp();
    }

    [Test]
    public async Task Without_a_current_activity_every_call_carries_the_environment_trace()
    {
        var fake = NewInvoker();
        var before = Activity.Current;
        Activity.Current = null;
        try
        {
            await MakeEveryKindOfCall(new InstrumentedCallInvoker(fake, Trace));
        }
        finally
        {
            Activity.Current = before;
        }

        // The fake records unary, server-streaming, and duplex calls; client streaming shares the same path.
        await Assert.That(fake.Calls.Select(c => c.Method)).IsEquivalentTo(new[] { "Health", "ExecSandbox", "ForwardTcp" });
        foreach (var (_, _, options) in fake.Calls)
        {
            // The environment context itself, or the SDK span parented on it when a listener samples one.
            await Assert.That(Header(options, "traceparent")).StartsWith($"00-{EnvironmentTraceContextTests.TraceId}-");
            await Assert.That(Header(options, "tracestate")).IsEqualTo("vendor=a");
        }

        await Assert.That(Header(fake.Calls[0].Options, "x-keep")).IsEqualTo("1");
    }

    [Test]
    public async Task Without_a_captured_context_calls_are_unchanged()
    {
        var fake = NewInvoker();
        var before = Activity.Current;
        Activity.Current = null;
        try
        {
            await MakeEveryKindOfCall(new InstrumentedCallInvoker(fake));
        }
        finally
        {
            Activity.Current = before;
        }

        foreach (var (_, _, options) in fake.Calls)
        {
            await Assert.That(Header(options, "traceparent")).IsNull();
        }
    }

    [Test]
    public async Task A_traceparent_the_caller_set_is_not_replaced()
    {
        var fake = NewInvoker();
        var before = Activity.Current;
        Activity.Current = null;
        try
        {
            var client = new Wire.OpenShell.OpenShellClient(new InstrumentedCallInvoker(fake, Trace));
            await client.HealthAsync(new Wire.HealthRequest(), new Metadata { { "traceparent", "caller" } });
        }
        finally
        {
            Activity.Current = before;
        }

        await Assert.That(Header(fake.Calls.Single().Options, "traceparent")).IsEqualTo("caller");
        await Assert.That(Header(fake.Calls.Single().Options, "tracestate")).IsNull();
    }

    [Test]
    public async Task A_current_activity_takes_precedence_over_the_environment_context()
    {
        using var source = new ActivitySource("OpenShell.Tests.AmbientTrace");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "OpenShell.Tests.AmbientTrace",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var fake = NewInvoker();

        using (source.StartActivity("ambient"))
        {
            await new Wire.OpenShell.OpenShellClient(new InstrumentedCallInvoker(fake, Trace)).HealthAsync(new Wire.HealthRequest());
        }

        await Assert.That(Header(fake.Calls.Single().Options, "traceparent")).IsNull();
    }

    [Test]
    public async Task With_a_listener_the_sdk_span_is_parented_on_the_environment_context()
    {
        using var capture = new TelemetryCapture();
        var fake = NewInvoker();
        var before = Activity.Current;
        Activity.Current = null;
        try
        {
            using var client = OpenShellClient.ForTesting(new OpenShellClientOptions { Address = new Uri("http://localhost") }, fake, trace: Trace);
            await client.Health.CheckAsync();
        }
        finally
        {
            Activity.Current = before;
        }

        var span = capture.Stopped.Single(a => a.Source.Name == OpenShellTelemetry.ActivitySourceName && a.TraceId == Trace.Context.TraceId);
        await Assert.That(span.ParentSpanId).IsEqualTo(Trace.Context.SpanId);
        await Assert.That(span.TraceStateString).IsEqualTo("vendor=a");
        await Assert.That(span.HasRemoteParent).IsTrue();

        // The span is sent explicitly, so the gateway parents on it even if other instrumentation starts a span below.
        await Assert.That(Header(fake.Calls.Single().Options, "traceparent")).IsEqualTo(span.Id);
        await Assert.That(Header(fake.Calls.Single().Options, "tracestate")).IsEqualTo("vendor=a");
    }

    [Test]
    public async Task With_a_listener_and_a_current_activity_the_sdk_span_stays_under_the_caller()
    {
        using var capture = new TelemetryCapture();
        var fake = NewInvoker();

        using (var client = OpenShellClient.ForTesting(new OpenShellClientOptions { Address = new Uri("http://localhost") }, fake, trace: Trace))
        {
            await client.Health.CheckAsync();
        }

        var span = capture.Spans.Single();
        await Assert.That(span.ParentSpanId).IsEqualTo(capture.Parent.SpanId);
        await Assert.That(span.TraceId).IsNotEqualTo(Trace.Context.TraceId);
    }
}
