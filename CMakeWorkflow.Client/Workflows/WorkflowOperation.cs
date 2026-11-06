using System.Text.Json;

namespace CMakeWorkflow.Client.Workflows;

/// <summary>Non-owning durable operation handle bound to a client connection.</summary>
/// <remarks>
/// Creating or abandoning a handle has no remote effect. Dispose/reconnect the client separately.
/// All tokens cancel individual RPCs, polling or local transfer work; they do not request durable operation cancellation.
/// Use RequestCancellationAsync for that separate mutation. No method implicitly reconnects or resubmits a workflow.
/// </remarks>
public sealed class WorkflowOperation(CMakeWorkflowClient client, string operationId)
{
    /// <summary>Durable Host operation identifier; meaningful within the initialized workspace.</summary>
    public string OperationId { get; } = operationId;

    /// <summary>Reads durable operation state; does not wait for completion.</summary>
    public Task<OperationSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
        client.Operations.GetAsync(OperationId, ct);

    /// <summary>Read normalized operation/node state, backed by verified terminal evidence.</summary>
    public Task<OperationOverview> GetOverviewAsync(CancellationToken ct = default) =>
        client.Operations.GetOverviewAsync(OperationId, ct);

    /// <summary>Read the verified terminal report bound to this durable operation on the Host.</summary>
    /// <remarks>Available after finalization, including failed and canceled runs.</remarks>
    public Task<OperationReportSnapshot> GetReportAsync(CancellationToken ct = default)
    {
        client.RequireCapability("operations/report");
        return client.Proxy.ReportAsync(new(OperationId), ct);
    }

    /// <summary>Discover finalized run logs, report and in-results-tree artifact files.</summary>
    public Task<OperationArtifactList> ListArtifactsAsync(CancellationToken ct = default)
    {
        client.RequireCapability("operations/artifacts/list");
        return client.Proxy.ArtifactsAsync(new(OperationId), ct);
    }

    /// <summary>Reads at most 64 KiB from a catalogued finalized artifact.</summary>
    /// <remarks>
    /// Size and last-write evidence are sent to detect changes. Chunk reads alone do not validate the complete file hash.
    /// Callers assembling chunks must verify the catalog SHA-256; use DownloadArtifactAsync for checked local persistence.
    /// </remarks>
    /// <param name="artifact">Entry from this operation artifact catalog.</param>
    /// <param name="offset">Nonnegative byte offset.</param>
    /// <param name="count">Requested byte count, from 1 to 65536.</param>
    /// <param name="ct">Cancels this read only.</param>
    public Task<OperationArtifactChunk> ReadArtifactChunkAsync(OperationArtifactInfo artifact,
        long offset, int count = 64 * 1024, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        client.RequireCapability("operations/artifacts/readChunk");
        return client.Proxy.ReadArtifactAsync(new(OperationId, artifact.ArtifactId, offset, count,
            artifact.Size, artifact.LastWriteUtcTicks), ct);
    }

    /// <summary>Downloads and verifies a finalized artifact into a new local file.</summary>
    /// <remarks>
    /// Destination must not exist and its parent directory must exist. On interruption, verified .cmwf.part and .meta state
    /// can be reused by an explicit call with the same operation and artifact after reconnect. Orphan state requires explicit removal.
    /// The SDK validates the complete SHA-256 before publishing the destination; it does not reconnect or redispatch workflows.
    /// </remarks>
    /// <param name="artifact">Catalog entry from this operation.</param>
    /// <param name="destinationPath">Local destination path; distinct from all Host paths.</param>
    /// <param name="progress">Optional cumulative byte progress; null disables callbacks.</param>
    /// <param name="ct">Cancels transfer/RPC work, retaining resumable state where available; does not cancel the operation.</param>
    /// <returns>Absolute local destination path after verification.</returns>
    public Task<string> DownloadArtifactAsync(OperationArtifactInfo artifact, string destinationPath,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        client.RequireCapability("operations/artifacts/readChunk");
        return OperationArtifactDownloader.DownloadAsync(this, artifact, destinationPath, progress, ct);
    }

