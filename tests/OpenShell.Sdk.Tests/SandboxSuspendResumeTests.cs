// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using OpenShell.Testing;
using OpenShell.Tests.Support;
using Wire = Openshell.V1;

namespace OpenShell.Tests;

public class SandboxSuspendResumeTests
{
    private static Wire.SandboxResponse Reply(string name, Wire.SandboxPhase phase) => ClientTests.SandboxReply(name, phase);

    private static SandboxSpec Spec() => new() { Image = "img:1" };

    private static Task AdvanceUntilDone(FakeTimeProvider time, Task task) => time.AdvanceUntilDoneAsync(task, TimeSpan.FromSeconds(1));

    [Test]
    public async Task Suspend_sends_the_workspace_and_name()
    {
        Wire.SuspendSandboxRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.SuspendSandboxRequest, Wire.SandboxResponse>("SuspendSandbox", r =>
        {
            seen = r;
            return Reply("sb", Wire.SandboxPhase.Suspending);
        });
        using var client = ClientTests.NewClient(invoker, "ws1");

        var sandbox = await client.Sandboxes.SuspendAsync("sb");

        await Assert.That(seen!.Name).IsEqualTo("sb");
        await Assert.That(seen.WorkspaceScope.Workspace).IsEqualTo("ws1");
        await Assert.That(sandbox.Phase).IsEqualTo(SandboxPhase.Suspending);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Resume_sends_restart_if_restore_fails(bool restart)
    {
        Wire.ResumeSandboxRequest? seen = null;
        var invoker = new FakeCallInvoker().Unary<Wire.ResumeSandboxRequest, Wire.SandboxResponse>("ResumeSandbox", r =>
        {
            seen = r;
            return Reply("sb", Wire.SandboxPhase.Starting);
        });
        using var client = ClientTests.NewClient(invoker, "ws1");

        var sandbox = await client.Sandboxes.ResumeAsync("sb", restart ? new ResumeSandboxOptions { RestartIfRestoreFails = true } : null);

        await Assert.That(seen!.Name).IsEqualTo("sb");
        await Assert.That(seen.WorkspaceScope.Workspace).IsEqualTo("ws1");
        await Assert.That(seen.RestartIfRestoreFails).IsEqualTo(restart);
        await Assert.That(sandbox.Phase).IsEqualTo(SandboxPhase.Starting);
    }

    [Test]
    public async Task Suspend_and_resume_validate_the_name()
    {
        using var client = ClientTests.NewClient(new FakeCallInvoker());

        await Assert.That(async () => await client.Sandboxes.SuspendAsync(" ")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.ResumeAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sandboxes.WaitSuspendedAsync("")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Gateway_precondition_failures_surface_as_conflict()
    {
        var invoker = new FakeCallInvoker().Throws<Wire.SuspendSandboxRequest, Wire.SandboxResponse>(
            "SuspendSandbox", StatusCode.FailedPrecondition, "compute driver does not support sandbox suspend");
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Sandboxes.SuspendAsync("sb")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(ex.Message).Contains("does not support sandbox suspend");
    }

    [Test]
    public async Task WaitSuspended_polls_through_suspending()
    {
        var time = new FakeTimeProvider();
        var phases = new Queue<Wire.SandboxPhase>([Wire.SandboxPhase.Suspending, Wire.SandboxPhase.Unknown, Wire.SandboxPhase.Suspended]);
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", phases.Dequeue()));
        using var client = ClientTests.NewClient(invoker, time: time);

        var task = client.Sandboxes.WaitSuspendedAsync("a", new WaitOptions { PollInterval = TimeSpan.FromSeconds(1) }).AsTask();
        await AdvanceUntilDone(time, task);

        await Assert.That((await task).Phase).IsEqualTo(SandboxPhase.Suspended);
        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(3);
    }

    [Test]
    [Arguments(Wire.SandboxPhase.Ready, "is Ready, not Suspended")]
    [Arguments(Wire.SandboxPhase.Stopped, "is Stopped, not Suspended")]
    [Arguments(Wire.SandboxPhase.Error, "error phase")]
    public async Task WaitSuspended_fails_when_the_suspension_is_not_happening(Wire.SandboxPhase phase, string expected)
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", phase));
        using var client = ClientTests.NewClient(invoker);

        var ex = await Assert.That(async () => await client.Sandboxes.WaitSuspendedAsync("a")).Throws<OpenShellException>();

        await Assert.That(ex!.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(ex.Message).Contains(expected);
    }

    [Test]
    [Arguments(Wire.SandboxPhase.Suspending)]
    [Arguments(Wire.SandboxPhase.Suspended)]
    public async Task WaitReady_and_WaitStopped_fail_fast_on_a_suspended_sandbox(Wire.SandboxPhase phase)
    {
        var invoker = new FakeCallInvoker().Unary<Wire.GetSandboxRequest, Wire.SandboxResponse>("GetSandbox", _ => Reply("a", phase));
        using var client = ClientTests.NewClient(invoker);

        var ready = await Assert.That(async () => await client.Sandboxes.WaitReadyAsync("a")).Throws<OpenShellException>();
        var stopped = await Assert.That(async () => await client.Sandboxes.WaitStoppedAsync("a")).Throws<OpenShellException>();

        await Assert.That(ready!.Message).IsEqualTo("sandbox 'a' is suspended; resume it before waiting for Ready");
        await Assert.That(stopped!.Message).IsEqualTo("sandbox 'a' is suspended; resume it before waiting for Stopped");
        await Assert.That(ready.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(invoker.CountOf("GetSandbox")).IsEqualTo(2);
    }

    [Test]
    public async Task Fake_suspends_and_resumes_through_the_phases()
    {
        var time = new FakeTimeProvider();
        using var client = new FakeOpenShellClient(new FakeGatewayOptions { TimeProvider = time, TransitionDelay = TimeSpan.FromSeconds(3) });
        await client.Sandboxes.CreateAsync("a", Spec());

        var suspending = await client.Sandboxes.SuspendAsync("a");
        var again = await client.Sandboxes.SuspendAsync("a");
        var wait = client.Sandboxes.WaitSuspendedAsync("a", new WaitOptions { PollInterval = TimeSpan.FromSeconds(1) }).AsTask();
        await AdvanceUntilDone(time, wait);
        var suspended = await wait;
        var idempotent = await client.Sandboxes.SuspendAsync("a");

        var resuming = await client.Sandboxes.ResumeAsync("a", new ResumeSandboxOptions { RestartIfRestoreFails = true });
        var resumeAgain = await client.Sandboxes.ResumeAsync("a");
        var ready = client.Sandboxes.WaitReadyAsync("a", new WaitOptions { PollInterval = TimeSpan.FromSeconds(1) }).AsTask();
        await AdvanceUntilDone(time, ready);

        await Assert.That(suspending.Phase).IsEqualTo(SandboxPhase.Suspending);
        await Assert.That(again.Phase).IsEqualTo(SandboxPhase.Suspending);
        await Assert.That(again.ResourceVersion).IsEqualTo(suspending.ResourceVersion);
        await Assert.That(suspended.Phase).IsEqualTo(SandboxPhase.Suspended);
        await Assert.That(idempotent.ResourceVersion).IsEqualTo(suspended.ResourceVersion);
        await Assert.That(resuming.Phase).IsEqualTo(SandboxPhase.Starting);
        await Assert.That(resumeAgain.Phase).IsEqualTo(SandboxPhase.Starting);
        await Assert.That((await ready).Phase).IsEqualTo(SandboxPhase.Ready);
        await Assert.That((await ready).RestartCount).IsEqualTo(0u);
        await Assert.That(client.Gateway.Calls.Select(c => c.Operation)).Contains("Sandboxes.Resume");
    }

    [Test]
    public async Task Fake_rejects_suspend_and_resume_in_the_wrong_phase()
    {
        using var client = new FakeOpenShellClient(new FakeGatewayOptions { TransitionDelay = TimeSpan.FromHours(1) });
        await client.Sandboxes.CreateAsync("a", Spec());

        var resume = await Assert.That(async () => await client.Sandboxes.ResumeAsync("a")).Throws<OpenShellException>();
        await client.Sandboxes.StopAsync("a");
        var suspend = await Assert.That(async () => await client.Sandboxes.SuspendAsync("a")).Throws<OpenShellException>();

        // A Starting sandbox that was started, not resumed, is not a resume in flight.
        client.Gateway.SetPhase("a", SandboxPhase.Stopped);
        await client.Sandboxes.StartAsync("a");
        var resumeWhileStarting = await Assert.That(async () => await client.Sandboxes.ResumeAsync("a")).Throws<OpenShellException>();

        await Assert.That(resume!.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(resume.Message).IsEqualTo("sandbox must be Suspended to resume (current phase: Ready)");
        await Assert.That(suspend!.Message).IsEqualTo("sandbox must be Ready to suspend (current phase: Stopping)");
        await Assert.That(resumeWhileStarting!.Message).Contains("current phase: Starting");
    }

    [Test]
    public async Task Fake_stop_and_start_discard_the_suspended_state()
    {
        using var client = new FakeOpenShellClient();
        await client.Sandboxes.CreateAsync("a", Spec());
        await client.Sandboxes.CreateAsync("b", Spec());
        await client.Sandboxes.SuspendAsync("a");
        await client.Sandboxes.SuspendAsync("b");

        await client.Sandboxes.StopAsync("a");
        var stopped = await client.Sandboxes.WaitStoppedAsync("a");
        await client.Sandboxes.StartAsync("b");
        var started = await client.Sandboxes.WaitReadyAsync("b");

        await Assert.That(stopped.Phase).IsEqualTo(SandboxPhase.Stopped);
        await Assert.That(started.Phase).IsEqualTo(SandboxPhase.Ready);
        await Assert.That(started.RestartCount).IsEqualTo(1u);
    }

    [Test]
    public async Task Fake_without_driver_support_fails_with_a_precondition()
    {
        using var client = new FakeOpenShellClient(new FakeGatewayOptions { SupportsSuspendResume = false });
        await client.Sandboxes.CreateAsync("a", Spec());

        var suspend = await Assert.That(async () => await client.Sandboxes.SuspendAsync("a")).Throws<OpenShellException>();
        var resume = await Assert.That(async () => await client.Sandboxes.ResumeAsync("a")).Throws<OpenShellException>();

        await Assert.That(suspend!.Kind).IsEqualTo(OpenShellErrorKind.Conflict);
        await Assert.That(suspend.Message).IsEqualTo("compute driver does not support sandbox suspend");
        await Assert.That(resume!.Message).IsEqualTo("compute driver does not support sandbox resume");
        await Assert.That((await client.Sandboxes.GetAsync("a")).Phase).IsEqualTo(SandboxPhase.Ready);
    }

    [Test]
    public async Task Fake_waits_fail_fast_on_a_suspended_sandbox()
    {
        using var client = new FakeOpenShellClient();
        await client.Sandboxes.CreateAsync("a", Spec());
        await client.Sandboxes.SuspendAsync("a");

        var ready = await Assert.That(async () => await client.Sandboxes.WaitReadyAsync("a")).Throws<OpenShellException>();

        await Assert.That(ready!.Message).IsEqualTo("sandbox 'a' is suspended; resume it before waiting for Ready");
    }
}
