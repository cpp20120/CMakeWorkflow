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
    private Action<IReadOnlyList<WorkflowEvent>>? commit;
    private bool resumed;
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

    internal static ProcessEventJournal OpenNative(string directory, bool resume, ProcessOperationReport report,
        long sequence, Action<IReadOnlyList<WorkflowEvent>> commit)
    {
        ProcessEventJournal journal = new(directory) { commit = commit, sequence = sequence, resumed = resume };
        if (resume)
        {
            foreach (ProcessNodeReport node in report.Nodes)
            {
                journal.last.Add(node.Id, node.Status);
            }
        }

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
        List<WorkflowEvent> pending = [];
        void Stage(string type, string? id, string status, int? exitCode, string? error) =>
            pending.Add(new(1, checked(sequence + pending.Count + 1), DateTimeOffset.UtcNow, type,
                directory, id, id is null ? null : "process", status, exitCode, error, []));

        if (!started)
        {
            Stage(resumed ? "workflow_resumed" : "workflow_started", null, "running", null, null);
        }
        foreach (ProcessNodeReport node in report.Nodes)
        {
            if (node.Status != "pending" && last.GetValueOrDefault(node.Id) != node.Status)
            {
                Stage(node.Status == "running" ? "node_started" : "node_finished", node.Id, node.Status, node.ExitCode, node.Error);
            }
        }
        if (!finished && report.Status != "running")
        {
            Stage("workflow_finished", null, report.Status, null, report.Error);
        }

        if (commit is not null)
        {
            commit(pending);
        }
        else if (writer is not null)
        {
            try
            {
                foreach (WorkflowEvent item in pending)
                {
                    await EventJournalFile.AppendAsync(writer, JsonSerializer.Serialize(item, WorkflowProtocolJson.Default.WorkflowEvent));
                }
            }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException)
            {
                await EventJournalFile.CloseAsync(writer);
                writer = null;
            }
        }

        sequence += pending.Count;
        started = true;
        finished = report.Status != "running";
        foreach (WorkflowEvent item in pending)
        {
            if (item.NodeId is { } id)
            {
                last[id] = item.Status!;
            }
        }
    }

    public ValueTask DisposeAsync() => EventJournalFile.CloseAsync(writer);
}
