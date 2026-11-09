// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Testing;
using OpenShell.Tests.Support;
using GoogleRpc = Google.Rpc;
using Wire = Openshell.V1;
using WireSandbox = Openshell.Sandbox.V1;

namespace OpenShell.Tests;

/// <summary>The additive changes in the NVIDIA/OpenShell v0.1.7 protos.</summary>
public class UpstreamCatchUpTests
{
    private static readonly RetryPolicy Retry = new()
    {
        MaxAttempts = 3,
        InitialBackoff = TimeSpan.FromMilliseconds(100),
        MaxBackoff = TimeSpan.FromSeconds(5),
        Jitter = 0,
    };

    private static RpcException LockTimeout(TimeSpan delay)
    {
        const string Message = "timed out waiting for the sandbox mutation lock";
        var envelope = new GoogleRpc.Status { Code = (int)StatusCode.Unavailable, Message = Message };
        envelope.Details.Add(Any.Pack(new GoogleRpc.ErrorInfo { Reason = "MUTATION_LOCK_TIMEOUT", Domain = "openshell.ai" }));
        envelope.Details.Add(Any.Pack(new GoogleRpc.RetryInfo { RetryDelay = Duration.FromTimeSpan(delay) }));
        return new RpcException(new Status(StatusCode.Unavailable, Message), new Metadata { { "grpc-status-details-bin", envelope.ToByteArray() } });
    }

    private static OpenShellClient RetryingClient(FakeCallInvoker invoker, FakeTimeProvider time) =>
        OpenShellClient.ForTesting(
            new OpenShellClientOptions { Address = new Uri("http://localhost"), Retry = Retry },
            invoker,
            time,
            useRetry: true);

    [Test]
    [Arguments(Wire.SandboxPhase.Suspending, SandboxPhase.Suspending)]
    [Arguments(Wire.SandboxPhase.Suspended, SandboxPhase.Suspended)]
    public async Task Suspend_phases_are_mapped(Wire.SandboxPhase wire, SandboxPhase expected)
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>(
            "GetSandbox", _ => ClientTests.SandboxReply("sb", wire));
        using var client = ClientTests.NewClient(invoker);

        var sandbox = await client.Sandboxes.GetAsync("sb");

        await Assert.That(sandbox.Phase).IsEqualTo(expected);
    }

    [Test]
    public async Task Raw_sandbox_updates_client_calls_the_separate_service()
    {
        Wire.UpdateSandboxRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.UpdateSandboxRequest, Wire.SandboxResponse>("UpdateSandbox", r =>
        {
            seen = r;
            return ClientTests.SandboxReply("sb", Wire.SandboxPhase.Stopped);
        });
        using var client = ClientTests.NewClient(invoker);

        var response = await client.RawSandboxUpdates.UpdateSandboxAsync(new Wire.UpdateSandboxRequest { Name = "sb", Image = "img:2", ExpectedResourceVersion = 3 });

        await Assert.That(response.Sandbox.Metadata.Name).IsEqualTo("sb");
        await Assert.That(seen!.Image).IsEqualTo("img:2");
        await Assert.That(invoker.UnaryFullNames.Single()).IsEqualTo("/openshell.v1.SandboxUpdates/UpdateSandbox");
    }

    [Test]
    public async Task Provider_listing_sends_the_label_selector()
    {
        Wire.ListProvidersRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.ListProvidersRequest, Wire.ListProvidersResponse>("ListProviders", r =>
        {
            seen = r;
            return new Wire.ListProvidersResponse();
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Providers.List(new ListOptions { LabelSelector = "team=a,env=prod" }).ToListAsync();

        await Assert.That(seen!.LabelSelector).IsEqualTo("team=a,env=prod");
    }

    [Test]
    public async Task Provider_listing_without_a_selector_sends_none()
    {
        Wire.ListProvidersRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.ListProvidersRequest, Wire.ListProvidersResponse>("ListProviders", r =>
        {
            seen = r;
            return new Wire.ListProvidersResponse();
        });
        using var client = ClientTests.NewClient(invoker);

        await client.Providers.List().ToListAsync();

        await Assert.That(seen!.LabelSelector).IsEqualTo("");
    }

    [Test]
    public async Task Retry_waits_at_least_the_delay_the_gateway_asked_for()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>("Health", _ =>
            ++attempts == 1 ? throw LockTimeout(TimeSpan.FromSeconds(2)) : new Wire.HealthResponse { Version = "ok" });
        using var client = RetryingClient(invoker, time);

        var task = client.Health.CheckAsync().AsTask();
        for (var i = 0; i < 20 && attempts < 2; i++)
        {
            await Task.Yield();
        }

        // The policy's own backoff (100 ms) has passed, but not the 2 s the gateway asked for.
        time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 20; i++)
        {
            await Task.Yield();
        }

        await Assert.That(attempts).IsEqualTo(1);

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await task).Version).IsEqualTo("ok");
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task Retry_gives_up_when_the_gateway_asks_for_longer_than_the_policy_allows()
    {
        var invoker = new FakeCallInvoker().Unary<Wire.HealthRequest, Wire.HealthResponse>(
            "Health", _ => throw LockTimeout(TimeSpan.FromMinutes(1)));
        using var client = RetryingClient(invoker, new FakeTimeProvider());

        var ex = await Assert.That(async () => await client.Health.CheckAsync()).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Unavailable);
        await Assert.That(ex.ErrorInfo!.Reason).IsEqualTo("MUTATION_LOCK_TIMEOUT");
        await Assert.That(ex.RetryDelay).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(invoker.CountOf("Health")).IsEqualTo(1);
    }

    [Test]
    public async Task Fake_rejects_reserved_annotations_on_sandbox_create()
    {
        using var client = new FakeOpenShellClient();

        var ex = await Assert.That(async () => await client.Sandboxes.CreateAsync(
            "a",
            new SandboxSpec { Image = "img" },
            new CreateSandboxOptions { Annotations = new Dictionary<string, string> { ["internal.openshell.ai/owner"] = "x" } })).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(ex.Message).Contains("reserved 'internal.openshell.ai/' prefix");
        await Assert.That(await client.Sandboxes.List().ToListAsync()).IsEmpty();
    }

    [Test]
    public async Task Fake_rejects_reserved_annotations_on_config_update()
    {
        using var client = new FakeOpenShellClient();
        await client.Sandboxes.CreateAsync("a", new SandboxSpec { Image = "img" });

        var ex = await Assert.That(async () => await client.Config.SetPolicyAsync(
            "a",
            new WireSandbox.SandboxPolicy(),
            new SetPolicyOptions { Annotations = new Dictionary<string, string> { ["internal.openshell.ai/x"] = "y" } })).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);

        // Ordinary annotation keys, including other openshell.ai keys, are still accepted.
        await client.Config.SetPolicyAsync(
            "a",
            new WireSandbox.SandboxPolicy(),
            new SetPolicyOptions { Annotations = new Dictionary<string, string> { ["openshell.ai/note"] = "ok" } });
    }
}
