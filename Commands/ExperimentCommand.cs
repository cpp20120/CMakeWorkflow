using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Experiments;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.History.Analysis;
using CMakeWorkflow.Pipelines.Coordinator;
using CMakeWorkflow.Pipelines.Persistence;

namespace CMakeWorkflow.Commands;

internal static class ExperimentCommand
{
    private const string StageId = "workflow";
    private const string Usage = """
        CMakeWorkflow experiment plan --repo URL --ref REF
            (--host HOST --workspace PATH --endpoint SOCKET | --host-pool FILE [--arch ARCH] [--min-cpus N])
            (--preset NAME [--config NAME] | --managed-clang --config NAME) [--build-target TARGET]... [--ctest]
            [--vcpkg] [--binary [--environment-lock ID]] [--benchmark-target TARGET --repeats N [--benchmark-arg ARG]... [--profile]]
            [--remote-executable PATH] [--user USER --key-file PATH --host-key-sha256 HASH [--port PORT]]
        CMakeWorkflow experiment deps plan ID
        CMakeWorkflow experiment deps apply ID --approve --plan-sha SHA256
        CMakeWorkflow experiment run --plan ID [--plan ID]... --approve [--wait-seconds N]
        CMakeWorkflow experiment report ID [--wait-seconds N]
        CMakeWorkflow experiment compare BASELINE CANDIDATE [--threshold-percent N | --across-hosts]
        CMakeWorkflow experiment download ID --artifact ARTIFACT_ID --output FILE
        """;

    internal static Task<int> ExecuteAsync(string[] args, CancellationToken ct) => args switch
    {
        [] => Help(),
        ["help" or "--help"] => Help(),
        ["plan", .. string[] tail] => PlanAsync(tail, ct),
        ["deps", "plan", string id] => DependenciesAsync(id, null, ct),
        ["deps", "apply", string id, "--approve", "--plan-sha", string sha] => DependenciesAsync(id, sha, ct),
        ["run", .. string[] tail] => RunAsync(tail, ct),
        ["report", string id, .. string[] tail] => ObserveAsync(id, ParseWait(tail, 0), false, ct),
        ["compare", string baseline, string candidate, .. string[] tail] => CompareAsync(baseline, candidate, tail, ct),
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
        bool vcpkg = false;
        bool binary = false;
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            switch (option)
            {
                case "--profile": profile = true; break;
                case "--vcpkg": vcpkg = true; break;
                case "--binary": binary = true; break;
                case "--managed-clang": managed = true; break;
                case "--ctest": break; // Always included in this recipe.
                case "--build-target": targets.Add(Next(args, ref i)); break;
                case "--benchmark-arg": arguments.Add(Next(args, ref i)); break;
                case "--repo" or "--ref" or "--host" or "--workspace" or "--endpoint" or "--preset"
                    or "--config" or "--benchmark-target" or "--repeats" or "--remote-executable" or "--user"
                    or "--key-file" or "--host-key-sha256" or "--port" or "--host-pool" or "--arch" or "--min-cpus"
                    or "--environment-lock":
                    if (!options.TryAdd(option, Next(args, ref i)))
                    {
                        throw new ArgumentException($"Duplicate option {option}.");
                    }
                    break;
                default: throw new ArgumentException($"Unknown experiment option: {option}");
            }
        }

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

        if (vcpkg && !managed)
        {
            throw new ArgumentException("--vcpkg requires --managed-clang.");
        }
        if (!binary && options.ContainsKey("--environment-lock"))
        {
            throw new ArgumentException("--environment-lock requires a native managed experiment.");
        }

