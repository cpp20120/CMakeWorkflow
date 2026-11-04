namespace CMakeWorkflow.Host;

internal static class HostBridgeCommand
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        string? endpoint = null;
        var stdio = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--stdio" when !stdio: stdio = true; break;
                case "--endpoint" when endpoint is null && i + 1 < args.Length: endpoint = args[++i]; break;
                default: throw new ArgumentException($"Invalid host bridge option: {args[i]}");
            }
        }

        if (!stdio || string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("host bridge requires --stdio --endpoint PATH.");
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        await StdioBridge.RunAsync(endpoint, input, output, ct);
        return 0;
    }
}
