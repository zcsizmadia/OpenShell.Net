// SPDX-FileCopyrightText: Copyright (c) 2026 Zoltan Csizmadia
// SPDX-License-Identifier: Apache-2.0

using Openshell.Sandbox.V1;

namespace OpenShell.IntegrationTests;

/// <summary>
/// Draft policy review against a real gateway. Denied connections make the supervisor's mechanistic mapper propose one
/// L4 rule per (host, port, binary), with no LLM involved; the tests then edit, approve, undo, reject, bulk-approve, and
/// clear those proposals.
/// </summary>
public class PolicyDraftTests
{
    private const string Host = "pypi.org";

    [Test]
    [Timeout(600_000)]
    public async Task Denied_connections_become_draft_chunks_that_can_be_reviewed(CancellationToken cancellationToken)
    {
        using var client = GatewayFixture.Connect();
        await using var sandbox = await TestSandbox.CreateAsync(client, "dn-draft", new SandboxSpec { Image = GatewayFixture.Image, Policy = NoNetworkPolicy() }, cancellationToken);
        var policy = client.Policy;

        // Three denied endpoints, so three proposals: one is reviewed one by one, one is bulk-approved, one is cleared.
        var probe = await client.Exec.RunAsync(
            sandbox.Name,
            ["bash", "-c", $"for port in 80 8080 8081; do if (exec 3<>/dev/tcp/{Host}/$port) 2>/dev/null; then echo \"ALLOWED $port\"; else echo \"DENIED $port\"; fi; done"],
            cancellationToken: cancellationToken);
        await Assert.That(probe.StdoutText.Split('\n', StringSplitOptions.RemoveEmptyEntries)).IsEquivalentTo(new[] { "DENIED 80", "DENIED 8080", "DENIED 8081" });

        var pending = await Poll.UntilAsync(
            async () =>
            {
                var draft = await policy.GetDraftAsync(sandbox.Name, "pending", cancellationToken);
                var chunks = new[] { 80u, 8080u, 8081u }.Select(port => draft.Chunks.FirstOrDefault(c => Targets(c, port))).ToArray();
                return (chunks.All(c => c is not null) ? chunks! : null, Describe(draft.Chunks));
            },
            TimeSpan.FromMinutes(3),
            $"pending proposals for {Host} ports 80, 8080 and 8081",
            cancellationToken);
        var (single, bulk, cleared) = (pending[0]!, pending[1]!, pending[2]!);
        await Assert.That(single.Status).IsEqualTo("pending");
        await Assert.That(single.ReviewToken).IsNotEmpty();
        await Assert.That(single.Rationale).Contains(Host);

        // Edit the pending chunk (to the same rule); the gateway re-evaluates it and issues a fresh review token.
        await policy.EditChunkAsync(sandbox.Name, single.Id, single.ProposedRule!, cancellationToken: cancellationToken);
        single = await GetChunkAsync(policy, sandbox.Name, single.Id, cancellationToken);
        await Assert.That(single.Status).IsEqualTo("pending");

        // Approve: the rule merges into a new policy revision, which the sandbox loads.
        var before = await policy.GetStatusAsync(sandbox.Name, cancellationToken: cancellationToken);
        var approved = await policy.ApproveChunkAsync(sandbox.Name, single.Id, single.ReviewToken, cancellationToken: cancellationToken);
        await Assert.That(approved.PolicyVersion).IsGreaterThan(before.ActiveVersion);
        await Assert.That(approved.PolicyHash).IsNotEmpty();
        await WaitLoadedAsync(policy, sandbox.Name, approved.PolicyVersion, cancellationToken);
        await Assert.That((await GetChunkAsync(policy, sandbox.Name, single.Id, cancellationToken)).Status).IsEqualTo("approved");
        var revisions = await policy.ListRevisions(sandbox.Name).ToListAsync(cancellationToken);
        await Assert.That(revisions.Select(r => r.Version)).Contains(approved.PolicyVersion);

        // Undo removes the rule again and returns the chunk to pending.
        var undone = await policy.UndoChunkAsync(sandbox.Name, single.Id, cancellationToken: cancellationToken);
        await Assert.That(undone.PolicyVersion).IsGreaterThan(approved.PolicyVersion);
        await Assert.That((await GetChunkAsync(policy, sandbox.Name, single.Id, cancellationToken)).Status).IsEqualTo("pending");

        // Reject records the reason.
        await policy.RejectChunkAsync(sandbox.Name, single.Id, "rejected by the dotnet integration test", cancellationToken: cancellationToken);
        var rejected = await policy.GetDraftAsync(sandbox.Name, "rejected", cancellationToken);
        var rejectedChunk = rejected.Chunks.Single(c => c.Id == single.Id);
        await Assert.That(rejectedChunk.RejectionReason).IsEqualTo("rejected by the dotnet integration test");

        // Bulk approval with an explicit selection approves only the selected chunk.
        bulk = await GetChunkAsync(policy, sandbox.Name, bulk.Id, cancellationToken);
        var all = await policy.ApproveAllAsync(
            sandbox.Name,
            new ApproveAllOptions { IncludeSecurityFlagged = true, Approvals = [new ChunkApproval(bulk.Id, bulk.ReviewToken)] },
            cancellationToken);
        await Assert.That(all.ChunksApproved).IsEqualTo(1u);
        await Assert.That(all.PolicyVersion).IsGreaterThan(undone.PolicyVersion);
        await Assert.That((await GetChunkAsync(policy, sandbox.Name, bulk.Id, cancellationToken)).Status).IsEqualTo("approved");
        await Assert.That((await GetChunkAsync(policy, sandbox.Name, cleared.Id, cancellationToken)).Status).IsEqualTo("pending");

        // Clear deletes the remaining pending chunk and leaves decided ones.
        var clearedCount = await policy.ClearChunksAsync(sandbox.Name, cancellationToken: cancellationToken);
        await Assert.That(clearedCount).IsEqualTo(1u);
        var remaining = await policy.GetDraftAsync(sandbox.Name, cancellationToken: cancellationToken);
        await Assert.That(remaining.Chunks.Select(c => c.Id).ToArray()).IsEquivalentTo(new[] { single.Id, bulk.Id });

        var history = await policy.GetDraftHistoryAsync(sandbox.Name, cancellationToken);
        await Assert.That(history.Where(e => e.ChunkId == single.Id).Select(e => e.EventType).ToArray()).IsEquivalentTo(new[] { "proposed", "rejected" });
        await Assert.That(history.Where(e => e.ChunkId == bulk.Id).Select(e => e.EventType).ToArray()).IsEquivalentTo(new[] { "proposed", "approved" });
    }

