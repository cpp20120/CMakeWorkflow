using System.Net.Sockets;

namespace CMakeWorkflow.Client;

public sealed partial class CMakeWorkflowClient
{
    /// <summary>
    /// Launch a private Host on one SSH.NET exec channel. The handshake is the
    /// readiness check; no socket or daemon is required on the remote machine.
    /// Disposing this client closes the channel and stops the private Host,
    /// including any unfinished workflow. Use ConnectSshNetAsync for work that
    /// must outlive the client. Uncertain starts are never retried automatically.
    /// </summary>
    public static async Task<CMakeWorkflowClient> LaunchSshNetAsync(
        SshNetLaunchOptions options, CancellationToken ct = default)
    {
        SshNetConnection.Validate(options);
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.ConnectionTimeout);
        SshNetConnection? connection = null;
        try
        {
            connection = await SshNetConnection.LaunchAsync(options, deadline.Token);
            return await InitializeAsync(connection.Input, options.Workspace, null, null,
                deadline.Token, connection.Output, connection);
        }
        catch (Exception error)
        {
            if (connection is not null) await connection.DisposeAsync();
            ct.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
                throw new TimeoutException("SSH.NET private Host startup timed out.", error);
            if (error is Renci.SshNet.Common.SshException or SocketException or IOException)
                throw new SshConnectionException(
                    "SSH.NET private Host startup failed. Check the pinned key, credentials and remote executable.",
                    connection?.Diagnostics ?? "", connection?.ExitCode, error);
            throw;
        }
    }
}
