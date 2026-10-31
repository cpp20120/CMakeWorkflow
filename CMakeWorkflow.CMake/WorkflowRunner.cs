using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed class WorkflowReport
{
    public int SchemaVersion { get; set; } = 1;
    public string Workflow { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "running";
    public List<NodeResult> Nodes { get; set; } = [];
    public string? Error { get; set; }
    public List<WorkflowFinding> Findings { get; set; } = [];
    public int ResumeCount { get; set; }
    public string? RecoveryPhase { get; set; }
    public List<WorkflowReconciliation> Reconciliations { get; set; } = [];
    public HostEnvironment? Environment { get; set; }
    public HostEnvironment? EnvironmentAtEnd { get; set; }
    public List<BuildEnvironment> BuildEnvironments { get; set; } = [];
}

public sealed record WorkflowFinding(string Code, string Severity, string NodeId,
    string Message, string[] SuggestedProfiles);

// A missing executable is a recoverable pre-execution admission failure, not
// proof that the workflow operation ran and failed.
public sealed class MissingWorkflowToolException(string executable, Exception inner)
    : IOException($"Required executable '{executable}' could not be started.", inner)
{
    public string Executable { get; } = executable;
}

public sealed record NativeBuildResult(CMakeTargetRef Target, string Verification,
    int? BackendExitCode, string? ObservationIndex, GenerationObservation? Generation = null);
public sealed record ArtifactReference(string BuildNode, CMakeTargetRef Target,
    string Executable, string Sha256, string Materialization);

public sealed class NodeResult
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<string> Dependencies { get; set; } = [];
    public string BuildDirectory { get; set; } = "";
    public string? Configuration { get; set; }
    public string Target { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public double? DurationSeconds { get; set; }
    public double? ProcessDurationSeconds { get; set; }
    public int? ExitCode { get; set; }
    public string? Executable { get; set; }
    public string? ExecutableSha256 { get; set; }
    public List<string> Arguments { get; set; } = [];
    public string? EnvironmentOverrideHash { get; set; }
    public string WorkingDirectory { get; set; } = "";
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public string? Error { get; set; }
    public RequestedBuildContext? RequestedContext { get; set; }
    public ObservedBuildContext? ObservedContext { get; set; }
    public ObservedBuildContext? ObservationAfter { get; set; }
    public NativeBuildResult? BuildVerification { get; set; }
    public ArtifactReference? Artifact { get; set; }
    public ConfigureProvenance? Configure { get; set; }
    public BuildContextSessionState? SessionBefore { get; set; }
    public BuildContextSessionState? SessionAfter { get; set; }
    public string ExperimentResult { get; set; } = "NotRun";
    public Dictionary<string, string> Outputs { get; set; } = [];
}

public static class WorkflowCommand
{
    public static async Task<int> ExecuteAsync(string command, string[] args, CancellationToken ct,
        IWorkflowOperationExecutor? extension = null, Action<string>? onRunDirectory = null)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
            throw new ArgumentException("workflow plan/run requires a JSON file.");
        var only = new List<string>();
        var jobs = 1;
        string? results = null, controller = null;
        var json = false;
        string? resumeDirectory = null;
        for (var i = 1; i < args.Length; ++i)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
            switch (args[i])
            {
                case "--only": only.Add(args[++i]); break;
                case "--jobs":
                    if (!int.TryParse(args[++i], out jobs) || jobs <= 0)
                        throw new ArgumentException("--jobs must be a positive integer.");
                    break;
                case "--results-dir": results = args[++i]; break;
                case "--resume-dir": resumeDirectory = args[++i]; break;
                case "--controller-build-dir": controller = args[++i]; break;
                default: throw new ArgumentException($"Invalid workflow option: {args[i]}");
            }
        }
        if (resumeDirectory is not null && (command != "run" || only.Count != 0 || results is not null))
            throw new ArgumentException("--resume-dir only supports workflow run without --only/--results-dir.");
        var plan = await WorkflowPlanner.LoadAsync(args[0], only, controller, ct);
        if (json && command == "plan")
        {
            var view = WorkflowQueries.PlanView(plan);
            Console.Out.WriteLine(JsonSerializer.Serialize(view, WorkflowProtocolJson.Default.WorkflowPlanView));
        }
        else foreach (var node in plan.Nodes)
        {
            Console.Error.WriteLine($"{node.Definition.Id}: {node.Definition.Kind} {node.Target.Target} " +
                $"[{node.Target.BuildDirectory}] requested-config={node.Requested.Configuration ?? "<unspecified>"} " +
                $"requested-preset={node.Requested.ConfigurePreset ?? "<unspecified>"} " +
                $"observed={node.Observed.State} configs=[{string.Join(", ", node.Observed.Configurations)}] " +
                $"after=[{string.Join(", ", node.Dependencies)}] resources=[{string.Join(", ", node.Definition.Resources)}]" +
                (node.Definition.Exclusive ? " exclusive" : ""));
            foreach (var diagnostic in node.Diagnostics)
                Console.Error.WriteLine($"  {diagnostic.Severity}: {diagnostic.Message}");
        }
        if (!json || command == "run")
            Console.Error.WriteLine("Scheduling: operations sharing a physical build tree or named resource conflict; exclusive nodes conflict with all nodes. Plain IDE/Ninja builds do not participate in tool locks.");
        // Both plan and run must reject a known-invalid selected graph *before*
        // creating result files, acquiring leases or launching independent branches.
        // A configure predecessor can defer checks to runtime (Severity=Runtime).
        var errors = plan.Nodes.Where(n => n.Diagnostics.Any(d => d.Severity == "Error")).ToArray();
        if (errors.Length != 0)
        {
            if (command == "run")
                Console.Error.WriteLine($"[CMakeWorkflow] Preflight rejected {errors.Length} node(s); no operations were launched.");
            return 1;
        }
        if (command == "plan") return 0;

        results ??= Path.Combine(Path.GetDirectoryName(plan.File)!, "out", "workflow-runs");
        var output = resumeDirectory is null
            ? Path.Combine(Path.GetFullPath(results), $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}")
            : Path.GetFullPath(resumeDirectory);
        Directory.CreateDirectory(output);
        onRunDirectory?.Invoke(output);
        Console.Error.WriteLine($"[CMakeWorkflow] Results: {output}");
        return await WorkflowRunner.RunAsync(plan, jobs, output, ct, extension, json, resumeDirectory is not null);
    }
}

