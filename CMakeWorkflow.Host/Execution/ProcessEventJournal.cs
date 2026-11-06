using System.Text.Json;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.CMake.Serialization;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Execution;
#pragma warning restore IDE0130

// Non-authoritative event projection. A failed journal append cannot change a
// durable checkpoint or license a retry of any external effect.
/// <summary>Best-effort audit projection owned by one process runner under its run lease.</summary>
/// <remarks>
/// Not thread-safe; Publish calls belong to the serial checkpoint path. Constructor/append I/O failures can
/// disable the writer without changing execution authority. AutoFlush is not the durable checkpoint protocol.
/// On resume, sequence numbers continue above parsed historical events and an interrupted final line is separated;
/// node events can be projected again by the new journal instance and must not be interpreted as new dispatches.
/// </remarks>
internal sealed class ProcessEventJournal : IDisposable
{
    private StreamWriter? writer;
    private readonly string directory;
    private readonly Dictionary<string, string> last = new(StringComparer.Ordinal);
    private long sequence;
    private bool started;
    private bool finished;

    internal ProcessEventJournal(string directory, bool resume = false)
    {
        this.directory = directory;
        try
        {
            bool needsSeparator = false;
            if (resume && File.Exists(Path.Combine(directory, "events.jsonl")))
            {
                foreach (string line in File.ReadLines(Path.Combine(directory, "events.jsonl")))
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
                using FileStream tail = File.OpenRead(Path.Combine(directory, "events.jsonl"));
                if (tail.Length > 0)
                {
                    _ = tail.Seek(-1, SeekOrigin.End);
                    needsSeparator = tail.ReadByte() != '\n';
                }
            }
            writer = new StreamWriter(new FileStream(Path.Combine(directory, "events.jsonl"),
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            { AutoFlush = true };
            // Preserve an interrupted final line without merging the first new
            // event into it. Replay skips that incomplete historical record.
            if (needsSeparator)
            {
                writer.WriteLine();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Appends observed state transitions without changing the report or licensing retry.</summary>
    /// <remarks>
    /// Caller invokes after durable checkpoint persistence. A journal failure cannot reverse that checkpoint;
    /// use report/ledger evidence, not event completeness, to decide finalization or safe recovery.
    /// </remarks>
    internal void Publish(ProcessOperationReport report)
    {
        if (!started)
        {
            started = true;
            Emit("workflow_started", null, "running", null, null);
        }
        foreach (ProcessNodeReport node in report.Nodes)
        {
            if (node.Status == "pending" || last.GetValueOrDefault(node.Id) == node.Status)
            {
                continue;
            }

            last[node.Id] = node.Status;
            Emit(node.Status == "running" ? "node_started" : "node_finished", node.Id,
                node.Status, node.ExitCode, node.Error);
        }
        if (!finished && report.Status != "running")
        {
            finished = true;
            Emit("workflow_finished", null, report.Status, null, report.Error);
        }
    }

    private void Emit(string type, string? id, string status, int? exitCode, string? error)
    {
        if (writer is null)
        {
            return;
        }

        WorkflowEvent evt = new(1, ++sequence, DateTimeOffset.UtcNow, type,
            directory, id, id is null ? null : "process", status, exitCode, error, []);
        try
        {
            writer.WriteLine(JsonSerializer.Serialize(evt, WorkflowProtocolJson.Default.WorkflowEvent));
        }
        catch (IOException) { writer.Dispose(); writer = null; }
        catch (ObjectDisposedException) { writer = null; }
    }

    public void Dispose() => writer?.Dispose();
}
