using System.Security.Cryptography;

namespace CMakeWorkflow.Host;

// Read-only views over an operation's durably finalized results tree. This is
// intentionally NOT a workspace filesystem API or an artifact upload service.
internal static class OperationArtifactReader
{
    internal const int MaximumChunkBytes = 64 * 1024;
    private const int MaximumCatalogEntries = 512;

    private sealed record Candidate(string Id, string Kind, string? NodeId, string Path);

    internal static async Task<WorkflowReportSnapshot> ReportAsync(
        WorkspaceReadPaths paths, string operationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        paths.Database(Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "operations.sqlite"));
        using var store = new DurableAcceptanceStore(paths.WorkspaceDirectory, readOnly: true);
        var accepted = store.Get(operationId)
                       ?? throw new KeyNotFoundException("Unknown operation.");
        var finalized = store.GetFinalization(operationId)
                        ?? throw new ApplicationRefusal("not_finalized", "No durable terminal report yet.");
        var directory = paths.Require(finalized.RunDirectory);
        var reportPath = WithinResults(paths, directory, "run.json");
        await using var reportFile = OperationArtifactFile.OpenRead(paths.WorkspaceDirectory, reportPath);
        if (reportFile.Length > 16 * 1024 * 1024)
            throw new ApplicationRefusal("resource_limit", "Terminal report exceeds 16 MiB.");
        using var buffer = new MemoryStream();
        await reportFile.CopyToAsync(buffer, ct);
        var report = WorkflowQueries.ReportFromBytes(directory, buffer.ToArray());
        if (!string.Equals(report.Revision, finalized.ReportSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(report.Report.Status, finalized.State, StringComparison.Ordinal) ||
            !string.Equals(report.Report.ManifestHash, accepted.ManifestSha256,
                StringComparison.OrdinalIgnoreCase) || report.Report.FinishedAt is null)
            throw new ApplicationRefusal("report_changed", "The durable report failed verification.");
        return report;
    }

    internal static async Task<OperationArtifactList> ListAsync(
        WorkspaceReadPaths paths, string operationId, CancellationToken ct)
    {
        var report = await ReportAsync(paths, operationId, ct);
        var result = new List<OperationArtifactInfo>();
        foreach (var candidate in Candidates(report))
        {
            ct.ThrowIfCancellationRequested();
            var path = WithinResults(paths, report.RunDirectory, candidate.Path);
            FileStream file;
            try { file = OperationArtifactFile.OpenRead(paths.WorkspaceDirectory, path); }
            catch (FileNotFoundException) when (candidate.Id != "report")
            {
                continue; // A node not dispatched has no logs.
            }
            catch (DirectoryNotFoundException) when (candidate.Id != "report")
            {
                continue;
            }
            await using var owned = file;
            var size = file.Length;
            var stamp = File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks;
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (file.Length != size || File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks != stamp)
                throw new ApplicationRefusal("artifact_changed", "Artifact changed during catalog creation.");
            result.Add(new(candidate.Id, candidate.Kind, candidate.NodeId,
                Path.GetRelativePath(report.RunDirectory, path).Replace('\\', '/'), size, stamp, sha));
            if (result.Count > MaximumCatalogEntries)
                throw new ApplicationRefusal("resource_limit", "Artifact catalog has too many files.");
        }
        return new(operationId, report.Revision, result);
    }

    internal static async Task<OperationArtifactChunk> ReadChunkAsync(
        WorkspaceReadPaths paths, OperationArtifactChunkRequest request, CancellationToken ct)
    {
        if (request.Count is < 1 or > MaximumChunkBytes || request.Offset < 0 ||
            request.ExpectedSize < 0 || request.ExpectedLastWriteUtcTicks < 0 ||
            request.Offset > request.ExpectedSize)
            throw new ArgumentException("Invalid artifact range or expected identity.");

        var report = await ReportAsync(paths, request.OperationId, ct);
        // The artifact ID is opaque; it selects a known report-bound file, never a path.
        var candidate = Candidates(report).FirstOrDefault(c => c.Id == request.ArtifactId)
                        ?? throw new ApplicationRefusal("not_found", "Unknown operation artifact.");
        var path = WithinResults(paths, report.RunDirectory, candidate.Path);
        await using var file = OperationArtifactFile.OpenRead(paths.WorkspaceDirectory, path);
        var size = file.Length;
        var stamp = File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks;
        if (size != request.ExpectedSize || stamp != request.ExpectedLastWriteUtcTicks)
            throw new ApplicationRefusal("artifact_changed", "The artifact changed since catalog discovery.");

        var count = (int)Math.Min(request.Count, size - request.Offset);
        var bytes = new byte[count];
        file.Seek(request.Offset, SeekOrigin.Begin);
        await file.ReadExactlyAsync(bytes, ct);
        if (file.Length != size || File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks != stamp)
            throw new ApplicationRefusal("artifact_changed", "The artifact changed during transfer.");
        return new(candidate.Id, request.Offset, size, stamp, bytes, request.Offset + count == size);
    }

    private static List<Candidate> Candidates(WorkflowReportSnapshot report)
    {
        var found = new List<Candidate> { new("report", "report", null, "run.json") };
        foreach (var node in report.Report.Nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.Stdout))
                found.Add(new("stdout:" + node.Id, "stdout", node.Id, node.Stdout));
            if (!string.IsNullOrWhiteSpace(node.Stderr))
                found.Add(new("stderr:" + node.Id, "stderr", node.Id, node.Stderr));
            for (var i = 0; i < node.DeclaredArtifacts.Count; i++)
            {
                var evidence = node.DeclaredArtifacts[i];
                // Only materializations under the owned results tree. An arbitrary
                // build/source-tree output is not authorized for remote download.
                if (evidence.SnapshotPath is { } snapshot)
                    found.Add(new($"snapshot:{node.Id}:{i}", "snapshot", node.Id, snapshot));
                else if (evidence.Role == "output" && IsLexicallyWithin(report.RunDirectory, evidence.Path))
                    found.Add(new($"output:{node.Id}:{i}", "output", node.Id, evidence.Path));
            }
        }
        if (found.Count > MaximumCatalogEntries)
            throw new ApplicationRefusal("resource_limit", "Too many artifact candidates.");
        return found;
    }

    private static bool IsLexicallyWithin(string runDirectory, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var full = Path.GetFullPath(Path.IsPathFullyQualified(candidate)
            ? candidate : Path.Combine(runDirectory, candidate));
        var relative = Path.GetRelativePath(runDirectory, full);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string WithinResults(WorkspaceReadPaths paths, string runDirectory, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            throw new ApplicationRefusal("path_not_allowed", "Empty artifact path.");
        var path = Path.GetFullPath(Path.IsPathFullyQualified(candidate)
            ? candidate : Path.Combine(runDirectory, candidate));
        // Preliminary workspace validation; final authority is the descriptor-
        // relative, no-follow open anchored to the workspace handle.
        // Do not substitute the resolved result into the lexical results boundary.
        var relative = Path.GetRelativePath(runDirectory, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ApplicationRefusal("path_not_allowed", "Artifact escapes its results directory.");
        paths.Require(path);
        return path;
    }

}
