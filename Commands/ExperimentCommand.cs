using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Pipelines.Coordinator;
using CMakeWorkflow.Pipelines.Persistence;

namespace CMakeWorkflow.Commands;

internal static class ExperimentCommand
{
    private const string StageId = "workflow";
    private const string Usage = """
        CMakeWorkflow experiment plan --repo URL --ref REF --host HOST --workspace PATH --endpoint SOCKET
            (--preset NAME [--config NAME] | --managed-clang --config NAME) [--build-target TARGET]... [--ctest]
            [--benchmark-target TARGET --repeats N [--benchmark-arg ARG]... [--profile]]
            [--remote-executable PATH] [--user USER --key-file PATH --host-key-sha256 HASH [--port PORT]]
        CMakeWorkflow experiment run --plan ID --approve [--wait-seconds N]
        CMakeWorkflow experiment report ID [--wait-seconds N]
        CMakeWorkflow experiment download ID --artifact ARTIFACT_ID --output FILE
        """;

    internal static Task<int> ExecuteAsync(string[] args, CancellationToken ct) => args switch
    {
        [] or ["help" or "--help"] => Help(),
        ["plan", .. string[] tail] => PlanAsync(tail, ct),
        ["run", .. string[] tail] => RunAsync(tail, ct),
        ["report", string id, .. string[] tail] => ObserveAsync(id, ParseWait(tail, 0), false, ct),
        ["download", string id, "--artifact", string artifact, "--output", string output] =>
            DownloadAsync(id, artifact, output, ct),
        _ => throw new ArgumentException(Usage)
    };

    private static Task<int> Help()
    {
        Console.WriteLine(Usage);
        return Task.FromResult(0);
    }

