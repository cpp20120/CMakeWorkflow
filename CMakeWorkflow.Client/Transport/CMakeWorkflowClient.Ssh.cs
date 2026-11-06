using System.ComponentModel;
using System.Diagnostics;
using CMakeWorkflow.Client.Transport;
using StreamJsonRpc;

namespace CMakeWorkflow.Client;

public sealed partial class CMakeWorkflowClient
{
    /// <summary>Connects through OpenSSH to a persistent Host on the remote machine.</summary>
    /// <remarks>
    /// Owns the SSH process/channel, not the persistent Host or accepted operations. Disposal closes SSH only.
    /// No automatic reconnect or request retry occurs. Remote paths are sent unchanged; initialization must match the workspace.
    /// </remarks>
    /// <param name="options">Remote Host credentials, workspace, executable/channel settings and connection deadline.</param>
    /// <param name="ct">Cancels connection/initialization only; not a durable operation stop token after the client is returned.</param>
    /// <returns>Initialized client; the caller owns asynchronous disposal of its transport resources.</returns>
    public static async Task<CMakeWorkflowClient> ConnectSshAsync(SshHostOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ProcessStartInfo start = SshCommand.Create(options);
        ct.ThrowIfCancellationRequested();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.ConnectionTimeout);
        SshProcessConnection? ssh = null;
        try
        {

#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
            ssh = new SshProcessConnection(start);
#pragma warning restore CA2000

            return await InitializeAsync(ssh.Input, options.Workspace, null, null, timeout.Token, ssh.Output, ssh);
        }
        catch (Exception error)
        {
            if (ssh is not null)
            {
                await ssh.DisposeAsync();
            }

            ct.ThrowIfCancellationRequested();
            if (error is OperationCanceledException && timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"SSH Host initialization timed out. {ssh?.Diagnostics}", error);
            }

            if (error is IOException or ConnectionLostException or Win32Exception)
            {
                throw new SshConnectionException("Cannot connect to the SSH Host. Check SSH authentication, the remote executable and endpoint.",
                    ssh?.Diagnostics ?? "", ssh?.ExitCode, error);
            }

            throw;
        }
    }

    /// <summary>Bounded tail of SSH stderr (at most 16 Ki characters); empty for local connections.</summary>
    public string SshDiagnostics => sshConnection?.Diagnostics ?? "";
}