    /// <summary>The conformance suite's policy with no network rules, so every outbound connection is denied.</summary>
    private static SandboxPolicy NoNetworkPolicy() => new()
    {
        Version = 1,
        Filesystem = new FilesystemPolicy
        {
            IncludeWorkdir = true,
            ReadOnly = { "/usr", "/bin", "/lib", "/lib64", "/proc", "/dev/urandom", "/app", "/etc", "/var/log" },
            ReadWrite = { "/sandbox", "/tmp", "/dev/null" },
        },
        Landlock = new LandlockPolicy { Compatibility = "best_effort" },
    };

    private static bool Targets(DraftChunk chunk, uint port) =>
        chunk.ProposedRule?.Endpoints.Any(e => string.Equals(e.Host, Host, StringComparison.OrdinalIgnoreCase) && (e.Port == port || e.Ports.Contains(port))) == true;

    private static string Describe(IReadOnlyList<DraftChunk> chunks) =>
        chunks.Count == 0 ? "no chunks" : string.Join("; ", chunks.Select(c => $"{c.RuleName} [{c.Status}]"));

    private static async Task<DraftChunk> GetChunkAsync(IPolicyClient policy, string sandbox, string id, CancellationToken cancellationToken) =>
        (await policy.GetDraftAsync(sandbox, cancellationToken: cancellationToken)).Chunks.Single(c => c.Id == id);

    private static Task<PolicyStatusResult> WaitLoadedAsync(IPolicyClient policy, string sandbox, uint version, CancellationToken cancellationToken) =>
        Poll.UntilAsync(
            async () =>
            {
                var status = await policy.GetStatusAsync(sandbox, new PolicyStatusOptions { Version = version }, cancellationToken);
                if (status.Revision?.Status == PolicyLoadStatus.Failed)
                {
                    throw new InvalidOperationException($"policy version {version} failed to load: {status.Revision.LoadError}");
                }

                return (status.Revision?.Status == PolicyLoadStatus.Loaded ? status : null, $"{status.Revision?.Status} (active {status.ActiveVersion})");
            },
            TimeSpan.FromMinutes(2),
            $"policy version {version} to load",
            cancellationToken);
}