public static class WorkflowRunner
{
    public static async Task<int> RunAsync(WorkflowPlan plan, int jobs, string output, CancellationToken ct,
        IWorkflowOperationExecutor? extension = null, bool eventsToStdout = false, bool resume = false,
        Func<string, CancellationToken, Task>? beforeExternalDispatch = null)
    {
        // The run lease spans validation, the recovery checkpoint, execution and final persistence.
        // No second runner may inspect/commit the same persisted run concurrently.
        Directory.CreateDirectory(output);
        await using var runLease = new FileStream(Path.Combine(output, ".workflow-run.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Validate resume observations under the same ownership used for dispatch.
        // Otherwise another cooperating runner can change the tree in between.
        using var resumedOwnership = resume ? await BuildTreeOwnership.AcquireAsync(
            plan.Nodes.Where(n => n.Definition.Kind != "inspect").Select(n => n.Target.BuildDirectory), ct) : null;
        var report = new WorkflowReport
        {
            Workflow = plan.File,
            ManifestHash = plan.ManifestHash,
            Nodes = plan.Nodes.Select(n => new NodeResult
            {
                Id = n.Definition.Id, Kind = n.Definition.Kind,
                Dependencies = n.Dependencies.ToList(), BuildDirectory = n.Target.BuildDirectory,
                Configuration = n.Target.Configuration, Target = n.Target.Target,
                WorkingDirectory = n.WorkingDirectory, RequestedContext = n.Requested
            }).ToList()
        };
        if (resume)
        {
            var file = Path.Combine(output, "run.json");
            if (!File.Exists(file)) throw new InvalidDataException("Run report not found for resume.");
            var saved = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(file, ct), WorkflowJson.Default.WorkflowReport)
                ?? throw new InvalidDataException("Invalid resume report.");
            if (saved.SchemaVersion != 1 || saved.Status is not ("needs_attention" or "running") || saved.ManifestHash != plan.ManifestHash ||
                !WorkflowPlanner.PathComparer.Equals(
                    BuildTreeOwnership.CanonicalPath(saved.Workflow), BuildTreeOwnership.CanonicalPath(plan.File)))
                throw new InvalidDataException("Resume requires a needs_attention checkpoint with identical workflow and manifest; interrupted running checkpoints can only be quarantined.");
            if (saved.Nodes.Count != report.Nodes.Count ||
                saved.Nodes.Where((n, i) => n.Id != report.Nodes[i].Id || n.Kind != report.Nodes[i].Kind ||
                    n.Target != report.Nodes[i].Target ||
                    !WorkflowPlanner.PathComparer.Equals(
                        BuildTreeOwnership.CanonicalPath(n.BuildDirectory),
                        BuildTreeOwnership.CanonicalPath(report.Nodes[i].BuildDirectory)) ||
                    n.Configuration != report.Nodes[i].Configuration ||
                    !n.Dependencies.SequenceEqual(report.Nodes[i].Dependencies)).Any())
                throw new InvalidDataException("Resume graph differs from saved execution.");
            // A write-ahead admission is not proof that a process started, nor
            // that it did not produce side effects. Classify before validating
            // old build/artifact observations: an interrupted operation may have
            // legitimately changed those. Never restore sessions or dispatch here.
            if (saved.Status == "running" || saved.Nodes.Any(n => n.Status is "running" or "outcome_unknown") ||
                saved.RecoveryPhase is "resuming" or "running")
            {
                foreach (var node in saved.Nodes.Where(n => n.Status == "running"))
                {
                    node.Status = "outcome_unknown";
                    node.Error = "Runner interrupted after dispatch admission; external side effects cannot be ruled out.";
                }
                saved.Status = "outcome_unknown";
                saved.Error = "Interrupted execution requires manual reconciliation; automatic resume is prohibited.";
                saved.RecoveryPhase = "interrupted";
                await WorkflowCheckpoint.SaveAsync(saved, output, ct);
                Console.Error.WriteLine($"[CMakeWorkflow] {saved.Error}");
                return 4;
            }
            // Compare the latest successful observation for each physical tree;
            // earlier configure/build nodes may legitimately observe older generations.
            foreach (var tree in saved.Nodes.Where(n => n.Status == "succeeded" && n.Kind != "inspect")
                         .GroupBy(n => n.BuildDirectory, WorkflowPlanner.PathComparer))
            {
                var last = tree.LastOrDefault(n => n.ObservationAfter is not null || n.ObservedContext is not null);
                var expected = last?.ObservationAfter ?? last?.ObservedContext;
                if (expected is null) throw new InvalidDataException($"Missing tree observation for '{tree.Key}'.");
                var observed = await CMakeInspection.InspectAsync(tree.Key, ct);
                if (GenerationObservation.From(expected) != GenerationObservation.From(observed) ||
                    expected.SourceDirectory != observed.SourceDirectory ||
                    expected.Generator != observed.Generator ||
                    expected.MultiConfig != observed.MultiConfig ||
                    !expected.Configurations.SequenceEqual(observed.Configurations))
                    throw new InvalidDataException($"Build tree changed since node '{last!.Id}'; resume refused.");
            }
            foreach (var node in saved.Nodes.Where(n => n.Status == "succeeded"))
            {
                if (node.Artifact is { } artifact)
                {
                    if (!File.Exists(artifact.Executable) ||
                        await HashExecutableAsync(artifact.Executable, ct) != artifact.Sha256)
                        throw new InvalidDataException($"Artifact changed since node '{node.Id}'; resume refused.");
                }
            }
            saved.ResumeCount++;
            saved.RecoveryPhase = "resuming";
            saved.Status = "running";
            saved.FinishedAt = null;
            saved.Error = null;
            saved.Findings.Clear();
            foreach (var node in saved.Nodes.Where(n => n.Status != "succeeded"))
            {
                // A run can only be resumed after a pre-execution admission failure.
                // Downstream nodes were never launched.
                if (node.Status is not ("needs_attention" or "pending" or "blocked"))
                    throw new InvalidDataException($"Unexpected non-resumable node '{node.Id}': {node.Status}.");
                node.Status = "pending";
                node.Error = null;
                node.StartedAt = null;
                node.FinishedAt = null;
            }
            report = saved;
        }
        try { report.Environment = EnvironmentProvenance.CaptureHost(); }
        catch (IOException error) { Console.Error.WriteLine($"[CMakeWorkflow] Environment provenance unavailable: {error.Message}"); }
        catch (UnauthorizedAccessException error) { Console.Error.WriteLine($"[CMakeWorkflow] Environment provenance unavailable: {error.Message}"); }
        var states = report.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var sessions = plan.Nodes.Select(n => n.Target.BuildDirectory).Distinct(WorkflowPlanner.PathComparer)
            .ToDictionary(p => p, _ => new BuildContextSession(), WorkflowPlanner.PathComparer);
        var remaining = plan.Nodes.Where(n => states[n.Definition.Id].Status != "succeeded").ToList();
        if (resume)
        {
            // Restore per-tree session admission state from the verified observations.
            foreach (var node in plan.Nodes.Where(n => states[n.Definition.Id].Status == "succeeded"))
            {
                var saved = states[node.Definition.Id];
                var observed = saved.ObservationAfter ?? saved.ObservedContext;
                if (observed is null || node.Definition.Kind == "inspect") continue;
                var session = sessions[node.Target.BuildDirectory];
                if (node.Definition.Kind == "configure") session.Configured(node.Requested, observed);
                else session.Observe(node.Requested, observed, "Resume");
            }
        }
        var active = new Dictionary<string, (PlannedNode Node, Task<NodeResult> Task)>(StringComparer.Ordinal);
        BuildTreeOwnership? ownership = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var events = new WorkflowEventJournal(output, eventsToStdout, resume ? report : null);
        await SaveAsync(report, output, events);
        try
        {
            var mutationTrees = plan.Nodes.Where(n => n.Definition.Kind != "inspect").Select(n => n.Target.BuildDirectory).ToArray();
            if (mutationTrees.Length != 0 && !resume)
                ownership = await BuildTreeOwnership.AcquireAsync(mutationTrees, stop.Token);

            while (remaining.Count > 0 || active.Count > 0)
            {
                if (report.Nodes.Any(n => n.Status == "needs_attention") && active.Count == 0)
                    break;
                foreach (var node in remaining.ToArray())
                {
                    var state = states[node.Definition.Id];
                    if (ct.IsCancellationRequested)
                    {
                        state.Status = "canceled";
                        state.Error = "Workflow canceled before this node started.";
                        remaining.Remove(node);
                    }
                    else if (node.Dependencies.Any(d => states[d].Status is "failed" or "blocked" or "canceled"))
                    {
                        state.Status = "blocked";
                        state.Error = "A prerequisite did not succeed.";
                        remaining.Remove(node);
                    }
                }

                var ready = remaining.Where(n => n.Dependencies.All(d => states[d].Status == "succeeded")).ToArray();
                var exclusive = ready.FirstOrDefault(n => n.Definition.Exclusive);
                // Once an exclusive node is ready, drain existing work before starting it.
                var candidates = exclusive is not null ? new[] { exclusive } : ready;
                // Reserve admission slots before launching anything. A persisted `running`
                // state is the write-ahead intent: after a crash its outcome is unknown.
                var dispatch = new List<(PlannedNode Node, bool Snapshot)>();
                foreach (var node in candidates)
                {
                    if (ct.IsCancellationRequested || active.Count + dispatch.Count >= jobs ||
                        report.Nodes.Any(n => n.Status == "needs_attention")) break;
                    if (active.Values.Any(a => Conflicts(node, a.Node)) ||
                        dispatch.Any(a => Conflicts(node, a.Node))) continue;
                    await RecoveryFaults.HitAsync("before-dispatch", node.Definition.Id, ct);
                    var state = states[node.Definition.Id];
                    state.Status = "running";
                    state.StartedAt = DateTimeOffset.UtcNow;
                    var logStem = LogStem(report, node.Definition.Id);
                    state.Stdout = logStem + ".stdout.log";
                    state.Stderr = logStem + ".stderr.log";
                    remaining.Remove(node);
                    Console.Error.WriteLine($"[CMakeWorkflow] START {node.Definition.Id}");
                    // Execution works on a separate record; report snapshots are written
                    // only by the scheduler, never concurrently with worker mutations.
                    // Snapshot bytes only for builds that have a selected dependent run.
                    // Build-only workflows need not hash potentially large binaries.
                    var snapshot = node.Definition.Kind == "build" && plan.Nodes.Any(p =>
                        p.Definition.Kind is "run" or "profile" && p.Definition.From == node.Definition.Id);
                    dispatch.Add((node, snapshot));
                }
                await SaveAsync(report, output, events);
                // The optional host-owned dispatch gate runs only after the runner's
                // write-ahead checkpoint is durably committed, and strictly before
                // any selected node starts an external process. CLI runs have no gate.
                if (dispatch.Count != 0 && beforeExternalDispatch is not null)
                    await beforeExternalDispatch(output, stop.Token);
                foreach (var (node, snapshot) in dispatch)
                {
                    await RecoveryFaults.HitAsync("after-checkpoint", node.Definition.Id, ct);
                    active.Add(node.Definition.Id, (node, ExecuteAsync(node, node.Definition.From is { } source ? states[source] : null,
                        snapshot, sessions[node.Target.BuildDirectory], output, LogStem(report, node.Definition.Id), stop.Token, extension)));
                }
                if (active.Count == 0)
                {
                    if (report.Nodes.Any(n => n.Status == "needs_attention")) break;
                    if (remaining.Count > 0)
                        throw new InvalidOperationException("No schedulable workflow node.");
                    break;
                }
                await Task.WhenAny(active.Values.Select(a => a.Task));
                foreach (var pair in active.Where(p => p.Value.Task.IsCompleted).ToArray())
                {
                    var result = await pair.Value.Task;
                    await RecoveryFaults.HitAsync("after-execution", pair.Key, ct);
                    states[pair.Key] = result;
                    report.Nodes[report.Nodes.FindIndex(n => n.Id == pair.Key)] = result;
                    active.Remove(pair.Key);
                    if (result.Status == "needs_attention")
                    {
                        report.Findings.Add(new WorkflowFinding("TOOL_EXECUTABLE_MISSING", "error", pair.Key,
                            result.Error ?? "Required executable is unavailable.",
                            result.Kind == "ctest" ? ["cmake"] : ["native"]));
                        // Independent work already launched is allowed to complete; no
                        // additional nodes start after this finding.
                    }
                    Console.Error.WriteLine($"[CMakeWorkflow] {result.Status.ToUpperInvariant()} {pair.Key}" +
                        (result.Error is null ? "" : $": {result.Error}"));
                }
                await SaveAsync(report, output, events);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var node in report.Nodes.Where(n => n.Status == "pending"))
                node.Status = "canceled";
        }
        catch (Exception error)
        {
            report.Error = error.Message;
            Console.Error.WriteLine($"[CMakeWorkflow] Workflow infrastructure failed: {error.Message}");
            foreach (var node in report.Nodes.Where(n => n.Status == "pending"))
            {
                node.Status = "blocked";
                node.Error = "Workflow infrastructure failed: " + error.Message;
            }
        }
        finally
        {
            // On report/IO failures, never leave launched processes or tree locks behind.
            await stop.CancelAsync();
            foreach (var pair in active)
            {
                var result = await pair.Value.Task;
                report.Nodes[report.Nodes.FindIndex(n => n.Id == pair.Key)] = result;
            }
            ownership?.Dispose();
        }
        report.FinishedAt = DateTimeOffset.UtcNow;
        report.RecoveryPhase = resume ? "finished" : report.RecoveryPhase;
        // An admission whose persistence failed must never appear as safely
        // replayable. No new worker starts before SaveAsync succeeds.
        if (report.Nodes.Any(n => n.Status == "running"))
        {
            foreach (var node in report.Nodes.Where(n => n.Status == "running"))
            {
                node.Status = "outcome_unknown";
                node.Error = "Admission did not commit reliably; manual reconciliation is required.";
            }
            report.RecoveryPhase = "interrupted";
        }
        report.Status = report.Nodes.Any(n => n.Status == "outcome_unknown") ? "outcome_unknown"
            : ct.IsCancellationRequested ? "canceled"
            : report.Nodes.Any(n => n.Status == "needs_attention") ? "needs_attention"
            : report.Error is null && report.Nodes.All(n => n.Status == "succeeded") ? "succeeded" : "failed";
        try
        {
            report.EnvironmentAtEnd = EnvironmentProvenance.CaptureHost();
            report.BuildEnvironments = report.Nodes.Select(n => (n.BuildDirectory, n.Configuration))
                .Where(x => !string.IsNullOrWhiteSpace(x.BuildDirectory)).Distinct()
                .Select(x => EnvironmentProvenance.CaptureBuild(x.BuildDirectory, x.Configuration))
                .OfType<BuildEnvironment>().ToList();
        }
        catch (IOException error) { Console.Error.WriteLine($"[CMakeWorkflow] Environment provenance unavailable: {error.Message}"); }
        catch (UnauthorizedAccessException error) { Console.Error.WriteLine($"[CMakeWorkflow] Environment provenance unavailable: {error.Message}"); }
        await SaveAsync(report, output, events);
        return report.Status == "canceled" ? 130 : report.Status == "succeeded" ? 0
            : report.Status == "needs_attention" ? 3 : report.Status == "outcome_unknown" ? 4 : 1;
    }

    private static bool Conflicts(PlannedNode left, PlannedNode right) =>
        left.Definition.Exclusive || right.Definition.Exclusive
        || WorkflowPlanner.PathComparer.Equals(left.Target.BuildDirectory, right.Target.BuildDirectory)
        || left.Definition.Resources.Intersect(right.Definition.Resources, StringComparer.Ordinal).Any();

    private static string LogStem(WorkflowReport report, string nodeId)
    {
        var retries = report.Reconciliations.Count(r => r.NodeId == nodeId && r.Decision == "retry");
        return retries == 0 ? nodeId : $"{nodeId}.attempt-{retries + 1}";
    }

    private static async Task<NodeResult> ExecuteAsync(PlannedNode node, NodeResult? prerequisite,
        bool snapshotExecutable, BuildContextSession session, string output, string logStem, CancellationToken ct,
        IWorkflowOperationExecutor? extension)
    {
        var definition = node.Definition;
        var result = new NodeResult
        {
            Id = definition.Id, Kind = definition.Kind, Dependencies = node.Dependencies.ToList(),
            BuildDirectory = node.Target.BuildDirectory, Configuration = node.Target.Configuration,
            Target = node.Target.Target, WorkingDirectory = node.WorkingDirectory, RequestedContext = node.Requested,
            EnvironmentOverrideHash = EnvironmentProvenance.FingerprintOverrides(definition.Environment),
            StartedAt = DateTimeOffset.UtcNow, Status = "running",
            Stdout = logStem + ".stdout.log", Stderr = logStem + ".stderr.log"
        };
        var timer = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (definition.TimeoutSeconds is { } seconds) deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            result.ObservedContext = await CMakeInspection.InspectAsync(node.Requested.BuildDirectory, deadline.Token);
            // Inspect never changes the execution session or claims ownership.
            result.SessionBefore = definition.Kind == "inspect"
                ? session.State(node.Requested with { Mode = "observe" }, result.ObservedContext)
                : session.Observe(node.Requested, result.ObservedContext, "BeforeOperation");
            if (definition.Kind == "inspect")
            {
                result.Status = "succeeded";
                result.SessionAfter = result.SessionBefore;
            }
            else if (definition.Kind == "configure")
            {
                session.BeginConfigure();
                await ConfigureAsync(node, result, session, output, deadline.Token);
            }
            else
            {
                // Re-inspect under ownership at every operation, including after configure.
                session.Validate(node.Requested, result.ObservedContext);
                var target = CMakeInspection.Resolve(BuildContextSession.ResolutionRequest(node.Requested), result.ObservedContext, node.Target.Target);
                result.Configuration = target.Configuration;
                switch (definition.Kind)
                {
                    case "build":
                    case "invoke":
                        if (definition.Kind == "build") CMakeInspection.RequireTarget(result.ObservedContext, target);
                        var command = CMakeBuildCommand.Create(target);
                        result.Executable = result.ObservedContext.CMakeExecutable ?? "cmake";
                        result.Arguments = command.Arguments.ToList();
                        break;
                    case "run":
                    case "profile":
                        if (prerequisite?.BuildVerification is not { Verification: "BackendSucceeded" or "ManuallyAccepted" } verified
                            || verified.Target != target)
                            throw new InvalidOperationException("Run requires a successful native build verification for this exact target/configuration.");
                        if (verified.Generation != GenerationObservation.From(result.ObservedContext))
                            throw new InvalidOperationException("Generation changed since the referenced build verification; add a new build before Run. Artifact existence does not verify the new buildsystem.");
                        var item = CMakeInspection.RequireTarget(result.ObservedContext, target, executableOnly: true);
                        if (item.Executable is null)
                            throw new InvalidOperationException("Executable artifact is Unknown/ambiguous; no path is guessed.");
                        if (prerequisite?.Artifact is not { } builtArtifact
                            || builtArtifact.Target != target
                            || !WorkflowPlanner.PathComparer.Equals(builtArtifact.Executable, item.Executable))
                            throw new InvalidOperationException("Run requires the executable artifact produced and recorded by its build node.");
                        var currentHash = await HashExecutableAsync(item.Executable, deadline.Token);
                        if (currentHash != builtArtifact.Sha256)
                            throw new InvalidOperationException("Executable changed since the referenced build node; add a new build dependency before Run.");
                        result.Executable = item.Executable;
                        result.ExecutableSha256 = currentHash;
                        result.Artifact = new ArtifactReference(definition.From!, target, item.Executable,
                            currentHash, "VerifiedAgainstBuild");
                        result.Arguments = definition.Arguments.ToList();
                        break;
                    case "ctest":
                        result.Executable = result.ObservedContext.CTestExecutable ?? "ctest";
                        result.Arguments = ["--test-dir", target.BuildDirectory, "--output-on-failure", "--no-tests=error"];
                        if (!string.IsNullOrEmpty(target.Configuration)) result.Arguments.AddRange(["-C", target.Configuration]);
                        if (definition.TestRegex is not null) result.Arguments.AddRange(["-R", definition.TestRegex]);
                        if (definition.LabelRegex is not null) result.Arguments.AddRange(["-L", definition.LabelRegex]);
                        break;
                }
                if (definition.Kind == "profile")
                {
                    if (extension is null || !extension.Supports("profile"))
                        throw new InvalidOperationException("Profile operation is unavailable in this CLI.");
                    var operation = await extension.ExecuteAsync(new WorkflowOperationRequest(
                        "profile", result.Executable!, result.Arguments, node.WorkingDirectory,
                        definition.Environment, definition.Options, output, definition.Id), deadline.Token);
                    result.ExitCode = operation.ExitCode;
                    result.Outputs = new Dictionary<string, string>(operation.Outputs);
                    result.ExperimentResult = operation.Status;
                    if (operation.Message is not null) result.Error = operation.Message;
                }
                else
                {
                    if (definition.Kind == "invoke" && definition.HarnessDirectory is not null)
                        DagFlowHarnessSnapshot.RequireFresh(node.Target.BuildDirectory, definition.HarnessDirectory);
                    result.ExitCode = await RunProcessAsync(result, definition.Environment, output, deadline.Token);
                }
                result.Status = result.ExitCode == 0 ? "succeeded" : "failed";
                if (result.ExitCode != 0) result.Error ??= $"Process exited with code {result.ExitCode}.";
                // The command's result remains factual even if subsequent context
                // validation rejects the node and blocks dependent execution.
                if (definition.Kind is "run" or "ctest" or "invoke") result.ExperimentResult = result.Status;
                if (definition.Kind == "build")
                {
                    result.BuildVerification = new(target, result.ExitCode == 0 ? "BackendSucceeded" : "BackendFailed",
                        result.ExitCode.Value, result.ObservedContext.IndexFile, GenerationObservation.From(result.ObservedContext));
                    if (result.ExitCode == 0)
                    {
                        result.ObservationAfter = await CMakeInspection.InspectAsync(target.BuildDirectory, deadline.Token);
                        result.BuildVerification = result.BuildVerification with { ObservationIndex = result.ObservationAfter.IndexFile,
                            Generation = GenerationObservation.From(result.ObservationAfter) };
                        result.SessionAfter = session.Observe(node.Requested, result.ObservationAfter, "AfterBuild");
                        // Classify actual target/configuration conflicts before applying
                        // provenance policy; a new generation alone is not a mismatch.
                        var after = CMakeInspection.Resolve(BuildContextSession.ResolutionRequest(node.Requested) with
                            { ProvenancePolicy = "allowUnverified" }, result.ObservationAfter, target.Target);
                        if (after != target) throw new InvalidOperationException("Build regenerated a different configuration; dependent execution is blocked.");
                        var builtTarget = CMakeInspection.RequireTarget(result.ObservationAfter, after);
                        var beforeTarget = CMakeInspection.RequireTarget(result.ObservedContext, target);
                        if (builtTarget.Type != beforeTarget.Type || builtTarget.Executable != beforeTarget.Executable)
                            throw new InvalidOperationException("Build regenerated a different target type or artifact path; dependent execution is blocked.");
                        session.Validate(node.Requested, result.ObservationAfter);
                        if (snapshotExecutable && builtTarget.Type == "EXECUTABLE" && builtTarget.Executable is { } executable
                            && File.Exists(executable))
                        {
                            var sha = await HashExecutableAsync(executable, deadline.Token);
                            result.Artifact = new ArtifactReference(definition.Id, target, executable,
                                sha, "VerifiedAfterBuild");
                            result.ExecutableSha256 = sha;
                        }
                    }
                }
                else if (definition.Kind == "invoke" && result.ExitCode == 0)
                {
                    result.ObservationAfter = await CMakeInspection.InspectAsync(target.BuildDirectory, deadline.Token);
                    result.SessionAfter = session.Observe(node.Requested, result.ObservationAfter, "AfterInvoke");
                    var after = CMakeInspection.Resolve(BuildContextSession.ResolutionRequest(node.Requested) with
                        { ProvenancePolicy = "allowUnverified" }, result.ObservationAfter, target.Target);
                    if (after != target) throw new InvalidOperationException("Invoke regenerated a different configuration; dependent execution is blocked.");
                    session.Validate(node.Requested, result.ObservationAfter);
                    // Only attach measurements after the CLI's provenance checks pass.
                    if (definition.HarnessDirectory is not null)
                        result.Outputs = await DagFlowHarnessSnapshot.CaptureAsync(
                            node.Target.BuildDirectory, definition.HarnessDirectory, output, definition.Id, deadline.Token);
                    else if (result.Stdout is not null && result.StartedAt is { } invokedAt)
                        result.Outputs = await DagFlowHarnessSnapshot.CaptureReportedAsync(
                            node.Target.BuildDirectory, Path.Combine(output, result.Stdout),
                            output, definition.Id, invokedAt, deadline.Token);
                }
            }
        }
        catch (MissingWorkflowToolException error)
        {
            result.Status = "needs_attention";
            result.Error = error.Message;
        }
        catch (OperationCanceledException)
        {
            result.Status = ct.IsCancellationRequested ? "canceled" : "failed";
            result.Error = ct.IsCancellationRequested ? "Canceled." : $"Timed out after {definition.TimeoutSeconds} seconds.";
            if (definition.Kind is "run" or "profile" or "ctest" or "invoke" && result.Executable is not null)
                result.ExperimentResult = ct.IsCancellationRequested ? "canceled" : "timedOut";
        }
        catch (Exception error)
        {
            result.Status = "failed";
            result.Error = error.Message;
            // Preserve the factual process result if later provenance or snapshot
            // validation rejects the node after the command has completed.
            if (definition.Kind is "run" or "profile" or "ctest" or "invoke"
                && result.ExperimentResult == "NotRun")
                result.ExperimentResult = "failed";
        }
        result.SessionAfter ??= session.State(node.Requested, result.ObservationAfter ?? result.ObservedContext ?? node.Observed);
        result.FinishedAt = DateTimeOffset.UtcNow;
        result.DurationSeconds = timer.Elapsed.TotalSeconds;
        return result;
    }

