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