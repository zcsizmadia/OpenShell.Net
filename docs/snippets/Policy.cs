// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using OpenShell;

namespace Snippets;

/// <summary>Snippets for docs/guides/policy.md.</summary>
internal static class Policy
{
    internal static async Task ReviewDraftAsync(IOpenShellClient client)
    {
        #region ReviewDraft
        var draft = await client.Policy.GetDraftAsync("agent-1", statusFilter: "pending");
        Console.WriteLine(draft.RollingSummary);

        foreach (var chunk in draft.Chunks)
        {
            Console.WriteLine($"{chunk.RuleName} for {chunk.Binary}: {chunk.Rationale} (confidence {chunk.Confidence:P0})");
            if (!string.IsNullOrEmpty(chunk.SecurityNotes))
            {
                // Rejection reasons are surfaced back to the agent in the sandbox.
                await client.Policy.RejectChunkAsync("agent-1", chunk.Id, reason: chunk.SecurityNotes);
                continue;
            }

            // The review token pins what you reviewed: approval fails with Conflict when the inputs changed since.
            var change = await client.Policy.ApproveChunkAsync("agent-1", chunk.Id, chunk.ReviewToken);
            Console.WriteLine($"policy version {change.PolicyVersion}");
        }
        #endregion
    }

    internal static async Task ApproveAllAsync(IOpenShellClient client)
    {
        #region ApproveAll
        // Approves the pending chunks in one write. Chunks with security notes are skipped unless included;
        // set Approvals to approve exactly the chunks (and review tokens) you reviewed.
        var result = await client.Policy.ApproveAllAsync("agent-1", new ApproveAllOptions
        {
            IncludeSecurityFlagged = false,
            RequestId = "approve-agent-1-draft-7",
        });
        Console.WriteLine($"approved {result.ChunksApproved}, skipped {result.ChunksSkipped}");

        // Every approval, rejection and undo is recorded.
        var history = await client.Policy.GetDraftHistoryAsync("agent-1");
        foreach (var entry in history)
        {
            Console.WriteLine($"{entry.EventTime} {entry.EventType} {entry.ChunkId}: {entry.Description}");
        }
        #endregion
    }

    internal static async Task RevisionsAsync(IOpenShellClient client)
    {
        #region Revisions
        var status = await client.Policy.GetStatusAsync("agent-1");
        Console.WriteLine($"active version {status.ActiveVersion}: {status.Revision?.Status}");

        await foreach (var revision in client.Policy.ListRevisions("agent-1", new PolicyListOptions { PageSize = 20 }))
        {
            Console.WriteLine($"v{revision.Version} {revision.Status} {revision.PolicyHash} {revision.LoadError}");
        }
        #endregion
    }

    internal static async Task SetPolicyAsync(IOpenShellClient client)
    {
        #region SetPolicy
        // Read the effective policy, change its network rules, and write it back. At sandbox scope only the
        // network policies may change; the static parts must match the create-time policy.
        var config = await client.Config.GetSandboxAsync("agent-1");
        var policy = config.Policy!.Clone();
        policy.NetworkPolicies.Remove("legacy-api");

        var sandbox = await client.Sandboxes.GetAsync("agent-1");
        var result = await client.Config.SetPolicyAsync("agent-1", policy, new SetPolicyOptions
        {
            ExpectedResourceVersion = sandbox.ResourceVersion,   // optimistic concurrency
            Wait = true,                                         // until the sandbox reports the new hash
            WaitTimeout = TimeSpan.FromSeconds(30),
        });
        Console.WriteLine($"policy v{result.Version} {result.PolicyHash}");
        #endregion
    }

    internal static async Task SettingsAsync(IOpenShellClient client)
    {
        #region Settings
        // Strings, booleans and integers convert to SettingValue implicitly.
        await client.Config.SetSettingAsync("agent-1", "log_level", "debug");
        await client.Config.SetGlobalSettingAsync("max_sandboxes", 50L);

        var config = await client.Config.GetSandboxAsync("agent-1");
        foreach (var (key, setting) in config.Settings)
        {
            // Scope says whether the value comes from the sandbox or from the gateway-global settings.
            Console.WriteLine($"{key} = {setting.Value} ({setting.Scope})");
        }

        await client.Config.DeleteGlobalSettingAsync("max_sandboxes");
        #endregion
    }
}
