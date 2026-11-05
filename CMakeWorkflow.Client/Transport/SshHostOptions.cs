#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

/// <summary>Connects to an independently running Linux Host via the installed OpenSSH client.</summary>
/// <remarks>All remote paths refer to the remote filesystem. No files are synchronized.</remarks>
public sealed record SshHostOptions(string Destination, string Endpoint, string Workspace)
{
    public string RemoteExecutable { get; init; } = "cmakeworkflow";
    public string SshExecutable { get; init; } = "ssh";
    /// <summary>Local initialization deadline, followed by at most a two-second process cleanup grace.</summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
