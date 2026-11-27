using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Developer;
using CMakeWorkflow.Scripting;
using StreamJsonRpc;
using CMakeWorkflowClient = CMakeWorkflow.Client.CMakeWorkflowClient;

namespace CMakeWorkflow.Commands;

internal static class ScriptCommand
{
    internal const string Usage = """
        CMakeWorkflow script compose[-binary] FILE.cs [--output-dir DIR] [--timeout-seconds N] [-- ARGS...]
        CMakeWorkflow script plan FILE.cs [HOST OPTIONS] [-- ARGS...]
        CMakeWorkflow script run FILE.cs --approve [--request-id ID] [HOST OPTIONS] [-- ARGS...]

        Host options: --workspace DIR, --manifest-base RELATIVE_DIR, --results-dir HOST_DIR,
                      --jobs N, --only NODE (repeatable), --no-history or --history-db HOST_FILE
        Use remote help for persistent local, OpenSSH and SSH.NET connection flags.
        Without connection flags, plan and run own a local Host for the command lifetime.
        These commands execute trusted C# on the controller.
        """;

    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args is [] or ["help"] or ["--help"])
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (args is not ["compose" or "compose-binary" or "plan" or "run", string source, .. string[] tail])
        {
            throw new ArgumentException(Usage);
        }

        string action = args[0];
        string path = Path.GetFullPath(source);
        int split = Array.IndexOf(tail, "--");
        string[] scriptArguments = split < 0 ? [] : tail[(split + 1)..];
        string[] commandArguments = split < 0 ? tail : tail[..split];
        bool approved = commandArguments.Contains("--approve", StringComparer.Ordinal);
        if ((action == "run" && !approved) || (action != "run" && approved))
        {
            throw new ArgumentException("--approve is required for script run and is only accepted by script run.");
        }
        (string[] clean, HistoryPolicy? history) = CliHistory.Parse(
            [.. commandArguments.Where(value => value != "--approve")]);
        (Dictionary<string, string> values, List<string> only) = RemoteCommand.Parse("script", clean);
        TimeSpan timeout = TimeSpan.FromSeconds(RemoteCommand.Number(values, "--timeout-seconds", 300, 1, 3600));

        if (action is "compose" or "compose-binary")
        {
            if (values.Keys.Any(key => key is not ("--output-dir" or "--timeout-seconds")) ||
                only.Count != 0 || history is not null)
            {
                throw new ArgumentException(Usage);
            }
            string directory = Path.GetFullPath(RemoteCommand.Get(values, "--output-dir") ?? Path.GetDirectoryName(path)!);
            WorkflowManifest composed = await ComposeAsync(path, scriptArguments, timeout, ct);
            if (action == "compose-binary")
            {
                string frame = Path.Combine(directory, "workflow.capnp");
                string sha = await WorkflowCliCommand.PublishBinaryFrameAsync(composed.ToDefinition(), frame, ct);
                Console.WriteLine($"SHA-256 {sha}  {frame}");
            }
            else
            {
                Console.WriteLine($"SHA-256 {composed.Sha256}  {await composed.SaveAsync(directory, ct)}");
            }
            return 0;
        }

        int jobs = (int)RemoteCommand.Number(values, "--jobs", 1, 1, 4096);
        bool ownsHost = values.Keys.All(key => key is "--workspace" or "--request-id" or "--manifest-base" or
            "--results-dir" or "--jobs" or "--timeout-seconds");
        if (!ownsHost)
        {
            RemoteCommand.Validate("script", values);
        }
        if (action != "run" && values.ContainsKey("--request-id"))
        {
            throw new ArgumentException("--request-id applies to script run.");
        }
        string workspace = ownsHost
            ? Path.GetFullPath(RemoteCommand.Get(values, "--workspace") ?? Path.GetDirectoryName(path)!)
            : values["--workspace"];
        string requestId = RemoteCommand.Get(values, "--request-id") ?? Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Any(char.IsControl))
        {
            throw new ArgumentException("Expected a nonempty request ID without control characters.");
        }
        WorkflowManifest manifest = await ComposeAsync(path, scriptArguments, timeout, ct);
        await using LocalDeveloperHost? owned = ownsHost ? await LocalDeveloperHost.OpenAsync(workspace, ct) : null;
        await using CMakeWorkflowClient? connected = ownsHost ? null : await RemoteCommand.ConnectAsync(values, ct);
        CMakeWorkflowClient client = owned?.Client ?? connected!;
        string manifestBase = RemoteCommand.Get(values, "--manifest-base") ?? ".";
        string requestDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)));
        string results = RemoteCommand.Get(values, "--results-dir") ?? (".cmakeworkflow/script-results/" + requestDigest);
        PreparedWorkflow prepared = await manifest.PrepareAsync(client.Workflows, results, manifestBase, ct);
        WorkflowPreflightResult preflight = await client.Workflows.PreflightAsync(
            new WorkflowPreflightOptions(prepared.Manifest, prepared.ManifestSha256, jobs, prepared.ResultsDirectory)
            {
                Only = only,
                HistoryPolicy = history
            }, ct);

        Console.WriteLine($"Manifest {prepared.Manifest}\nSHA-256 {prepared.ManifestSha256}\nResults {prepared.ResultsDirectory}");
        foreach (WorkflowPreflightNode node in preflight.Nodes)
        {
            Console.WriteLine($"{node.Id}: {node.Kind} after [{string.Join(", ", node.Dependencies)}]");
            foreach (WorkflowPreflightDiagnostic diagnostic in node.Diagnostics)
            {
                Console.WriteLine($"  {diagnostic.Severity}: {diagnostic.Message}");
            }
        }
        if (action == "plan" || !preflight.Valid)
        {
            return preflight.Valid ? 0 : 1;
        }

        await Console.Error.WriteLineAsync($"[CMakeWorkflow] Client request ID: {requestId}");
        await Console.Error.FlushAsync(ct);
        try
        {
            WorkflowOperation operation = await client.Workflows.StartAsync(preflight.CreateStartOptions(requestId), ct);
            return await ObserveAsync(operation, owned, ct);
        }
        catch (ConnectionLostException) when (!ownsHost && !ct.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync($"[CMakeWorkflow] Reconnecting to look up request {requestId}.");
            await using CMakeWorkflowClient recovered = await RemoteCommand.ConnectAsync(values, ct);
            OperationSnapshot found = await recovered.Operations.FindAsync(requestId, ct);
            return found.ManifestSha256.Equals(prepared.ManifestSha256, StringComparison.OrdinalIgnoreCase)
                ? await ObserveAsync(recovered.Operations.GetHandle(found.OperationId), null, ct)
                : throw new InvalidDataException("The recovered operation has a different manifest digest.");
        }
    }

    private static async Task<WorkflowManifest> ComposeAsync(string path, string[] arguments, TimeSpan timeout, CancellationToken ct)
    {
        using Stream output = Console.OpenStandardOutput();
        using Stream error = Console.OpenStandardError();
        return await WorkflowScriptCompiler.ComposeAsync(path, arguments, output, error, timeout, ct);
    }

    private static async Task<int> ObserveAsync(WorkflowOperation operation, LocalDeveloperHost? owned, CancellationToken ct)
    {
        Console.WriteLine($"Operation {operation.OperationId}");
        string state = owned is null
            ? (await operation.WaitForCompletionAsync(ct: ct)).State
            : await ObserveOwnedAsync(operation, owned, ct);
        Console.WriteLine(state);
        return state switch { "succeeded" => 0, "canceled" => 130, _ => 1 };
    }

    private static async Task<string> ObserveOwnedAsync(WorkflowOperation operation, LocalDeveloperHost owned,
        CancellationToken ct)
    {
        OperationOverview overview;
        try
        {
            overview = await owned.WaitForOperationAsync(operation.OperationId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15));
            RecoverySnapshot recovery = await operation.InspectRecoveryAsync(cleanup.Token);
            _ = await operation.RequestCancellationAsync(recovery.Revision, cleanup.Token);
            _ = await owned.WaitForOperationAsync(operation.OperationId, cleanup.Token);
            throw;
        }

        if (!overview.IsFinalized)
        {
            return "needs-attention";
        }
        _ = await owned.ObserveHistoryAsync(operation.OperationId, TimeSpan.FromSeconds(10), ct);
        return overview.Operation.State;
    }
}
