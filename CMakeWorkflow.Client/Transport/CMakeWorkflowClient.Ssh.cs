using System.ComponentModel;
using StreamJsonRpc;

namespace CMakeWorkflow.Client;

public sealed partial class CMakeWorkflowClient
{
    /// <summary>Connects through SSH to a persistent Linux Host. Disposal closes SSH, not accepted operations.</summary>
    /// <remarks>Does not retry connection or execution requests. Remote paths are never resolved locally.</remarks>
    public static async Task<CMakeWorkflowClient> ConnectSshAsync(SshHostOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var start = SshCommand.Create(options);
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.ConnectionTimeout);
        SshProcessConnection? ssh = null;
        try
        {
            ssh = new SshProcessConnection(start);
            return await InitializeAsync(ssh.Input, options.Workspace, null, null, timeout.Token, ssh.Output, ssh);
        }
        catch (Exception error)
        {
            if (ssh is not null) await ssh.DisposeAsync();
            ct.ThrowIfCancellationRequested();
            if (error is OperationCanceledException && timeout.IsCancellationRequested)
                throw new TimeoutException($"SSH Host initialization timed out. {ssh?.Diagnostics}", error);
            if (error is IOException or ConnectionLostException or Win32Exception)
                throw new SshConnectionException("Cannot connect to the SSH Host. Check SSH authentication, the remote executable and endpoint.",
                    ssh?.Diagnostics ?? "", ssh?.ExitCode, error);
            throw;
        }
    }

    /// <summary>Bounded tail of SSH stderr (at most 16 Ki characters); empty for local connections.</summary>
    public string SshDiagnostics => sshConnection?.Diagnostics ?? "";
}
