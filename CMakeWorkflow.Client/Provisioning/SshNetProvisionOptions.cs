namespace CMakeWorkflow.Client.Provisioning;

/// <summary>Explicit, user-scoped Linux Host installation over authenticated SSH.NET.</summary>
public sealed record SshNetProvisionOptions(
    string Host, string Username, string PrivateKeyPath, string HostKeySha256,
    string Workspace, string PublishDirectory, string InstallRoot, string RuntimeIdentifier)
{
    public int Port { get; init; } = 22;
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed record SshNetProvisionFile(string RelativePath, string Sha256, long Size);

/// <summary>Read-only plan. Approval binds host identity, target, RID and every local file hash.</summary>
public sealed record SshNetProvisionPlan(
    string ApprovalSha256, string RuntimeIdentifier, string RemoteArchitecture,
    string InstallRoot, string ReleaseDirectory, string Executable,
    bool AlreadyActive,
#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] ToolProbes
#pragma warning restore CA1819
,
#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
SshNetProvisionFile[] Files
#pragma warning restore CA1819
);

public sealed record SshNetProvisionReceipt(
    string ApprovalSha256, string ReleaseDirectory, string Executable,
    bool AlreadyActive);
