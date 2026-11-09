// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class ClientTests
{
    internal static OpenShellClient NewClient(FakeCallInvoker invoker, string workspace = "default", TimeProvider? time = null, IAuthProvider? auth = null) =>
        OpenShellClient.ForTesting(
            new OpenShellClientOptions { Address = new Uri("http://localhost"), DefaultWorkspace = workspace, Auth = auth ?? Auth.None },
            invoker,
            time);

    internal static Wire.SandboxResponse SandboxReply(
        string name,
        Wire.SandboxPhase phase = Wire.SandboxPhase.Ready,
        string id = "id-1",
        string workspace = "default") =>
        new()
        {
            Sandbox = new Wire.Sandbox
            {
                Metadata = new Openshell.Datamodel.V1.ObjectMeta { Id = id, Name = name, Workspace = workspace },
                Status = new Wire.SandboxStatus { Phase = phase },
            },
        };

    [Test]
    public async Task Health_maps_status_and_version()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>(
            "Health", _ => new Wire.HealthResponse { Status = Wire.ServiceStatus.Degraded, Version = "1.2.3" });
        using var client = NewClient(invoker);

        var result = await client.Health.CheckAsync();

        await Assert.That(result.Status).IsEqualTo(ServiceStatus.Degraded);
        await Assert.That(result.Version).IsEqualTo("1.2.3");
    }

    [Test]
    public async Task GatewayInfo_maps_drivers_and_extensions()
    {
        var reply = new Wire.GetGatewayInfoResponse { Status = Wire.ServiceStatus.Healthy, GatewayVersion = "9" };
        reply.ComputeDrivers.Add(new Wire.ComputeDriverInfo { Name = "docker" });
        reply.Extensions.Add(new Wire.NegotiatedExtensionInfo
        {
            ConfiguredName = "cfg",
            ImplementationName = "impl",
            ImplementationVersion = "2",
            ProtocolMajor = 1,
            ProtocolMinor = 3,
            SupportedCapabilities = { "a" },
            RequiredCapabilities = { "b" },
        });
        var invoker = new FakeCallInvoker().Unary<Wire.GetGatewayInfoRequest, Wire.GetGatewayInfoResponse>("GetGatewayInfo", _ => reply);
        using var client = NewClient(invoker);

        var info = await client.Health.GetGatewayInfoAsync();

        await Assert.That(info.GatewayVersion).IsEqualTo("9");
        await Assert.That(info.ComputeDrivers.Single().Name).IsEqualTo("docker");
        var ext = info.Extensions.Single();
        await Assert.That(ext.ConfiguredName).IsEqualTo("cfg");
        await Assert.That(ext.ProtocolMinor).IsEqualTo(3u);
        await Assert.That(ext.SupportedCapabilities.Single()).IsEqualTo("a");
        await Assert.That(ext.RequiredCapabilities.Single()).IsEqualTo("b");
    }

    [Test]
    public async Task CurrentUser_maps_fields()
    {
        var reply = new Wire.GetCurrentUserResponse { Subject = "u1", DisplayName = "User", IdentityProvider = "idp" };
        reply.Roles.Add("admin");
        reply.Scopes.Add("sandbox:read");
        var invoker = new FakeCallInvoker().Unary<Wire.GetCurrentUserRequest, Wire.GetCurrentUserResponse>("GetCurrentUser", _ => reply);
        using var client = NewClient(invoker);

        var user = await client.Health.GetCurrentUserAsync();

        await Assert.That(user.Subject).IsEqualTo("u1");
        await Assert.That(user.Roles.Single()).IsEqualTo("admin");
        await Assert.That(user.Scopes.Single()).IsEqualTo("sandbox:read");
        await Assert.That(user.IdentityProvider).IsEqualTo("idp");
    }

    [Test]
    public async Task Grpc_failures_surface_as_OpenShellException()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Unavailable, "down");
        using var client = NewClient(invoker);

        var ex = await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
        await Assert.That(ex.Message).IsEqualTo("down");
        await Assert.That(ex.IsTransient).IsTrue();
    }

    [Test]
    public async Task Unauthenticated_triggers_one_recovery_then_retries()
    {
        var attempts = 0;
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ =>
            ++attempts == 1
                ? throw new RpcException(new Status(StatusCode.Unauthenticated, "expired"))
                : new Wire.HealthResponse { Version = "ok" });
        using var token = new RefreshableToken(new AccessToken("old"), _ => ValueTask.FromResult(new AccessToken("new")));
        using var client = NewClient(invoker, auth: token.AsAuthProvider());

        var result = await client.Health.CheckAsync();

        await Assert.That(result.Version).IsEqualTo("ok");
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task Unauthenticated_without_recovery_is_not_retried()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Unauthenticated, "no");
        using var client = NewClient(invoker);

        var ex = await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unauthenticated);
        await Assert.That(invoker.CountOf("Health")).IsEqualTo(1);
    }

    [Test]
    public async Task Unauthenticated_after_recovery_fails_on_second_attempt()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Unauthenticated, "still bad");
        using var token = new RefreshableToken(new AccessToken("old"), _ => ValueTask.FromResult(new AccessToken("new")));
        using var client = NewClient(invoker, auth: token.AsAuthProvider());

        var ex = await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unauthenticated);
        await Assert.That(invoker.CountOf("Health")).IsEqualTo(2);
    }

    [Test]
    public async Task Recovery_failure_is_reported_as_auth_error()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Unauthenticated, "x");
        using var token = new RefreshableToken(new AccessToken("old"), _ => throw new InvalidOperationException("idp"));
        using var client = NewClient(invoker, auth: token.AsAuthProvider());

        var ex = await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Auth);
    }

    /// <summary>An auth provider whose recovery is scripted.</summary>
    private sealed class ScriptedRecovery(Func<bool> recover) : IAuthProvider
    {
        public int Recoveries { get; private set; }

        public bool SendsCredentials => true;

        public ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<bool> TryRecoverAsync(CancellationToken cancellationToken)
        {
            Recoveries++;
            return ValueTask.FromResult(recover());
        }
    }

    [Test]
    public async Task Only_unauthenticated_failures_try_recovery()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.NotFound, "gone");
        var auth = new ScriptedRecovery(() => true);
        using var client = NewClient(invoker, auth: auth);

        var ex = await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();

        await Assert.That(ex!.IsNotFound).IsTrue();
        await Assert.That(auth.Recoveries).IsEqualTo(0);
        await Assert.That(invoker.CountOf("Health")).IsEqualTo(1);
    }

    [Test]
    public async Task Recovery_is_attempted_once_even_when_it_keeps_succeeding()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Unauthenticated, "still bad");
        var auth = new ScriptedRecovery(() => true);
        using var client = NewClient(invoker, auth: auth);

        var ex = await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unauthenticated);
        await Assert.That(auth.Recoveries).IsEqualTo(1);
        await Assert.That(invoker.CountOf("Health")).IsEqualTo(2);
    }

    [Test]
    public async Task Recovery_exceptions_are_wrapped_unless_already_typed()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Unauthenticated, "x");
        var cause = new InvalidOperationException("idp down");
        var typed = new OpenShellException(OpenShellErrorKind.PermissionDenied, "typed");

        using (var client = NewClient(invoker, auth: new ScriptedRecovery(() => throw cause)))
        {
            var ex = await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();
            await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Auth);
            await Assert.That(ex.Message).IsEqualTo("credential recovery failed");
            await Assert.That(ex.InnerException).IsSameReferenceAs(cause);
        }

        using (var client = NewClient(invoker, auth: new ScriptedRecovery(() => throw typed)))
        {
            var ex = await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();
            await Assert.That(ex).IsSameReferenceAs(typed);
        }

        using (var client = NewClient(invoker, auth: new ScriptedRecovery(() => throw new OperationCanceledException("stop"))))
        {
            var ex = await Assert.That(async () => await client.Health.CheckAsync()).Throws<OperationCanceledException>();
            await Assert.That(ex!.Message).IsEqualTo("stop");
        }
    }

    [Test]
    public async Task Disposing_a_client_disposes_its_channel()
    {
        var handler = new TrackingHandler();
        var client = new OpenShellClient(new OpenShellClientOptions { Address = new Uri("http://localhost:9"), HttpHandler = handler });

        client.Dispose();

        await Assert.That(handler.Disposed).IsTrue();
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("tests never send through this handler");

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Test]
    public async Task Caller_cancellation_surfaces_as_OperationCanceled()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.HealthRequest, Wire.HealthResponse>("Health", StatusCode.Cancelled, "c");
        using var client = NewClient(invoker);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await client.Health.CheckAsync(cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Disposed_client_rejects_calls()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ => new Wire.HealthResponse());
        var client = NewClient(invoker);
        client.Dispose();
        client.Dispose();

        await Assert.That(async () => await client.Health.CheckAsync()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task Default_timeout_becomes_call_deadline()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ => new Wire.HealthResponse());
        using var client = OpenShellClient.ForTesting(
            new OpenShellClientOptions { Address = new Uri("http://localhost"), DefaultTimeout = TimeSpan.FromSeconds(5) }, invoker);

        await client.Health.CheckAsync();

        await Assert.That(invoker.Calls.Single().Options.Deadline).IsNotNull();
    }

    [Test]
    public async Task Disposed_client_rejects_streams_without_opening_them()
    {
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>("ExecSandbox", _ => []);
        var client = OpenShellClient.ForTesting(
            new OpenShellClientOptions { Address = new Uri("http://localhost"), RouteToOwnerReplica = false }, invoker);
        client.Dispose();

        await Assert.That(async () => await client.Exec.RunAsync("sb", ["true"])).Throws<ObjectDisposedException>();
        await Assert.That(invoker.CountOf("ExecSandbox")).IsEqualTo(0);
    }

    [Test]
    public async Task Default_timeout_does_not_limit_streams()
    {
        var invoker = new FakeCallInvoker().Stream<Wire.ExecSandboxRequest, Wire.ExecSandboxEvent>(
            "ExecSandbox", _ => [new Wire.ExecSandboxEvent { Exit = new Wire.ExecSandboxExit { ExitCode = 0 } }]);
        using var client = OpenShellClient.ForTesting(
            new OpenShellClientOptions { Address = new Uri("http://localhost"), DefaultTimeout = TimeSpan.FromSeconds(5), RouteToOwnerReplica = false },
            invoker);

        await client.Exec.RunAsync("sb", ["true"]);

        await Assert.That(invoker.Calls.Single(c => c.Method == "ExecSandbox").Options.Deadline).IsNull();
    }

    [Test]
    public async Task InWorkspace_shares_connection_and_changes_scope()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => SandboxReply("sb"));
        using var client = NewClient(invoker);
        var other = client.InWorkspace("team-a");

        await other.Sandboxes.GetAsync("sb");
        await client.Sandboxes.GetAsync("sb");
        other.Dispose();

        var requests = invoker.Calls.Select(c => ((Wire.GetSandboxRequest)c.Request).WorkspaceScope.Workspace).ToArray();
        await Assert.That(requests).IsEquivalentTo(new[] { "team-a", "default" });
        await Assert.That(other.Workspace).IsEqualTo("team-a");
        // Disposing a scoped view must not tear down the shared connection.
        await client.Sandboxes.GetAsync("sb");
    }

    [Test]
    public async Task InWorkspace_rejects_blank_name()
    {
        using var client = NewClient(new FakeCallInvoker());
        await Assert.That(() => client.InWorkspace(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Public_constructor_validates_and_builds_channel()
    {
        using var client = new OpenShellClient(new OpenShellClientOptions { Address = new Uri("http://localhost:1") });
        await Assert.That(client.Workspace).IsEqualTo("default");
        await Assert.That(client.Raw).IsNotNull();

        await Assert.That(() => new OpenShellClient(new OpenShellClientOptions { Address = new Uri("http://remote.example.com"), Auth = Auth.StaticToken("t") }))
            .Throws<OpenShellException>();
    }

    [Test]
    public async Task Struct_json_roundtrip_in_spec_mapping()
    {
        var spec = new SandboxSpec
        {
            Image = "img",
            Template = new SandboxTemplateSpec
            {
                RuntimeClassName = "gvisor",
                UserNamespaces = true,
                ResourcesJson = """{"limits":{"cpu":"1"}}""",
                DriverConfigJson = """{"k":"v"}""",
                Labels = new Dictionary<string, string> { ["a"] = "b" },
                Annotations = new Dictionary<string, string> { ["c"] = "d" },
                Environment = new Dictionary<string, string> { ["E"] = "1" },
            },
            Command = ["sleep", "1"],
            Environment = new Dictionary<string, string> { ["X"] = "y" },
            Providers = ["github"],
            LogLevel = "debug",
            Tty = true,
            RestartPolicy = SandboxRestartPolicy.OnFailure,
            RawSpec = w => w.LogLevel = "trace",
        };

        var wire = SandboxMapping.ToWire(spec);

        await Assert.That(wire.Template.Image).IsEqualTo("img");
        await Assert.That(wire.Template.RuntimeClassName).IsEqualTo("gvisor");
        await Assert.That(wire.Template.UserNamespaces).IsTrue();
        await Assert.That(wire.Template.Resources.Fields["limits"].StructValue.Fields["cpu"].StringValue).IsEqualTo("1");
        await Assert.That(wire.Template.DriverConfig.Fields["k"].StringValue).IsEqualTo("v");
        await Assert.That(wire.Template.Labels["a"]).IsEqualTo("b");
        await Assert.That(wire.Template.Annotations["c"]).IsEqualTo("d");
        await Assert.That(wire.Template.Environment["E"]).IsEqualTo("1");
        await Assert.That(wire.Command).IsEquivalentTo(new[] { "sleep", "1" });
        await Assert.That(wire.Environment["X"]).IsEqualTo("y");
        await Assert.That(wire.Providers).IsEquivalentTo(new[] { "github" });
        await Assert.That(wire.Tty).IsTrue();
        await Assert.That(wire.RestartPolicy).IsEqualTo(Wire.SandboxRestartPolicy.OnFailure);
        await Assert.That(wire.LogLevel).IsEqualTo("trace");
    }

    [Test]
    public async Task Spec_without_template_or_image_leaves_template_unset()
    {
        var wire = SandboxMapping.ToWire(new SandboxSpec());
        await Assert.That(wire.Template).IsNull();
        await Assert.That(wire.Policy).IsNull();
    }

    [Test]
    public async Task Policy_is_passed_through()
    {
        var policy = new Openshell.Sandbox.V1.SandboxPolicy { Version = 7 };
        var wire = SandboxMapping.ToWire(new SandboxSpec { Policy = policy });
        await Assert.That(wire.Policy.Version).IsEqualTo(7u);
    }
}
