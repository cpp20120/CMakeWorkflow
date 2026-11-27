using System.Collections;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.CMake.Artifacts;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Toolchains;
using CMakeWorkflow.Core.Persistence;

namespace CMakeWorkflow.CMake.Execution;

internal sealed class WorkflowActionCache(CasActions actions, string key, string output)
{
    internal string Key => key;

    internal Task<bool> TryRestoreAsync(CancellationToken ct) => actions.TryRestoreAsync(key, output, ct);

    internal Task PublishAsync(string digest, CancellationToken ct) => actions.PublishAsync(key, output, digest, ct);

    internal static WorkflowActionCache Create(NodeResult result, string destination,
        IReadOnlyDictionary<string, string> overrides, string? sourceSnapshotId)
    {
        string? directory = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_ARTIFACT_CAS");
        if (directory is null || !Path.IsPathFullyQualified(directory))
        {
            throw new InvalidOperationException("Set CMAKEWORKFLOW_ARTIFACT_CAS to an absolute local store path.");
        }

        string host = EnvironmentProvenance.HostId()
                      ?? throw new InvalidOperationException("An action cache requires a stable Host identity.");
        Dictionary<string, string> environment = new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = (string)entry.Value!;
        }
        foreach ((string name, string value) in overrides)
        {
            environment[name] = value;
        }

        // The cache stores the digest only. Length prefixes separate values even
        // when arguments, paths or environment values contain delimiters.
        using MemoryStream bytes = new();
        using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(sourceSnapshotId is null ? "cmakeworkflow-action-v1" : "cmakeworkflow-action-source-v1");
        if (sourceSnapshotId is not null)
        {
            writer.Write(sourceSnapshotId);
        }
        writer.Write(host);
        writer.Write(result.Executable!);
        writer.Write(result.ExecutableSha256!);
        writer.Write(result.WorkingDirectory);
        writer.Write(result.ToolchainFingerprint ?? "");
        writer.Write(result.Configuration ?? "");
        writer.Write(destination);
        writer.Write(result.Arguments.Count);
        foreach (string argument in result.Arguments)
        {
            writer.Write(argument);
        }

        writer.Write(result.DeclaredArtifacts.Count);
        foreach (WorkflowArtifactEvidence input in result.DeclaredArtifacts)
        {
            writer.Write(input.Path);
            writer.Write(input.Sha256);
        }

        writer.Write(environment.Count);
        foreach ((string name, string value) in environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            writer.Write(OperatingSystem.IsWindows() ? name.ToUpperInvariant() : name);
            writer.Write(value);
        }

        writer.Flush();
        string key = Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
        return new(new CasActions(new ArtifactStoreFiles(directory), sourceSnapshotId is null ? 1 : 2), key, destination);
    }
}
