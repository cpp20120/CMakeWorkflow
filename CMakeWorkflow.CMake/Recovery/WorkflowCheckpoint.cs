using System.Text.Json;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Core.Persistence;

namespace CMakeWorkflow.CMake.Recovery;

// run.json and its audit are a single commit. Callers must hold the run lease.
/// <summary>Serializes authoritative run evidence through the durable byte replacement boundary.</summary>
/// <remarks>
/// Caller owns the exclusive run/output lease, directory creation and recovery decisions. The report and
/// its embedded reconciliation audit are one serialized run.json commit; events.jsonl is not part of that commit.
/// </remarks>
internal static class WorkflowCheckpoint
{
    /// <summary>Serializes the current report synchronously, then durably replaces run.json.</summary>
    /// <remarks>
    /// Report mutation must be serialized with this call. A visible destination after failure does not prove
    /// confirmed durability; dispatch must wait for successful completion. Fault-injection barriers can interrupt
    /// between file flush, replacement and directory sync without rolling back bytes already written.
    /// </remarks>
    public static Task SaveAsync(WorkflowReport report, string directory, CancellationToken ct)
    {
        string path = Path.Combine(directory, "run.json");
        string? faultNode = report.Nodes.FirstOrDefault(n => n.Status == "running")?.Id
                        ?? report.Reconciliations.LastOrDefault()?.NodeId;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, Serialization.WorkflowJson.Default.WorkflowReport);
        return WriteDurableBytesAsync(path, bytes, ct, faultNode, injectRecoveryFaults: true);
    }

    // Same write-ahead durability boundary for the DAG report and ordered
    // research-command journals. A command must never launch until this commit
    // succeeds. Callers hold their exclusive output-directory lease.
    /// <summary>Commits pre-encoded bytes before a command or DAG worker may be launched.</summary>
    /// <remarks>
    /// Caller holds the destination output lease; the underlying writer uses a fixed .tmp sibling. Cancellation
    /// can leave temporary or replaced bytes, so failure/cancellation does not prove that the old checkpoint remains.
    /// </remarks>
    /// <param name="path">Destination in an existing, exclusively owned output directory.</param>
    /// <param name="bytes">Complete encoded checkpoint, including any audit that must commit with it.</param>
    /// <param name="ct">Write/barrier cancellation; safety callers may deliberately use CancellationToken.None.</param>
    /// <param name="faultNode">Optional diagnostic node for recovery fault barriers.</param>
    /// <param name="injectRecoveryFaults">Whether to enable configured checkpoint fault barriers.</param>
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
