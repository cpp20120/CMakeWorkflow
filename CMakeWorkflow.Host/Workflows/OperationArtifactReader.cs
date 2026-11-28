using System.Security.Cryptography;
using CMakeWorkflow.CMake.Artifacts;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Host.Execution;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Storage.Documents;

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

    private sealed record Candidate(string Id, string Kind, string? NodeId, string Path, string? ExpectedDigest = null);

    /// <summary>Reads and verifies a finalized CMake report against ledger identity and terminal state.</summary>
    /// <remarks>
    /// Refuses unfinalized/wrong-domain operations and reports over 16 MiB. The opened file bytes must match
    /// the committed report SHA-256; live or uncertain run.json files are not exposed as terminal evidence.
    /// Cancellation affects this read only and does not alter execution or finalization.
    /// </remarks>
    internal static async Task<WorkflowReportSnapshot> ReportAsync(
        WorkspaceReadPaths paths, string operationId, CancellationToken ct) =>
        (await ReadReportAsync(paths, operationId, ct)).Report;

    private static async Task<(WorkflowReportSnapshot Report, NativeReportReference? Native)> ReadReportAsync(
        WorkspaceReadPaths paths, string operationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        }

        _ = paths.Database(WorkflowLedgerPath.Resolve(paths.WorkspaceDirectory));
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
        if (store.UsesNativeWorkflowReports && store.ReadBinaryInput(operationId) is not null)
        {
            NativeReportReference reference = store.ReadNativeReportReference(operationId)
                ?? throw new ApplicationRefusal("report_changed", "Native terminal report reference is absent.");
            NativeExecutionReport evidence = await store.ReadNativeReportAsync(accepted, reference, ct);
            return evidence.Report.Status != finalized.State || evidence.ExitCode != finalized.ExitCode ||
                   !string.Equals(evidence.RunDirectory, directory, StringComparison.Ordinal)
                ? throw new ApplicationRefusal("report_changed", "Native terminal report failed verification.")
                : (new WorkflowReportSnapshot(1, directory, finalized.ReportSha256, evidence.Report), reference);
        }

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
            : (report, null);
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

        (WorkflowReportSnapshot report, NativeReportReference? native) = await ReadReportAsync(paths, operationId, ct);
        List<OperationArtifactInfo> result = [];
        if (native is not null)
        {
            byte[] projection = VerifiedReportProjection(report);
            result.Add(new("report", "report", null, "run.json", projection.LongLength,
                report.Report.FinishedAt.GetValueOrDefault().UtcTicks, report.Revision));
            result.Add(new("native-report", "native-report", null, "execution-report.capnp", native.StoredLength,
                report.Report.FinishedAt.GetValueOrDefault().UtcTicks, native.Blob.Value));
        }

        foreach (Candidate candidate in Candidates(report, native is null))
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
            if (candidate.ExpectedDigest is { } digest && !string.Equals(sha, digest, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApplicationRefusal("artifact_changed", "A saved executable no longer matches the report digest.");
            }

            result.Add(new(candidate.Id, candidate.Kind, candidate.NodeId,
                Path.GetRelativePath(report.RunDirectory, path).Replace('\\', '/'), size, stamp, sha));
            if (result.Count > MaximumCatalogEntries)
            {
                throw new ApplicationRefusal("resource_limit", "Artifact catalog has too many files.");
            }
        }
        return new(operationId, report.Revision, result)
        {
            NativeReportDocumentId = native?.Document.Value,
            NativeReportBlobId = native?.Blob.Value
        };
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

        (WorkflowReportSnapshot report, NativeReportReference? native) = await ReadReportAsync(paths, request.OperationId, ct);
        if (native is not null && request.ArtifactId == "report")
        {
            byte[] projection = VerifiedReportProjection(report);
            long ticks = report.Report.FinishedAt.GetValueOrDefault().UtcTicks;
            if (projection.LongLength != request.ExpectedSize || ticks != request.ExpectedLastWriteUtcTicks)
            {
                throw new ApplicationRefusal("artifact_changed", "Report projection changed since catalog discovery.");
            }

            int offset = checked((int)request.Offset);
            int length = Math.Min(request.Count, projection.Length - offset);
            return new("report", request.Offset, projection.LongLength, ticks,
                projection.AsSpan(offset, length).ToArray(), offset + length == projection.Length);
        }

        if (native is not null && request.ArtifactId == "native-report")
        {
            long ticks = report.Report.FinishedAt.GetValueOrDefault().UtcTicks;
            if (native.StoredLength != request.ExpectedSize || ticks != request.ExpectedLastWriteUtcTicks)
            {
                throw new ApplicationRefusal("artifact_changed", "Native report changed since catalog discovery.");
            }

            using DurableAcceptanceStore store = new(paths.WorkspaceDirectory, readOnly: true);
            using DocumentSession session = await store.OpenNativeReportDocumentAsync(native, ct);
            int offset = checked((int)request.Offset);
            int length = (int)Math.Min(request.Count, native.StoredLength - offset);
            byte[] content = session.InspectStoredBytes(frame => frame.Slice(offset, length).ToArray());
            return new("native-report", request.Offset, native.StoredLength, ticks, content,
                request.Offset + length == native.StoredLength);
        }

        // The artifact ID is opaque; it selects a known report-bound file, never a path.
        Candidate candidate = Candidates(report, native is null).FirstOrDefault(c => c.Id == request.ArtifactId)
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

    private static byte[] VerifiedReportProjection(WorkflowReportSnapshot report)
    {
        byte[] projection = DurableAcceptanceStore.ProjectWorkflowReport(report.Report);
        return Convert.ToHexString(SHA256.HashData(projection)) == report.Revision
            ? projection : throw new ApplicationRefusal("report_changed", "Native report projection differs from finalization.");
    }

    internal static async Task VerifyTerminalArtifactsAsync(WorkspaceReadPaths paths, NativeExecutionReport terminal,
        CancellationToken ct)
    {
        WorkflowReportSnapshot report = new(1, terminal.RunDirectory, "", terminal.Report);
        foreach (Candidate candidate in Candidates(report, includePhysicalReport: false))
        {
            if (candidate.ExpectedDigest is not { } expected || candidate.Kind is not ("output" or "snapshot" or "trace"))
            {
                continue;
            }

            string path = WithinResults(paths, terminal.RunDirectory, candidate.Path);
            await using FileStream stream = OperationArtifactFile.OpenRead(paths.WorkspaceDirectory, path);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Required terminal artifact differs from the runner evidence.");
            }
        }
    }

    private static List<Candidate> Candidates(WorkflowReportSnapshot report, bool includePhysicalReport)
    {
        List<Candidate> found = includePhysicalReport ? [new("report", "report", null, "run.json")] : [];
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

            if (node.Artifact is { Executable: { } executable } artifact && IsLexicallyWithin(report.RunDirectory, executable))
            {
                found.Add(new("binary:" + node.Id, "binary", node.Id, executable, artifact.Sha256));
            }

            if (node.Kind is "profile" or "profile_snapshot"
                && node.Outputs.TryGetValue("rawTrace", out string? trace)
                && IsLexicallyWithin(report.RunDirectory, trace)
                && !string.Equals(Path.GetExtension(trace), ".trace", StringComparison.OrdinalIgnoreCase))
            {
                string? hash = node.Profile is { } profile && profile.RawTrace == trace ? profile.RawTraceSha256 : null;
                found.Add(new("trace:" + node.Id, "trace", node.Id, trace, hash));
            }

            for (int i = 0; i < node.DeclaredArtifacts.Count; i++)
            {
                WorkflowArtifactEvidence evidence = node.DeclaredArtifacts[i];
                // Only materializations under the owned results tree. An arbitrary
                // build/source-tree output is not authorized for remote download.
                if (evidence.SnapshotPath is { } snapshot)
                {
                    found.Add(new($"snapshot:{node.Id}:{i}", "snapshot", node.Id, snapshot,
                        includePhysicalReport ? null : evidence.Sha256));
                }
                else if (evidence.Role == "output" && IsLexicallyWithin(report.RunDirectory, evidence.Path))
                {
                    string relative = Path.GetRelativePath(report.RunDirectory, evidence.Path);
                    bool bound = !includePhysicalReport || (report.Report.SourceSnapshotId is not null &&
                        relative.StartsWith(SourceBoundArtifactLayout.OutputDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                    string? digest = bound ? evidence.Sha256 : null;
                    found.Add(new($"output:{node.Id}:{i}", "output", node.Id, evidence.Path, digest));
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
