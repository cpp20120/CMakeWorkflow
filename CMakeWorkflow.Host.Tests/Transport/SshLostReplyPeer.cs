using System.Text;
using System.Text.Json;
using CMakeWorkflow.Host.Transport;

// A deterministic fault between durable acceptance and delivery of its response.
// Only this test peer reads frames; production continues to use the byte bridge.
namespace CMakeWorkflow.Host.Tests.Transport;

internal static class SshLostReplyPeer
{
    internal static async Task RunAsync(string endpoint, string marker, bool interruptPrepare)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
        CancellationToken ct = deadline.Token;
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        await using Stream host = await LocalTransport.ConnectAsync(endpoint, ct);
        for (int request = 0; request < 2048; request++)
        {
            (byte[]? Header, byte[]? Body) = await ReadFrameAsync(input, ct);
            using JsonDocument json = JsonDocument.Parse(Body);
            string? method = json.RootElement.GetProperty("method").GetString();
            SshTransportTests.Check(request == 0 ? method == "initialize" :
                method is "experiment/sourceChunk" or "experiment/stage" or "experiment/dependencies" or "workflow/prepareManifest" or "workflow/preflight" or "workflow/start",
                "Unexpected RPC in lost-reply fixture.");
            await host.WriteAsync(Header, ct);
            if (interruptPrepare && method == "workflow/prepareManifest")
            {
                await host.WriteAsync(Body.AsMemory(0, Body.Length - 1), ct);
                await host.FlushAsync(ct);
                await File.WriteAllTextAsync(marker, "prepare interrupted", ct);
                return;
            }
            await host.WriteAsync(Body, ct);
            await host.FlushAsync(ct);
            (byte[] Header, byte[] Body) response = await ReadFrameAsync(host, ct);
            using JsonDocument reply = JsonDocument.Parse(response.Body);
            SshTransportTests.Check(reply.RootElement.GetProperty("id").GetRawText() ==
                                    json.RootElement.GetProperty("id").GetRawText(), "Wrong response ID.");
            SshTransportTests.Check(reply.RootElement.TryGetProperty("result", out JsonElement result),
                "Host refused the fault-injection request.");
            if (method == "experiment/sourceChunk" && result.GetInt64() > 0 && !File.Exists(marker + ".upload"))
            {
                await File.WriteAllTextAsync(marker + ".upload", "chunk saved", ct);
                return;
            }
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
        throw new InvalidDataException("No workflow/start passed through the lost-reply fixture.");
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