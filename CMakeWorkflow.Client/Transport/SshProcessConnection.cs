using System.Diagnostics;

namespace CMakeWorkflow.Client.Transport;

// Owns only the local SSH transport process, never the remote persistent Host.
/// <summary>Owns the local OpenSSH process and redirected byte streams for a persistent Host bridge.</summary>
/// <remarks>
/// The remote persistent Host and its operations are not owned here. A background stderr reader feeds a
/// locked bounded diagnostic buffer. Callers borrow streams until disposal and must serialize disposal calls.
/// </remarks>
internal sealed class SshProcessConnection : IRemoteSshConnection
{
    private readonly Process process;
    private readonly BoundedSshDiagnostics diagnostics = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task stderr;
    private bool disposed;

    /// <summary>Starts the supplied redirected OpenSSH command and begins diagnostic draining.</summary>
    /// <remarks>
    /// Does not perform an RPC readiness handshake. Caller must dispose even when initialization fails;
    /// the start specification must provide redirected stdin/stdout/stderr as prepared by SshCommand.Create.
    /// </remarks>
    internal SshProcessConnection(ProcessStartInfo start)
    {
        process = Process.Start(start) ?? throw new IOException("Could not start OpenSSH.");
        stderr = ReadDiagnosticsAsync();
    }

    public Stream Input => process.StandardOutput.BaseStream;
    public Stream Output => process.StandardInput.BaseStream;
    public string Diagnostics => diagnostics.Snapshot();
    public int? ExitCode { get; private set; }

    private async Task ReadDiagnosticsAsync()
    {
        char[] buffer = new char[1024];
        try
        {
            while (true)
            {
                int count = await process.StandardError.ReadAsync(buffer, stop.Token);
                if (count == 0)
                {
                    return;
                }

                diagnostics.Append(buffer.AsSpan(0, count));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Process teardown can close the diagnostics pipe while a read is pending.
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Kills the owned process tree before closing raw pipes, then shares one two-second budget between
    /// process reaping and diagnostic drain. Deadline expiry throws IOException; termination/disposal errors
    /// can also propagate. The finally path attempts pipe/process cleanup independently of a request token.
    /// This sends no operation cancellation RPC and makes no terminal-state guarantee about the remote Host.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        // One budget for reaping AND diagnostics, rather than additive unbounded
        // waits. The connection deadline may be followed by at most this grace.
        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(2));
        try
        {
            // Kill before closing streams: pending synchronous pipe operations
            // must not delay termination of an unresponsive SSH process.
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync(cleanup.Token);
            ExitCode = process.ExitCode;
            // Drain normal exit diagnostics, but never wait indefinitely for an
            // inherited stderr handle held by a descendant of a custom SSH wrapper.
            await stderr.WaitAsync(cleanup.Token);
        }
        catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
        {
            throw new IOException("SSH cleanup exceeded its two-second deadline while reaping the process or draining stderr.");
        }
        finally
        {
            await stop.CancelAsync();
            // Dispose raw streams, not StreamWriter (which would flush a pipe
            // already closed by JSON-RPC). No stream is used after teardown.
            process.StandardInput.BaseStream.Dispose();
            process.StandardOutput.BaseStream.Dispose();
            process.StandardError.Dispose();
            process.Dispose();
            stop.Dispose();
        }
    }
}
