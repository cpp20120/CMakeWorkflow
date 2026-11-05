using System.ComponentModel;
using System.Diagnostics;
using StreamJsonRpc;

namespace CMakeWorkflow.Client;

public sealed partial class CMakeWorkflowClient
{
    /// <summary>Connects through SSH to a persistent Linux Host. Disposal closes SSH, not accepted operations.</summary>
    /// <remarks>Does not retry connection or execution requests. Remote paths are never resolved locally.</remarks>
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
