using System.Net.Sockets;

namespace CMakeWorkflow.Client;

public sealed partial class CMakeWorkflowClient
{
    /// <summary>
    /// Connect directly using SSH.NET to the existing Host bridge. The pinned
    /// host key is mandatory. A dropped channel never implicitly retries a start.
    /// </summary>
    public static async Task<CMakeWorkflowClient> ConnectSshNetAsync(
        SshNetHostOptions options, CancellationToken ct = default)
    {
        SshNetConnection.Validate(options);
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.ConnectionTimeout);
        SshNetConnection? connection = null;
        try
        {
            connection = await SshNetConnection.ConnectAsync(options, deadline.Token);
            return await InitializeAsync(connection.Input, options.Workspace, null, null,
                deadline.Token, connection.Output, connection);
        }
        catch (Exception error)
        {
            if (connection is not null) await connection.DisposeAsync();
            ct.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
                throw new TimeoutException("SSH.NET Host initialization timed out.", error);
            if (error is Renci.SshNet.Common.SshException or SocketException or IOException)
                throw new SshConnectionException(
                    "SSH.NET Host connection failed. Check pinned host key, credentials and remote bridge.",
                    connection?.Diagnostics ?? "", connection?.ExitCode, error);
            throw;
        }
    }
}
