#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.

namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

internal static class HostBridgeCommand
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        string? endpoint = null;
        bool stdio = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--stdio" when !stdio: stdio = true; break;
                case "--endpoint" when endpoint is null && i + 1 < args.Length: endpoint = args[++i]; break;
                default: throw new ArgumentException($"Invalid host bridge option: {args[i]}");
            }
        }

        if (!stdio || string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("host bridge requires --stdio --endpoint PATH.");
        }

        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        await StdioBridge.RunAsync(endpoint, input, output, ct);
        return 0;
    }
}
