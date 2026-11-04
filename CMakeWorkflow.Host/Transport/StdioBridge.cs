namespace CMakeWorkflow.Host;

/// <summary>A byte tunnel to a persistent Host, with no ownership of that Host.</summary>
public static class StdioBridge
{
    public static async Task RunAsync(string endpoint, Stream input, Stream output, CancellationToken ct)
    {
        await using var connection = await LocalTransport.ConnectAsync(endpoint, ct);
        await ForwardAsync(input, output, connection, ct);
    }

    // Either EOF ends this connection. Bytes copied are not an acknowledgement
    // of an RPC request: only the Host's complete RPC response establishes that.
    // The caller owns input/output and must supply cancellable asynchronous streams.
    public static async Task ForwardAsync(Stream input, Stream output, Stream connection, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var upstream = CopyAsync(input, connection, stop.Token);
        var downstream = CopyAsync(connection, output, stop.Token);
        var first = await Task.WhenAny(upstream, downstream);
        try
        {
            await first;
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            await stop.CancelAsync();
            // Closing the local endpoint never requests Host shutdown or operation cancellation.
            connection.Dispose();
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
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) return;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            await destination.FlushAsync(ct);
        }
    }
}
