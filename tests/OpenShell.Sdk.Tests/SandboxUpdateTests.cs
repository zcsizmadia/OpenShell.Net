// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using OpenShell.Testing;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class SandboxUpdateTests
{
    private static (FakeCallInvoker Invoker, List<Wire.UpdateSandboxRequest> Seen) Gateway()
    {
        var seen = new List<Wire.UpdateSandboxRequest>();
        var invoker = new FakeCallInvoker().Unary<Wire.UpdateSandboxRequest, Wire.SandboxResponse>("UpdateSandbox", r =>
        {
            seen.Add(r);
            return ClientTests.SandboxReply(r.Name, Wire.SandboxPhase.Stopped);
        });
        return (invoker, seen);
    }

    [Test]
    public async Task Update_sends_only_the_selected_fields_to_the_sandbox_updates_service()
    {
        var (invoker, seen) = Gateway();
        using var client = ClientTests.NewClient(invoker, "ws1");

        var sandbox = await client.Sandboxes.UpdateAsync(
            "sb",
            7,
            new SandboxUpdate { Image = "registry.example.com/agent:2", Labels = new Dictionary<string, string> { ["team"] = "a" } },
            new UpdateSandboxOptions { RequestId = "8d7f4c1e-0000-4000-8000-000000000001" });

        var request = seen.Single();
        await Assert.That(sandbox.Phase).IsEqualTo(SandboxPhase.Stopped);
        await Assert.That(invoker.UnaryFullNames.Single()).IsEqualTo("/openshell.v1.SandboxUpdates/UpdateSandbox");
        await Assert.That(request.WorkspaceScope.Workspace).IsEqualTo("ws1");
        await Assert.That(request.Name).IsEqualTo("sb");
        await Assert.That(request.ExpectedResourceVersion).IsEqualTo(7ul);
        await Assert.That(request.RequestId).IsEqualTo("8d7f4c1e-0000-4000-8000-000000000001");
        await Assert.That(request.HasImage).IsTrue();
        await Assert.That(request.Image).IsEqualTo("registry.example.com/agent:2");
        await Assert.That(request.Labels.Values["team"]).IsEqualTo("a");
        await Assert.That(request.Command).IsNull();
        await Assert.That(request.Environment).IsNull();
        await Assert.That(request.Annotations).IsNull();
    }

    [Test]
    public async Task Empty_collections_are_sent_as_present_and_empty()
    {
        var (invoker, seen) = Gateway();
        using var client = ClientTests.NewClient(invoker);

        await client.Sandboxes.UpdateAsync("sb", 3, new SandboxUpdate
        {
            Command = [],
            Environment = new Dictionary<string, string>(),
            Annotations = new Dictionary<string, string>(),
        });

        var request = seen.Single();
        await Assert.That(request.HasImage).IsFalse();
        await Assert.That(request.Command).IsNotNull();
        await Assert.That(request.Command.Argv).IsEmpty();
        await Assert.That(request.Environment).IsNotNull();
        await Assert.That(request.Environment.Values).IsEmpty();
        await Assert.That(request.Annotations).IsNotNull();
        await Assert.That(request.Labels).IsNull();
    }

    [Test]
    public async Task Command_and_environment_are_replaced_as_given()
    {
        var (invoker, seen) = Gateway();
        using var client = ClientTests.NewClient(invoker);

        await client.Sandboxes.UpdateAsync("sb", 3, new SandboxUpdate
        {
            Command = ["python", "-m", "agent"],
            Environment = new Dictionary<string, string> { ["MODE"] = "prod" },
        });

        await Assert.That(string.Join(" ", seen.Single().Command.Argv)).IsEqualTo("python -m agent");
        await Assert.That(seen.Single().Environment.Values["MODE"]).IsEqualTo("prod");
    }

    [Test]
    public async Task Resume_pending_update_sends_no_fields()
    {
        var (invoker, seen) = Gateway();
        using var client = ClientTests.NewClient(invoker);

        await client.Sandboxes.ResumePendingUpdateAsync("sb", 9);

        var request = seen.Single();
        await Assert.That(request.ExpectedResourceVersion).IsEqualTo(9ul);
        await Assert.That(request.HasImage).IsFalse();
        await Assert.That(request.Command).IsNull();
        await Assert.That(request.Environment).IsNull();
        await Assert.That(request.Labels).IsNull();
        await Assert.That(request.Annotations).IsNull();
        await Assert.That(request.RequestId).IsEqualTo("");
    }

    [Test]
    public async Task Arguments_are_validated_before_calling_the_gateway()
    {
        var (invoker, seen) = Gateway();
        using var client = ClientTests.NewClient(invoker);
        var change = new SandboxUpdate { Image = "img:2" };

        await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", 1, new SandboxUpdate())).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", 1, new SandboxUpdate { Image = " " })).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", 0, change)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Sandboxes.UpdateAsync("", 1, change)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", 1, null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Sandboxes.ResumePendingUpdateAsync("sb", 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(seen).IsEmpty();
    }

    [Test]
    [Arguments(StatusCode.Aborted, OpenShellErrorKind.Conflict)]
    [Arguments(StatusCode.FailedPrecondition, OpenShellErrorKind.Conflict)]
    [Arguments(StatusCode.Unimplemented, OpenShellErrorKind.Unimplemented)]
    public async Task Gateway_failures_are_mapped(StatusCode code, OpenShellErrorKind kind)
    {
        var invoker = new FakeCallInvoker().Throws<Wire.UpdateSandboxRequest, Wire.SandboxResponse>("UpdateSandbox", code, "nope");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", 1, new SandboxUpdate { Image = "img:2" })).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(kind);
    }

    [Test]
    public async Task The_fake_updates_a_stopped_sandbox_and_bumps_its_version()
    {
        using var client = new FakeOpenShellClient();
        await client.Sandboxes.CreateAsync("sb", new SandboxSpec { Image = "img:1" }, new CreateSandboxOptions { Labels = new Dictionary<string, string> { ["old"] = "1" } });
        await client.Sandboxes.StopAsync("sb");
        var stopped = await client.Sandboxes.GetAsync("sb");

        var updated = await client.Sandboxes.UpdateAsync("sb", stopped.ResourceVersion, new SandboxUpdate
        {
            Image = "img:2",
            Command = ["run"],
            Environment = new Dictionary<string, string> { ["A"] = "1" },
            Labels = new Dictionary<string, string> { ["new"] = "2" },
            Annotations = new Dictionary<string, string>(),
        });

        await Assert.That(updated.Phase).IsEqualTo(SandboxPhase.Stopped);
        await Assert.That(updated.Image).IsEqualTo("img:2");
        await Assert.That(updated.Labels).IsEquivalentTo(new Dictionary<string, string> { ["new"] = "2" });
        await Assert.That(updated.ResourceVersion).IsGreaterThan(stopped.ResourceVersion);

        var stale = await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", stopped.ResourceVersion, new SandboxUpdate { Image = "img:3" })).Throws<OpenShellException>();
        await Assert.That(stale!.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(stale.Message).Contains("does not match expected");
    }

    [Test]
    public async Task The_fake_updates_the_template_image_when_the_sandbox_has_a_template()
    {
        using var client = new FakeOpenShellClient();
        await client.Sandboxes.CreateAsync("sb", new SandboxSpec { Template = new SandboxTemplateSpec { Image = "img:1" } });
        await client.Sandboxes.StopAsync("sb");

        var updated = await client.Sandboxes.UpdateAsync("sb", (await client.Sandboxes.GetAsync("sb")).ResourceVersion, new SandboxUpdate { Image = "img:2" });

        await Assert.That(updated.Image).IsEqualTo("img:2");
    }

    [Test]
    public async Task The_fake_requires_a_stopped_sandbox_and_the_feature()
    {
        using var client = new FakeOpenShellClient();
        var ready = await client.Sandboxes.CreateAsync("sb", new SandboxSpec { Image = "img:1" });
        using var disabled = new FakeOpenShellClient(new FakeGatewayOptions { SandboxUpdatesEnabled = false });
        var other = await disabled.Sandboxes.CreateAsync("sb", new SandboxSpec { Image = "img:1" });

        var running = await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", (await client.Sandboxes.GetAsync("sb")).ResourceVersion, new SandboxUpdate { Image = "img:2" })).Throws<OpenShellException>();
        var off = await Assert.That(async () => await disabled.Sandboxes.UpdateAsync("sb", other.ResourceVersion, new SandboxUpdate { Image = "img:2" })).Throws<OpenShellException>();

        await Assert.That(running!.Message).IsEqualTo("sandbox must be Stopped before updating its configuration");
        await Assert.That(off!.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(off.Message).Contains("disabled");
        await Assert.That(ready.Phase).IsNotEqualTo(SandboxPhase.Stopped);
    }

    [Test]
    [Arguments("internal.openshell.ai/epoch")]
    [Arguments("openshell.ai/managed-by")]
    [Arguments("openshell.nvidia.com/retention")]
    public async Task The_fake_rejects_system_metadata(string key)
    {
        using var client = new FakeOpenShellClient();
        await client.Sandboxes.CreateAsync("sb", new SandboxSpec { Image = "img:1" });
        await client.Sandboxes.StopAsync("sb");
        var version = (await client.Sandboxes.GetAsync("sb")).ResourceVersion;
        var values = new Dictionary<string, string> { [key] = "x" };

        var labels = await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", version, new SandboxUpdate { Labels = values })).Throws<OpenShellException>();
        var annotations = await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", version, new SandboxUpdate { Annotations = values })).Throws<OpenShellException>();
        var image = await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", version, new SandboxUpdate { Image = "img 2" })).Throws<OpenShellException>();

        await Assert.That(labels!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(labels.Message).IsEqualTo("system metadata cannot be updated");
        await Assert.That(annotations!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
        await Assert.That(image!.Kind).IsEqualTo(OpenShellErrorKind.InvalidArgument);
    }

    [Test]
    public async Task The_fake_blocks_start_and_stop_until_a_pending_update_is_resumed()
    {
        using var client = new FakeOpenShellClient();
        await client.Sandboxes.CreateAsync("sb", new SandboxSpec { Image = "img:1" });
        await client.Sandboxes.StopAsync("sb");
        var version = (await client.Sandboxes.GetAsync("sb")).ResourceVersion;

        var nothingPending = await Assert.That(async () => await client.Sandboxes.ResumePendingUpdateAsync("sb", version)).Throws<OpenShellException>();
        client.Gateway.SetPendingUpdate("sb", new SandboxUpdate { Image = "img:2" });
        var pendingVersion = (await client.Sandboxes.GetAsync("sb")).ResourceVersion;
        var start = await Assert.That(async () => await client.Sandboxes.StartAsync("sb")).Throws<OpenShellException>();
        var stop = await Assert.That(async () => await client.Sandboxes.StopAsync("sb")).Throws<OpenShellException>();
        var another = await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", pendingVersion, new SandboxUpdate { Image = "img:3" })).Throws<OpenShellException>();

        var resumed = await client.Sandboxes.ResumePendingUpdateAsync("sb", pendingVersion);
        await client.Sandboxes.StartAsync("sb");

        await Assert.That(nothingPending!.Message).IsEqualTo("sandbox has no pending update to resume");
        await Assert.That(start!.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(start.Message).Contains("pending configuration update");
        await Assert.That(stop!.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(another!.Message).Contains("pending update");
        await Assert.That(resumed.Image).IsEqualTo("img:2");
        await Assert.That((await client.Sandboxes.WaitReadyAsync("sb")).Phase).IsEqualTo(SandboxPhase.Ready);
    }

    [Test]
    public async Task The_fake_validates_arguments_like_the_client()
    {
        using var client = new FakeOpenShellClient();

        await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", 1, new SandboxUpdate())).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", 0, new SandboxUpdate { Image = "i" })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Sandboxes.UpdateAsync("sb", 1, null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await client.Sandboxes.ResumePendingUpdateAsync(" ", 1)).Throws<ArgumentException>();
    }
}
