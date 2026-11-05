using System.Text;
using System.Text.Json;

internal static class SshProtocolMismatchPeer
{
    internal static async Task RunAsync()
    {
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        List<byte> header = [];
        byte[] one = new byte[1];
        while (header.Count < 4096)
        {
            await input.ReadExactlyAsync(one);
            header.Add(one[0]);
            if (header.Count >= 4 && header.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()))
            {
                break;
            }
        }
        int length = int.Parse(Encoding.ASCII.GetString([.. header]).Split("\r\n")
            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], System.Globalization.CultureInfo.CurrentCulture);
        if (length is < 1 or > 65536)
        {
            throw new System.Diagnostics.UnreachableException("Invalid test request size.");
        }

        byte[] body = new byte[length];
        await input.ReadExactlyAsync(body);
        using JsonDocument request = JsonDocument.Parse(body);
        byte[] response = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + request.RootElement.GetProperty("id").GetRawText()
            + ",\"result\":{\"protocolVersion\":99,\"processId\":1,\"workspaceDirectory\":\"/\",\"capabilities\":[],\"profiles\":[]}}");
        await output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {response.Length}\r\n\r\n"));
        await output.WriteAsync(response);
        await output.FlushAsync();
        await Task.Delay(5000);
    }
}
