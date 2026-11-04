using System.Text;
using System.Text.Json;
using CMakeWorkflow.Host;

// A deterministic fault between durable acceptance and delivery of its response.
// Only this test peer reads frames; production continues to use the byte bridge.
internal static class SshLostReplyPeer
{
    internal static async Task RunAsync(string endpoint, string marker)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        await using var host = await LocalTransport.ConnectAsync(endpoint, ct);
        foreach (var method in new[] { "initialize", "workflow/start" })
        {
            var request = await ReadFrameAsync(input, ct);
            using var json = JsonDocument.Parse(request.Body);
            SshTransportTests.Check(json.RootElement.GetProperty("method").GetString() == method,
                "Unexpected RPC in lost-reply fixture.");
            await host.WriteAsync(request.Header, ct);
            await host.WriteAsync(request.Body, ct);
            await host.FlushAsync(ct);
            var response = await ReadFrameAsync(host, ct);
            using var reply = JsonDocument.Parse(response.Body);
            SshTransportTests.Check(reply.RootElement.GetProperty("id").GetRawText() ==
                                    json.RootElement.GetProperty("id").GetRawText(), "Wrong response ID.");
            SshTransportTests.Check(reply.RootElement.TryGetProperty("result", out var result),
                "Host refused the fault-injection request.");
            if (method == "workflow/start")
            {
                // Record proof of acceptance, but never deliver its operation ID to the client.
                await File.WriteAllTextAsync(marker, result.GetProperty("operationId").GetString(), ct);
                return;
            }
            await output.WriteAsync(response.Header, ct);
            await output.WriteAsync(response.Body, ct);
            await output.FlushAsync(ct);
        }
    }

    private static async Task<(byte[] Header, byte[] Body)> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (header.Count >= 4096) throw new InvalidDataException("Test header too large.");
            await stream.ReadExactlyAsync(one, ct);
            header.Add(one[0]);
            if (header.Count >= 4 && header.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) break;
        }
        var length = int.Parse(Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")
            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
        if (length is < 1 or > 1024 * 1024) throw new InvalidDataException("Test frame too large.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return (header.ToArray(), body);
    }
}