    private static async Task ConfigureAsync(PlannedNode node, NodeResult result, BuildContextSession session, string output, CancellationToken ct)
    {
        var request = node.Requested;
        if (request.SourceDirectory is null || !Directory.Exists(request.SourceDirectory))
            throw new InvalidOperationException("Configure requires an existing context.sourceDirectory.");
        // Context paths are explicit CLI arguments; CMake interprets the named preset.
        // No cache variables, preset inheritance or toolchain rules are reconstructed.
        Directory.CreateDirectory(request.BuildDirectory);
        result.Configure = new ConfigureProvenance
        {
            BuildDirectory = request.BuildDirectory, SourceDirectory = request.SourceDirectory,
            Preset = request.ConfigurePreset
        };
        result.Executable = result.ObservedContext?.CMakeExecutable ?? "cmake";
        result.WorkingDirectory = request.SourceDirectory;
        result.Arguments = [];
        if (request.ConfigurePreset is not null) result.Arguments.AddRange(["--preset", request.ConfigurePreset]);
        result.Arguments.AddRange(["-S", request.SourceDirectory, "-B", request.BuildDirectory]);
        result.Configure.Executable = result.Executable;
        result.Configure.WorkingDirectory = result.WorkingDirectory;
        result.Configure.Arguments = result.Arguments.ToList();
        await CMakeInspection.WriteProvenanceAsync(request.BuildDirectory, result.Configure);
        try
        {
            var query = Path.Combine(request.BuildDirectory, ".cmake", "api", "v1", "query", "client-cmake-workflow");
            Directory.CreateDirectory(query);
            foreach (var kind in new[] { "codemodel-v2", "cache-v2", "toolchains-v1" })
                await File.WriteAllTextAsync(Path.Combine(query, kind), "", ct);
            result.ExitCode = await RunProcessAsync(result, new Dictionary<string, string>(), output, ct);
            result.Configure.ExitCode = result.ExitCode;
            result.Configure.State = result.ExitCode == 0 ? "Succeeded" : "Failed";
            await CMakeInspection.WriteProvenanceAsync(request.BuildDirectory, result.Configure);
            result.ObservationAfter = await CMakeInspection.InspectAsync(request.BuildDirectory, ct);
            result.Configure.IndexFile = result.ObservationAfter.IndexFile;
            result.Configure.IndexHash = result.ObservationAfter.IndexHash;
            await CMakeInspection.WriteProvenanceAsync(request.BuildDirectory, result.Configure);
            result.ObservationAfter = await CMakeInspection.InspectAsync(request.BuildDirectory, ct);
            result.Status = result.ExitCode == 0 ? "succeeded" : "failed";
            if (result.ExitCode != 0) result.Error = $"Configure exited with code {result.ExitCode}.";
            else
            {
                // Verify requested selection against the new observation; never silently
                // replace a requested Debug/Release configuration with the generated one.
                CMakeInspection.ValidateIdentity(request, result.ObservationAfter);
                if (request.Configuration is not null || result.ObservationAfter.MultiConfig == false)
                    result.Configuration = CMakeInspection.Resolve(request, result.ObservationAfter).Configuration;
                session.Configured(request, result.ObservationAfter);
            }
        }
        catch
        {
            if (result.Configure.State is "Attempted" or "Succeeded")
            {
                result.Configure.State = result.Configure.State == "Attempted" ? "Interrupted" : "Rejected";
                await CMakeInspection.WriteProvenanceAsync(request.BuildDirectory, result.Configure);
            }
            throw;
        }
    }

