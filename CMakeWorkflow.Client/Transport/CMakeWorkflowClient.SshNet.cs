using System.Net.Sockets;
using CMakeWorkflow.Client.Transport;

namespace CMakeWorkflow.Client;

public sealed partial class CMakeWorkflowClient
{
    /// <summary>Connects through a pinned-key SSH.NET channel to an existing persistent Host.</summary>
    /// <remarks>
    /// Owns the SSH channel, not accepted operations or the persistent Host. Disposal disconnects without durable cancellation.
    /// A dropped channel never retries an uncertain start; reconnect and find its saved ClientRequestId in the same workspace.
    /// </remarks>
    /// <param name="options">Remote Host credentials, workspace, executable/channel settings and connection deadline.</param>
    /// <param name="ct">Cancels connection/initialization only; not a durable operation stop token after the client is returned.</param>
    /// <returns>Initialized client; the caller owns asynchronous disposal of its transport resources.</returns>
    public static async Task<CMakeWorkflowClient> ConnectSshNetAsync(
        SshNetHostOptions options, CancellationToken ct = default)
    {
        _ = SshNetConnection.Validate(options);
        ct.ThrowIfCancellationRequested();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.ConnectionTimeout);
        SshNetConnection? connection = null;
        try
        {

#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
            connection = await SshNetConnection.ConnectAsync(options, deadline.Token);
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
                throw new TimeoutException("SSH.NET Host initialization timed out.", error);
            }

            if (error is Renci.SshNet.Common.SshException or SocketException or IOException)
            {
                throw new SshConnectionException(
                    "SSH.NET Host connection failed. Check pinned host key, credentials and remote bridge.",
                    connection?.Diagnostics ?? "", connection?.ExitCode, error);
            }

            throw;
        }
    }
}
