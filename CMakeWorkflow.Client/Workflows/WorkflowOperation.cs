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

    public async IAsyncEnumerable<JsonElement> WatchEventsAsync(long afterSequence = 0,
        TimeSpan? pollInterval = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var cursor = afterSequence;
        var delay = pollInterval ?? TimeSpan.FromMilliseconds(300);
        while (true)
        {
            var page = await ReadEventsAsync(cursor, 100, ct);
            foreach (var evt in page.Events) yield return evt;
            cursor = page.NextSequence;
            if (page.Events.Count == 0) await Task.Delay(delay, ct);
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