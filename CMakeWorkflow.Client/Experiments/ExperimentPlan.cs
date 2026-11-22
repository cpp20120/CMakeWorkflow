using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.Client.Transport;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Client.Experiments;

/// <summary>The SSH connection and workspace bound to an experiment plan.</summary>
public sealed record ExperimentHost(string Destination, string Workspace, string Endpoint, string Executable = "cmakeworkflow",
    string? Username = null, string? KeyFile = null, string? HostKeySha256 = null, int Port = 22)
{
    internal ExperimentHost Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(Executable);
        if (Destination.StartsWith('-') || Destination.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException("Expected an SSH destination or alias.", nameof(Destination));
        }

        bool validAuth = Username is null
            ? KeyFile is null && HostKeySha256 is null && Port == 22
            : !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(KeyFile) &&
              !string.IsNullOrWhiteSpace(HostKeySha256) && Port is >= 1 and <= 65535;
        return validAuth ? this with
        {
            Workspace = RequirePosixPath(Workspace),
            Endpoint = RequirePosixPath(Endpoint),
            KeyFile = KeyFile is null ? null : Path.GetFullPath(KeyFile)
        } : throw new ArgumentException("SSH.NET requires Username, KeyFile and HostKeySha256 together. OpenSSH uses its configured alias.");
    }

    private static string RequirePosixPath(string path) =>
        path is not null && path.StartsWith('/') && !path.Contains('\\', StringComparison.Ordinal) &&
        !path.Any(char.IsControl) && !path.Split('/').Skip(1).Any(part => part is "" or "." or "..")
            ? path : throw new ArgumentException($"Expected a canonical absolute Linux path: {path}");

    /// <summary>Connects to the persistent Host using the selected SSH transport.</summary>
    public async Task<CMakeWorkflowClient> ConnectAsync(CancellationToken ct = default) =>
        Username is null
            ? await CMakeWorkflowClient.ConnectSshAsync(new SshHostOptions(Destination, Endpoint, Workspace)
            {
                RemoteExecutable = Executable
            }, ct)
            : await CMakeWorkflowClient.ConnectSshNetAsync(
                new SshNetHostOptions(Destination, Username, KeyFile!, HostKeySha256!, Endpoint, Workspace)
                {
                    Port = Port,
                    RemoteExecutable = Executable
                }, ct);
}

internal sealed record ExperimentPlan(int SchemaVersion, string Repository, string RequestedRef, string Commit,
    ExperimentHost Host, string SourceSnapshotId, string ManifestSha256, string Manifest,
    IReadOnlyList<ExperimentSourceFile> Files)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExperimentPlacement? Placement { get; init; }

    public ExperimentSourceBundle? Bundle { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DependencyPlanSha256 { get; init; }

    internal static async Task<ExperimentPlan> ReadAsync(string path, string id, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        if (Hash(bytes) != id)
        {
            throw new InvalidDataException("Stored experiment plan does not match its approved SHA-256.");
        }
        ExperimentPlan? plan = JsonSerializer.Deserialize(bytes, ExperimentPlanJson.Default.ExperimentPlan);
        return plan is { SchemaVersion: 1, Bundle: null } or { SchemaVersion: 2, Bundle: not null }
            ? plan : throw new InvalidDataException("Unsupported experiment plan.");
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
