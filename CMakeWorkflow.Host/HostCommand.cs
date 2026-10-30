using System.Text.Json;
using CMakeWorkflow.Tooling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public static class HostCommand
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is not ("serve" or "doctor" or "fix"))
            throw new ArgumentException("host serve --endpoint PATH | host <doctor|fix> --endpoint PATH [--workspace DIR] [--profile NAME] [--install-dir DIR] [--json]; fix: [--pm NAME] [--no-fallback] [--yes|--dry-run]");
        string? endpoint = null, install = null;
        var workspace = Directory.GetCurrentDirectory();
        var profiles = new List<string>(); var json = false; var yes = false; var dryRun = false;
        var manager = PackageManagerKind.Auto; var fallback = true;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--endpoint" when i + 1 < args.Length: endpoint = args[++i]; break;
                case "--workspace" when args[0] != "serve" && i + 1 < args.Length: workspace = Path.GetFullPath(args[++i]); break;
                case "--install-dir" when args[0] != "serve" && i + 1 < args.Length: install = Path.GetFullPath(args[++i]); break;
                case "--profile" when args[0] != "serve" && i + 1 < args.Length: profiles.Add(args[++i]); break;
                case "--json" when args[0] != "serve": json = true; break;
                case "--yes" when args[0] == "fix": yes = true; break;
                case "--dry-run" when args[0] == "fix": dryRun = true; break;
                case "--no-fallback" when args[0] == "fix": fallback = false; break;
                case "--pm" when args[0] == "fix" && i + 1 < args.Length:
                    var name = args[++i];
                    if (!Enum.TryParse(name, true, out manager) || !Enum.IsDefined(manager) || name.Any(char.IsDigit))
                        throw new ArgumentException("Unknown package manager.");
                    break;
                default: throw new ArgumentException($"Invalid host option: {args[i]}");
            }
        }
        if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("--endpoint is required.");
        if (args[0] == "serve") { await HostServer.RunAsync(endpoint, ct); return 0; }
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var stream = await LocalTransport.ConnectAsync(endpoint, connectTimeout.Token);
        using var rpc = HostConnection.Create(stream);
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IHostNotifications>(), new Notifications(), null);
        var host = rpc.Attach<IHostRpc>();
        rpc.StartListening();
        var hello = await host.InitializeAsync(new(1, workspace), connectTimeout.Token);
        if (hello.ProtocolVersion != 1 || !hello.Capabilities.Contains("tooling/doctor"))
            throw new InvalidDataException("Incompatible Host protocol or capabilities.");
        if (args[0] == "fix")
        {
            if (!hello.Capabilities.Contains("tooling/applyFix") || !hello.Capabilities.Contains("tooling/planFix"))
                throw new InvalidDataException("This Host does not support Fix. Restart it using an updated CLI.");
            var reviewed = await host.PlanFixAsync(new(profiles.ToArray(), install, manager, fallback), ct);
            if (!yes || dryRun)
            {
                if (json) Console.Out.WriteLine(JsonSerializer.Serialize(reviewed, HostJson.Default.FixPlan));
                else PrintPlan(reviewed.Plan);
                Console.Error.WriteLine("Plan only; no installation attempted. Use host fix --yes to explicitly authorize installation. Package versions are selected by the package manager/release source.");
                return dryRun || reviewed.Plan.Ready ? 0 : 2;
            }
            PrintPlan(reviewed.Plan, Console.Error);
            Console.Error.WriteLine($"[Host fix] Applying reviewed plan {reviewed.PlanId}; directory: {reviewed.Plan.InstallDirectory}. Cancellation can leave partial installation; no rollback is available.");
            var result = await host.ApplyFixAsync(new(Guid.NewGuid().ToString("N"), reviewed.PlanId), ct);
            if (json) Console.Out.WriteLine(JsonSerializer.Serialize(result, HostJson.Default.FixResult));
            else
            {
                Console.Out.WriteLine("Fix: " + result.Status);
                foreach (var diagnostic in result.Diagnostics) Console.Error.WriteLine(diagnostic);
                if (result.Verification is not null) PrintPlan(result.Verification);
            }
            return result.Status == "succeeded" ? 0 : result.Status == "canceled" ? 130 : 2;
        }
        var plan = await host.DoctorAsync(new(Guid.NewGuid().ToString("N"), profiles.ToArray(), install), ct);
        if (json) Console.Out.WriteLine(JsonSerializer.Serialize(plan, HostJson.Default.ToolingPlan));
        else { Console.Out.WriteLine($"Host {hello.ProcessId}; workspace: {hello.WorkspaceDirectory}"); PrintPlan(plan); }
        return plan.Ready ? 0 : 2;
    }

    private static void PrintPlan(ToolingPlan plan, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        foreach (var tool in plan.Tools)
            writer.WriteLine($"{(tool.Installed ? "ready" : "missing"),-7} {tool.Id}: {tool.Command ?? tool.Diagnostic} {tool.Version}");
        foreach (var step in plan.Steps)
            writer.WriteLine($"  {step.Tool}: {step.Method} {step.Executable} {JsonSerializer.Serialize(step.Arguments, HostJson.Default.StringArray)} {step.Source} {step.Diagnostic}");
    }
    private sealed class Notifications : IHostNotifications
    {
        public void Progress(DoctorProgress progress) => Console.Error.WriteLine(
            $"[Host doctor:{progress.Sequence}] {progress.Stage} {progress.Tool?.Id}");
        public void FixProgress(FixProgress progress) => Console.Error.WriteLine(
            $"[Host fix:{progress.Sequence}] {progress.Stage} {progress.Tool} {progress.Message}");
    }
}
