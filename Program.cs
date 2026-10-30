using System.Text.Json;
using CMakeWorkflow.Campaigns;
using CMakeWorkflow.Profiling;
using CMakeWorkflow.History;
using CMakeWorkflow.Tooling;

namespace CMakeWorkflow;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            if (args.Length > 0 && args[0] == "host")
                return await Host.HostCommand.ExecuteAsync(args[1..], cts.Token);
            if (args.Length > 0 && args[0] == "history")
                return await HistoryCommand.ExecuteAsync(args[1..], cts.Token);
            if (args.Length > 0 && args[0] == "env")
                return await EnvironmentCommand.ExecuteAsync(args[1..], cts.Token);
            if (args.Length > 0 && args[0] == "tooling")
                return await ToolingCommand.ExecuteAsync(args[1..], cts.Token);
            if (args.Length > 0 && args[0] == "setup")
                return await ToolingCommand.ExecuteAsync(["setup", .. args[1..]], cts.Token);
            if (args.Length > 1 && args[0] == "dagflow" && args[1] == "profile")
                return await ExecuteProfileAsync(args[2..], cts.Token);
            if (args.Length > 0 && args[0] == "profile")
                return await ExecuteProfileAsync(args[1..], cts.Token);
            if (args.Length > 0 && args[0] == "dagflow")
                return await CampaignCommand.ExecuteAsync(args[1..], cts.Token);
            if (args.Length > 0 && args[0] is ("mutation" or "weak-memory" or "fuzz"))
                return await CampaignCommand.ExecuteAsync(args, cts.Token);
            if (args.Length is 3 or 4 && args[0] == "context" && args[1] == "inspect"
                && (args.Length == 3 || args[3] == "--json"))
            {
                var observed = await CMakeInspection.InspectAsync(BuildTreeOwnership.CanonicalPath(args[2]), cts.Token);
                Console.WriteLine(JsonSerializer.Serialize(observed, WorkflowJson.Default.ObservedBuildContext));
                return 0;
            }
            if (args.Length >= 2 && args[0] == "workflow" && args[1] == "reconcile")
                return await WorkflowReconcileCommand.ExecuteAsync(args[2..], cts.Token);
            if (args.Length >= 2 && args[0] == "workflow" && args[1] is "plan" or "run" or "resume")
            {
                if (args[1] == "plan")
                    return await WorkflowCommand.ExecuteAsync("plan", args[2..], cts.Token,
                        new ProfileWorkflowExecutor());
                if (args[1] == "resume")
                {
                    if (args.Length != 3) throw new ArgumentException("Usage: workflow resume <run-directory>");
                    var directory = Path.GetFullPath(args[2]);
                    var saved = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(
                        Path.Combine(directory, "run.json"), cts.Token), WorkflowJson.Default.WorkflowReport)
                        ?? throw new InvalidDataException("Invalid run report.");
                    return await WorkflowCommand.ExecuteAsync("run",
                        [saved.Workflow, "--resume-dir", directory], cts.Token, new ProfileWorkflowExecutor());
                }
                var history = HistoryOptions(args[2..]);
                return await RunAndIndexAsync(async onRun =>
                {
                    string? runDirectory = null;
                    var code = await WorkflowCommand.ExecuteAsync("run", history.Args, cts.Token,
                        new ProfileWorkflowExecutor(), directory =>
                        { runDirectory = directory; onRun(directory); });
                    // Explicit profile nodes are captured as independent passes. Analyze
                    // their saved artifacts before History import; ordinary workflows do
                    // not invoke perf, WPR or the analyzer.
                    if (code == 0 && runDirectory is not null
                        && Directory.EnumerateFiles(runDirectory, "*.results.json").Any())
                    {
                        code = await ProfileAnalyzer.AnalyzeAsync(runDirectory, cts.Token,
                            emitResultPath: false);
                        Console.Error.WriteLine($"[CMakeWorkflow] Profile analysis: {Path.Combine(runDirectory, "analysis.json")}");
                    }
                    return code;
                }, history.Database, history.Disabled);
            }

            if (args.Length is 3 or 4 && args[0] == "workflow" && args[1] == "report"
                && (args.Length == 3 || args[3] == "--json"))
                return await WorkflowReportCommand.ExecuteAsync(args[2], cts.Token,
                    args.Length == 4);

            if (args.Length >= 2 && args[0] == "workflow" && args[1] == "sequence")
                return await TwoTargetWorkflow.ExecuteAsync(args[2..], cts.Token);

            if (args.Length < 2 || args[0] != "target" || args[1] is not ("build" or "invoke" or "run" or "resolve"))
            {
                Usage();
                return 2;
            }

            var command = args[1];
            string? buildDir = null, targetName = null, config = null, controller = null;
            bool prepared = false, json = false;
            var runArgs = new List<string>();
            for (var i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--build-dir" when i + 1 < args.Length: buildDir = args[++i]; break;
                    case "--target" when i + 1 < args.Length: targetName = args[++i]; break;
                    case "--config" when i + 1 < args.Length: config = args[++i]; break;
                    case "--controller-build-dir" when i + 1 < args.Length: controller = args[++i]; break;
                    case "--prepared": prepared = true; break;
                    case "--json" when command == "resolve": json = true; break;
                    case "--" when command == "run":
                        runArgs.AddRange(args[(i + 1)..]);
                        i = args.Length;
                        break;
                    default: throw new ArgumentException($"Invalid option: {args[i]}");
                }
            }
            if (string.IsNullOrWhiteSpace(buildDir) || string.IsNullOrWhiteSpace(targetName))
                throw new ArgumentException("--build-dir and --target are required.");
            if (command is "resolve" or "invoke" && prepared)
                throw new ArgumentException("--prepared is only valid with build/run.");

            buildDir = BuildTreeOwnership.CanonicalPath(buildDir);
            if (!prepared && command != "resolve") BuildTreeOwnership.RejectController(buildDir, controller);
            // Prepared calls run under the native caller's DEPENDS/ownership contract.
            // Taking our lock there could deadlock an explicit invoke of that bridge.
            using var ownership = !prepared && command != "resolve"
                ? await BuildTreeOwnership.AcquireAsync([buildDir], cts.Token) : null;
            var target = CMakeConfiguration.Resolve(
                new CMakeTargetRef(buildDir, targetName, config));
            if (command == "invoke" || command == "build" && !prepared)
            {
                var observed = await CMakeInspection.InspectAsync(buildDir, cts.Token);
                if (command == "build") CMakeInspection.RequireTarget(observed, target);
                var invocation = CMakeBuildCommand.Create(target);
                Console.Error.WriteLine(command == "invoke"
                    ? "[CMakeWorkflow] Explicit target invocation; arbitrary native commands may execute."
                    : "[CMakeWorkflow] Native build request; incremental verification belongs to CMake/backend.");
                await ProcessRunner.RunAsync(observed.CMakeExecutable ?? "cmake", invocation.Arguments, invocation.WorkingDirectory, cts.Token);
                return 0;
            }
            ICMakeBuildContext context = prepared
                ? new PreparedCMakeBuildContext()
                : new ExternalCMakeBuildContext();
            var artifact = command == "resolve"
                ? await CMakeFileApi.ResolveExecutableAsync(target, cts.Token)
                : await context.EnsureBuiltAsync(target, cts.Token);
            Console.Error.WriteLine($"[CMakeWorkflow] {target.Target} ({target.Configuration ?? "default"}) => {artifact.ExecutablePath}");
            if (json) Console.Out.WriteLine(JsonSerializer.Serialize(new TargetResolutionView(1,
                target.BuildDirectory, target.Target, target.Configuration, artifact.ExecutablePath),
                WorkflowProtocolJson.Default.TargetResolutionView));

            if (command == "run")
            {
                if (!File.Exists(artifact.ExecutablePath))
                    throw new FileNotFoundException("Built executable is missing.", artifact.ExecutablePath);
                await ProcessRunner.RunAsync(artifact.ExecutablePath, runArgs, target.BuildDirectory, cts.Token);
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[CMakeWorkflow] Canceled.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[CMakeWorkflow] {error.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecuteProfileAsync(string[] args, CancellationToken ct)
    {
        if (args.Length > 0 && args[0] == "replay")
        {
            var replayHistory = HistoryOptions(args);
            return await RunAndIndexAsync(onRun => ProfileReplayCommand.ExecuteAsync(replayHistory.Args, ct,
                onRun), replayHistory.Database, replayHistory.Disabled);
        }
        if (args.Length == 0 || args[0] != "run")
            return await ProfileCommand.ExecuteAsync(args, ct);
        var history = HistoryOptions(args);
        return await RunAndIndexAsync(onRun => ProfileCommand.ExecuteAsync(history.Args, ct,
            onRun), history.Database, history.Disabled);
    }

    private static (string[] Args, string? Database, bool Disabled) HistoryOptions(string[] args)
    {
        var clean = new List<string>(args.Length);
        string? database = null;
        var disabled = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") { clean.AddRange(args[i..]); break; }
            if (args[i] == "--no-history") { disabled = true; continue; }
            if (args[i] == "--history-db")
            {
                if (database is not null || i + 1 >= args.Length
                    || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("--history-db requires one database path.");
                database = args[++i];
                continue;
            }
            clean.Add(args[i]);
        }
        if (disabled && database is not null)
            throw new ArgumentException("--no-history and --history-db cannot be combined.");
        return (clean.ToArray(), database, disabled);
    }

    private static async Task<int> RunAndIndexAsync(Func<Action<string>, Task<int>> run,
        string? database, bool disabled)
    {
        string? directory = null;
        var result = 1;
        try { result = await run(path => directory = path); }
        finally
        {
            if (!disabled && directory is not null && (File.Exists(Path.Combine(directory, "run.json"))
                || Directory.EnumerateFiles(directory, "*.results.json").Any()))
            {
                try
                {
                    var path = database ?? HistoryStore.DefaultDatabasePath;
                    using var store = new HistoryStore(path);
                    var summary = store.Import(directory, recursive: false);
                    Console.Error.WriteLine($"[CMakeWorkflow] History: {summary} Database: {Path.GetFullPath(path)}");
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"[CMakeWorkflow] History indexing failed: {error.Message}");
                }
            }
        }
        return result;
    }

    private static void Usage() => Console.Error.WriteLine(
        "Usage: CMakeWorkflow host serve --endpoint PATH | host <doctor|fix> --endpoint PATH [--workspace DIR] [--profile NAME] [--json]; fix: [--yes|--dry-run] [--pm NAME] [--no-fallback]\n" +
        "Usage: CMakeWorkflow history <import|runs|show|metrics|query|compare|detect> ...\n" +
        "Usage: CMakeWorkflow env <doctor|plan|setup> REQUIREMENTS.json [--json] [--yes --plan-sha SHA256] [--allow-system-changes]\n" +
        "Usage: CMakeWorkflow tooling <list|doctor|plan|setup> [profile...] [--profile NAME] [--json] [--pm auto|none|apt|pacman|dnf|brew|winget] [--install-dir PATH] [--no-fallback] [--dry-run] [--yes]\n" +
        "Usage: CMakeWorkflow dagflow mutation run --suite NAME [--source-dir DIR]\n" +
        "       CMakeWorkflow dagflow fuzz seed --layer NAME [--source-dir DIR]\n" +
        "       CMakeWorkflow dagflow fuzz run --build-dir DIR --layer NAME [--duration SECONDS] [--corpus DIR] [--replay-only]\n" +
        "       CMakeWorkflow dagflow fuzz <triage|merge> --run-dir DIR [--coverage (merge only)]\n" +
        "       CMakeWorkflow dagflow weak-memory run --protocol NAME --relacy DIR\n" +
        "       CMakeWorkflow dagflow profile run --build-dir DIR --target NAME [--repeats N] [--backend auto|perf|etw|instruments] [--mode cpu|scheduler|counters|allocations]\n" +
        "       CMakeWorkflow dagflow profile run --variant NAME=DIR --variant NAME=DIR --target NAME [--repeats N]\n" +
        "       CMakeWorkflow dagflow profile analyze RUN_DIRECTORY\n" +
        "       CMakeWorkflow profile replay BASELINE_DIR CANDIDATE_DIR --node ID [--case NAME] [--harness INDEX] [--backend auto|perf|etw] [--results-dir DIR]\n" +
        "Usage: CMakeWorkflow context inspect BUILD_DIR [--json]\n" +
        "       CMakeWorkflow target <build|invoke|run|resolve> --build-dir DIR --target NAME [--config CONFIG] [--prepared] [--json for resolve] [-- executable-args...]\n" +
        "  build: request a known native executable/library target through CMake\n" +
        "  run:   build (or verify prepared target), then execute the binary\n" +
        "  resolve: only inspect the CMake File API codemodel; never build\n" +
        "  invoke: intentionally invoke any target, including utility/campaign targets\n" +
        "  --prepared: only for a CMake custom target with a DEPENDS edge\n" +
        "       CMakeWorkflow workflow sequence --build-dir DIR --first TARGET --second TARGET\n" +
        "         [--config CONFIG] [--first-arg ARG]... [--second-arg ARG]...\n" +
        "         [--controller-build-dir DIR] (only when invoked by a CMake build target)\n" +
        "  workflow sequence: build first, execute first, build second, execute second\n" +
        "       CMakeWorkflow workflow <plan|run> FILE [--only NODE]... [--jobs N] [--json] [--history-db PATH] [--no-history]\n" +
        "       CMakeWorkflow workflow report RUN_DIRECTORY [--json]\n" +
        "       CMakeWorkflow workflow resume RUN_DIRECTORY\n" +
        "       CMakeWorkflow workflow reconcile RUN_DIRECTORY [--node ID] --decision succeeded|retry\n" +
        "         --reason TEXT --expected-checkpoint SHA256 --confirm-stopped [--json]\n" +
        "         [--results-dir DIR] [--controller-build-dir DIR]\n" +
        "  plan: validate and show the selected DAG without building or running\n" +
        "  run: execute dependencies, retain logs and write run.json");
}
