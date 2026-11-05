#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

/// <summary>
/// A direct SSH.NET connection to a persistent Linux Host, without spawning OpenSSH.
/// The caller must pin the server host key and provide a private-key file.
/// </summary>
public sealed record SshNetHostOptions(
    string Host, string Username, string PrivateKeyPath, string HostKeySha256,
    string Endpoint, string Workspace)
{
    public int Port { get; init; } = 22;
    public string RemoteExecutable { get; init; } = "cmakeworkflow";
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
