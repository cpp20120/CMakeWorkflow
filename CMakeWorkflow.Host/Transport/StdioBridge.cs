namespace CMakeWorkflow.Host.Transport;

/// <summary>A byte tunnel to a persistent Host, with no ownership of that Host.</summary>
public static class StdioBridge
{
    /// <summary>Connects a binary stdio tunnel to an existing persistent Host.</summary>
    /// <remarks>
    /// Owns the local Host connection. Ending the tunnel closes that connection without stopping the persistent Host.
    /// </remarks>
    public static async Task RunAsync(string endpoint, Stream input, Stream output, CancellationToken ct)
    {
        await using Stream connection = await LocalTransport.ConnectAsync(endpoint, ct);
        await ForwardAsync(input, output, connection, ct);
    }

    // Either EOF ends this connection. Bytes copied are not an acknowledgement
    // of an RPC request: only the Host's complete RPC response establishes that.
    // The caller owns input/output and must supply cancellable asynchronous streams.
    /// <summary>Copies bytes in both directions until either EOF, failure or cancellation.</summary>
    /// <remarks>
    /// Caller supplies cancellable asynchronous streams and retains ownership of input/output.
    /// This method closes connection, cancels the sibling copy and observes both tasks before returning.
    /// Copied bytes are not an RPC acknowledgement; an interrupted reply leaves mutation outcome uncertain.
    /// Closing this tunnel does not request persistent Host shutdown or durable operation cancellation.
    /// </remarks>
    public static async Task ForwardAsync(Stream input, Stream output, Stream connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task upstream = CopyAsync(input, connection, stop.Token);
        Task downstream = CopyAsync(connection, output, stop.Token);
        Task first = await Task.WhenAny(upstream, downstream);
        try
        {
            await first;
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            await stop.CancelAsync();
            // Closing the local endpoint never requests Host shutdown or operation cancellation.
            await connection.DisposeAsync();
            try
            {
                await Task.WhenAll(upstream, downstream);
            }
            catch (Exception error) when (stop.IsCancellationRequested &&
                                          error is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // The first pump's error (or caller cancellation) is propagated above.
            }
        }
    }

    private static async Task CopyAsync(Stream source, Stream destination, CancellationToken ct)
    {
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            await destination.FlushAsync(ct);
        }
    }
}
