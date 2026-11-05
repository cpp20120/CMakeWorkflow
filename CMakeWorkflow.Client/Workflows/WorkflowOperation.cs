using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;

namespace CMakeWorkflow.Client;

public sealed class WorkflowOperation(CMakeWorkflowClient client, string operationId)
{
    public string OperationId { get; } = operationId;

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

    /// <summary>Read at most 64 KiB. Callers must validate the final SHA-256 from ListArtifactsAsync.</summary>
    public Task<OperationArtifactChunk> ReadArtifactChunkAsync(OperationArtifactInfo artifact,
        long offset, int count = 64 * 1024, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        client.RequireCapability("operations/artifacts/readChunk");
        return client.Proxy.ReadArtifactAsync(new(OperationId, artifact.ArtifactId, offset, count,
            artifact.Size, artifact.LastWriteUtcTicks), ct);
    }

    /// <summary>
    /// Download a finalized artifact to a new local file. Keeps verified .cmwf.part
    /// state after transport loss/cancellation for explicit resume after reconnect.
    /// The destination must not exist; no implicit SSH reconnect or workflow redispatch.
    /// </summary>
    public Task<string> DownloadArtifactAsync(OperationArtifactInfo artifact, string destinationPath,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        client.RequireCapability("operations/artifacts/readChunk");
        return OperationArtifactDownloader.DownloadAsync(this, artifact, destinationPath, progress, ct);
    }

    public Task<CancellationReceipt> RequestCancellationAsync(long expectedRevision, CancellationToken ct = default) =>
        client.Proxy.CancelAsync(new(OperationId, expectedRevision), ct);

    public Task<RecoverySnapshot> InspectRecoveryAsync(CancellationToken ct = default) =>
        client.Proxy.InspectAsync(new(OperationId), ct);

    public Task<RecoverySnapshot> ReconcileAsync(long revision, string decision, string note,
        bool confirmedQuiescent, CancellationToken ct = default) =>
        client.Proxy.ReconcileAsync(new(OperationId, revision, decision, note, confirmedQuiescent), ct);

    public Task<RecoverySnapshot> AdmitResumeAsync(long revision, bool confirmedQuiescent,
        CancellationToken ct = default) =>
        client.Proxy.AdmitResumeAsync(new(OperationId, revision, confirmedQuiescent), ct);

    public Task<RecoverySnapshot> ExecuteResumeAsync(long revision = 2, CancellationToken ct = default) =>
        client.Proxy.ExecuteResumeAsync(new(OperationId, revision, true), ct);

    public Task<EventPage> ReadEventsAsync(long afterSequence = 0, int limit = 100, CancellationToken ct = default) =>
        client.Proxy.EventsAsync(new(OperationId, afterSequence, limit), ct);

    /// <summary>Replay events from the supplied cursor and follow until terminal finalization.</summary>
    /// <remarks>Event cursors survive reconnects. RPC failures propagate; no implicit reconnect,
    /// resubmission or remote cancellation occurs. A terminal state triggers an extra drain of
    /// the journal, not an assumption that the most recent page was complete.</remarks>
    public async IAsyncEnumerable<JsonElement> WatchEventsAsync(long afterSequence = 0,
        TimeSpan? pollInterval = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        var delay = pollInterval ?? TimeSpan.FromMilliseconds(300);
        if (delay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        var cursor = afterSequence;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ReadEventsAsync(cursor, 100, ct);
            if (page.NextSequence < cursor || (page.Events.Count > 0 && page.NextSequence <= cursor) ||
                (page.HasMore && page.Events.Count == 0))
                throw new InvalidDataException("Non-progressing event cursor from Host.");
            foreach (var evt in page.Events) yield return evt;
            cursor = page.NextSequence;
            if (page.HasMore || page.Events.Count > 0) continue;

            var operation = await GetSnapshotAsync(ct);
            if (operation.State is "succeeded" or "failed" or "canceled")
            {
                // The journal is published before durable finalization; drain once
                // again in case the first read raced with the final append.
                var final = await ReadEventsAsync(cursor, 100, ct);
                if (final.NextSequence < cursor || (final.Events.Count > 0 && final.NextSequence <= cursor) ||
                    (final.HasMore && final.Events.Count == 0))
                    throw new InvalidDataException("Non-progressing final event cursor from Host.");
                foreach (var evt in final.Events) yield return evt;
                cursor = final.NextSequence;
                if (!final.HasMore && final.Events.Count == 0) yield break;
                continue;
            }
            await Task.Delay(delay, ct);
        }
    }

    public async Task<OperationSnapshot> WaitForCompletionAsync(TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        var delay = pollInterval ?? TimeSpan.FromMilliseconds(300);
        while (true)
        {
            var state = await GetSnapshotAsync(ct);
            if (state.State is "succeeded" or "failed" or "canceled") return state;
            await Task.Delay(delay, ct);
        }
    }
}