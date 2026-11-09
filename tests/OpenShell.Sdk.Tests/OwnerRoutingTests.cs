// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Tests.Support;
using OpenShell.Transport;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class OwnerRoutingTests
{
    private const string Owner = "openshell-gateway-2";

    private static string? Replica(CallOptions options) => options.Headers?.GetValue(OwnerRouting.ReplicaHeader);

    private static string? WantOwner(CallOptions options) => options.Headers?.GetValue(OwnerRouting.WantOwnerHeader);

    /// <summary>A gateway whose GetSandbox reports <paramref name="owner"/> to callers that ask for it.</summary>
    private static FakeCallInvoker Gateway(string? owner = Owner)
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => ClientTests.SandboxReply("sb"));
        invoker.ResponseHeaders = (method, options) =>
            Task.FromResult(method == "GetSandbox" && WantOwner(options) == "1" && owner is not null
                ? new Metadata { { OwnerRouting.OwnerHeader, owner } }
                : new Metadata());
        return invoker;
    }

    private static IEnumerable<Wire.ExecSandboxEvent> Output(string text)
    {
        yield return new Wire.ExecSandboxEvent { Stdout = new Wire.ExecSandboxStdout { Data = ByteString.CopyFromUtf8(text) } };
        yield return new Wire.ExecSandboxEvent { Exit = new Wire.ExecSandboxExit { ExitCode = 0 } };
    }

    /// <summary>Makes routed calls of <paramref name="method"/> end before the gateway answers.</summary>
    private static void FailRouted(FakeCallInvoker invoker, string method, Func<Task<Metadata>> failure)
    {
        var inner = invoker.ResponseHeaders!;
        invoker.ResponseHeaders = (m, options) => m == method && Replica(options) is not null ? failure() : inner(m, options);
    }

    private static List<CallOptions> OptionsOf(FakeCallInvoker invoker, string method) =>
        [.. invoker.Calls.Where(c => c.Method == method).Select(c => c.Options)];

    [Test]
    public async Task Exec_is_routed_to_the_owner_the_gateway_reports()
    {
        var invoker = Gateway().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => Output("hi"));
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Exec.RunAsync("sb", ["echo", "hi"]);

        await Assert.That(result.StdoutText).IsEqualTo("hi");
        await Assert.That(WantOwner(OptionsOf(invoker, "GetSandbox").Single())).IsEqualTo("1");
        await Assert.That(Replica(OptionsOf(invoker, "ExecSandbox").Single())).IsEqualTo(Owner);
    }

    [Test]
    public async Task Owners_are_cached_per_sandbox_until_the_ttl_passes()
    {
        var time = new FakeTimeProvider();
        var invoker = Gateway().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => Output("x"));
        using var client = ClientTests.NewClient(invoker, time: time);

        await client.Exec.RunAsync("sb", ["true"]);
        await client.Exec.RunAsync("sb", ["true"]);
        await client.InWorkspace("other").Exec.RunAsync("sb", ["true"]);
        time.Advance(OwnerRouting.CacheTtl + TimeSpan.FromSeconds(1));
        await client.Exec.RunAsync("sb", ["true"]);

        // Two lookups for (default, sb), one for (other, sb); every exec is routed.
        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(3);
        await Assert.That(OptionsOf(invoker, "ExecSandbox").All(o => Replica(o) == Owner)).IsTrue();
    }

    [Test]
    public async Task Without_an_owner_exec_is_unrouted_and_the_answer_is_cached()
    {
        var invoker = Gateway(owner: null).Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => Output("x"));
        using var client = ClientTests.NewClient(invoker);

        await client.Exec.RunAsync("sb", ["true"]);
        await client.Exec.RunAsync("sb", ["true"]);

        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(1);
        await Assert.That(OptionsOf(invoker, "ExecSandbox").All(o => Replica(o) is null)).IsTrue();
    }

    [Test]
    public async Task A_failed_lookup_leaves_exec_unrouted()
    {
        var invoker = new FakeCallInvoker()
            .Throws<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", StatusCode.PermissionDenied, "no")
            .Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => Output("x"));
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Exec.RunAsync("sb", ["true"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(Replica(OptionsOf(invoker, "ExecSandbox").Single())).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("no healthy upstream")]
    [Arguments("upstream connect error or disconnect/reset before headers. reset reason: connection failure")]
    public async Task An_unreachable_replica_is_retried_once_unrouted(string detail)
    {
        var invoker = Gateway().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => Output("ok"));
        FailRouted(invoker, "ExecSandbox", () => Task.FromException<Metadata>(new RpcException(new Status(StatusCode.Unavailable, detail))));
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Exec.RunAsync("sb", ["true"]);
        await client.Exec.RunAsync("sb", ["true"]);

        await Assert.That(result.StdoutText).IsEqualTo("ok");
        var calls = OptionsOf(invoker, "ExecSandbox");
        await Assert.That(string.Join(",", calls.Select(o => Replica(o) ?? "-"))).IsEqualTo($"{Owner},-,{Owner},-");

        // The owner is forgotten after an unrouted retry, so the second exec looked it up again.
        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(2);
    }

    [Test]
    public async Task A_trailers_only_unavailable_answer_is_retried_unrouted()
    {
        var invoker = Gateway().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => Output("ok"));
        FailRouted(invoker, "ExecSandbox", () => Task.FromResult(new Metadata { { "grpc-status", "14" }, { "grpc-message", "no%20healthy%20upstream" } }));
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Exec.RunAsync("sb", ["true"]);

        await Assert.That(result.StdoutText).IsEqualTo("ok");
        await Assert.That(string.Join(",", OptionsOf(invoker, "ExecSandbox").Select(o => Replica(o) ?? "-"))).IsEqualTo($"{Owner},-");
    }

    [Test]
    public async Task Gateway_errors_on_a_routed_stream_are_not_retried()
    {
        static IEnumerable<Wire.ExecSandboxEvent> Fail()
        {
            yield break;
        }

        var invoker = Gateway().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>(
            "ExecSandbox",
            (_, options) => Replica(options) is null ? Output("unrouted") : Fail());
        FailRouted(invoker, "ExecSandbox", () => Task.FromException<Metadata>(new RpcException(new Status(StatusCode.Unavailable, "supervisor not connected"))));
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Exec.RunAsync("sb", ["true"])).Throws<OpenShellException>();

        await Assert.That(ex!.Message).Contains("without an exit event");
        await Assert.That(invoker.CountOf("ExecSandbox")).IsEqualTo(1);
    }

    [Test]
    public async Task Trailers_only_success_and_unparsable_statuses_count_as_answered()
    {
        await Assert.That(await OwnerRouting.UnansweredAsync(Task.FromResult(new Metadata { { "grpc-status", "0" } }))).IsNull();
        await Assert.That(await OwnerRouting.UnansweredAsync(Task.FromResult(new Metadata { { "grpc-status", "x" } }))).IsNull();
        await Assert.That(await OwnerRouting.UnansweredAsync(Task.FromResult(new Metadata { { "content-type", "application/grpc" } }))).IsNull();

        var failure = await OwnerRouting.UnansweredAsync(Task.FromResult(new Metadata { { "grpc-status", "9" } }));
        await Assert.That(failure!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
        await Assert.That(failure.Status.Detail).IsEqualTo("");
        await Assert.That(OwnerRouting.RetryUnrouted(failure)).IsFalse();
    }

    [Test]
    public async Task Routing_can_be_turned_off()
    {
        var invoker = Gateway()
            .Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => Output("x"))
            .Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", Relay.Echo);
        using var client = OpenShellClient.ForTesting(
            new OpenShellClientOptions { Address = new Uri("http://localhost"), RouteToOwnerReplica = false },
            invoker);

        await client.Exec.RunAsync("sb", ["true"]);
        await using var stream = await client.Tcp.ForwardAsync("sb", 8080);

        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(1);
        await Assert.That(WantOwner(OptionsOf(invoker, "GetSandbox").Single())).IsNull();
        await Assert.That(Replica(OptionsOf(invoker, "ExecSandbox").Single())).IsNull();
        await Assert.That(Replica(OptionsOf(invoker, "ForwardTcp").Single())).IsNull();
    }

    [Test]
    public async Task Port_forwarding_is_routed_through_its_existence_check()
    {
        var invoker = Gateway().Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", Relay.Echo);
        using var client = ClientTests.NewClient(invoker);

        await using var stream = await client.Tcp.ForwardAsync("sb", 8080);
        await stream.WriteAsync("ping"u8.ToArray());

        await Assert.That(await Relay.ReadExactlyAsync(stream, 4)).IsEqualTo("ping");
        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(1);
        await Assert.That(Replica(OptionsOf(invoker, "ForwardTcp").Single())).IsEqualTo(Owner);
    }

    [Test]
    public async Task A_routed_forward_is_reopened_unrouted_with_the_init_frame_resent()
    {
        var invoker = Gateway().Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", Relay.Echo);
        FailRouted(invoker, "ForwardTcp", () => Task.FromException<Metadata>(new RpcException(new Status(StatusCode.Unavailable, "no healthy upstream"))));
        using var client = ClientTests.NewClient(invoker);

        await using var stream = await client.Tcp.ForwardAsync("sb", 8080);
        await stream.WriteAsync("ping"u8.ToArray());

        await Assert.That(await Relay.ReadExactlyAsync(stream, 4)).IsEqualTo("ping");
        await Assert.That(string.Join(",", OptionsOf(invoker, "ForwardTcp").Select(o => Replica(o) ?? "-"))).IsEqualTo($"{Owner},-");
        await Assert.That(Relay.Channel(invoker, 0).Received[0].Init.Tcp.Port).IsEqualTo(8080u);
        await Assert.That(Relay.Channel(invoker, 1).Received[0].Init.Tcp.Port).IsEqualTo(8080u);
    }

    [Test]
    public async Task A_routed_forward_that_the_gateway_rejects_fails_without_a_retry()
    {
        var invoker = Gateway().Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", Relay.Echo);
        invoker.DuplexFailWritesAfter = 0;
        FailRouted(invoker, "ForwardTcp", () => Task.FromException<Metadata>(new RpcException(new Status(StatusCode.FailedPrecondition, "sandbox is not ready"))));
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Tcp.ForwardAsync("sb", 8080)).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(ex.Message).Contains("sandbox is not ready");
        await Assert.That(invoker.CountOf("ForwardTcp")).IsEqualTo(1);
    }

    [Test]
    public async Task Interactive_exec_is_routed()
    {
        var invoker = Gateway().Duplex<Wire.ExecSandboxInput, Wire.ExecSandboxEvent>("ExecSandboxInteractive", async ch =>
        {
            await ch.ReadAsync();
            ch.Send(new Wire.ExecSandboxEvent { Exit = new Wire.ExecSandboxExit { ExitCode = 7 } });
        });
        using var client = ClientTests.NewClient(invoker);

        await using var session = await client.Exec.InteractiveAsync("sb", ["bash"], 80, 24);

        await Assert.That(await session.WaitForExitAsync()).IsEqualTo(7);
        await Assert.That(Replica(OptionsOf(invoker, "ExecSandboxInteractive").Single())).IsEqualTo(Owner);
    }

    [Test]
    public async Task Interactive_exec_is_reopened_unrouted_with_the_start_frame_resent()
    {
        var invoker = Gateway().Duplex<Wire.ExecSandboxInput, Wire.ExecSandboxEvent>("ExecSandboxInteractive", async ch =>
        {
            var start = await ch.ReadAsync();
            ch.Send(new Wire.ExecSandboxEvent { Exit = new Wire.ExecSandboxExit { ExitCode = start!.Start.Command.Count } });
        });
        FailRouted(invoker, "ExecSandboxInteractive", () => Task.FromException<Metadata>(new RpcException(new Status(StatusCode.Unavailable, ""))));
        using var client = ClientTests.NewClient(invoker);

        await using var session = await client.Exec.InteractiveAsync("sb", ["bash", "-l"], 80, 24);

        await Assert.That(await session.WaitForExitAsync()).IsEqualTo(2);
        await Assert.That(string.Join(",", OptionsOf(invoker, "ExecSandboxInteractive").Select(o => Replica(o) ?? "-"))).IsEqualTo($"{Owner},-");
        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(1);

        // The unreachable owner was forgotten, so the next stream looks it up again.
        await using var next = await client.Exec.InteractiveAsync("sb", ["sh"], 80, 24);
        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(2);
    }

    [Test]
    public async Task A_cancelled_routed_open_releases_the_call()
    {
        var invoker = Gateway().Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", Relay.Echo);
        using var client = ClientTests.NewClient(invoker);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await client.Tcp.ForwardAsync("sb", 8080, cancellationToken: cts.Token)).Throws<OperationCanceledException>();

        await Assert.That(Replica(OptionsOf(invoker, "ForwardTcp").Single())).IsEqualTo(Owner);
        await Assert.That(Relay.Channel(invoker).Received).IsEmpty();
    }

    [Test]
    public async Task Ssh_tunnels_are_routed()
    {
        var invoker = Gateway()
            .Unary<Wire.CreateSshSessionRequest, Wire.CreateSshSessionResponse>("CreateSshSession", _ => new Wire.CreateSshSessionResponse
            {
                SandboxId = "id-1",
                Token = "tok",
                GatewayHost = "gateway.example.com",
                GatewayPort = 443,
                GatewayScheme = "https",
                HostKeyFingerprint = "SHA256:abc",
            })
            .Unary<Wire.RevokeSshSessionRequest, Wire.RevokeSshSessionResponse>("RevokeSshSession", _ => new Wire.RevokeSshSessionResponse())
            .Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", Relay.Echo);
        using var client = ClientTests.NewClient(invoker);

        FailRouted(invoker, "ForwardTcp", () => Task.FromException<Metadata>(new RpcException(new Status(StatusCode.Unavailable, "no healthy upstream"))));

        await using (await client.Ssh.TunnelAsync("sb"))
        {
            await Assert.That(string.Join(",", OptionsOf(invoker, "ForwardTcp").Select(o => Replica(o) ?? "-"))).IsEqualTo($"{Owner},-");
            await Assert.That(Relay.Channel(invoker, 1).Received[0].Init.AuthorizationToken).IsEqualTo("tok");
        }

        await Assert.That(invoker.CountOf("RevokeSshSession")).IsEqualTo(1);
    }

    [Test]
    public async Task The_owner_cache_is_bounded()
    {
        var time = new FakeTimeProvider();
        var invoker = Gateway().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => Output("x"));
        using var client = ClientTests.NewClient(invoker, time: time);

        for (var i = 0; i <= OwnerRouting.MaxCacheEntries; i++)
        {
            await client.Exec.RunAsync($"sb-{i}", ["true"]);
        }

        // Adding entry 1025 cleared the full cache, so sb-0 is looked up again.
        await client.Exec.RunAsync("sb-0", ["true"]);

        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(OwnerRouting.MaxCacheEntries + 2);
    }
}
