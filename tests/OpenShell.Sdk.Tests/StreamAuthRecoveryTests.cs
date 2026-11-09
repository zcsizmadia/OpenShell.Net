// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Grpc.Core;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

/// <summary>
/// <c>Unauthenticated</c> recovery for streams: a stream or duplex call the gateway rejects before anything was
/// exchanged is reopened once after the auth provider renews its credentials, like a unary call; nothing is replayed
/// after a message arrived.
/// </summary>
public class StreamAuthRecoveryTests
{
    private static RpcException Unauthenticated() => new(new Status(StatusCode.Unauthenticated, "expired"));

    /// <summary>An auth provider that sends credentials and whose recovery is scripted.</summary>
    private sealed class ScriptedRecovery(bool recovers) : IAuthProvider
    {
        private int _recoveries;

        public int Recoveries => Volatile.Read(ref _recoveries);

        public bool SendsCredentials => true;

        public ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<bool> TryRecoverAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _recoveries);
            return ValueTask.FromResult(recovers);
        }
    }

    /// <summary>A server-stream reply that fails with <paramref name="failure"/> after yielding <paramref name="before"/>.</summary>
    private static IEnumerable<Wire.ExecSandboxEvent> Failing(Exception failure, params Wire.ExecSandboxEvent[] before)
    {
        foreach (var e in before)
        {
            yield return e;
        }

        throw failure;
    }

    private static FakeCallInvoker ExecStream(Func<int, IEnumerable<Wire.ExecSandboxEvent>> reply)
    {
        var calls = 0;
        return new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>(
            "ExecSandbox", _ => reply(Interlocked.Increment(ref calls)));
    }

    [Test]
    public async Task Exec_stream_rejected_before_any_message_recovers_once_and_retries()
    {
        var invoker = ExecStream(n => n == 1 ? Failing(Unauthenticated()) : [Interactive.Out("hi"), Interactive.Exit(0)]);
        var auth = new ScriptedRecovery(recovers: true);
        using var client = ClientTests.NewClient(invoker, auth: auth);

        var result = await client.Exec.RunAsync("sb", ["echo", "hi"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(Encoding.UTF8.GetString(result.Stdout.Span)).IsEqualTo("hi");
        await Assert.That(auth.Recoveries).IsEqualTo(1);
        await Assert.That(invoker.CountOf("ExecSandbox")).IsEqualTo(2);
        await Assert.That(invoker.StreamDisposals).IsEqualTo(2);
    }

    [Test]
    public async Task Exec_stream_is_not_retried_when_recovery_fails()
    {
        var invoker = ExecStream(_ => Failing(Unauthenticated()));
        var auth = new ScriptedRecovery(recovers: false);
        using var client = ClientTests.NewClient(invoker, auth: auth);

        var ex = await Assert.That(async () => await client.Exec.RunAsync("sb", ["x"])).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unauthenticated);
        await Assert.That(auth.Recoveries).IsEqualTo(1);
        await Assert.That(invoker.CountOf("ExecSandbox")).IsEqualTo(1);
    }

    [Test]
    public async Task Exec_stream_retries_at_most_once()
    {
        var invoker = ExecStream(_ => Failing(Unauthenticated()));
        var auth = new ScriptedRecovery(recovers: true);
        using var client = ClientTests.NewClient(invoker, auth: auth);

        var ex = await Assert.That(async () => await client.Exec.RunAsync("sb", ["x"])).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unauthenticated);
        await Assert.That(auth.Recoveries).IsEqualTo(1);
        await Assert.That(invoker.CountOf("ExecSandbox")).IsEqualTo(2);
    }

    [Test]
    public async Task Exec_stream_is_not_replayed_after_a_message_arrived()
    {
        var invoker = ExecStream(_ => Failing(Unauthenticated(), Interactive.Out("partial")));
        var auth = new ScriptedRecovery(recovers: true);
        using var client = ClientTests.NewClient(invoker, auth: auth);

        var ex = await Assert.That(async () => await client.Exec.RunAsync("sb", ["x"])).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unauthenticated);
        await Assert.That(auth.Recoveries).IsEqualTo(0);
        await Assert.That(invoker.CountOf("ExecSandbox")).IsEqualTo(1);
    }

    [Test]
    public async Task An_empty_stream_still_ends_cleanly()
    {
        var invoker = ExecStream(_ => []);
        using var client = ClientTests.NewClient(invoker, auth: new ScriptedRecovery(recovers: true));

        var events = new List<ExecEvent>();
        var ex = await Assert.That(async () =>
        {
            await foreach (var e in client.Exec.StreamAsync("sb", ["x"]))
            {
                events.Add(e);
            }
        }).Throws<OpenShellException>();

        await Assert.That(ex!.Message).Contains("without an exit event");
        await Assert.That(events).IsEmpty();
        await Assert.That(invoker.CountOf("ExecSandbox")).IsEqualTo(1);
    }

    /// <summary>Fails the first <paramref name="method"/> call with <c>Unauthenticated</c> before the gateway answers.</summary>
    private static void RejectFirst(FakeCallInvoker invoker, string method)
    {
        var calls = 0;
        invoker.ResponseHeaders = (m, _) =>
            m == method && Interlocked.Increment(ref calls) == 1
                ? Task.FromException<Metadata>(Unauthenticated())
                : Task.FromResult(new Metadata());
    }

    [Test]
    public async Task Interactive_exec_rejected_before_the_gateway_answered_recovers_and_resends_the_start_frame()
    {
        var invoker = Interactive.With(Interactive.Shell);
        RejectFirst(invoker, "ExecSandboxInteractive");
        var auth = new ScriptedRecovery(recovers: true);
        using var client = ClientTests.NewClient(invoker, auth: auth);

        await using var session = await client.Exec.InteractiveAsync("sb", ["sh"], 80, 24);
        await session.WriteStdinAsync(Encoding.UTF8.GetBytes("abc"));
        await session.CompleteStdinAsync();
        var events = await Interactive.CollectAsync(session);

        await Assert.That(auth.Recoveries).IsEqualTo(1);
        await Assert.That(invoker.DuplexCalls.Count).IsEqualTo(2);
        var retried = (DuplexChannel<Wire.ExecSandboxInput, Wire.ExecSandboxEvent>)invoker.DuplexCalls[1].Channel;
        await Assert.That(retried.Received[0].Start.Command.ToArray()).IsEquivalentTo(new[] { "sh" });
        await Assert.That(Encoding.UTF8.GetString(events.OfType<ExecOutput>().Single().Data.Span)).IsEqualTo("ABC");
        await Assert.That(await session.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Interactive_exec_is_not_retried_when_recovery_fails()
    {
        var invoker = Interactive.With(Interactive.Hang);
        RejectFirst(invoker, "ExecSandboxInteractive");
        var auth = new ScriptedRecovery(recovers: false);
        using var client = ClientTests.NewClient(invoker, auth: auth);

        var ex = await Assert.That(async () => await client.Exec.InteractiveAsync("sb", ["sh"], 80, 24)).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unauthenticated);
        await Assert.That(auth.Recoveries).IsEqualTo(1);
        await Assert.That(invoker.DuplexCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Duplex_retries_at_most_once()
    {
        var invoker = Interactive.With(Interactive.Hang);
        invoker.ResponseHeaders = (_, _) => Task.FromException<Metadata>(Unauthenticated());
        var auth = new ScriptedRecovery(recovers: true);
        using var client = ClientTests.NewClient(invoker, auth: auth);

        // The second open is not checked again: its rejection surfaces when the session is read.
        await using var session = await client.Exec.InteractiveAsync("sb", ["sh"], 80, 24);

        await Assert.That(auth.Recoveries).IsEqualTo(1);
        await Assert.That(invoker.DuplexCalls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Duplex_is_not_reopened_after_the_gateway_answered()
    {
        var invoker = Interactive.With(_ => throw Unauthenticated());
        var auth = new ScriptedRecovery(recovers: true);
        using var client = ClientTests.NewClient(invoker, auth: auth);

        await using var session = await client.Exec.InteractiveAsync("sb", ["sh"], 80, 24);
        var ex = await Assert.That(async () => await Interactive.CollectAsync(session)).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unauthenticated);
        await Assert.That(auth.Recoveries).IsEqualTo(0);
        await Assert.That(invoker.DuplexCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Tcp_forward_rejected_before_the_gateway_answered_recovers_and_resends_the_init_frame()
    {
        var invoker = Relay.EchoInvoker();
        RejectFirst(invoker, "ForwardTcp");
        var auth = new ScriptedRecovery(recovers: true);
        using var client = ClientTests.NewClient(invoker, auth: auth);

        await using var stream = await client.Tcp.ForwardAsync("sb", 8080);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("ping"));

        await Assert.That(await Relay.ReadExactlyAsync(stream, 4)).IsEqualTo("ping");
        await Assert.That(auth.Recoveries).IsEqualTo(1);
        await Assert.That(invoker.CountOf("ForwardTcp")).IsEqualTo(2);
        await Assert.That(Relay.Channel(invoker, 1).Received[0].Init.Tcp.Port).IsEqualTo(8080u);
        await Assert.That(Relay.Channel(invoker, 0).ClientDisposed).IsTrue();
    }

    [Test]
    public async Task Without_credentials_a_rejected_duplex_is_not_waited_on_or_retried()
    {
        var invoker = Interactive.With(Interactive.Hang);
        RejectFirst(invoker, "ExecSandboxInteractive");
        using var client = ClientTests.NewClient(invoker);

        // Auth.None sends nothing to renew, so the open neither waits for the answer nor retries.
        await using var session = await client.Exec.InteractiveAsync("sb", ["sh"], 80, 24);

        await Assert.That(invoker.DuplexCalls.Count).IsEqualTo(1);
    }
}