        string repository = Required(options, "--repo");
        string revision = Required(options, "--ref");
        int repeats = Number(options.GetValueOrDefault("--repeats", "3"), "--repeats", 1, 200);
        (ExperimentHost remote, ExperimentPlacement? placement) = options.GetValueOrDefault("--host-pool") is { } pool
            ? await SelectHostAsync(pool, options, managed, ct)
            : (DirectHost(options), null);
        PreparedExperimentPlan plan = await ExperimentPlanner.CreateAsync(new(repository, revision, remote)
        {
            ConfigurePreset = preset,
            Configuration = configuration,
            BuildTargets = targets,
            BenchmarkTarget = benchmark,
            Repeats = repeats,
            BenchmarkArguments = arguments,
            Profile = profile,
            Vcpkg = vcpkg,
            Placement = placement
        }, ExperimentStore(), binary ? new(options.GetValueOrDefault("--environment-lock"),
            static workflow => CapnpWire.Frame(CapnpWire.Canonicalize(ProductionWorkflowCodec.Encode(workflow))),
            EncodeDependencyPlan) : null, ct);
        string id = plan.Id;
        if (plan.DependencyPlanSha256 is { } dependencyDigest)
        {
            Console.WriteLine($"Dependency plan {dependencyDigest}. Review with experiment deps plan {id}.");
        }
        if (placement is not null)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"Selected Host {placement.Name} ({placement.Resources.Architecture}, {placement.Resources.AvailableProcessors} CPUs)"));
        }
        Console.WriteLine($"""
            Repository {plan.Repository}
            Ref {plan.RequestedRef} -> {plan.Commit}
            Source snapshot {plan.SourceSnapshotId} ({plan.SourceFileCount} files)
            Workflow manifest {plan.ManifestSha256}
            Host {remote.Destination} workspace {remote.Workspace} ({preset ?? "managed Clang"}, {configuration ?? "preset default"})
            {plan.Manifest}
            Plan ID {id}
            Approve only on an isolated Host: cmakeworkflow experiment run --plan {id} --approve
            """);
        return 0;
    }

    private static byte[] EncodeDependencyPlan(CMakeWorkflow.Client.Experiments.VcpkgProjectPlan value)
    {
        static CMakeWorkflow.Tooling.Tools.ManagedToolRelease Release(CMakeWorkflow.Client.Experiments.ManagedToolRelease release) =>
            new(release.Id, release.Version, release.Platform, release.Source, release.Sha256, release.TreeSha256, release.TarRoot);

        return CMakeWorkflow.Tooling.Tools.BinaryVcpkgPlan.Encode(new(value.SchemaVersion, value.Directory,
            value.ManifestSha256, value.ConfigurationSha256, value.EnvironmentSha256, Release(value.Executable), Release(value.Checkout))
        {
            Commit = value.Commit,
            SourceSnapshotId = value.SourceSnapshotId,
            TargetTriplet = value.TargetTriplet,
            HostTriplet = value.HostTriplet,
            Registries = value.Registries
        });
    }

    private static ExperimentHost DirectHost(Dictionary<string, string> options)
    {
        if (options.ContainsKey("--arch") || options.ContainsKey("--min-cpus"))
        {
            throw new ArgumentException("--arch and --min-cpus require --host-pool.");
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
        return host.StartsWith('-') || host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            ? throw new ArgumentException("Supply an SSH destination or configured SSH alias for --host.")
            : new(host,
                PosixPath(Required(options, "--workspace")), PosixPath(Required(options, "--endpoint")),
                options.GetValueOrDefault("--remote-executable", "cmakeworkflow"), user,
                key is null ? null : Path.GetFullPath(key), hostKey,
                Number(options.GetValueOrDefault("--port", "22"), "--port", 1, 65535));
    }

    private static async Task<(ExperimentHost Host, ExperimentPlacement Placement)> SelectHostAsync(string path,
        Dictionary<string, string> options, bool managed, CancellationToken ct)
    {
        string[] directOptions = ["--host", "--workspace", "--endpoint", "--user", "--key-file",
            "--host-key-sha256", "--port", "--remote-executable"];
        if (directOptions.Any(options.ContainsKey))
        {
            throw new ArgumentException("--host-pool supplies all Host connection options.");
        }
        string? architecture = options.GetValueOrDefault("--arch")?.ToUpperInvariant();
        if (architecture is not (null or "X64" or "ARM64" or "X86" or "ARM"))
        {
            throw new ArgumentException("--arch requires x64, arm64, x86 or arm.");
        }
        int minimum = Number(options.GetValueOrDefault("--min-cpus", "1"), "--min-cpus", 1, int.MaxValue);
        return await ExperimentHostSelection.SelectAsync(path, architecture, minimum, managed, ct);
    }

    private static async Task<int> DependenciesAsync(string id, string? approval, CancellationToken ct)
    {
        string directory = PlanDirectory(id);
        ExperimentPlan plan = await ReadPlanAsync(directory, id, ct);
        string expected = plan.DependencyPlanSha256 ??
            throw new InvalidOperationException("This experiment does not opt into vcpkg.");
        if (approval is not null && !string.Equals(expected, approval, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Approval does not match the experiment dependency plan SHA-256.");
        }

        await using CMakeWorkflowClient client = await plan.Host.ConnectAsync(ct);
        PreparedExperimentPlan prepared = await ExperimentService.ReadPlanAsync(Path.Combine(directory, "plan.json"), id, ct);
        ExperimentDependencyResult result = await client.Experiments.PrepareDependenciesAsync(prepared, approval, ct);
        Console.WriteLine(result.Plan);
        Console.WriteLine($"Dependency plan SHA-256 {result.PlanSha256}");
        if (result.InstalledTreeSha256 is { } tree)
        {
            Console.WriteLine($"Installed package tree SHA-256 {tree}");
        }
        if (result.DownloadTreeSha256 is { } downloads)
        {
            Console.WriteLine($"Download tree SHA-256 {downloads}");
        }
        if (result.ReceiptIdentity is { } receipt)
        {
            Console.WriteLine($"Binary provisioning receipt identity {receipt}");
        }
        return 0;
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        List<string> ids = [];
        int index = 0;
        while (index + 1 < args.Length && args[index] == "--plan")
        {
            ids.Add(args[index + 1]);
            index += 2;
        }
        if (ids.Count == 0 || index == args.Length || args[index] != "--approve" ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new ArgumentException("Execution requires explicit approval: experiment run --plan ID [--plan ID]... --approve.");
        }
        int wait = ParseWait(args[(index + 1)..], 600);
        foreach (string id in ids)
        {
            _ = await ReadPlanAsync(PlanDirectory(id), id, ct);
        }
        int status = 0;
        foreach (string id in ids)
        {
            status = Math.Max(status, await ObserveAsync(id, wait, true, ct));
        }
        return status;
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

        string manifestRelative = $".cmakeworkflow/experiments/{id}/{plan.ManifestName}";
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
            new ClientWorkflowChildHost(client, plan.SourceSnapshotId, new(HistoryPolicyMode.Default))
            {
                VerifiedBinaryWorkflow = plan.BinaryManifest.HasValue ? new(1, manifest, plan.ManifestSha256, results) : null
            });
        pipeline ??= ledger.Accept(id, new()
        {
            SchemaVersion = 1,
            Name = "git-experiment",
            Stages = [new() { Id = StageId, Kind = "workflow", Workflow = new() { Manifest = manifestRelative } }]
        });
        if (approve && pipeline.Stages.Single().Status == PipelineStageStates.Pending)
        {
            if (plan.Placement is not null)
            {
                await client.Experiments.VerifyPlacementAsync(plan.Placement, ct);
            }

            ExperimentStageRequest stage = new(id, plan.SourceSnapshotId, plan.ManifestSha256, plan.Manifest, plan.Files)
            {
                Bundle = plan.Bundle,
                BinaryManifest = plan.BinaryManifest
            };
            ExperimentStageReceipt staged = plan.Bundle is null
                ? await client.Workflows.StageExperimentAsync(stage, ct)
                : await client.Experiments.StageAsync(stage, Path.Combine(directory, "source.zip"), ct);
            if (staged != new ExperimentStageReceipt(manifest, plan.ManifestSha256, plan.SourceSnapshotId))
            {
                throw new InvalidDataException("Host staged identity does not match the approved plan.");
            }

            if (plan.DependencyPlanSha256 is not null)
            {
                PreparedExperimentPlan prepared = await ExperimentService.ReadPlanAsync(Path.Combine(directory, "plan.json"), id, ct);
                await client.Experiments.VerifyDependenciesAsync(prepared, ct);
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

    private static async Task<int> CompareAsync(string baselineId, string candidateId, string[] args, CancellationToken ct)
    {
        bool acrossHosts = args is ["--across-hosts"];
        double threshold = args switch
        {
            [] => 5,
            ["--across-hosts"] => 5,
            ["--threshold-percent", string value] when double.TryParse(value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed) && parsed > 0 => parsed,
            _ => throw new ArgumentException("--threshold-percent requires a positive number.")
        };
        (ExperimentPlan baseline, string baselineReport, string baselineHash) = await ReadComparisonInputAsync(baselineId, ct);
        (ExperimentPlan candidate, string candidateReport, string candidateHash) = await ReadComparisonInputAsync(candidateId, ct);
        if (baseline.Repository != candidate.Repository || (!acrossHosts && baseline.ManifestSha256 != candidate.ManifestSha256))
        {
            Console.WriteLine("Inconclusive. Repository or workflow inputs differ.");
            return 2;
        }

        if (acrossHosts && (baseline.SourceSnapshotId != candidate.SourceSnapshotId ||
                            !BenchmarkInputs(baseline).SequenceEqual(BenchmarkInputs(candidate), StringComparer.Ordinal)))
        {
            Console.WriteLine("Inconclusive. Host comparison requires the same source snapshot and benchmark target, arguments and repetitions.");
            return 2;
        }

        HistoryDifference difference = await HistoryComparison.CompareWorkflowTimingAsync(baselineReport, baselineHash,
            candidateReport, candidateHash, ct);
        Console.WriteLine($"Baseline {baseline.Commit} | candidate {candidate.Commit}");
        if (acrossHosts)
        {
            if (difference.Baseline is not { } first || difference.Candidate is not { } second || first.Median <= 0)
            {
                Console.WriteLine("Inconclusive. Both Hosts must report benchmark durations with a positive baseline median.");
                return 2;
            }
            await PrintHostTimingAsync(baseline.Host, baselineReport, baselineHash, first, ct);
            await PrintHostTimingAsync(candidate.Host, candidateReport, candidateHash, second, ct);
            Console.WriteLine(FormattableString.Invariant($"Candidate median is {second.Median / first.Median:P2} of baseline. Descriptive Host comparison."));
            return 0;
        }
        if (difference.Status != "compatible" || difference.Baseline is not { Count: >= 3 } left ||
            difference.Candidate is not { Count: >= 3 } right || difference.PercentChange is not { } delta ||
            !double.IsFinite(delta))
        {
            Console.WriteLine(difference.Status == "compatible"
                ? "Inconclusive. At least three timings per revision and a positive baseline median are required."
                : $"Inconclusive. {difference.Reason}");
            return 2;
        }

        string verdict = delta >= threshold ? "Regression" : delta <= -threshold ? "Improvement" : "Within threshold";
        Console.WriteLine(FormattableString.Invariant(
            $"{verdict}. Median {left.Median:G6} s ({left.Count} samples) -> {right.Median:G6} s ({right.Count} samples), {delta:+0.00;-0.00;0.00}% (threshold {threshold:G}%). Descriptive comparison."));
        return delta >= threshold ? 1 : 0;
    }

    private static string[] BenchmarkInputs(ExperimentPlan plan)
    {
        using JsonDocument document = JsonDocument.Parse(plan.Manifest);
        return [.. document.RootElement.GetProperty("nodes").EnumerateArray()
            .Where(node => node.GetProperty("id").GetString() == "benchmark_build" ||
                           node.GetProperty("id").GetString()!.StartsWith("timing_", StringComparison.Ordinal))
            .Select(node => node.GetRawText())];
    }

    private static async Task PrintHostTimingAsync(ExperimentHost host, string path, string hash, HistoryStats stats,
        CancellationToken ct)
    {
        using JsonDocument document = JsonDocument.Parse(await ReadVerifiedRunAsync(path, hash, ct));
        JsonElement environment = document.RootElement.GetProperty("environment");
        JsonElement.ArrayEnumerator builds = document.RootElement.GetProperty("buildEnvironments").EnumerateArray();
        string[] compilers = [.. builds.Select(build => $"{build.GetProperty("compilerId")} {build.GetProperty("compilerVersion")}".Trim())
            .Where(name => name.Length > 0).Distinct(StringComparer.Ordinal)];
        string platform = environment.ValueKind == JsonValueKind.Object
            ? $"{environment.GetProperty("operatingSystem")} {environment.GetProperty("architecture")}"
            : "unknown platform";
        Console.WriteLine(FormattableString.Invariant(
            $"{host.Destination}:{host.Port} | {platform} | {(compilers.Length == 0 ? "unknown compiler" : string.Join(", ", compilers))} | {stats.Median:G6} s ({stats.Count} samples)"));
    }

    private static async Task<(ExperimentPlan Plan, string ReportPath, string ReportHash)> ReadComparisonInputAsync(
        string id, CancellationToken ct)
    {
        string directory = PlanDirectory(id);
        ExperimentPlan plan = await ReadPlanAsync(directory, id, ct);
        using PipelineLedger ledger = new(Path.Combine(directory, "pipeline.db"));
        PipelineStageSnapshot? terminal = ledger.Find(id)?.Stages.Single();
        ExperimentResult? result = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(Path.Combine(directory, "result.json"), ct),
            ExperimentJson.Default.ExperimentResult);
        return result is not null && result.ExperimentId == id && result.State == PipelineStageStates.Succeeded &&
               terminal?.Status == result.State && terminal.ChildOperationId == result.OperationId &&
               terminal.EvidenceReference == ReportReference(result.OperationId, result.ReportSha256)
            ? (plan, Path.Combine(directory, "run.json"), result.ReportSha256)
            : throw new InvalidDataException("Comparison requires finalized, verified experiment reports.");
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

    private static Task<ExperimentPlan> ReadPlanAsync(string directory, string id, CancellationToken ct) =>
        ExperimentPlan.ReadAsync(Path.Combine(directory, "plan.json"), id, ct);

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

    private static string PlanDirectory(string id) =>
        id.Length == 64 && id.All(char.IsAsciiHexDigitUpper)
            ? Path.Combine(ExperimentStore(), id)
            : throw new ArgumentException("Experiment ID must be the 64-character uppercase plan SHA-256.", nameof(id));

    private static string ExperimentStore() => Path.GetFullPath(
        Environment.GetEnvironmentVariable("CMAKEWORKFLOW_EXPERIMENT_STORE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify), "CMakeWorkflow", "experiments"));

    private static string Next(string[] args, ref int index) => ++index < args.Length ? args[index]
        : throw new ArgumentException($"Missing value for {args[index - 1]}.");

    private static string Required(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Required option {key}.");

    internal static string PosixPath(string path) =>
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
