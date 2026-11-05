using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CMakeWorkflow.Host;

// Domain-owned input validation, request identity and report interpretation.
internal static class ProcessWorkflowDomain
{
    internal static DurableImmutableRequest Admission(ExecutionStartRequest request)
    {
        if (request.Jobs is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(request.Jobs));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            "execution/start:process:v1", request.Manifest, request.ManifestSha256.ToUpperInvariant(),
            request.Jobs.ToString(System.Globalization.CultureInfo.InvariantCulture), request.ResultsDirectory))));
        return new(request.ClientRequestId, request.Manifest, request.ManifestSha256,
            request.ResultsDirectory, digest, "process");
    }

    internal static ProcessWorkflowManifest ReadPlan(byte[] snapshot, WorkspaceReadPaths paths)
    {
        if (snapshot.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Process manifest exceeds 2 MiB.");
        var manifest = JsonSerializer.Deserialize(snapshot, ProcessWorkflowJson.Default.ProcessWorkflowManifest)
                       ?? throw new InvalidDataException("Invalid process execution manifest.");
        if (manifest.Domain != "process" || manifest.SchemaVersion != 1 ||
            manifest.Nodes is not { Count: > 0 and <= 128 })
            throw new InvalidDataException("Unsupported process manifest domain/schema/size.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in manifest.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || node.Id.Length > 128 || !ids.Add(node.Id) ||
                string.IsNullOrWhiteSpace(node.Executable) || node.Executable.Length > 4096 ||
                node.Arguments is { Count: > 256 } || node.Dependencies is { Count: > 128 } ||
                node.Resources is { Count: > 128 } || node.TimeoutSeconds is < 1 or > 86400)
                throw new InvalidDataException("Invalid or duplicate process workflow node.");
            if (node.Arguments?.Any(a => a is null) == true ||
                node.Resources?.Any(x => string.IsNullOrWhiteSpace(x)) == true ||
                node.Dependencies?.Any(x => string.IsNullOrWhiteSpace(x)) == true ||
                node.Environment?.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value is null) == true)
                throw new InvalidDataException("Invalid process node arguments, resources or environment.");
            if (node.WorkingDirectory is not null)
            {
                var wd = Path.IsPathFullyQualified(node.WorkingDirectory)
                    ? node.WorkingDirectory : Path.Combine(paths.WorkspaceDirectory, node.WorkingDirectory);
                paths.Require(wd);
                if (!Directory.Exists(wd)) throw new DirectoryNotFoundException(wd);
            }
        }

        var visited = new Dictionary<string, int>(StringComparer.Ordinal);
        var table = manifest.Nodes.ToDictionary(x => x.Id, StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.GetValueOrDefault(id) == 2) return;
            if (visited.GetValueOrDefault(id) == 1)
                throw new InvalidDataException("Process workflow dependency cycle.");
            visited[id] = 1;
            foreach (var dep in table[id].Dependencies ?? [])
            {
                if (!table.ContainsKey(dep)) throw new InvalidDataException("Unknown process dependency: " + dep);
                Visit(dep);
            }
            visited[id] = 2;
        }
        foreach (var node in manifest.Nodes) Visit(node.Id);
        return manifest;
    }
}

internal sealed class ProcessCheckpointEvidenceCodec : IOperationCheckpointEvidenceCodec
{
    internal static ProcessCheckpointEvidenceCodec Instance { get; } = new();

    private static ProcessOperationReport Parse(byte[] bytes) =>
        JsonSerializer.Deserialize(bytes, ProcessWorkflowJson.Default.ProcessOperationReport)
        ?? throw new InvalidDataException("Invalid process operation checkpoint.");

    public OperationCheckpointEvidence ForDispatch(byte[] checkpointBytes)
    {
        var report = Parse(checkpointBytes);
        if (report.Domain != "process" || report.SchemaVersion != 1 || report.Status != "running" ||
            !report.Nodes.Any(n => n.Status == "running"))
            throw new InvalidDataException("Process checkpoint has no admitted dispatch.");
        return new(report.SnapshotSha256);
    }

    public OperationCheckpointEvidence ForResume(byte[] _) =>
        throw new InvalidOperationException("process_domain_resume_not_supported");

    public OperationCheckpointEvidence ForFinalization(byte[] checkpointBytes, int exitCode)
    {
        var report = Parse(checkpointBytes);
        if (report.Domain != "process" || report.SchemaVersion != 1 || report.FinishedAt is null ||
            report.Status is not ("succeeded" or "failed" or "canceled") ||
            report.Nodes.Any(n => n.Status is "pending" or "running" or "outcome_unknown") ||
            (report.Status == "succeeded" ? 0 : report.Status == "canceled" ? 130 : 1) != exitCode)
            throw new InvalidOperationException("process_operation_report_not_terminal");
        return new(report.SnapshotSha256, report.Status);
    }
}
