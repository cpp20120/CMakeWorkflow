using System.Globalization;
using CMakeWorkflow.Pipelines.Persistence;

namespace CMakeWorkflow.Pipelines.GitHub;

/// <summary>Durable one-shot GitHub dispatch and strictly read-only subsequent reconciliation.</summary>
/// <remarks>
/// Caller must own serialized ledger access and external dispatch ownership. This class never claims
/// cross-process leases. Once dispatch_intent commits, neither restart nor transient failure may POST again.
/// The target workflow must declare cmwf_request_id and set run-name exactly to cmwf-${{ inputs.cmwf_request_id }}.
/// </remarks>
public sealed class GitHubActionsDispatchCoordinator(PipelineLedger ledger,
    IGitHubActionsDispatcher dispatcher, IGitHubActionsRunReader reader)
{
    private readonly PipelineLedger ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    private readonly IGitHubActionsDispatcher dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly IGitHubActionsRunReader reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>Persist dispatch intent, then make exactly one POST; never retry on errors or lost responses.</summary>
    public async Task<PipelineRunSnapshot> DispatchReadyAsync(string operationId, string stageId,
        CancellationToken cancellationToken = default)
    {
        PipelineRunSnapshot before = ledger.Get(operationId);
        _ = Frozen(before, stageId, PipelineStageStates.Pending);
        // CAS rechecks the prerequisites. No network call occurs before the durable intent commits.
        PipelineRunSnapshot reserved = ledger.ReserveGitHubDispatch(operationId, stageId, before.Revision);
        PipelineStageSnapshot stage = Stage(reserved, stageId);
        PipelineGitHubActionsStage frozen = Frozen(reserved, stageId, PipelineStageStates.DispatchIntent);
        string correlation = Correlation(stage);
        // This method MUST NOT be invoked a second time for dispatch_intent, regardless of exception type.
        GitHubDispatchReceipt receipt = await dispatcher.DispatchOnceAsync(frozen, correlation, cancellationToken);
        if (receipt.RunId <= 0)
        {
            throw new InvalidDataException("Invalid workflow_run_id; dispatch outcome remains unknown.");
        }

        // Store the provider-assigned identity immediately, before any possibly failing follow-up GET.
        return ledger.BindGitHubDispatch(operationId, stageId, reserved.Revision, receipt.RunId, 1);
    }

    /// <summary>Read-only, operator-supplied candidate recovery after a lost POST receipt.</summary>
    /// <remarks>An absent or mismatched candidate is never evidence that dispatch did not happen.</remarks>
    public async Task<PipelineRunSnapshot> ReconcileKnownRunAsync(string operationId, string stageId,
        long candidateRunId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(candidateRunId);
        PipelineRunSnapshot before = ledger.Get(operationId);
        PipelineGitHubActionsStage frozen = Frozen(before, stageId, PipelineStageStates.DispatchIntent);
        PipelineGitHubActionsStage expected = Pin(frozen, candidateRunId, 1);
        GitHubRunAttempt observed = await reader.GetAttemptAsync(expected, cancellationToken);
        Verify(expected, observed, Correlation(Stage(before, stageId)));
        return ledger.BindGitHubDispatch(operationId, stageId, before.Revision, candidateRunId, 1);
    }

    /// <summary>Observe only the exact durable binding; verified terminal outcome advances the ledger.</summary>
    public async Task<PipelineRunSnapshot> ObserveAsync(string operationId, string stageId,
        CancellationToken cancellationToken = default)
    {
        PipelineRunSnapshot before = ledger.Get(operationId);
        PipelineStageSnapshot stage = Stage(before, stageId);
        PipelineGitHubActionsStage frozen = Frozen(before, stageId, stage.Status);
        if (stage.Status is PipelineStageStates.Succeeded or PipelineStageStates.Failed)
        {
            return before;
        }
        if (stage.Status != PipelineStageStates.WaitingExternal)
        {
            throw new InvalidOperationException("github_dispatch_not_bound; explicit reconciliation required");
        }

        string correlation = Correlation(stage);
        // Binding can be recovered without guessing a recent GitHub run.
        long runId = BoundRunId(stage.ExternalReference, frozen);
        PipelineGitHubActionsStage expected = Pin(frozen, runId, 1);
        GitHubRunAttempt observed = await reader.GetAttemptAsync(expected, cancellationToken);
        Verify(expected, observed, correlation);
        if (observed.Status != "completed")
        {
            return before;
        }

        string evidence = "github-attempt:" + expected.Repository + ":" +
            runId.ToString(CultureInfo.InvariantCulture) + ":1:sha256:" +
            observed.ResponseSha256.ToUpperInvariant();
        return ledger.RecordOutcome(operationId, stageId, before.Revision,
            observed.Conclusion == "success", evidence);
    }

    /// <summary>Explicit, cancellable observation loop. Does not rerun or cancel the external workflow.</summary>
    public async Task<PipelineRunSnapshot> WatchUntilTerminalAsync(string operationId, string stageId,
        TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        TimeSpan interval = pollInterval ?? TimeSpan.FromSeconds(30);
        if (interval < TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PipelineRunSnapshot current = await ObserveAsync(operationId, stageId, cancellationToken);
            if (Stage(current, stageId).Status is PipelineStageStates.Succeeded or PipelineStageStates.Failed)
            {
                return current;
            }
            await Task.Delay(interval, cancellationToken);
        }
    }

    private static void Verify(PipelineGitHubActionsStage expected, GitHubRunAttempt actual, string correlation)
    {
        GitHubActionsStageWatcher.Verify(expected, actual);
        if (actual.Event != "workflow_dispatch" || actual.DisplayTitle != "cmwf-" + correlation)
        {
            throw new InvalidDataException("GitHub dispatch marker/event does not match original durable intent.");
        }
    }

    private static long BoundRunId(string? binding, PipelineGitHubActionsStage frozen)
    {
        // The entire binding is checked; a URL or an unrelated stage cannot be substituted.
        if (binding is null)
        {
            throw new InvalidDataException("No persisted GitHub dispatch binding.");
        }
        string prefix = $"github:{frozen.Repository}:{frozen.Workflow}:";
        string suffix = $":1:{frozen.ExpectedHeadSha}";
        if (!binding.StartsWith(prefix, StringComparison.Ordinal) ||
            !binding.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Persisted GitHub dispatch binding differs from frozen manifest.");
        }
        string middle = binding[prefix.Length..^suffix.Length];
        return long.TryParse(middle, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0
            ? id : throw new InvalidDataException("Invalid persisted GitHub run ID.");
    }

    private static PipelineGitHubActionsStage Pin(PipelineGitHubActionsStage frozen, long runId, int attempt) => new()
    {
        Mode = "attach",
        Repository = frozen.Repository,
        Workflow = frozen.Workflow,
        ExpectedHeadSha = frozen.ExpectedHeadSha,
        RunId = runId,
        RunAttempt = attempt
    };

    private static PipelineGitHubActionsStage Frozen(PipelineRunSnapshot run, string stageId, string status)
    {
        PipelineStageSnapshot stage = Stage(run, stageId);
        if (stage.Kind != "github.actions" || stage.Status != status)
        {
            throw new InvalidOperationException("invalid_github_dispatch_stage_state");
        }
        PipelineDocument manifest = PipelineManifest.ParseJson(run.ManifestJson);
        PipelineGitHubActionsStage options = manifest.Stages.Single(s => s.Id == stageId).GitHubActions
            ?? throw new InvalidDataException("Missing immutable GitHub dispatch options.");
        return options.Mode == "dispatch" ? options :
            throw new InvalidOperationException("not_a_github_dispatch_stage");
    }

    private static string Correlation(PipelineStageSnapshot stage) =>
        !string.IsNullOrWhiteSpace(stage.ChildRequestId) ? stage.ChildRequestId :
            throw new InvalidDataException("Dispatch stage lacks durable correlation identity.");

    private static PipelineStageSnapshot Stage(PipelineRunSnapshot run, string stageId) =>
        run.Stages.SingleOrDefault(s => s.Id == stageId)
        ?? throw new ArgumentException("Unknown pipeline stage.", nameof(stageId));
}
