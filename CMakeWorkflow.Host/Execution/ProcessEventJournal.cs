using System.Text.Json;
using CMakeWorkflow;

namespace CMakeWorkflow.Host;

// Non-authoritative event projection. A failed journal append cannot change a
// durable checkpoint or license a retry of any external effect.
internal sealed class ProcessEventJournal : IDisposable
{
    private StreamWriter? writer;
    private readonly string directory;
    private readonly Dictionary<string, string> last = new(StringComparer.Ordinal);
    private long sequence;
    private bool started;
    private bool finished;

    internal ProcessEventJournal(string directory)
    {
        this.directory = directory;
        try
        {
            writer = new StreamWriter(new FileStream(Path.Combine(directory, "events.jsonl"),
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal void Publish(ProcessOperationReport report)
    {
        if (!started)
        {
            started = true;
            Emit("workflow_started", null, "running", null, null);
        }
        foreach (var node in report.Nodes)
        {
            if (node.Status == "pending" || last.GetValueOrDefault(node.Id) == node.Status) continue;
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
        if (writer is null) return;
        var evt = new WorkflowEvent(1, ++sequence, DateTimeOffset.UtcNow, type,
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