    private static async Task<int> PlanAsync(string[] args, CancellationToken ct)
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        List<string> targets = [];
        List<string> arguments = [];
        bool profile = false;
        bool managed = false;
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            switch (option)
            {
                case "--profile": profile = true; break;
                case "--managed-clang": managed = true; break;
                case "--ctest": break; // Always included in this recipe.
                case "--build-target": targets.Add(Next(args, ref i)); break;
                case "--benchmark-arg": arguments.Add(Next(args, ref i)); break;
                case "--repo" or "--ref" or "--host" or "--workspace" or "--endpoint" or "--preset"
                    or "--config" or "--benchmark-target" or "--repeats" or "--remote-executable" or "--user"
                    or "--key-file" or "--host-key-sha256" or "--port":
                    if (!options.TryAdd(option, Next(args, ref i)))
                    {
                        throw new ArgumentException($"Duplicate option {option}.");
                    }
                    break;
                default: throw new ArgumentException($"Unknown experiment option: {option}");
            }
        }

        string? user = options.GetValueOrDefault("--user");
        string? key = options.GetValueOrDefault("--key-file");
        string? hostKey = options.GetValueOrDefault("--host-key-sha256");
        if (user is null ? key is not null || hostKey is not null || options.ContainsKey("--port") :
                string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(hostKey))
        {
            throw new ArgumentException("SSH.NET requires --user, --key-file and --host-key-sha256 together; otherwise use an OpenSSH alias.");
        }
        string host = Required(options, "--host");
        if (host.StartsWith('-') || host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException("Supply an SSH destination or configured SSH alias for --host.");
        }
        ExperimentHost remote = new(host,
            PosixPath(Required(options, "--workspace")), PosixPath(Required(options, "--endpoint")),
            options.GetValueOrDefault("--remote-executable", "cmakeworkflow"), user,
            key is null ? null : Path.GetFullPath(key), hostKey,
            Number(options.GetValueOrDefault("--port", "22"), "--port", 1, 65535));

        string? benchmark = options.GetValueOrDefault("--benchmark-target");
        if (benchmark is null && options.ContainsKey("--repeats"))
        {
            throw new ArgumentException("--repeats requires --benchmark-target.");
        }
        string? preset = options.GetValueOrDefault("--preset");
        string? configuration = options.GetValueOrDefault("--config");
        if (managed == (preset is not null) || (managed && configuration is null))
        {
            throw new ArgumentException("Select --preset NAME or --managed-clang --config NAME.");
        }

        ExperimentPlan plan = await ExperimentPlanning.CreateAsync(Required(options, "--repo"),
            Required(options, "--ref"), remote, preset, configuration,
            targets, benchmark, Number(options.GetValueOrDefault("--repeats", "3"), "--repeats", 1, 200),
            arguments, profile, ct);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan, ExperimentJson.Default.ExperimentPlan);
        string id = ExperimentPlan.Hash(bytes);
        string directory = PlanDirectory(id);
        _ = Directory.CreateDirectory(directory);
        await PublishAsync(Path.Combine(directory, "plan.json"), bytes, ct);
        Console.WriteLine($"""
            Repository {plan.Repository}
            Ref {plan.RequestedRef} -> {plan.Commit}
            Source snapshot {plan.SourceSnapshotId} ({plan.Files.Count} files)
            Workflow manifest {plan.ManifestSha256}
            Host {remote.Destination} workspace {remote.Workspace} ({preset ?? "managed Clang"}, {configuration ?? "preset default"})
            {plan.Manifest}
            Plan ID {id}
            Approve only on an isolated Host: cmakeworkflow experiment run --plan {id} --approve
            """);
        return 0;
    }

    private static Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        return args is ["--plan", string id, "--approve", .. string[] tail]
            ? ObserveAsync(id, ParseWait(tail, 600), true, ct)
            : throw new ArgumentException("Execution requires explicit approval: experiment run --plan ID --approve.");
    }

    private static async Task<int> ObserveAsync(string id, int waitSeconds, bool approve, CancellationToken ct)
    {
        string directory = PlanDirectory(id);
        ExperimentPlan plan = await ReadPlanAsync(directory, id, ct);
        await using FileStream lease = new(Path.Combine(directory, "controller.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        string resultPath = Path.Combine(directory, "result.json");
        using PipelineLedger ledger = new(Path.Combine(directory, "pipeline.db"));
        PipelineRunSnapshot? pipeline = ledger.Find(id);
        if (!approve && (pipeline is null || pipeline.Stages.Single().Status == PipelineStageStates.Pending))
        {
            Console.WriteLine($"Experiment {id} is pending approval. Run with --plan {id} --approve.");
            return 0;
        }
        if (File.Exists(resultPath))
        {
            ExperimentResult result = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(resultPath, ct),
                ExperimentJson.Default.ExperimentResult) ?? throw new InvalidDataException("Invalid experiment result.");
            PipelineStageSnapshot? terminal = pipeline?.Stages.Single();
            if (terminal is null || result.ExperimentId != id || result.OperationId != terminal.ChildOperationId ||
                result.State != terminal.Status ||
                terminal.EvidenceReference != ReportReference(result.OperationId, result.ReportSha256))
            {
                throw new InvalidDataException("Saved experiment result differs from the durable child evidence.");
            }
            _ = await ReadVerifiedRunAsync(Path.Combine(directory, "run.json"), result.ReportSha256, ct);
            PrintResult(result, resultPath);
            return result.State == PipelineStageStates.Succeeded ? 0 : 1;
        }

        string manifestRelative = $".cmakeworkflow/experiments/{id}/workflow.json";
        string root = plan.Host.Workspace;
        string manifest = root + "/" + manifestRelative;
        string results = root + $"/.cmakeworkflow/experiment-results/{id}";
        PipelineChildLaunch launch = new(root, manifest, plan.ManifestSha256, 1, results);

        await using CMakeWorkflowClient client = await plan.Host.ConnectAsync(ct);
        if (client.Host.WorkspaceDirectory != root)
        {
            throw new InvalidDataException("Initialized Host workspace differs from the approved plan.");
        }
        PipelineChildCoordinator coordinator = new(ledger,
            new ClientWorkflowChildHost(client, plan.SourceSnapshotId, new(HistoryPolicyMode.Default)));
        pipeline ??= ledger.Accept(id, new()
        {
            SchemaVersion = 1,
            Name = "git-experiment",
            Stages = [new() { Id = StageId, Kind = "workflow", Workflow = new() { Manifest = manifestRelative } }]
        });
        if (approve && pipeline.Stages.Single().Status == PipelineStageStates.Pending)
        {
            ExperimentStageReceipt staged = await client.Workflows.StageExperimentAsync(
                new(id, plan.SourceSnapshotId, plan.ManifestSha256, plan.Manifest, plan.Files), ct);
            if (staged != new ExperimentStageReceipt(manifest, plan.ManifestSha256, plan.SourceSnapshotId))
            {
                throw new InvalidDataException("Host staged identity does not match the approved plan.");
            }

            await Console.Error.WriteLineAsync($"Experiment {id}. If the connection is interrupted, run experiment report {id}.");
            pipeline = await coordinator.StartReadyAsync(pipeline.OperationId, StageId, launch, ct);
        }

        Stopwatch timer = Stopwatch.StartNew();
        while (pipeline.Stages.Single().Status is PipelineStageStates.DispatchIntent or PipelineStageStates.WaitingExternal)
        {
            PipelineStageSnapshot stage = pipeline.Stages.Single();
            if (stage.Status == PipelineStageStates.DispatchIntent)
            {
                await Console.Error.WriteLineAsync($"Recovering experiment {id} by Host request {stage.ChildRequestId}.");
            }
            pipeline = await coordinator.RecoverAsync(pipeline.OperationId, StageId, ct);
            if (pipeline.Stages.Single().Status != PipelineStageStates.WaitingExternal ||
                timer.Elapsed >= TimeSpan.FromSeconds(waitSeconds))
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        PipelineStageSnapshot final = pipeline.Stages.Single();
        if (final.Status is not (PipelineStageStates.Succeeded or PipelineStageStates.Failed))
        {
            Console.WriteLine($"Experiment {id} {final.Status}. Host request {final.ChildRequestId}, operation {final.ChildOperationId}.");
            return 0;
        }

        ExperimentResult saved = await SaveResultAsync(directory, id, plan, pipeline, final, client, ct);
        await PublishAsync(resultPath, JsonSerializer.SerializeToUtf8Bytes(saved, ExperimentJson.Default.ExperimentResult), ct);
        PrintResult(saved, resultPath);
        return final.Status == PipelineStageStates.Succeeded ? 0 : 1;
    }

    private static async Task<ExperimentResult> SaveResultAsync(string directory, string id, ExperimentPlan plan,
        PipelineRunSnapshot pipeline, PipelineStageSnapshot stage, CMakeWorkflowClient client, CancellationToken ct)
    {
        string child = stage.ChildOperationId ?? throw new InvalidDataException("Terminal experiment has no Host operation.");
        WorkflowOperation operation = client.Operations.GetHandle(child);
        OperationArtifactList catalog = await operation.ListArtifactsAsync(ct);
        OperationArtifactInfo report = catalog.Artifacts.Single(a => a.RelativePath == "run.json");
        if (catalog.OperationId != child ||
            stage.EvidenceReference != ReportReference(child, catalog.ReportSha256) ||
            !string.Equals(report.Sha256, catalog.ReportSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Host artifact catalog differs from finalized report evidence.");
        }

        string runJson = Path.Combine(directory, "run.json");
        if (!File.Exists(runJson))
        {
            _ = await operation.DownloadArtifactAsync(report, runJson, ct: ct);
        }
        using JsonDocument raw = JsonDocument.Parse(await ReadVerifiedRunAsync(runJson, report.Sha256, ct));
        _ = await WorkflowHtmlReport.ExecuteAsync(directory, ct);
        string history = (await client.History.GetIndexStatusAsync(child, ct)).State.ToString();
        return new(1, id, plan.Repository, plan.RequestedRef, plan.Commit, plan.SourceSnapshotId,
            plan.ManifestSha256, plan.Host.Destination, plan.Host.Workspace, pipeline.OperationId,
            stage.ChildRequestId!, child, stage.Status, report.Sha256, runJson,
            Path.Combine(directory, "report.html"), history, catalog.Artifacts, raw.RootElement.Clone());
    }

    private static async Task<int> DownloadAsync(string id, string artifactId, string output, CancellationToken ct)
    {
        string directory = PlanDirectory(id);
        ExperimentPlan plan = await ReadPlanAsync(directory, id, ct);
        using PipelineLedger ledger = new(Path.Combine(directory, "pipeline.db"));
        PipelineStageSnapshot stage = ledger.Find(id)?.Stages.Single()
            ?? throw new InvalidOperationException("Run and finalize the experiment before downloading artifacts.");
        if (stage.Status is not (PipelineStageStates.Succeeded or PipelineStageStates.Failed) ||
            stage.ChildOperationId is not { } child)
        {
            throw new InvalidOperationException("Finalize the experiment with experiment report before downloading artifacts.");
        }

        await using CMakeWorkflowClient client = await plan.Host.ConnectAsync(ct);
        WorkflowOperation operation = client.Operations.GetHandle(child);
        OperationArtifactList catalog = await operation.ListArtifactsAsync(ct);
        if (client.Host.WorkspaceDirectory != plan.Host.Workspace || catalog.OperationId != child ||
            stage.EvidenceReference != ReportReference(child, catalog.ReportSha256))
        {
            throw new InvalidDataException("Artifact catalog differs from the finalized experiment evidence.");
        }
        OperationArtifactInfo artifact = catalog.Artifacts.SingleOrDefault(a => a.ArtifactId == artifactId)
            ?? throw new ArgumentException($"Unknown artifact {artifactId}.");
        Console.WriteLine(await operation.DownloadArtifactAsync(artifact, output, ct: ct));
        return 0;
    }

    private static string ReportReference(string operation, string hash) =>
        $"child-report:{operation}:sha256:{hash.ToUpperInvariant()}";

    private static async Task<byte[]> ReadVerifiedRunAsync(string path, string hash, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        return string.Equals(ExperimentPlan.Hash(bytes), hash, StringComparison.OrdinalIgnoreCase) ? bytes
            : throw new InvalidDataException($"Cached report hash mismatch: {path}");
    }

    private static void PrintResult(ExperimentResult result, string resultPath)
    {
        Console.WriteLine($"Experiment {result.ExperimentId} {result.State} ({result.Commit})");
        Console.WriteLine($"Host operation {result.OperationId}, History {result.HistoryStatus}");
        Console.WriteLine($"Result {resultPath}");
        Console.WriteLine($"HTML {result.HtmlReport}");
        Console.WriteLine($"Verified workflow report {result.RunJson}");
    }

    private static async Task<ExperimentPlan> ReadPlanAsync(string directory, string id, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "plan.json"), ct);
        if (ExperimentPlan.Hash(bytes) != id)
        {
            throw new InvalidDataException("Stored experiment plan does not match its approved SHA-256.");
        }
        ExperimentPlan? plan = JsonSerializer.Deserialize(bytes, ExperimentJson.Default.ExperimentPlan);
        return plan is { SchemaVersion: 1 } ? plan : throw new InvalidDataException("Unsupported experiment plan.");
    }

    private static async Task PublishAsync(string path, byte[] bytes, CancellationToken ct)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, ct);
                await output.FlushAsync(ct);
            }
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path))
            {
                if (!(await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(bytes))
                {
                    throw;
                }
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string PlanDirectory(string id)
    {
        if (id.Length != 64 || !id.All(char.IsAsciiHexDigitUpper))
        {
            throw new ArgumentException("Experiment ID must be the 64-character uppercase plan SHA-256.", nameof(id));
        }
        string store = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_EXPERIMENT_STORE") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify), "CMakeWorkflow", "experiments");
        return Path.Combine(Path.GetFullPath(store), id);
    }

    private static string Next(string[] args, ref int index) => ++index < args.Length ? args[index]
        : throw new ArgumentException($"Missing value for {args[index - 1]}.");

    private static string Required(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Required option {key}.");

    private static string PosixPath(string path) =>
        path.StartsWith('/') && !path.Contains('\\', StringComparison.Ordinal) &&
            !path.Any(char.IsControl) && !path.Split('/').Skip(1).Any(p => p is "" or "." or "..")
            ? path : throw new ArgumentException($"Expected a canonical absolute Linux path: {path}");

    private static int ParseWait(string[] arguments, int fallback) => arguments switch
    {
        [] => fallback,
        ["--wait-seconds", string value] => Number(value, "--wait-seconds", 0, 86400),
        _ => throw new ArgumentException(Usage)
    };

    private static int Number(string value, string option, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) &&
            parsed >= minimum && parsed <= maximum ? parsed
            : throw new ArgumentException($"{option} must be between {minimum} and {maximum}.");
}
