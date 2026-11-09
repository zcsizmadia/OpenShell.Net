// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class SshClientTests
{
    private static Wire.CreateSshSessionResponse ValidResponse(string sandboxId = "id-1") => new()
    {
        SandboxId = sandboxId,
        Token = "tok.en_~+/=-1",
        GatewayHost = "gateway.example.com",
        GatewayPort = 443,
        GatewayScheme = "https",
        HostKeyFingerprint = "SHA256:abc+/=",
        ExpirationTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddDays(3)),
    };

    private static FakeCallInvoker Session(Wire.CreateSshSessionResponse? response = null) =>
        new FakeCallInvoker().Unary<Wire.CreateSshSessionRequest, Wire.CreateSshSessionResponse>("CreateSshSession", _ => response ?? ValidResponse());

    [Test]
    public async Task CreateSession_maps_and_redacts_the_token()
    {
        Wire.CreateSshSessionRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.CreateSshSessionRequest, Wire.CreateSshSessionResponse>("CreateSshSession", r =>
        {
            seen = r;
            return ValidResponse();
        });
        using var client = ClientTests.NewClient(invoker, "ws");

        var session = await client.Ssh.CreateSessionAsync("sb");

        await Assert.That(seen!.WorkspaceScope.Workspace).IsEqualTo("ws");
        await Assert.That(seen.Sandbox).IsEqualTo("sb");
        await Assert.That(session.SandboxId).IsEqualTo("id-1");
        await Assert.That(session.Token).IsEqualTo("tok.en_~+/=-1");
        await Assert.That(session.GatewayHost).IsEqualTo("gateway.example.com");
        await Assert.That(session.GatewayPort).IsEqualTo(443);
        await Assert.That(session.GatewayScheme).IsEqualTo("https");
        await Assert.That(session.HostKeyFingerprint).IsEqualTo("SHA256:abc+/=");
        await Assert.That(session.ExpiresAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddDays(3));
        var text = session.ToString();
        await Assert.That(text).DoesNotContain("tok.en");
        await Assert.That(text).Contains("<redacted>");
        await Assert.That(text).Contains("gateway.example.com");
    }

    [Test]
    public async Task CreateSession_without_fingerprint_or_expiry_maps_to_null()
    {
        var response = ValidResponse();
        response.HostKeyFingerprint = "";
        response.ExpirationTime = null;
        using var client = ClientTests.NewClient(Session(response));

        var session = await client.Ssh.CreateSessionAsync("sb");

        await Assert.That(session.HostKeyFingerprint).IsNull();
        await Assert.That(session.ExpiresAt).IsNull();
    }

    public static IEnumerable<(string Name, Action<Wire.CreateSshSessionResponse> Mutate, string Field)> InvalidResponses()
    {
        yield return ("empty sandbox id", r => r.SandboxId = "", "sandbox_id must match [A-Za-z0-9._-]{1,128}");
        yield return ("sandbox id with slash", r => r.SandboxId = "a/b", "sandbox_id");
        yield return ("sandbox id too long", r => r.SandboxId = new string('a', 129), "sandbox_id");
        yield return ("sandbox id trailing newline", r => r.SandboxId = "abc\n", "sandbox_id");
        yield return ("empty token", r => r.Token = "", "token must be 1..4096 bytes of [A-Za-z0-9._~+/=-]");
        yield return ("token with space", r => r.Token = "a b", "token");
        yield return ("token with quote", r => r.Token = "a'b", "token");
        yield return ("token too long", r => r.Token = new string('a', 4097), "token");
        yield return ("empty host", r => r.GatewayHost = "", "gateway_host must be a valid DNS name, IPv4 address, or bracketed IPv6 address");
        yield return ("host with shell metachar", r => r.GatewayHost = "a;rm", "gateway_host");
        yield return ("host with space", r => r.GatewayHost = "a b", "gateway_host");
        yield return ("host too long", r => r.GatewayHost = string.Join('.', Enumerable.Repeat(new string('a', 60), 5)), "gateway_host");
        yield return ("host label too long", r => r.GatewayHost = new string('a', 64) + ".com", "gateway_host");
        yield return ("host label leading dash", r => r.GatewayHost = "-a.com", "gateway_host");
        yield return ("dotted quad with an invalid label", r => r.GatewayHost = "1.2.3.-4", "gateway_host");
        yield return ("host one byte too long", r => r.GatewayHost = Host253() + "a", "gateway_host");
        yield return ("host empty label", r => r.GatewayHost = "a..com", "gateway_host");
        yield return ("host only dot", r => r.GatewayHost = ".", "gateway_host");
        yield return ("unbracketed ipv6", r => r.GatewayHost = "::1", "gateway_host");
        yield return ("bracketed non-ipv6", r => r.GatewayHost = "[1.2.3.4]", "gateway_host");
        yield return ("bracketed garbage", r => r.GatewayHost = "[nope]", "gateway_host");
        yield return ("port zero", r => r.GatewayPort = 0, "gateway_port must be an integer in 1..65535");
        yield return ("port too large", r => r.GatewayPort = 65536, "gateway_port");
        yield return ("bad scheme", r => r.GatewayScheme = "ftp", "gateway_scheme must be exactly 'http' or 'https'");
        yield return ("empty scheme", r => r.GatewayScheme = "", "gateway_scheme");
        yield return ("fingerprint with space", r => r.HostKeyFingerprint = "a b", "host_key_fingerprint must be at most 256 bytes of [A-Za-z0-9:+/=-] when non-empty");
        yield return ("fingerprint too long", r => r.HostKeyFingerprint = new string('a', 257), "host_key_fingerprint");
    }

    [Test]
    [MethodDataSource(nameof(InvalidResponses))]
    public async Task CreateSession_rejects_responses_that_violate_the_contract(string name, Action<Wire.CreateSshSessionResponse> mutate, string field)
    {
        var response = ValidResponse();
        mutate(response);
        using var client = ClientTests.NewClient(Session(response));

        var ex = await Assert.That(async () => await client.Ssh.CreateSessionAsync("sb")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
        await Assert.That(ex.Message).Contains(field);
        await Assert.That(ex.Message).DoesNotContain("tok.en");
        await Assert.That(name).IsNotNull();
    }

    [Test]
    [Arguments("gateway.example.com")]
    [Arguments("gateway.example.com.")]
    [Arguments("localhost")]
    [Arguments("10.0.0.1")]
    [Arguments("255.255.255.255")]
    [Arguments("1.2.3")]
    [Arguments("[::1]")]
    [Arguments("[2001:db8::1]")]
    [Arguments("a-b.c1.io")]
    public async Task CreateSession_accepts_valid_gateway_hosts(string host)
    {
        var response = ValidResponse();
        response.GatewayHost = host;
        using var client = ClientTests.NewClient(Session(response));

        var session = await client.Ssh.CreateSessionAsync("sb");

        await Assert.That(session.GatewayHost).IsEqualTo(host);
    }

    /// <summary>A valid DNS name of exactly 253 bytes: three 63-byte labels and one of 61.</summary>
    private static string Host253() =>
        string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));

    public static IEnumerable<(string Name, Action<Wire.CreateSshSessionResponse> Mutate)> BoundaryResponses()
    {
        yield return ("one-byte token", r => r.Token = "a");
        yield return ("4096-byte token", r => r.Token = new string('a', 4096));
        yield return ("one-letter host", r => r.GatewayHost = "a");
        yield return ("253-byte host with 63-byte labels", r => r.GatewayHost = Host253());
        yield return ("port 1", r => r.GatewayPort = 1);
        yield return ("port 65535", r => r.GatewayPort = 65535);
        yield return ("http scheme", r => r.GatewayScheme = "http");
        yield return ("256-byte fingerprint", r => r.HostKeyFingerprint = new string('f', 256));
    }

    [Test]
    [MethodDataSource(nameof(BoundaryResponses))]
    public async Task CreateSession_accepts_values_at_the_contract_limits(string name, Action<Wire.CreateSshSessionResponse> mutate)
    {
        var response = ValidResponse();
        mutate(response);
        using var client = ClientTests.NewClient(Session(response));

        var session = await client.Ssh.CreateSessionAsync("sb");

        await Assert.That(session.Token).IsEqualTo(response.Token);
        await Assert.That(session.GatewayHost).IsEqualTo(response.GatewayHost);
        await Assert.That(session.GatewayPort).IsEqualTo((int)response.GatewayPort);
        await Assert.That(name).IsNotNull();
    }

    [Test]
    public async Task CreateSession_validates_the_sandbox_before_calling_the_gateway()
    {
        var invoker = Session();
        using var client = ClientTests.NewClient(invoker);

        await Assert.That(async () => await client.Ssh.CreateSessionAsync(" ")).Throws<ArgumentException>();

        await Assert.That(invoker.CountOf("CreateSshSession")).IsEqualTo(0);
    }

    [Test]
    public async Task Validate_checks_the_expected_sandbox_id()
    {
        SshClient.Validate(ValidResponse(), "id-1");

        var ex = await Assert.That(() => SshClient.Validate(ValidResponse(), "other")).Throws<OpenShellException>();
        await Assert.That(ex!.Message).IsEqualTo("CreateSshSession response sandbox_id must match requested sandbox 'other'");
    }

    [Test]
    public async Task RevokeSession_does_not_allow_missing_by_default()
    {
        Wire.RevokeSshSessionRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.RevokeSshSessionRequest, Wire.RevokeSshSessionResponse>("RevokeSshSession", r =>
        {
            seen = r;
            return new Wire.RevokeSshSessionResponse { Outcome = Wire.DeletionOutcome.Completed };
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Ssh.RevokeSessionAsync("tok");

        await Assert.That(seen!.AllowMissing).IsFalse();
    }

    [Test]
    public async Task RevokeSession_maps_outcome_and_options()
    {
        Wire.RevokeSshSessionRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.RevokeSshSessionRequest, Wire.RevokeSshSessionResponse>("RevokeSshSession", r =>
        {
            seen = r;
            return new Wire.RevokeSshSessionResponse { Outcome = Wire.DeletionOutcome.Completed };
        });
        using var client = ClientTests.NewClient(invoker);

        var result = await client.Ssh.RevokeSessionAsync("tok", new DeleteOptions { AllowMissing = true });

        await Assert.That(result.Outcome).IsEqualTo(DeletionOutcome.Completed);
        await Assert.That(seen!.Token).IsEqualTo("tok");
        await Assert.That(seen.AllowMissing).IsTrue();
        await Assert.That(async () => await client.Ssh.RevokeSessionAsync(" ")).Throws<ArgumentException>();
    }

    private static FakeCallInvoker TunnelInvoker(Func<Wire.RevokeSshSessionRequest, Wire.RevokeSshSessionResponse>? revoke = null) =>
        Relay.WithSandbox(Session())
            .Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", Relay.Echo)
            .Unary<Wire.RevokeSshSessionRequest, Wire.RevokeSshSessionResponse>(
                "RevokeSshSession", revoke ?? (_ => new Wire.RevokeSshSessionResponse { Outcome = Wire.DeletionOutcome.Completed }));

    [Test]
    public async Task Tunnel_opens_an_ssh_relay_and_revokes_the_session_on_dispose()
    {
        var invoker = TunnelInvoker();
        using var client = ClientTests.NewClient(invoker, "ws");

        var tunnel = await client.Ssh.TunnelAsync("sb", new TunnelOptions { ServiceId = "svc" });
        await tunnel.WriteAsync(Encoding.UTF8.GetBytes("SSH-2.0"));
        await Assert.That(await Relay.ReadExactlyAsync(tunnel, 7)).IsEqualTo("SSH-2.0");

        var init = Relay.Channel(invoker).Received[0].Init;
        await Assert.That(init.TargetCase).IsEqualTo(Wire.TcpForwardInit.TargetOneofCase.Ssh);
        await Assert.That(init.AuthorizationToken).IsEqualTo("tok.en_~+/=-1");
        await Assert.That(init.Sandbox).IsEqualTo("sb");
        await Assert.That(init.Workspace).IsEqualTo("ws");
        await Assert.That(init.ServiceId).IsEqualTo("svc");
        await Assert.That(invoker.CountOf("RevokeSshSession")).IsEqualTo(0);

        await tunnel.DisposeAsync();
        await tunnel.DisposeAsync();

        await Assert.That(invoker.CountOf("RevokeSshSession")).IsEqualTo(1);
        var revoke = (Wire.RevokeSshSessionRequest)invoker.Calls.Last(c => c.Method == "RevokeSshSession").Request;
        await Assert.That(revoke.Token).IsEqualTo("tok.en_~+/=-1");
        await Assert.That(revoke.AllowMissing).IsTrue();
    }

    [Test]
    public async Task Tunnel_dispose_survives_a_failing_revoke()
    {
        var invoker = TunnelInvoker(_ => throw new RpcException(new Status(StatusCode.Unavailable, "revoke failed")));
        using var client = ClientTests.NewClient(invoker);
        var tunnel = await client.Ssh.TunnelAsync("sb");

        await tunnel.DisposeAsync();

        await Assert.That(invoker.CountOf("RevokeSshSession")).IsEqualTo(1);
    }

    [Test]
    public async Task Tunnel_requires_an_existing_sandbox_and_creates_no_session_otherwise()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", StatusCode.NotFound, "gone");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Ssh.TunnelAsync("sb")).Throws<OpenShellException>();

        await Assert.That(ex!.IsNotFound).IsTrue();
        await Assert.That(invoker.CountOf("CreateSshSession")).IsEqualTo(0);
        await Assert.That(async () => await client.Ssh.TunnelAsync(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Tunnel_revokes_and_fails_when_the_session_targets_another_sandbox()
    {
        var invoker = Relay.WithSandbox(Session(ValidResponse("someone-else")))
            .Unary<Wire.RevokeSshSessionRequest, Wire.RevokeSshSessionResponse>("RevokeSshSession", _ => new Wire.RevokeSshSessionResponse());
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Ssh.TunnelAsync("sb")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidConfig);
        await Assert.That(ex.Message).Contains("sandbox_id does not match the requested sandbox");
        await Assert.That(invoker.CountOf("RevokeSshSession")).IsEqualTo(1);
        await Assert.That(invoker.CountOf("ForwardTcp")).IsEqualTo(0);
    }

    [Test]
    public async Task Tunnel_skips_the_id_check_when_the_sandbox_reports_no_id_and_defaults_the_service_id()
    {
        var invoker = new FakeCallInvoker()
            .Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => ClientTests.SandboxReply("sb", id: ""))
            .Unary<Wire.CreateSshSessionRequest, Wire.CreateSshSessionResponse>("CreateSshSession", _ => ValidResponse("someone-else"))
            .Duplex<Wire.TcpForwardFrame, Wire.TcpForwardFrame>("ForwardTcp", Relay.Echo)
            .Unary<Wire.RevokeSshSessionRequest, Wire.RevokeSshSessionResponse>("RevokeSshSession", _ => new Wire.RevokeSshSessionResponse());
        using var client = ClientTests.NewClient(invoker);

        var tunnel = await client.Ssh.TunnelAsync("sb");
        await tunnel.DisposeAsync();

        var init = Relay.Channel(invoker).Received[0].Init;
        await Assert.That(init.ServiceId).IsEqualTo("");
        await Assert.That(invoker.CountOf("RevokeSshSession")).IsEqualTo(1);
    }

    [Test]
    public async Task Tunnel_revokes_when_the_relay_cannot_be_opened()
    {
        var invoker = TunnelInvoker();
        invoker.DuplexFailWritesAfter = 0;
        using var client = ClientTests.NewClient(invoker);

        await Assert.That(async () => await client.Ssh.TunnelAsync("sb")).Throws<OpenShellException>();

        await Assert.That(invoker.CountOf("RevokeSshSession")).IsEqualTo(1);
    }
}
