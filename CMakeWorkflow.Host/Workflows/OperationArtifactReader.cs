using System.Security.Cryptography;
using CMakeWorkflow.CMake.Artifacts;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Host.Execution;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Sessions;

namespace CMakeWorkflow.Host.Workflows;

// Read-only views over an operation's durably finalized results tree. This is
// intentionally NOT a workspace filesystem API or an artifact upload service.
/// <summary>Exposes report-bound finalized files rather than arbitrary workspace paths.</summary>
/// <remarks>
/// Final report bytes must match durable finalization and accepted snapshot identity. Artifact IDs select
/// known candidates, never caller-supplied paths. Handle-relative opens protect reads against path redirection;
/// size/time checks detect observed changes but are not a filesystem snapshot or a complete content-lock guarantee.
/// </remarks>
internal static class OperationArtifactReader
{
    internal const int MaximumChunkBytes = 64 * 1024;
    private const int MaximumCatalogEntries = 512;

    private sealed record Candidate(string Id, string Kind, string? NodeId, string Path);

    /// <summary>Reads and verifies a finalized CMake report against ledger identity and terminal state.</summary>
    /// <remarks>
    /// Refuses unfinalized/wrong-domain operations and reports over 16 MiB. The opened file bytes must match
    /// the committed report SHA-256; live or uncertain run.json files are not exposed as terminal evidence.
    /// Cancellation affects this read only and does not alter execution or finalization.
    /// </remarks>
    internal static async Task<WorkflowReportSnapshot> ReportAsync(
        WorkspaceReadPaths paths, string operationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        }

