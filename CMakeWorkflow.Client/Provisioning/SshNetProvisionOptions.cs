namespace CMakeWorkflow.Client;

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
    bool AlreadyActive, string[] ToolProbes, SshNetProvisionFile[] Files);

public sealed record SshNetProvisionReceipt(
    string ApprovalSha256, string ReleaseDirectory, string Executable,
    bool AlreadyActive);
