using System.Text.Json;
using CMakeWorkflow;

namespace CMakeWorkflow.Host;

public static class OperationEventReplay
{
    // Events are an audit/progress feed, never a recovery checkpoint.
    internal static async Task<OperationEventsPage> ReadAsync(WorkspaceReadPaths paths,
        OperationEventsRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128 ||
            request.AfterSequence < 0 || request.Limit is < 1 or > 500)
            throw new ArgumentException("Invalid event cursor or page limit.");
        var db = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "operations.sqlite");
        paths.Require(db);
        if (!File.Exists(db)) throw new KeyNotFoundException("Unknown operation.");
        using var store = new DurableAcceptanceStore(paths.WorkspaceDirectory, readOnly: true);
        var inspection = store.InspectRecovery(request.OperationId);
        if (inspection.RunDirectory is null)
            return new(request.OperationId, request.AfterSequence, request.AfterSequence, false, []);
        var journalPath = paths.Require(Path.Combine(inspection.RunDirectory, "events.jsonl"));
        if (!File.Exists(journalPath))
            return new(request.OperationId, request.AfterSequence, request.AfterSequence, false, []);
        if (new FileInfo(journalPath).Length > 32 * 1024 * 1024)
            throw new InvalidDataException("Event journal exceeds the 32 MiB read limit.");
        return await ReadJournalAsync(journalPath, request.OperationId, request.AfterSequence, request.Limit, ct);
    }

    public static async Task<OperationEventsPage> ReadJournalAsync(string journalPath, string operationId,
        long afterSequence, int limit, CancellationToken ct)
    {
        if (afterSequence < 0 || limit is < 1 or > 500) throw new ArgumentException("Invalid cursor or limit.");
        var items = new List<WorkflowEvent>();
        long previous = 0;
        using var file = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        var nextLine = await reader.ReadLineAsync(ct);
        while (nextLine is not null)
        {
            var line = nextLine;
            nextLine = await reader.ReadLineAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (line.Length > 1024 * 1024) throw new InvalidDataException("Oversized journal entry.");
            WorkflowEvent? entry;
            try { entry = JsonSerializer.Deserialize(line, WorkflowProtocolJson.Default.WorkflowEvent); }
            catch (JsonException) when (nextLine is null) { break; } // interrupted final append
            if (entry is null || entry.Sequence <= previous)
                throw new InvalidDataException("Invalid or non-monotonic event sequence.");
            previous = entry.Sequence;
            if (entry.Sequence <= afterSequence) continue;
            if (items.Count == limit)
                return new(operationId, afterSequence, items[^1].Sequence, true, items);
            items.Add(entry);
        }
        return new(operationId, afterSequence,
            items.Count == 0 ? afterSequence : items[^1].Sequence, false, items);
    }
}