        _ = paths.Database(Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "operations.sqlite"));
        using DurableAcceptanceStore store = new(paths.WorkspaceDirectory, readOnly: true);
        AcceptedWorkflow accepted = store.Get(operationId)
                       ?? throw new KeyNotFoundException("Unknown operation.");
        if (store.GetDomainKind(operationId) != "workflow")
        {
            throw new ApplicationRefusal("wrong_domain", "Use execution/report for process operations.");
        }

        DurableAcceptanceStore.Finalization finalized = store.GetFinalization(operationId)
                        ?? throw new ApplicationRefusal("not_finalized", "No durable terminal report yet.");
        string directory = paths.Require(finalized.RunDirectory);
        string reportPath = WithinResults(paths, directory, "run.json");
        await using FileStream reportFile = OperationArtifactFile.OpenRead(paths.WorkspaceDirectory, reportPath);
        if (reportFile.Length > 16 * 1024 * 1024)
        {
            throw new ApplicationRefusal("resource_limit", "Terminal report exceeds 16 MiB.");
        }

        using MemoryStream buffer = new();
        await reportFile.CopyToAsync(buffer, ct);
        WorkflowReportSnapshot report = WorkflowQueries.ReportFromBytes(directory, buffer.ToArray());
        return !string.Equals(report.Revision, finalized.ReportSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(report.Report.Status, finalized.State, StringComparison.Ordinal) ||
            !string.Equals(report.Report.ManifestHash, accepted.ManifestSha256,
                StringComparison.OrdinalIgnoreCase) || report.Report.FinishedAt is null
            ? throw new ApplicationRefusal("report_changed", "The durable report failed verification.")
            : report;
    }

    /// <summary>Builds a bounded catalog with complete file hashes from verified finalization evidence.</summary>
    /// <remarks>
    /// Delegates process-domain catalogs to their adapter. Missing non-report files are skipped because nodes
    /// may never have started. Checks size and last-write time around hashing; at most 512 catalog entries.
    /// Returns evidence observed now, not a promise that external writers cannot later change those files.
    /// </remarks>
    internal static async Task<OperationArtifactList> ListAsync(
        WorkspaceReadPaths paths, string operationId, CancellationToken ct)
    {
        if (ProcessArtifactReader.IsProcess(paths, operationId))
        {
            return await ProcessArtifactReader.ListAsync(paths, operationId, ct);
        }

        WorkflowReportSnapshot report = await ReportAsync(paths, operationId, ct);
        List<OperationArtifactInfo> result = [];
        foreach (Candidate candidate in Candidates(report))
        {
            ct.ThrowIfCancellationRequested();
            string path = WithinResults(paths, report.RunDirectory, candidate.Path);
            await using FileStream? file = OperationArtifactFile.OpenCatalogFile(
                paths.WorkspaceDirectory, path, optional: candidate.Id != "report");
            if (file is null)
            {
                continue; // A node not dispatched has no logs.
            }
            long size = file.Length;
            long stamp = File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks;
            string sha = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (file.Length != size || File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks != stamp)
            {
                throw new ApplicationRefusal("artifact_changed", "Artifact changed during catalog creation.");
            }

            result.Add(new(candidate.Id, candidate.Kind, candidate.NodeId,
                Path.GetRelativePath(report.RunDirectory, path).Replace('\\', '/'), size, stamp, sha));
            if (result.Count > MaximumCatalogEntries)
            {
                throw new ApplicationRefusal("resource_limit", "Artifact catalog has too many files.");
            }
        }
        return new(operationId, report.Revision, result);
    }

    /// <summary>Reads at most 64 KiB from an opaque report-bound artifact ID.</summary>
    /// <remarks>
    /// Revalidates terminal report binding, expected file size and last-write ticks, then checks the same metadata
    /// after the range read. These checks cannot detect every same-size/timestamp-preserving content change;
    /// consumers must verify the assembled file against the catalog SHA-256. Cancellation has no operation side effects.
    /// </remarks>
    internal static async Task<OperationArtifactChunk> ReadChunkAsync(
        WorkspaceReadPaths paths, OperationArtifactChunkRequest request, CancellationToken ct)
    {
        if (request.Count is < 1 or > MaximumChunkBytes || request.Offset < 0 ||
            request.ExpectedSize < 0 || request.ExpectedLastWriteUtcTicks < 0 ||
            request.Offset > request.ExpectedSize)
        {
            throw new ArgumentException("Invalid artifact range or expected identity.");
        }

        if (ProcessArtifactReader.IsProcess(paths, request.OperationId))
        {
            return await ProcessArtifactReader.ReadChunkAsync(paths, request, ct);
        }

        WorkflowReportSnapshot report = await ReportAsync(paths, request.OperationId, ct);
        // The artifact ID is opaque; it selects a known report-bound file, never a path.
        Candidate candidate = Candidates(report).FirstOrDefault(c => c.Id == request.ArtifactId)
                        ?? throw new ApplicationRefusal("not_found", "Unknown operation artifact.");
        string path = WithinResults(paths, report.RunDirectory, candidate.Path);
        await using FileStream file = OperationArtifactFile.OpenRead(paths.WorkspaceDirectory, path);
        long size = file.Length;
        long stamp = File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks;
        if (size != request.ExpectedSize || stamp != request.ExpectedLastWriteUtcTicks)
        {
            throw new ApplicationRefusal("artifact_changed", "The artifact changed since catalog discovery.");
        }

        int count = (int)Math.Min(request.Count, size - request.Offset);
        byte[] bytes = new byte[count];
        _ = file.Seek(request.Offset, SeekOrigin.Begin);
        await file.ReadExactlyAsync(bytes, ct);
        return file.Length != size || File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks != stamp
            ? throw new ApplicationRefusal("artifact_changed", "The artifact changed during transfer.")
            : new(candidate.Id, request.Offset, size, stamp, bytes, request.Offset + count == size);
    }

    private static List<Candidate> Candidates(WorkflowReportSnapshot report)
    {
        List<Candidate> found = [new("report", "report", null, "run.json")];
        foreach (NodeResult node in report.Report.Nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.Stdout))
            {
                found.Add(new("stdout:" + node.Id, "stdout", node.Id, node.Stdout));
            }

            if (!string.IsNullOrWhiteSpace(node.Stderr))
            {
                found.Add(new("stderr:" + node.Id, "stderr", node.Id, node.Stderr));
            }

            for (int i = 0; i < node.DeclaredArtifacts.Count; i++)
            {
                WorkflowArtifactEvidence evidence = node.DeclaredArtifacts[i];
                // Only materializations under the owned results tree. An arbitrary
                // build/source-tree output is not authorized for remote download.
                if (evidence.SnapshotPath is { } snapshot)
                {
                    found.Add(new($"snapshot:{node.Id}:{i}", "snapshot", node.Id, snapshot));
                }
                else if (evidence.Role == "output" && IsLexicallyWithin(report.RunDirectory, evidence.Path))
                {
                    found.Add(new($"output:{node.Id}:{i}", "output", node.Id, evidence.Path));
                }
            }
        }
        return found.Count > MaximumCatalogEntries ? throw new ApplicationRefusal("resource_limit", "Too many artifact candidates.") : found;
    }

    private static bool IsLexicallyWithin(string runDirectory, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string full = Path.GetFullPath(Path.IsPathFullyQualified(candidate)
            ? candidate : Path.Combine(runDirectory, candidate));
        string relative = Path.GetRelativePath(runDirectory, full);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string WithinResults(WorkspaceReadPaths paths, string runDirectory, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new ApplicationRefusal("path_not_allowed", "Empty artifact path.");
        }

        string path = Path.GetFullPath(Path.IsPathFullyQualified(candidate)
            ? candidate : Path.Combine(runDirectory, candidate));
        // Preliminary workspace validation; final authority is the descriptor-
        // relative, no-follow open anchored to the workspace handle.
        // Do not substitute the resolved result into the lexical results boundary.
        string relative = Path.GetRelativePath(runDirectory, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ApplicationRefusal("path_not_allowed", "Artifact escapes its results directory.");
        }

        _ = paths.Require(path);
        return path;
    }

}
