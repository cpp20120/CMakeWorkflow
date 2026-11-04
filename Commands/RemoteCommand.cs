using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.Client;

namespace CMakeWorkflow;

/// <summary>
/// A thin, one-connection CLI over the durable SDK. In particular, a lost
/// workflow/start response is NEVER treated as permission to retry dispatch.
/// </summary>
internal static class RemoteCommand
{
    private static readonly HashSet<string> ConnectionFlags =
    [
        "--destination", "--endpoint", "--workspace", "--ssh-executable",
        "--remote-executable", "--connect-timeout-seconds", "--transport",
        "--host", "--user", "--key-file", "--host-key-sha256", "--port"
    ];

    internal const string Usage = """
        Usage: CMakeWorkflow remote COMMAND --endpoint REMOTE_SOCKET --workspace REMOTE_DIR [CONNECTION] [OPTIONS]
        Commands:
          provision plan|apply  (SSH.NET user-scoped Host installation; see remote provision help)
          start     --request-id ID --manifest REMOTE_FILE --manifest-sha256 SHA256 --results-dir REMOTE_DIR [--jobs N]
          find      --request-id ID
          status    --operation ID
          inspect   --operation ID   (recovery revision/checkpoint)
          events    --operation ID [--after SEQUENCE] [--limit 1..500]
          report    --operation ID
          artifacts --operation ID
          download  --operation ID --artifact-id ID --output LOCAL_FILE
          cancel    --operation ID --revision N
        Connection options: [--remote-executable PATH] [--connect-timeout-seconds N]
        Default transport: OpenSSH, --destination USER@HOST [--ssh-executable PATH]
        SSH.NET transport: --transport sshnet --host HOST --user USER --key-file LOCAL_PRIVATE_KEY
          --host-key-sha256 SHA256:PIN [--port 22] (no ssh-agent or ~/.ssh/config)
        All paths except --output and --key-file refer to the REMOTE filesystem.
        The Host must already be running.
        On a lost start response use 'find' with the SAME request ID; do not silently start again.
        No command reconnects or retries a failed RPC automatically.
        """;

    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length > 0 && args[0] == "provision")
            return RemoteProvisionCommand.Execute(args[1..], ct);
        if (args is [] or ["help"] or ["--help"])
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var action = args[0];
        var values = Parse(action, args[1..]);
        // Reject malformed input before spawning SSH or accepting any workflow.
        Validate(action, values);
        var timeout = TimeSpan.FromSeconds(Number(values, "--connect-timeout-seconds", 15, 1, 3600));
        var transport = Get(values, "--transport") ?? "openssh";
        // Both paths connect to the same durable Host and identical RPC surface.
        // No auto-reconnect or retry of an uncertain workflow/start response.
        await using var client = transport == "sshnet"
            ? await CMakeWorkflowClient.ConnectSshNetAsync(new SshNetHostOptions(
                Require(values, "--host"), Require(values, "--user"),
                Require(values, "--key-file"), Require(values, "--host-key-sha256"),
                Require(values, "--endpoint"), Require(values, "--workspace"))
            {
                Port = (int)Number(values, "--port", 22, 1, 65535),
                RemoteExecutable = Get(values, "--remote-executable") ?? "cmakeworkflow",
                ConnectionTimeout = timeout
            }, ct)
            : await CMakeWorkflowClient.ConnectSshAsync(new SshHostOptions(
                Require(values, "--destination"), Require(values, "--endpoint"),
                Require(values, "--workspace"))
            {
                SshExecutable = Get(values, "--ssh-executable") ?? "ssh",
                RemoteExecutable = Get(values, "--remote-executable") ?? "cmakeworkflow",
                ConnectionTimeout = timeout
            }, ct);
        switch (action)
        {
            case "start":
            {
                // ClientRequestId and manifest digest are caller-supplied and stable.
                // An ambiguous transport failure must be resolved with 'find'.
                var operation = await client.Workflows.StartAsync(new StartWorkflowOptions(
                    Require(values, "--request-id"), Require(values, "--manifest"),
                    Require(values, "--manifest-sha256"), (int)Number(values, "--jobs", 1, 1, 4096),
                    Require(values, "--results-dir")), ct);
                Print(new RemoteStartReceipt(operation.OperationId, Require(values, "--request-id")),
                    RemoteCliJson.Default.RemoteStartReceipt);
                return 0;
            }
            case "find":
                Print(await client.Operations.FindAsync(Require(values, "--request-id"), ct),
                    RemoteCliJson.Default.OperationSnapshot);
                return 0;
            case "status":
                Print(await client.Operations.GetAsync(Require(values, "--operation"), ct),
                    RemoteCliJson.Default.OperationSnapshot);
                return 0;
            case "inspect":
                Print(await client.Operations.GetHandle(Require(values, "--operation")).InspectRecoveryAsync(ct),
                    RemoteCliJson.Default.RecoverySnapshot);
                return 0;
            case "events":
            {
                var op = client.Operations.GetHandle(Require(values, "--operation"));
                Print(await op.ReadEventsAsync(Number(values, "--after", 0, 0, long.MaxValue),
                        (int)Number(values, "--limit", 100, 1, 500), ct),
                    RemoteCliJson.Default.EventPage);
                return 0;
            }
            case "report":
                Print(await client.Operations.GetHandle(Require(values, "--operation")).GetReportAsync(ct),
                    RemoteCliJson.Default.OperationReportSnapshot);
                return 0;
            case "artifacts":
                Print(await client.Operations.GetHandle(Require(values, "--operation")).ListArtifactsAsync(ct),
                    RemoteCliJson.Default.OperationArtifactList);
                return 0;
            case "download":
            {
                var op = client.Operations.GetHandle(Require(values, "--operation"));
                var id = Require(values, "--artifact-id");
                var catalog = await op.ListArtifactsAsync(ct);
                var artifact = catalog.Artifacts.SingleOrDefault(item => item.ArtifactId == id)
                               ?? throw new KeyNotFoundException($"No artifact '{id}' belongs to this operation.");
                var local = await op.DownloadArtifactAsync(artifact, Require(values, "--output"), ct: ct);
                Print(new RemoteDownloadReceipt(op.OperationId, id, local, artifact.Size, artifact.Sha256),
                    RemoteCliJson.Default.RemoteDownloadReceipt);
                return 0;
            }
            case "cancel":
                Print(await client.Operations.GetHandle(Require(values, "--operation"))
                        .RequestCancellationAsync(Number(values, "--revision", 0, 0, long.MaxValue), ct),
                    RemoteCliJson.Default.CancellationReceipt);
                return 0;
            default:
                throw new InvalidOperationException("Unhandled validated remote command.");
        }
    }

    private static Dictionary<string, string> Parse(string action, string[] args)
    {
        if (action is not ("start" or "find" or "status" or "inspect" or "events" or "report" or "artifacts" or "download" or "cancel"))
            throw new ArgumentException($"Unknown remote command '{action}'.\n{Usage}");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) ||
                args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Remote CLI expects --option VALUE pairs.\n{Usage}");
            if (!options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException($"Duplicate option '{args[i]}'.");
        }
        return options;
    }

    private static void Validate(string action, Dictionary<string, string> values)
    {
        string[] actionFlags = action switch
        {
            "start" => new[] { "--request-id", "--manifest", "--manifest-sha256", "--results-dir", "--jobs" },
            "find" => ["--request-id"],
            "status" or "inspect" or "report" or "artifacts" => ["--operation"],
            "events" => ["--operation", "--after", "--limit"],
            "download" => ["--operation", "--artifact-id", "--output"],
            "cancel" => ["--operation", "--revision"],
            _ => throw new ArgumentException("Unknown remote command.")
        };
        foreach (var (key, value) in values)
        {
            if (!ConnectionFlags.Contains(key) && !actionFlags.Contains(key, StringComparer.Ordinal))
                throw new ArgumentException($"Option '{key}' is invalid for remote {action}.");
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
                throw new ArgumentException($"Option '{key}' must be nonempty and contain no control characters.");
        }
        var transport = Get(values, "--transport") ?? "openssh";
        if (transport is not ("openssh" or "sshnet"))
            throw new ArgumentException("--transport must be openssh or sshnet.");
        foreach (var key in new[] { "--endpoint", "--workspace" }) Require(values, key);
        if (transport == "sshnet")
        {
            foreach (var key in new[] { "--host", "--user", "--key-file", "--host-key-sha256" })
                Require(values, key);
            if (values.ContainsKey("--destination") || values.ContainsKey("--ssh-executable"))
                throw new ArgumentException("--destination/--ssh-executable are OpenSSH-only options.");
            Number(values, "--port", 22, 1, 65535);
        }
        else
        {
            Require(values, "--destination");
            if (new[] { "--host", "--user", "--key-file", "--host-key-sha256", "--port" }
                .Any(values.ContainsKey))
                throw new ArgumentException("SSH.NET options require --transport sshnet.");
        }
        switch (action)
        {
            case "start":
                foreach (var key in new[] { "--request-id", "--manifest", "--manifest-sha256", "--results-dir" })
                    Require(values, key);
                var sha = Require(values, "--manifest-sha256");
                if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
                    throw new ArgumentException("--manifest-sha256 must contain exactly 64 hexadecimal characters.");
                Number(values, "--jobs", 1, 1, 4096);
                break;
            case "find": Require(values, "--request-id"); break;
            default: Require(values, "--operation"); break;
        }
        if (action == "events")
        {
            Number(values, "--after", 0, 0, long.MaxValue);
            Number(values, "--limit", 100, 1, 500);
        }
        if (action == "download")
        {
            Require(values, "--artifact-id");
            Require(values, "--output");
        }
        if (action == "cancel")
        {
            Require(values, "--revision");
            Number(values, "--revision", 0, 0, long.MaxValue);
        }
        Number(values, "--connect-timeout-seconds", 15, 1, 3600);
    }

    private static string Require(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required remote option {key}.");

    private static string? Get(Dictionary<string, string> values, string key) =>
        values.GetValueOrDefault(key);

    private static long Number(Dictionary<string, string> values, string key, long fallback, long minimum, long maximum)
    {
        if (!values.TryGetValue(key, out var raw)) return fallback;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < minimum || value > maximum)
            throw new ArgumentException($"{key} must be an integer from {minimum} through {maximum}.");
        return value;
    }

    private static void Print<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        Console.WriteLine(JsonSerializer.Serialize(value, info));
}

internal sealed record RemoteStartReceipt(string OperationId, string ClientRequestId);
internal sealed record RemoteDownloadReceipt(string OperationId, string ArtifactId, string LocalPath,
    long Size, string Sha256);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(OperationSnapshot))]
[JsonSerializable(typeof(RecoverySnapshot))]
[JsonSerializable(typeof(OperationReportSnapshot))]
[JsonSerializable(typeof(OperationArtifactList))]
[JsonSerializable(typeof(EventPage))]
[JsonSerializable(typeof(CancellationReceipt))]
[JsonSerializable(typeof(RemoteStartReceipt))]
[JsonSerializable(typeof(RemoteDownloadReceipt))]
internal partial class RemoteCliJson : JsonSerializerContext;
