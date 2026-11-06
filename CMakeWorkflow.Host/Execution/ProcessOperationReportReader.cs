using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Host.Workflows;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Execution;
#pragma warning restore IDE0130

// A typed non-CMake report view. Legacy operations/report stays CMake-specific.
/// <summary>Reads process-domain terminal evidence only after ledger finalization.</summary>
internal static class ProcessOperationReportReader
{
    internal static async Task<JsonElement> ReadAsync(WorkspaceReadPaths paths, string operationId,
        CancellationToken ct)
    {
        (ProcessOperationReport Report, string Directory, string Revision) verified = await ReadVerifiedAsync(paths, operationId, ct);
        return JsonSerializer.SerializeToElement(verified.Report,
            ProcessWorkflowJson.Default.ProcessOperationReport);
    }

    /// <summary>Verifies exact process report bytes, exit/state consistency and accepted snapshot identity.</summary>
    /// <remarks>
    /// Requires a finalized process operation. Uses a secure artifact handle and a 16 MiB report limit, then
    /// verifies SHA-256 before interpreting terminal evidence. Does not read mutable checkpoints as results.
    /// Cancellation interrupts reading only; all ledger connections and file handles are owned within this call.
    /// </remarks>
    internal static async Task<(ProcessOperationReport Report, string Directory, string Revision)>
        ReadVerifiedAsync(WorkspaceReadPaths paths, string operationId, CancellationToken ct)
    {
        string path = paths.Database(Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "operations.sqlite"));
        if (!File.Exists(path))
        {
            throw new KeyNotFoundException("Unknown operation.");
        }

        using DurableAcceptanceStore store = new(paths.WorkspaceDirectory, readOnly: true);
        if (store.Get(operationId) is null)
        {
            throw new KeyNotFoundException("Unknown operation.");
        }

        if (store.GetDomainKind(operationId) != "process")
        {
            throw new ApplicationRefusal("wrong_domain", "Use operations/report for CMake operations.");
        }

        DurableAcceptanceStore.Finalization finalized = store.GetFinalization(operationId) ??
                        throw new ApplicationRefusal("not_finalized", "No durable terminal report yet.");
        string output = paths.Require(finalized.RunDirectory);
        string filePath = paths.Require(Path.Combine(output, "run.json"));
        await using FileStream stream = OperationArtifactFile.OpenRead(paths.WorkspaceDirectory, filePath);
        if (stream.Length > 16 * 1024 * 1024)
        {
            throw new ApplicationRefusal("resource_limit", "Report exceeds 16 MiB.");
        }

        using MemoryStream memory = new();
        await stream.CopyToAsync(memory, ct);
        byte[] bytes = memory.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(finalized.ReportSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ApplicationRefusal("report_changed", "Durable report hash mismatch.");
        }

        ProcessOperationReport report = JsonSerializer.Deserialize(bytes, ProcessWorkflowJson.Default.ProcessOperationReport)
                     ?? throw new InvalidDataException("Invalid process report.");
        AcceptedWorkflow accepted = store.Get(operationId)!;
        OperationCheckpointEvidence evidence = ProcessCheckpointEvidenceCodec.Instance.ForFinalization(bytes, finalized.ExitCode);
        return !string.Equals(evidence.SnapshotSha256, accepted.ManifestSha256,
                StringComparison.OrdinalIgnoreCase) || evidence.TerminalState != finalized.State
            ? throw new ApplicationRefusal("report_changed", "Durable process report binding mismatch.")
            : ((ProcessOperationReport Report, string Directory, string Revision))(report, output, finalized.ReportSha256);
    }
}