    /// <summary>Commits a durable request to stop accepted execution.</summary>
    /// <remarks>
    /// Requested means cancellation intent was recorded; it does not prove workers stopped or the outcome is canceled.
    /// A completed operation returns Requested=false. A nonterminal operation uses a separate cancellation CAS revision: 0 then 1.
    /// Canceling this RPC or losing its response can leave the intent committed. Inspect recovery state before retrying.
    /// Host cancellation may race with normal completion; observe the eventual terminal state separately.
    /// </remarks>
    /// <param name="expectedRevision">Current cancellation-intent revision: initially 0; use 1 after intent has been recorded. This is not the recovery revision.</param>
    /// <param name="ct">Cancels the RPC only; it does not revoke a committed cancellation intent.</param>
    /// <returns>Committed cancellation intent or an already terminal state; not a completion acknowledgement.</returns>
    public Task<CancellationReceipt> RequestCancellationAsync(long expectedRevision, CancellationToken ct = default) =>
        client.Proxy.CancelAsync(new(OperationId, expectedRevision), ct);

    /// <summary>Reads durable checkpoint bindings, cancellation intent and recovery decisions.</summary>
    /// <remarks>
    /// A missing checkpoint or terminal state is represented by null, not evidence that no external effects occurred.
    /// </remarks>
    public Task<RecoverySnapshot> InspectRecoveryAsync(CancellationToken ct = default) =>
        client.Proxy.InspectAsync(new(OperationId), ct);

    /// <summary>Records an operator recovery decision without dispatch or releasing results ownership.</summary>
    /// <remarks>
    /// The operation must be inactive and dispatch-authorized, without finalization. Decision is immutable once recorded.
    /// Initial revision 0 becomes 1; an identical decision/note retry must use current revision 1.
    /// A lost reply may leave the decision committed; inspect before retrying.
    /// </remarks>
    /// <param name="revision">Expected recovery revision, distinct from cancellation revision.</param>
    /// <param name="decision">needs_attention or outcome_unknown; the latter prevents resume admission.</param>
    /// <param name="note">Nonempty operator rationale, at most 4096 characters.</param>
    /// <param name="confirmedQuiescent">Explicit assertion that external execution has stopped; the Host cannot prove orphan descendants are gone.</param>
    /// <param name="ct">Cancels the RPC, not any previously committed mutation.</param>
    public Task<RecoverySnapshot> ReconcileAsync(long revision, string decision, string note,
        bool confirmedQuiescent, CancellationToken ct = default) =>
        client.Proxy.ReconcileAsync(new(OperationId, revision, decision, note, confirmedQuiescent), ct);

    /// <summary>Admits recovery of an inactive reconciled operation without launching workers.</summary>
    /// <remarks>
    /// Requires needs_attention, no terminal result and no cancellation intent. Admission advances recovery revision 1 to 2.
    /// Retry an admitted request with revision 2 after inspecting state. Runner validation and a permanent attempt claim still precede execution.
    /// </remarks>
    /// <param name="revision">Expected current recovery revision.</param>
    /// <param name="confirmedQuiescent">Operator assertion that external execution has stopped.</param>
    /// <param name="ct">Cancels the RPC; committed admission is retained.</param>
    /// <returns>Persisted recovery evidence whose Revision must be passed explicitly to ExecuteResumeAsync.</returns>
    public Task<RecoverySnapshot> AdmitResumeAsync(long revision, bool confirmedQuiescent,
        CancellationToken ct = default) =>
        client.Proxy.AdmitResumeAsync(new(OperationId, revision, confirmedQuiescent), ct);

    /// <summary>Schedules an already admitted, one-shot recovery attempt under Host ownership.</summary>
    /// <remarks>
    /// The current protocol records reconciliation at revision 1 and resume admission at revision 2; these are
    /// persisted transition revisions, not a retry counter. Pass this operation's admitted Revision from
    /// AdmitResumeAsync or a subsequent InspectRecoveryAsync; this method does not read or infer the revision.
    /// Requires prior quiescence confirmation; this wrapper sends that confirmation as true.
    /// The runner permanently claims the attempt before mutation or external dispatch. A crash after that claim cannot authorize a second attempt.
    /// Cancellation or loss of this RPC reply does not stop scheduled execution. Inspect recovery after an uncertain response; never blindly redispatch.
    /// </remarks>
    /// <param name="revision">This operation's admitted recovery revision, currently 2; distinct from cancellation-intent revision.</param>
    /// <param name="ct">Cancels the RPC, not the Host-owned recovery attempt.</param>
    /// <returns>Recovery evidence after scheduling; not an acknowledgement of execution completion.</returns>
    public Task<RecoverySnapshot> ExecuteResumeAsync(long revision, CancellationToken ct = default) =>
        client.Proxy.ExecuteResumeAsync(new(OperationId, revision, true), ct);

