using System.Text.Json;
using CMakeWorkflow.CMake.Artifacts;
using CMakeWorkflow.CMake.Reporting;

namespace CMakeWorkflow.CMake.Recovery;

/// <summary>Best-effort event projection owned by one CMake runner under its run lease.</summary>
/// <remarks>
/// Not thread-safe; PublishAsync is called on the serial report persistence path. Resume recovers the maximum
/// parseable sequence and suppresses already successful node events; malformed JSON lines are ignored during
/// that scan. It does not validate recovery authority or prove event completeness. Flushing the event stream does not make it a durable checkpoint.
/// Handled journal/stdout I/O failures disable those outputs; unexpected errors may still propagate.
/// </remarks>
internal sealed class WorkflowEventJournal : IAsyncDisposable
{
    private StreamWriter? journal;
    private bool stdout;
    private readonly string directory;
    private readonly Dictionary<string, string> previous = new(StringComparer.Ordinal);
    private bool started;
    private bool finished;
    private bool resumedEventEmitted;
    private long sequence;

    private WorkflowEventJournal(string directory, bool stdout)
    {
        this.directory = directory;
        this.stdout = stdout;
    }

    internal static async Task<WorkflowEventJournal> OpenAsync(string directory, bool stdout, WorkflowReport? resumed)
    {
        WorkflowEventJournal events = new(directory, stdout);
        if (resumed is not null)
        {
            foreach (NodeResult node in resumed.Nodes.Where(n => n.Status == "succeeded"))
            {
                events.previous[node.Id] = node.Status;
            }

            events.started = true;
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
            await EmitAsync("workflow_started", null, "running", null, null,
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

            await EmitAsync(node.Status == "running" ? "node_started" : "node_finished", node,
                node.Status, node.ExitCode, node.Error, Artifacts(node));
        }

        if (!finished && report.Status != "running")
        {
            finished = true;
            await EmitAsync("workflow_finished", null, report.Status, null, report.Error,
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
        string line = JsonSerializer.Serialize(item, Serialization.WorkflowProtocolJson.Default.WorkflowEvent);
        if (journal is null)
        {
            return;
        }

        try
        {
            await EventJournalFile.AppendAsync(journal, line);
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException)
        {
            await EventJournalFile.CloseAsync(journal);
            journal = null;
            return;
        }

        if (stdout)
        {
            try
            {
                await Console.Out.WriteLineAsync(line);
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