using System.Text.Json;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.CMake.Serialization;

namespace CMakeWorkflow.Host.Execution;

// Non-authoritative event projection. A failed journal append cannot change a
// durable checkpoint or license a retry of any external effect.
/// <summary>Best-effort audit projection owned by one process runner under its run lease.</summary>
/// <remarks>
/// Not thread-safe; PublishAsync calls belong to the serial checkpoint path. Open/append I/O failures can
/// disable the writer without changing execution authority. The event flush is not the durable checkpoint protocol.
/// On resume, sequence numbers continue above parsed historical events and an interrupted final line is separated;
/// node events can be projected again by the new journal instance and must not be interpreted as new dispatches.
/// </remarks>
internal sealed class ProcessEventJournal : IAsyncDisposable
{
    private StreamWriter? writer;
    private readonly string directory;
    private readonly Dictionary<string, string> last = new(StringComparer.Ordinal);
    private long sequence;
    private bool started;
    private bool finished;

    private ProcessEventJournal(string directory)
    {
        this.directory = directory;
    }

    internal static async Task<ProcessEventJournal> OpenAsync(string directory, bool resume)
    {
        ProcessEventJournal journal = new(directory);
        try
        {
            journal.writer = await EventJournalFile.OpenAsync(Path.Combine(directory, "events.jsonl"),
                resume ? journal.RestoreSequence : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return journal;
    }

    private void RestoreSequence(string line)
    {
        try
        {
            WorkflowEvent? evt = JsonSerializer.Deserialize(line, WorkflowProtocolJson.Default.WorkflowEvent);
            if (evt is not null)
            {
                sequence = Math.Max(sequence, evt.Sequence);
            }
        }
        catch (JsonException) { }
    }

    /// <summary>Appends observed state transitions without changing the report or licensing retry.</summary>
    /// <remarks>
    /// Caller invokes after durable checkpoint persistence. A journal failure cannot reverse that checkpoint;
    /// use report/ledger evidence, not event completeness, to decide finalization or safe recovery.
    /// </remarks>
    internal async Task PublishAsync(ProcessOperationReport report)
    {
        if (!started)
        {
            started = true;
            await EmitAsync("workflow_started", null, "running", null, null);
        }
        foreach (ProcessNodeReport node in report.Nodes)
        {
            if (node.Status == "pending" || last.GetValueOrDefault(node.Id) == node.Status)
            {
                continue;
            }

            last[node.Id] = node.Status;
            await EmitAsync(node.Status == "running" ? "node_started" : "node_finished", node.Id,
                node.Status, node.ExitCode, node.Error);
        }
        if (!finished && report.Status != "running")
        {
            finished = true;
            await EmitAsync("workflow_finished", null, report.Status, null, report.Error);
        }
    }

    private async Task EmitAsync(string type, string? id, string status, int? exitCode, string? error)
    {
        if (writer is null)
        {
            return;
        }

        WorkflowEvent evt = new(1, ++sequence, DateTimeOffset.UtcNow, type,
            directory, id, id is null ? null : "process", status, exitCode, error, []);
        try
        {
            await EventJournalFile.AppendAsync(writer, JsonSerializer.Serialize(evt, WorkflowProtocolJson.Default.WorkflowEvent));
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException)
        {
            await EventJournalFile.CloseAsync(writer);
            writer = null;
        }
    }

    public ValueTask DisposeAsync() => EventJournalFile.CloseAsync(writer);
}
