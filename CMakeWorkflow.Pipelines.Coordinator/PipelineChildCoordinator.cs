using CMakeWorkflow.Pipelines.Persistence;

namespace CMakeWorkflow.Pipelines.Coordinator;

/// <summary>Explicit one-stage child admission, recovery and observation over a durable pipeline ledger.</summary>
/// <remarks>
/// The caller owns a persistent Host, its workspace lease and each ledger instance's serialized access.
/// StartReadyAsync may invoke Host start exactly once, and only after the durable reservation commits.
/// RecoverAsync is read-only toward Host: it never starts, resumes or cancels a child, even if lookup fails.
/// No scheduler, background polling, GitHub adapter or automatic retry is provided in P2b.
/// </remarks>
public sealed class PipelineChildCoordinator(PipelineLedger ledger, IWorkflowChildHost host)
{
    private readonly PipelineLedger ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    private readonly IWorkflowChildHost host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>Preflight, durably reserve, then make one Host start call for a pending, unblocked workflow stage.</summary>
    /// <remarks>
    /// A lost RPC response leaves dispatch_intent. Do not invoke StartReadyAsync again: call RecoverAsync
    /// only after ensuring no other coordinator is actively dispatching this stage.
    /// </remarks>
    public async Task<PipelineRunSnapshot> StartReadyAsync(string pipelineOperationId, string stageId,
        PipelineChildLaunch launch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        RequireWorkspace(launch);
        // This is advisory. Ledger validates the frozen manifest, prerequisites and revision again in its CAS.
        await host.ValidatePreflightAsync(launch, cancellationToken);
        PipelineRunSnapshot before = ledger.Get(pipelineOperationId);
        PipelineRunSnapshot reserved = ledger.ReserveWorkflow(pipelineOperationId, stageId, before.Revision, launch);
        PipelineStageSnapshot intent = Stage(reserved, stageId);
        PipelineChildLaunch pinned = ledger.GetChildLaunch(pipelineOperationId, stageId)
            ?? throw new InvalidDataException("Committed workflow reservation has no pinned Host launch.");
        // No catch/retry: any exception leaves the durable intent for read-only recovery.
        WorkflowChildIdentity child = await host.StartAsync(pinned, intent.ChildRequestId!, cancellationToken);
        RequireChild(child, intent.ChildRequestId!, pinned);
        return ledger.BindChild(pipelineOperationId, stageId, reserved.Revision, child.OperationId);
    }

    /// <summary>Read-only Host recovery/observation; never dispatch an unresolved child.</summary>
    /// <remarks>
    /// A dispatch_intent is probed by exact child request ID. A Host not-found or connection error
    /// propagates and the intent remains unchanged; absence does not establish that no process ran.
    /// A terminal child advances its stage only after a verified Host finalization/report hash.
    /// </remarks>
    public async Task<PipelineRunSnapshot> RecoverAsync(string pipelineOperationId, string stageId,
        CancellationToken cancellationToken = default)
    {
        PipelineRunSnapshot before = ledger.Get(pipelineOperationId);
        PipelineStageSnapshot stage = Stage(before, stageId);
        if (stage.Status is not (PipelineStageStates.DispatchIntent or PipelineStageStates.WaitingExternal))
        {
            return before;
        }

        if (stage.Kind != "workflow")
        {
            // GitHub attach/watch is P3, not this Host child adapter.
            return before;
        }

        PipelineChildLaunch launch = ledger.GetChildLaunch(pipelineOperationId, stageId)
            ?? throw new InvalidOperationException("Legacy unpinned dispatch cannot be automatically reconciled.");
        RequireWorkspace(launch);
        if (string.IsNullOrWhiteSpace(stage.ChildRequestId))
        {
            throw new InvalidDataException("Reserved workflow has no child request identity.");
        }

        if (stage.Status == PipelineStageStates.DispatchIntent)
        {
            WorkflowChildIdentity found = await host.FindAsync(stage.ChildRequestId, cancellationToken);
            RequireChild(found, stage.ChildRequestId, launch);
            return ledger.BindChild(pipelineOperationId, stageId, before.Revision, found.OperationId);
        }

        if (string.IsNullOrWhiteSpace(stage.ChildOperationId))
        {
            throw new InvalidDataException("Bound workflow has no child operation ID.");
        }

        WorkflowChildProgress progress = await host.ObserveAsync(stage.ChildOperationId, cancellationToken);
        RequireChild(progress.Identity, stage.ChildRequestId, launch);
        if (progress.Identity.OperationId != stage.ChildOperationId)
        {
            throw new InvalidDataException("Host observation refers to a different child operation.");
        }

        if (!progress.IsFinalized)
        {
            return before;
        }

        if (progress.State is not ("succeeded" or "failed" or "canceled") ||
            string.IsNullOrWhiteSpace(progress.ReportSha256) || progress.ReportSha256.Length != 64 ||
            !progress.ReportSha256.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Host declared finalization without a valid terminal report hash.");
        }

        string evidence = $"child-report:{progress.Identity.OperationId}:sha256:{progress.ReportSha256.ToUpperInvariant()}";
        return ledger.RecordOutcome(pipelineOperationId, stageId, before.Revision,
            progress.State == "succeeded", evidence);
    }

    private void RequireWorkspace(PipelineChildLaunch launch)
    {
        if (!string.Equals(host.WorkspaceDirectory, launch.WorkspaceDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("host_workspace_changed");
        }
    }

    private static void RequireChild(WorkflowChildIdentity child, string expectedRequestId,
        PipelineChildLaunch launch)
    {
        if (string.IsNullOrWhiteSpace(child.OperationId) || child.Kind != "workflow" ||
            child.ClientRequestId != expectedRequestId ||
            !string.Equals(child.ManifestSha256, launch.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Host child identity does not match the durable launch intent.");
        }
    }

    private static PipelineStageSnapshot Stage(PipelineRunSnapshot snapshot, string stageId) =>
        snapshot.Stages.SingleOrDefault(s => s.Id == stageId)
        ?? throw new ArgumentException("Unknown pipeline stage.", nameof(stageId));
}
