using System.Net.Sockets;
using CMakeWorkflow.Client.Transport;

namespace CMakeWorkflow.Client;

public sealed partial class CMakeWorkflowClient
{
    /// <summary>Launches a private Host on a pinned-key SSH.NET exec channel.</summary>
    /// <remarks>
    /// The initialization handshake establishes readiness; no daemon socket is required.
    /// The channel owns the private Host lifetime: disposal/disconnect cancels active execution and drains Host cleanup.
    /// Durable records may require recovery afterward. No uncertain workflow request is automatically retried.
    /// </remarks>
    /// <param name="options">Remote Host credentials, workspace, executable/channel settings and connection deadline.</param>
    /// <param name="ct">Cancels connection/initialization only; not a durable operation stop token after the client is returned.</param>
    /// <returns>Initialized client; the caller owns asynchronous disposal of its transport resources.</returns>
    public static async Task<CMakeWorkflowClient> LaunchSshNetAsync(
        SshNetLaunchOptions options, CancellationToken ct = default)
    {
        _ = SshNetConnection.Validate(options);
        ct.ThrowIfCancellationRequested();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.ConnectionTimeout);
        SshNetConnection? connection = null;
        try
        {

#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
            connection = await SshNetConnection.LaunchAsync(options, deadline.Token);
#pragma warning restore CA2000

            return await InitializeAsync(connection.Input, options.Workspace, null, null,
                deadline.Token, connection.Output, connection);
        }
        catch (Exception error)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            ct.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
            {
                throw new TimeoutException("SSH.NET private Host startup timed out.", error);
            }

            if (error is Renci.SshNet.Common.SshException or SocketException or IOException)
            {
                throw new SshConnectionException(
                    "SSH.NET private Host startup failed. Check the pinned key, credentials and remote executable.",
                    connection?.Diagnostics ?? "", connection?.ExitCode, error);
            }

            throw;
        }
    }
}
