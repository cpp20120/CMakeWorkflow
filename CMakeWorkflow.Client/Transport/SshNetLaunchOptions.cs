namespace CMakeWorkflow.Client.Transport;

/// <summary>
/// Launch a private, single-session Linux Host on an SSH.NET exec channel.
/// Unlike a persistent Host connection, disposing this client stops its Host
/// and cancels any work still running there. It does not deploy the executable.
/// </summary>
public sealed record SshNetLaunchOptions(
    string Host, string Username, string PrivateKeyPath, string HostKeySha256,
    string Workspace)
{
    public int Port { get; init; } = 22;
    public string RemoteExecutable { get; init; } = "cmakeworkflow";
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
