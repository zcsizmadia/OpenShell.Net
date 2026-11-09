// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using Wire = Openshell.V1;

namespace OpenShell;

/// <summary>Sandbox lifecycle operations, scoped to one workspace.</summary>
public interface ISandboxClient
{
    /// <summary>Creates a sandbox. The call returns before the sandbox is ready; see <see cref="WaitReadyAsync"/>.</summary>
    ValueTask<Sandbox> CreateAsync(string? name, SandboxSpec spec, CreateSandboxOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Creates a sandbox from a reusable workload template; <paramref name="spec"/> may override fields.</summary>
    ValueTask<Sandbox> CreateFromTemplateAsync(string? name, string templateName, SandboxSpec? spec = null, CreateSandboxOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Gets a sandbox by name.</summary>
    ValueTask<Sandbox> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Lists sandboxes lazily, page by page.</summary>
    Pager<Sandbox> List(ListOptions? options = null);

    /// <summary>Lists the ids of all matching sandboxes, across every page.</summary>
    ValueTask<IReadOnlyList<string>> ListIdsAsync(ListOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Stops a running sandbox.</summary>
    ValueTask<Sandbox> StopAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Starts a stopped sandbox. Starting a suspended sandbox discards its retained memory state; see <see cref="ResumeAsync"/>.</summary>
    ValueTask<Sandbox> StartAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Suspends a ready sandbox: it is frozen with its memory retained, moving through
    /// <see cref="SandboxPhase.Suspending"/> to <see cref="SandboxPhase.Suspended"/> (see <see cref="WaitSuspendedAsync"/>).
    /// Repeating the call while suspending or suspended is safe. Fails with <see cref="OpenShellErrorKind.Conflict"/>
    /// (<c>FAILED_PRECONDITION</c>) in any other phase, or when the compute driver does not support suspension.
    /// </summary>
    ValueTask<Sandbox> SuspendAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a suspended sandbox from its retained memory state. The sandbox returns as
    /// <see cref="SandboxPhase.Starting"/> and becomes ready with a new runtime generation (see <see cref="WaitReadyAsync"/>).
    /// Fails with <see cref="OpenShellErrorKind.Conflict"/> (<c>FAILED_PRECONDITION</c>) unless the sandbox is suspended.
    /// </summary>
    ValueTask<Sandbox> ResumeAsync(string name, ResumeSandboxOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the configuration of a stopped sandbox through the <c>SandboxUpdates</c> service, which the gateway serves
    /// only when <c>[openshell.gateway] enable_sandbox_updates = true</c>. The update does not start the sandbox.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="expectedResourceVersion"/> is required (compare-and-swap): a stale version fails with
    /// <see cref="OpenShellErrorKind.Conflict"/> (<c>ABORTED</c>). The sandbox must be stopped, otherwise the call fails with
    /// <see cref="OpenShellErrorKind.Conflict"/> (<c>FAILED_PRECONDITION</c>), as it does while the feature is disabled.
    /// Workload changes (image, command, environment) a compute driver cannot apply fail with
    /// <see cref="OpenShellErrorKind.Unimplemented"/>; label and annotation changes work on every driver.
    /// </para>
    /// <para>
    /// A failed or interrupted update leaves a pending intent that blocks start and stop until
    /// <see cref="ResumePendingUpdateAsync"/> completes it.
    /// </para>
    /// </remarks>
    ValueTask<Sandbox> UpdateAsync(string name, ulong expectedResourceVersion, SandboxUpdate update, UpdateSandboxOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a sandbox update that failed or was interrupted, by sending an update with the current resource version
    /// and no fields. Fails with <see cref="OpenShellErrorKind.Conflict"/> when no update is pending.
    /// </summary>
    ValueTask<Sandbox> ResumePendingUpdateAsync(string name, ulong expectedResourceVersion, UpdateSandboxOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a sandbox. Inspect <see cref="DeletionResult.Outcome"/>; <c>Accepted</c> means cleanup is pending.</summary>
    ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Polls until the sandbox is ready (or completed). Fails fast when the sandbox is suspending or suspended.</summary>
    ValueTask<Sandbox> WaitReadyAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Polls until the sandbox is stopped. Fails fast when the sandbox is suspending or suspended.</summary>
    ValueTask<Sandbox> WaitStoppedAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls until the sandbox is suspended. Fails when it reaches a phase other than
    /// <see cref="SandboxPhase.Suspending"/> or <see cref="SandboxPhase.Suspended"/>.
    /// </summary>
    ValueTask<Sandbox> WaitSuspendedAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Polls until the sandbox name is gone, or resolves to a different id when <see cref="WaitOptions.ExpectedSandboxId"/> is set.</summary>
    ValueTask WaitDeletedAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class SandboxClient(ClientCore core, string workspace) : ISandboxClient
{
    public ValueTask<Sandbox> CreateAsync(string? name, SandboxSpec spec, CreateSandboxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return CreateCoreAsync(name, null, spec, options, cancellationToken);
    }

    public ValueTask<Sandbox> CreateFromTemplateAsync(string? name, string templateName, SandboxSpec? spec = null, CreateSandboxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        return CreateCoreAsync(name, templateName, spec, options, cancellationToken);
    }

    public async ValueTask<Sandbox> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.GetSandboxRequest { WorkspaceScope = ClientCore.Scope(workspace), Name = name };
        var response = await core.UnaryAsync(o => core.Grpc.GetSandboxAsync(request, o), cancellationToken).ConfigureAwait(false);
        return SandboxMapping.ToDomain(response);
    }

    public Pager<Sandbox> List(ListOptions? options = null)
    {
        options ??= new ListOptions();
        return new Pager<Sandbox>(
            async (token, ct) =>
            {
                var request = new Wire.ListSandboxesRequest
                {
                    WorkspaceScope = ClientCore.ListScope(workspace, options.AllWorkspaces),
                    PageSize = options.PageSize,
                    PageToken = token ?? "",
                    LabelSelector = options.LabelSelector ?? "",
                };
                var response = await core.UnaryAsync(o => core.Grpc.ListSandboxesAsync(request, o), ct).ConfigureAwait(false);
                return new Page<Sandbox>(
                    response.Sandboxes.Select(s => SandboxMapping.ToDomain(s)).ToArray(),
                    response.NextPageToken is { Length: > 0 } next ? next : null);
            },
            options.PageToken);
    }

    public async ValueTask<IReadOnlyList<string>> ListIdsAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        await foreach (var sandbox in List(options).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(sandbox.Id);
        }

        return ids;
    }

    public async ValueTask<Sandbox> StopAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.StopSandboxRequest { WorkspaceScope = ClientCore.Scope(workspace), Name = name };
        return SandboxMapping.ToDomain(await core.UnaryAsync(o => core.Grpc.StopSandboxAsync(request, o), cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<Sandbox> StartAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.StartSandboxRequest { WorkspaceScope = ClientCore.Scope(workspace), Name = name };
        return SandboxMapping.ToDomain(await core.UnaryAsync(o => core.Grpc.StartSandboxAsync(request, o), cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<DeletionResult> DeleteAsync(string name, DeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.DeleteSandboxRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Name = name,
            AllowMissing = options?.AllowMissing ?? false,
            RequestId = options?.RequestId ?? "",
        };
        return SandboxMapping.ToDomain(await core.UnaryAsync(o => core.Grpc.DeleteSandboxAsync(request, o), cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<Sandbox> SuspendAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.SuspendSandboxRequest { WorkspaceScope = ClientCore.Scope(workspace), Name = name };
        return SandboxMapping.ToDomain(await core.UnaryAsync(o => core.Grpc.SuspendSandboxAsync(request, o), cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<Sandbox> ResumeAsync(string name, ResumeSandboxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var request = new Wire.ResumeSandboxRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Name = name,
            RestartIfRestoreFails = options?.RestartIfRestoreFails ?? false,
        };
        return SandboxMapping.ToDomain(await core.UnaryAsync(o => core.Grpc.ResumeSandboxAsync(request, o), cancellationToken).ConfigureAwait(false));
    }

    public ValueTask<Sandbox> UpdateAsync(string name, ulong expectedResourceVersion, SandboxUpdate update, UpdateSandboxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.IsEmpty)
        {
            throw new ArgumentException("select at least one field to update; use ResumePendingUpdateAsync to complete a pending update", nameof(update));
        }

        if (update.Image is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(update.Image, nameof(update));
        }

        return SendUpdateAsync(name, expectedResourceVersion, update, options, cancellationToken);
    }

    public ValueTask<Sandbox> ResumePendingUpdateAsync(string name, ulong expectedResourceVersion, UpdateSandboxOptions? options = null, CancellationToken cancellationToken = default) =>
        SendUpdateAsync(name, expectedResourceVersion, new SandboxUpdate(), options, cancellationToken);

    private async ValueTask<Sandbox> SendUpdateAsync(string name, ulong expectedResourceVersion, SandboxUpdate update, UpdateSandboxOptions? options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfZero(expectedResourceVersion);
        var request = new Wire.UpdateSandboxRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Name = name,
            ExpectedResourceVersion = expectedResourceVersion,
            RequestId = options?.RequestId ?? "",
        };
        if (update.Image is { } image)
        {
            request.Image = image;
        }

        if (update.Command is { } command)
        {
            request.Command = new Wire.SandboxCommandUpdate();
            request.Command.Argv.AddRange(command);
        }

        request.Environment = MapUpdate(update.Environment);
        request.Labels = MapUpdate(update.Labels);
        request.Annotations = MapUpdate(update.Annotations);
        return SandboxMapping.ToDomain(await core.UnaryAsync(o => core.SandboxUpdates.UpdateSandboxAsync(request, o), cancellationToken).ConfigureAwait(false));
    }

    // Presence is the change marker: null leaves the field unset (unchanged), an empty map is sent (replace with none).
    private static Wire.SandboxMapUpdate? MapUpdate(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var update = new Wire.SandboxMapUpdate();
        SandboxMapping.AddAll(update.Values, values);
        return update;
    }

    public ValueTask<Sandbox> WaitReadyAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default) =>
        PollAsync(name, options, SandboxWaits.Ready, cancellationToken);

    public ValueTask<Sandbox> WaitStoppedAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default) =>
        PollAsync(name, options, SandboxWaits.Stopped, cancellationToken);

    public ValueTask<Sandbox> WaitSuspendedAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default) =>
        PollAsync(name, options, SandboxWaits.SuspendedStep, cancellationToken);

    public async ValueTask WaitDeletedAsync(string name, WaitOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new WaitOptions();
        using var deadline = new Deadline(core.Time, options.Timeout, cancellationToken);
        var delay = options.PollInterval;
        var timeoutMessage = $"timed out waiting for sandbox '{name}' to delete";
        while (await FindBeforeDeadlineAsync(name, deadline, timeoutMessage).ConfigureAwait(false) is { } sandbox
            && (options.ExpectedSandboxId is not { } expected || sandbox.Id == expected))
        {
            delay = await deadline.SleepAsync(delay, options.MaxPollInterval, timeoutMessage).ConfigureAwait(false);
        }
    }

    /// <summary>Gets the sandbox, or null when the name is gone; a get cut by the deadline fails with <paramref name="timeoutMessage"/>.</summary>
    private async ValueTask<Sandbox?> FindBeforeDeadlineAsync(string name, Deadline deadline, string timeoutMessage)
    {
        try
        {
            return await GetBeforeDeadlineAsync(name, deadline, timeoutMessage).ConfigureAwait(false);
        }
        catch (OpenShellException e) when (e.IsNotFound)
        {
            return null;
        }
    }

    /// <summary>Gets the sandbox; a get cut by the deadline fails with <paramref name="timeoutMessage"/>.</summary>
    private async ValueTask<Sandbox> GetBeforeDeadlineAsync(string name, Deadline deadline, string timeoutMessage)
    {
        try
        {
            return await GetAsync(name, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.TimedOut)
        {
            throw Deadline.Expired(timeoutMessage);
        }
    }

    private async ValueTask<Sandbox> CreateCoreAsync(string? name, string? templateName, SandboxSpec? spec, CreateSandboxOptions? options, CancellationToken cancellationToken)
    {
        var request = new Wire.CreateSandboxRequest
        {
            WorkspaceScope = ClientCore.Scope(workspace),
            Name = name ?? "",
            WorkloadTemplate = templateName ?? "",
            AwaitMainProcessAttachment = options?.AwaitMainProcessAttachment ?? false,
            RequestId = options?.RequestId ?? "",
        };
        if (spec is not null)
        {
            request.Spec = SandboxMapping.ToWire(spec);
            if (spec.ServiceExposures is { } exposures)
            {
                request.ServiceExposures.AddRange(exposures.Select(SandboxMapping.ToWire));
            }
        }

        SandboxMapping.AddAll(request.Labels, options?.Labels);
        SandboxMapping.AddAll(request.Annotations, options?.Annotations);
        return SandboxMapping.ToDomain(await core.UnaryAsync(o => core.Grpc.CreateSandboxAsync(request, o), cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<Sandbox> PollAsync(string name, WaitOptions? options, Func<Sandbox, string, string?> step, CancellationToken cancellationToken)
    {
        options ??= new WaitOptions();
        using var deadline = new Deadline(core.Time, options.Timeout, cancellationToken);
        var delay = options.PollInterval;
        var timeoutMessage = $"timed out waiting for sandbox '{name}'";
        while (true)
        {
            var sandbox = await GetBeforeDeadlineAsync(name, deadline, timeoutMessage).ConfigureAwait(false);

            // See SandboxWaits: null is done, empty keeps polling, anything else is a terminal failure.
            var outcome = step(sandbox, name);
            if (outcome is null)
            {
                return sandbox;
            }

            if (outcome.Length > 0)
            {
                throw new OpenShellException(OpenShellErrorKind.Conflict, outcome);
            }

            delay = await deadline.SleepAsync(delay, options.MaxPollInterval, timeoutMessage).ConfigureAwait(false);
        }
    }
}
