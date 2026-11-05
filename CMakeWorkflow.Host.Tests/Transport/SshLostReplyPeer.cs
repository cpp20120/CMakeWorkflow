using System.Text;
using System.Text.Json;
using CMakeWorkflow.Host;

// A deterministic fault between durable acceptance and delivery of its response.
// Only this test peer reads frames; production continues to use the byte bridge.
internal static class SshLostReplyPeer
{
    internal static async Task RunAsync(string endpoint, string marker)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        CancellationToken ct = deadline.Token;
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        await using Stream host = await LocalTransport.ConnectAsync(endpoint, ct);
        foreach (string? method in new[] { "initialize", "workflow/start" })
        {
            (byte[]? Header, byte[]? Body) = await ReadFrameAsync(input, ct);
            using JsonDocument json = JsonDocument.Parse(Body);
            SshTransportTests.Check(json.RootElement.GetProperty("method").GetString() == method,
                "Unexpected RPC in lost-reply fixture.");
            await host.WriteAsync(Header, ct);
            await host.WriteAsync(Body, ct);
            await host.FlushAsync(ct);
            (byte[] Header, byte[] Body) response = await ReadFrameAsync(host, ct);
            using JsonDocument reply = JsonDocument.Parse(response.Body);
            SshTransportTests.Check(reply.RootElement.GetProperty("id").GetRawText() ==
                                    json.RootElement.GetProperty("id").GetRawText(), "Wrong response ID.");
            SshTransportTests.Check(reply.RootElement.TryGetProperty("result", out JsonElement result),
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
        List<byte> header = [];
        byte[] one = new byte[1];
        while (true)
        {
            if (header.Count >= 4096)
            {
                throw new InvalidDataException("Test header too large.");
            }

            await stream.ReadExactlyAsync(one, ct);
            header.Add(one[0]);
            if (header.Count >= 4 && header.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()))
            {
                break;
            }
        }
        int length = int.Parse(Encoding.ASCII.GetString([.. header]).Split("\r\n")
            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], System.Globalization.CultureInfo.CurrentCulture);
        if (length is < 1 or > 1024 * 1024)
        {
            throw new InvalidDataException("Test frame too large.");
        }

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return (header.ToArray(), body);
    }
}
