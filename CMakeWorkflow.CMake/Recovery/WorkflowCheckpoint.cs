using System.Text.Json;
using CMakeWorkflow.CMake.Reporting;

namespace CMakeWorkflow.CMake.Recovery;

// run.json and its audit are a single commit. Callers must hold the run lease.
internal static class WorkflowCheckpoint
{
    public static Task SaveAsync(WorkflowReport report, string directory, CancellationToken ct)
    {
        string path = Path.Combine(directory, "run.json");
        string? faultNode = report.Nodes.FirstOrDefault(n => n.Status == "running")?.Id
                        ?? report.Reconciliations.LastOrDefault()?.NodeId;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport);
        return WriteDurableBytesAsync(path, bytes, ct, faultNode, injectRecoveryFaults: true);
    }

    // Same write-ahead durability boundary for the DAG report and ordered
    // research-command journals. A command must never launch until this commit
    // succeeds. Callers hold their exclusive output-directory lease.
    internal static async Task WriteDurableBytesAsync(string path, ReadOnlyMemory<byte> bytes,
        CancellationToken ct, string? faultNode = null, bool injectRecoveryFaults = false)
    {
        Func<Task>? afterFlushed = injectRecoveryFaults
            ? () => RecoveryFaults.HitAsync("checkpoint-flushed", faultNode, ct)
            : null;
        Func<Task>? afterReplaced = injectRecoveryFaults
            ? () => RecoveryFaults.HitAsync("checkpoint-replaced", faultNode, ct)
            : null;
        Func<Task>? beforeDirectorySync = injectRecoveryFaults
            ? () => RecoveryFaults.HitAsync("checkpoint-directory-sync", faultNode, ct)
            : null;
        await DurableByteWriter.WriteAsync(path, bytes, ct, afterFlushed, afterReplaced, beforeDirectorySync);
    }
}
