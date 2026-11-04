using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.Client;

namespace CMakeWorkflow;

/// <summary>Read-only plan and explicitly approved remote Host installation.</summary>
internal static class RemoteProvisionCommand
{
    private const string Usage = """
        Usage: CMakeWorkflow remote provision <plan|apply> --host HOST --user USER
          --key-file LOCAL_KEY --host-key-sha256 SHA256:PIN --workspace REMOTE_DIR
          --publish-dir LOCAL_PUBLISH --install-root REMOTE_DIR --rid linux-x64|linux-arm64
          [--port N] [--connect-timeout-seconds N]
          apply additionally requires --approval-sha256 DIGEST from an unchanged plan.
        No sudo, package installs, unattended upgrades or persistent Host restarts.
        """;

    internal static int Execute(string[] args, CancellationToken ct)
    {
        if (args is [] or ["--help"] or ["help"])
        {
            Console.WriteLine(Usage);
            return 0;
        }
        var action = args[0];
        if (action is not ("plan" or "apply")) throw new ArgumentException(Usage);
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) ||
                args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
                !parsed.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Use unique --key VALUE pairs.\n" + Usage);
        }
        string[] allowed = ["--host", "--user", "--key-file", "--host-key-sha256", "--workspace",
            "--publish-dir", "--install-root", "--rid", "--port", "--connect-timeout-seconds",
            "--approval-sha256"];
        foreach (var (key, value) in parsed)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(value) ||
                value.Any(char.IsControl))
                throw new ArgumentException("Invalid remote provisioning option: " + key);
        }
        if (action == "plan" && parsed.ContainsKey("--approval-sha256"))
            throw new ArgumentException("Approval is accepted only for apply, not plan.");
        string Need(string key) => parsed.TryGetValue(key, out var value)
            ? value : throw new ArgumentException("Missing required remote provisioning option: " + key);
        int Number(string key, int fallback, int min, int max)
        {
            if (!parsed.TryGetValue(key, out var input)) return fallback;
            if (!int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value < min || value > max) throw new ArgumentException("Invalid integer: " + key);
            return value;
        }
        var options = new SshNetProvisionOptions(
            Need("--host"), Need("--user"), Need("--key-file"), Need("--host-key-sha256"),
            Need("--workspace"), Need("--publish-dir"), Need("--install-root"), Need("--rid"))
        {
            Port = Number("--port", 22, 1, 65535),
            ConnectionTimeout = TimeSpan.FromSeconds(Number("--connect-timeout-seconds", 15, 1, 3600))
        };
        if (action == "plan")
        {
            var plan = SshNetHostProvisioner.Plan(options, ct);
            Console.WriteLine(JsonSerializer.Serialize(plan, RemoteProvisionJson.Default.SshNetProvisionPlan));
            return 0;
        }
        var result = SshNetHostProvisioner.Apply(options, Need("--approval-sha256"), ct);
        Console.WriteLine(JsonSerializer.Serialize(result, RemoteProvisionJson.Default.SshNetProvisionReceipt));
        return 0;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SshNetProvisionPlan))]
[JsonSerializable(typeof(SshNetProvisionReceipt))]
internal partial class RemoteProvisionJson : JsonSerializerContext;
