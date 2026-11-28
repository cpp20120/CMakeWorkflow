using System.Text.Json;
using CMakeWorkflow.CMake.Artifacts;
using CMakeWorkflow.CMake.Reporting;

namespace CMakeWorkflow.CMake.Recovery;

/// <summary>Ordered event projection owned by one CMake runner under its run lease.</summary>
/// <remarks>
/// Not thread-safe; PublishAsync is called on the serial report persistence path. Resume recovers the maximum
/// parseable sequence and suppresses already successful node events; malformed JSON lines are ignored during
/// that scan. A Host can provide a mandatory durable append sink instead. Recovery authority remains with the checkpoint.
/// Handled legacy journal and stdout I/O failures disable those outputs.
/// </remarks>
internal sealed class WorkflowEventJournal : IAsyncDisposable
{
    private StreamWriter? journal;
    private bool stdout;
    private readonly string directory;
    private readonly Action<WorkflowEvent>? appendEvent;
    private readonly bool suppressFileEvidence;
    private readonly Dictionary<string, string> previous = new(StringComparer.Ordinal);
    private bool started;
    private bool finished;
    private bool resumedEventEmitted;
    private long sequence;

    private WorkflowEventJournal(string directory, bool stdout, Action<WorkflowEvent>? appendEvent, bool suppressFileEvidence)
    {
        this.directory = directory;
        this.stdout = stdout;
        this.appendEvent = appendEvent;
        this.suppressFileEvidence = suppressFileEvidence;
    }

    internal static async Task<WorkflowEventJournal> OpenAsync(string directory, bool stdout, WorkflowReport? resumed,
        Action<WorkflowEvent>? appendEvent, long previousSequence, bool suppressFileEvidence)
    {
        WorkflowEventJournal events = new(directory, stdout, appendEvent, suppressFileEvidence) { sequence = previousSequence };
        if (resumed is not null)
        {
            foreach (NodeResult node in resumed.Nodes.Where(n => n.Status == "succeeded"))
            {
                events.previous[node.Id] = node.Status;
            }

            events.started = true;
        }

        if (appendEvent is not null)
        {
            return events;
        }

        try
        {
            events.journal = await EventJournalFile.OpenAsync(Path.Combine(directory, "events.jsonl"),
                resumed is null ? null : events.RestoreSequence);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"[CMakeWorkflow] Event journal unavailable: {error.Message}");
        }
        return events;
    }

    private void RestoreSequence(string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement value = doc.RootElement;
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("sequence", out JsonElement number) &&
                number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out long n))
            {
                sequence = Math.Max(sequence, n);
            }
        }
        catch (JsonException)
        {
            /* Truncated final event is not a checkpoint. */
        }
    }

    /// <summary>Projects report state changes after checkpoint persistence without owning authoritative state.</summary>
    /// <remarks>
    /// Does not mutate the report or retry execution. A workflow_finished event can describe a suspended
    /// needs_attention outcome and must not be used as proof of ledger terminal finalization.
    /// </remarks>
    internal async Task PublishAsync(WorkflowReport report)
    {
        if (started && report.RecoveryPhase == "resuming" && !resumedEventEmitted)
        {
            resumedEventEmitted = true;
            await EmitAsync("workflow_resumed", null, "running", null, null, []);
        }

        if (!started)
        {
            started = true;
            List<WorkflowArtifactView> paths = suppressFileEvidence ? [] : [new("report", Path.Combine(directory, "run.json"), null)];
            if (appendEvent is null)
            {
                paths.Add(new("event-journal", Path.Combine(directory, "events.jsonl"), null));
            }
            await EmitAsync("workflow_started", null, "running", null, null, paths);
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

            await EmitAsync(node.Status == "running" ? "node_started" : "node_finished", node,
                node.Status, node.ExitCode, node.Error, Artifacts(node));
        }

        if (!finished && report.Status != "running")
        {
            finished = true;
            await EmitAsync("workflow_finished", null, report.Status, null, report.Error,
                suppressFileEvidence ? [] : [new("report", Path.Combine(directory, "run.json"), null)]);
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

        if (node.Kind is "profile" or "profile_snapshot")
        {
            Add("capture-manifest", node.Id + ".manifest.json");
            Add("capture-result", node.Id + ".results.json");
        }

        return artifacts;
    }

    private async Task EmitAsync(string type, NodeResult? node, string? status, int? exitCode,
        string? error, List<WorkflowArtifactView> artifacts)
    {
        WorkflowEvent item = new(1, ++sequence, DateTimeOffset.UtcNow, type, directory,
            node?.Id, node?.Kind, status, exitCode, error, artifacts);
        if (appendEvent is not null)
        {
            appendEvent(item);
        }
        else
        {
            if (journal is null)
            {
                return;
            }
            try
            {
                await EventJournalFile.AppendAsync(journal,
                    JsonSerializer.Serialize(item, Serialization.WorkflowProtocolJson.Default.WorkflowEvent));
            }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException)
            {
                await EventJournalFile.CloseAsync(journal);
                journal = null;
                return;
            }
        }

        if (stdout)
        {
            try
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(item, Serialization.WorkflowProtocolJson.Default.WorkflowEvent));
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

    public ValueTask DisposeAsync() => EventJournalFile.CloseAsync(journal);
}