    /// <summary>Reads a bounded audit/progress page after an exclusive event cursor.</summary>
    /// <remarks>
    /// Events are not recovery authorization. An empty page can mean no checkpoint or no new events.
    /// Persist the last consumed sequence for reconnect; no implicit reconnect occurs.
    /// </remarks>
    /// <param name="afterSequence">Exclusive nonnegative sequence cursor; zero reads from the beginning.</param>
    /// <param name="limit">Maximum events, from 1 to 500.</param>
    /// <param name="ct">Cancels this read only.</param>
    public Task<EventPage> ReadEventsAsync(long afterSequence = 0, int limit = 100, CancellationToken ct = default) =>
        client.Proxy.EventsAsync(new(OperationId, afterSequence, limit), ct);

    /// <summary>Replays events and polls until terminal finalization and journal drain.</summary>
    /// <remarks>
    /// Cursors survive reconnects. Persist each consumed event sequence for explicit replay.
    /// Cancellation or abandoning enumeration stops observation only; no remote cancellation, reconnect or resubmission occurs.
    /// RPC errors propagate. Unresolved recovery may require operator action before this stream finishes.
    /// </remarks>
    /// <param name="afterSequence">Exclusive nonnegative starting sequence; zero starts at the beginning.</param>
    /// <param name="pollInterval">Positive poll delay; null uses 300 milliseconds.</param>
    /// <param name="ct">Cancels enumeration and its RPC reads only.</param>
    public async IAsyncEnumerable<JsonElement> WatchEventsAsync(long afterSequence = 0,
        TimeSpan? pollInterval = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);

        TimeSpan delay = pollInterval ?? TimeSpan.FromMilliseconds(300);
        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        long cursor = afterSequence;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            EventPage page = await ReadEventsAsync(cursor, 100, ct);
            if (page.NextSequence < cursor || (page.Events.Count > 0 && page.NextSequence <= cursor) ||
                (page.HasMore && page.Events.Count == 0))
            {
                throw new InvalidDataException("Non-progressing event cursor from Host.");
            }

            foreach (JsonElement evt in page.Events)
            {
                yield return evt;
            }

            cursor = page.NextSequence;
            if (page.HasMore || page.Events.Count > 0)
            {
                continue;
            }

            OperationSnapshot operation = await GetSnapshotAsync(ct);
            if (operation.State is "succeeded" or "failed" or "canceled")
            {
                // The journal is published before durable finalization; drain once
                // again in case the first read raced with the final append.
                EventPage final = await ReadEventsAsync(cursor, 100, ct);
                if (final.NextSequence < cursor || (final.Events.Count > 0 && final.NextSequence <= cursor) ||
                    (final.HasMore && final.Events.Count == 0))
                {
                    throw new InvalidDataException("Non-progressing final event cursor from Host.");
                }

                foreach (JsonElement evt in final.Events)
                {
                    yield return evt;
                }

                cursor = final.NextSequence;
                if (!final.HasMore && final.Events.Count == 0)
                {
                    yield break;
                }

                continue;
            }
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>Polls until durable state is succeeded, failed or canceled.</summary>
    /// <remarks>
    /// Cancellation stops polling only. Failed/canceled operation outcomes are returned as snapshots, not thrown as task cancellation.
    /// Unresolved recovery states are not terminal and may require operator action; RPC failures propagate without retry.
    /// </remarks>
    /// <param name="pollInterval">Delay between reads; null uses 300 milliseconds.</param>
    /// <param name="ct">Cancels reads/delays without requesting operation cancellation.</param>
    public async Task<OperationSnapshot> WaitForCompletionAsync(TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        TimeSpan delay = pollInterval ?? TimeSpan.FromMilliseconds(300);
        while (true)
        {
            OperationSnapshot state = await GetSnapshotAsync(ct);
            if (state.State is "succeeded" or "failed" or "canceled")
            {
                return state;
            }

            await Task.Delay(delay, ct);
        }
    }
}