    private static async Task<string> HashExecutableAsync(string executable, CancellationToken ct)
    {
        await using var file = File.OpenRead(executable);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
    }

    private static async Task<int> RunProcessAsync(NodeResult result,
        IReadOnlyDictionary<string, string> environment, string output, CancellationToken ct)
    {
        var info = new ProcessStartInfo(result.Executable!)
        {
            UseShellExecute = false, WorkingDirectory = result.WorkingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (var argument in result.Arguments) info.ArgumentList.Add(argument);
        foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        BuildTreeOwnership.PropagateTo(info);
        await using var stdout = File.Create(Path.Combine(output, result.Stdout!));
        await using var stderr = File.Create(Path.Combine(output, result.Stderr!));
        ct.ThrowIfCancellationRequested();
        var processTimer = Stopwatch.StartNew();
        Process process;
        try { process = Process.Start(info) ?? throw new MissingWorkflowToolException(result.Executable!,
            new IOException("Process.Start returned null.")); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        { throw new MissingWorkflowToolException(result.Executable!, error); }
        using (process)
        {
        // Windows Kill(entireProcessTree:true) does not wait for grandchildren.
        // A Job Object retains custody of descendants even if Ninja exits first.
        using var windowsJob = OperatingSystem.IsWindows() ? WindowsProcessJob.Attach(process) : null;
        process.StandardInput.Close();
        var copies = Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(stdout),
            process.StandardError.BaseStream.CopyToAsync(stderr));
        try
        {
            await process.WaitForExitAsync(ct);
            await copies.WaitAsync(ct);
            return process.ExitCode;
        }
        finally
        {
            processTimer.Stop();
            result.ProcessDurationSeconds = processTimer.Elapsed.TotalSeconds;
            if (windowsJob is not null && (ct.IsCancellationRequested || !process.HasExited))
            {
                // Terminate descendants only on cancellation/failure. Successful
                // executions must never query or terminate their job in cleanup.
                // Await the entire job, not only the direct Ninja/CMake process.
                await windowsJob.TerminateAndWaitAsync(CancellationToken.None);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            else if (windowsJob is null && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            // A detached descendant can keep pipes open after the direct child exits.
            // Closing readers also makes cancellation bounded in that case.
            if (!copies.IsCompleted)
            {
                process.StandardOutput.Close();
                process.StandardError.Close();
            }
            try { await copies; }
            catch (Exception error) when (ct.IsCancellationRequested && error is IOException or ObjectDisposedException) { }
        }
        }
    }

    private static async Task SaveAsync(WorkflowReport report, string output, WorkflowEventJournal events)
    {
        await WorkflowCheckpoint.SaveAsync(report, output, CancellationToken.None);
        events.Publish(report);
    }
}
