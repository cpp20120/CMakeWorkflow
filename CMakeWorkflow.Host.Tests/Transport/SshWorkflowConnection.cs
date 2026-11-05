using CMakeWorkflow.Client;

// Test-only OpenSSH replacement: real shell, bridge and persistent Host, no sshd/credentials.
internal sealed class SshWorkflowConnection : IDisposable
{
    private readonly SshTestPrograms files = new();
    internal SshHostOptions Options { get; }
    internal string LostReply => Path.Combine(files.Root, "lost-reply");

    internal SshWorkflowConnection(string endpoint, string workspace, bool dropStartReply = false)
    {
        string assembly = SshTestPrograms.Quote(typeof(SshWorkflowConnection).Assembly.Location);
        string command = dropStartReply
            ? $"exec dotnet {assembly} --ssh-drop-start-worker {SshTestPrograms.Quote(endpoint)} {SshTestPrograms.Quote(LostReply)}"
            : $"shift 2\nexec dotnet {assembly} --bridge-worker \"$@\"";
        string remote = files.Script("remote host ' executable", "test \"$1\" = host\ntest \"$2\" = bridge\n" + command);
        Options = new("remote-workflow-fixture", endpoint, workspace)
        {
            RemoteExecutable = remote,
            SshExecutable = files.Ssh(),
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        };
    }

    internal Task<CMakeWorkflowClient> ConnectAsync(CancellationToken ct) =>
        CMakeWorkflowClient.ConnectSshAsync(Options, ct);

    internal void AssertExited() => files.AssertExited();
    public void Dispose() => files.Dispose();
}
