using System.Text.Json;
using CMakeWorkflow.CMake.Artifacts;
using CMakeWorkflow.CMake.Reporting;

namespace CMakeWorkflow.CMake.Recovery;

/// <summary>Best-effort event projection owned by one CMake runner under its run lease.</summary>
/// <remarks>
/// Not thread-safe; Publish is called on the serial report persistence path. Resume recovers the maximum
/// parseable sequence and suppresses already successful node events; malformed JSON lines are ignored during
/// that scan. It does not validate recovery authority or prove event completeness. AutoFlush is not durable sync.
/// Handled journal/stdout I/O failures disable those outputs; unexpected errors may still propagate.
/// </remarks>
internal sealed class WorkflowEventJournal : IDisposable
{
    private StreamWriter? journal;
    private bool stdout;
    private readonly string directory;
    private readonly Dictionary<string, string> previous = new(StringComparer.Ordinal);
    private bool started;
    private bool finished;
    private bool resumedEventEmitted;
    private long sequence;

    public WorkflowEventJournal(string directory, bool stdout, WorkflowReport? resumed = null)
    {
        this.directory = directory;
        this.stdout = stdout;
        if (resumed is not null)
        {
            // Replay only journal metadata, not previously completed node events.
            // The report is the checkpoint; the event file is a best-effort audit trail.
            foreach (NodeResult? node in resumed.Nodes.Where(n => n.Status == "succeeded"))
            {
                previous[node.Id] = node.Status;
            }

            string file = Path.Combine(directory, "events.jsonl");
            if (File.Exists(file))
            {
                foreach (string line in File.ReadLines(file))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(line);
                        JsonElement value = doc.RootElement;
                        if (value.TryGetProperty("sequence", out JsonElement number) && number.TryGetInt64(out long n))
                        {
                            sequence = Math.Max(sequence, n);
                        }
                    }
                    catch (JsonException)
                    {
                        /* Truncated final event is not a checkpoint. */
                    }
                }
            }

            started = true;
        }

        try
        {
            journal = new StreamWriter(new FileStream(Path.Combine(directory, "events.jsonl"),
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            { AutoFlush = true };
        }
        catch (IOException error)
        {
            Console.Error.WriteLine($"[CMakeWorkflow] Event journal unavailable: {error.Message}");
        }
        catch (UnauthorizedAccessException error)
        {
            Console.Error.WriteLine($"[CMakeWorkflow] Event journal unavailable: {error.Message}");
        }
    }

    /// <summary>Projects report state changes after checkpoint persistence without owning authoritative state.</summary>
    /// <remarks>
    /// Does not mutate the report or retry execution. A workflow_finished event can describe a suspended
    /// needs_attention outcome and must not be used as proof of ledger terminal finalization.
    /// </remarks>
    public void Publish(WorkflowReport report)
    {
        if (started && report.RecoveryPhase == "resuming" && !resumedEventEmitted)
        {
            resumedEventEmitted = true;
            Emit("workflow_resumed", null, "running", null, null, []);
        }

        if (!started)
        {
            started = true;
            Emit("workflow_started", null, "running", null, null,
            [
                new("report", Path.Combine(directory, "run.json"), null),
                new("event-journal", Path.Combine(directory, "events.jsonl"), null)
            ]);
        }

        foreach (NodeResult node in report.Nodes)
        {
            _ = previous.TryGetValue(node.Id, out string? old);
            if (old == node.Status)
            {
                continue;
            }

            previous[node.Id] = node.Status;
            if (node.Status == "pending")
            {
                continue;
            }

            Emit(node.Status == "running" ? "node_started" : "node_finished", node,
                node.Status, node.ExitCode, node.Error, Artifacts(node));
        }

        if (!finished && report.Status != "running")
        {
            finished = true;
            Emit("workflow_finished", null, report.Status, null, report.Error,
                [new("report", Path.Combine(directory, "run.json"), null)]);
        }
    }

    private List<WorkflowArtifactView> Artifacts(NodeResult node)
    {
        List<WorkflowArtifactView> artifacts = [];

        void Add(string type, string? path, string? hash = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(directory, path));
            if (File.Exists(full) || Directory.Exists(full))
            {
                artifacts.Add(new(type, full, hash));
            }
        }

        Add("stdout", node.Stdout);
        Add("stderr", node.Stderr);
        Add("executable", node.Artifact?.Executable, node.Artifact?.Sha256);
        foreach (WorkflowArtifactEvidence item in node.DeclaredArtifacts)
        {
            if (item.Role == "output")
            {
                Add("declared:" + item.Name, item.Path, item.Sha256);
            }

            if (item.SnapshotPath is not null)
            {
                Add("snapshot:" + item.Name, item.SnapshotPath, item.Sha256);
            }
        }

        foreach ((string? type, string? path) in node.Outputs)
        {
            if (type is "rawTrace" || type.StartsWith("dagflow-", StringComparison.Ordinal))
            {
                Add(type, path);
            }
        }

        if (node.Kind == "profile")
        {
            Add("capture-manifest", node.Id + ".manifest.json");
            Add("capture-result", node.Id + ".results.json");
        }

        return artifacts;
    }

    private void Emit(string type, NodeResult? node, string? status, int? exitCode,
        string? error, List<WorkflowArtifactView> artifacts)
    {
        WorkflowEvent item = new(1, ++sequence, DateTimeOffset.UtcNow, type, directory,
            node?.Id, node?.Kind, status, exitCode, error, artifacts);
        string line = JsonSerializer.Serialize(item, Serialization.WorkflowProtocolJson.Default.WorkflowEvent);
        if (journal is null)
        {
            return;
        }

        try
        {
            journal.WriteLine(line);
        }
        catch (IOException)
        {
            journal.Dispose();
            journal = null;
            return;
        }

        if (stdout)
        {
            try
            {
                Console.Out.WriteLine(line);
            }
            catch (IOException)
            {
                stdout = false;
            }
            catch (ObjectDisposedException)
            {
                stdout = false;
            }
        }
    }

    public void Dispose() => journal?.Dispose();
}