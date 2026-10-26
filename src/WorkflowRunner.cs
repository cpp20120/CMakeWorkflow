using System.Diagnostics;
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
}

public sealed record NativeBuildResult(CMakeTargetRef Target, string Verification,
    int BackendExitCode, string? ObservationIndex, GenerationObservation? Generation = null);
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
    public int? ExitCode { get; set; }
    public string? Executable { get; set; }
    public string? ExecutableSha256 { get; set; }
    public List<string> Arguments { get; set; } = [];
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
}

public static class WorkflowCommand
{
    public static async Task<int> ExecuteAsync(string command, string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
            throw new ArgumentException("workflow plan/run requires a JSON file.");
        var only = new List<string>();
        var jobs = 1;
        string? results = null, controller = null;
        for (var i = 1; i < args.Length; ++i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
            switch (args[i])
            {
                case "--only": only.Add(args[++i]); break;
                case "--jobs":
                    if (!int.TryParse(args[++i], out jobs) || jobs <= 0)
                        throw new ArgumentException("--jobs must be a positive integer.");
                    break;
                case "--results-dir": results = args[++i]; break;
                case "--controller-build-dir": controller = args[++i]; break;
                default: throw new ArgumentException($"Invalid workflow option: {args[i]}");
            }
        }
        var plan = await WorkflowPlanner.LoadAsync(args[0], only, controller, ct);
        foreach (var node in plan.Nodes)
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
        var output = Path.Combine(Path.GetFullPath(results),
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        Console.Error.WriteLine($"[CMakeWorkflow] Results: {output}");
        return await WorkflowRunner.RunAsync(plan, jobs, output, ct);
    }
}

public static class WorkflowRunner
{
    public static async Task<int> RunAsync(WorkflowPlan plan, int jobs, string output, CancellationToken ct)
    {
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
        var states = report.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var sessions = plan.Nodes.Select(n => n.Target.BuildDirectory).Distinct(WorkflowPlanner.PathComparer)
            .ToDictionary(p => p, _ => new BuildContextSession(), WorkflowPlanner.PathComparer);
        var remaining = plan.Nodes.ToList();
        var active = new Dictionary<string, (PlannedNode Node, Task<NodeResult> Task)>(StringComparer.Ordinal);
        BuildTreeOwnership? ownership = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await SaveAsync(report, output);
        try
        {
            var mutationTrees = plan.Nodes.Where(n => n.Definition.Kind != "inspect").Select(n => n.Target.BuildDirectory).ToArray();
            if (mutationTrees.Length != 0)
                ownership = await BuildTreeOwnership.AcquireAsync(mutationTrees, stop.Token);

            while (remaining.Count > 0 || active.Count > 0)
            {
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
                foreach (var node in candidates)
                {
                    if (ct.IsCancellationRequested || active.Count >= jobs) break;
                    if (active.Values.Any(a => Conflicts(node, a.Node))) continue;
                    var state = states[node.Definition.Id];
                    state.Status = "running";
                    state.StartedAt = DateTimeOffset.UtcNow;
                    remaining.Remove(node);
                    Console.Error.WriteLine($"[CMakeWorkflow] START {node.Definition.Id}");
                    // Execution works on a separate record; report snapshots are written
                    // only by the scheduler, never concurrently with worker mutations.
                    // Snapshot bytes only for builds that have a selected dependent run.
                    // Build-only workflows need not hash potentially large binaries.
                    var snapshot = node.Definition.Kind == "build" && plan.Nodes.Any(p =>
                        p.Definition.Kind == "run" && p.Definition.From == node.Definition.Id);
                    active.Add(node.Definition.Id, (node, ExecuteAsync(node, node.Definition.From is { } source ? states[source] : null,
                        snapshot, sessions[node.Target.BuildDirectory], output, stop.Token)));
                }
                await SaveAsync(report, output);
                if (active.Count == 0)
                {
                    if (remaining.Count > 0)
                        throw new InvalidOperationException("No schedulable workflow node.");
                    break;
                }
                await Task.WhenAny(active.Values.Select(a => a.Task));
                foreach (var pair in active.Where(p => p.Value.Task.IsCompleted).ToArray())
                {
                    var result = await pair.Value.Task;
                    states[pair.Key] = result;
                    report.Nodes[report.Nodes.FindIndex(n => n.Id == pair.Key)] = result;
                    active.Remove(pair.Key);
                    Console.Error.WriteLine($"[CMakeWorkflow] {result.Status.ToUpperInvariant()} {pair.Key}" +
                        (result.Error is null ? "" : $": {result.Error}"));
                }
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
        report.Status = ct.IsCancellationRequested ? "canceled"
            : report.Error is null && report.Nodes.All(n => n.Status == "succeeded") ? "succeeded" : "failed";
        await SaveAsync(report, output);
        return report.Status == "canceled" ? 130 : report.Status == "succeeded" ? 0 : 1;
    }

    private static bool Conflicts(PlannedNode left, PlannedNode right) =>
        left.Definition.Exclusive || right.Definition.Exclusive
        || WorkflowPlanner.PathComparer.Equals(left.Target.BuildDirectory, right.Target.BuildDirectory)
        || left.Definition.Resources.Intersect(right.Definition.Resources, StringComparer.Ordinal).Any();

    private static async Task<NodeResult> ExecuteAsync(PlannedNode node, NodeResult? prerequisite,
        bool snapshotExecutable, BuildContextSession session, string output, CancellationToken ct)
    {
        var definition = node.Definition;
        var result = new NodeResult
        {
            Id = definition.Id, Kind = definition.Kind, Dependencies = node.Dependencies.ToList(),
            BuildDirectory = node.Target.BuildDirectory, Configuration = node.Target.Configuration,
            Target = node.Target.Target, WorkingDirectory = node.WorkingDirectory, RequestedContext = node.Requested,
            StartedAt = DateTimeOffset.UtcNow, Status = "running",
            Stdout = definition.Id + ".stdout.log", Stderr = definition.Id + ".stderr.log"
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
                        if (prerequisite?.BuildVerification is not { Verification: "BackendSucceeded" } verified
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
                result.ExitCode = await RunProcessAsync(result, definition.Environment, output, deadline.Token);
                result.Status = result.ExitCode == 0 ? "succeeded" : "failed";
                if (result.ExitCode != 0) result.Error = $"Process exited with code {result.ExitCode}.";
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
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.Status = ct.IsCancellationRequested ? "canceled" : "failed";
            result.Error = ct.IsCancellationRequested ? "Canceled." : $"Timed out after {definition.TimeoutSeconds} seconds.";
            if (definition.Kind is "run" or "ctest" or "invoke" && result.Executable is not null)
                result.ExperimentResult = ct.IsCancellationRequested ? "canceled" : "timedOut";
        }
        catch (Exception error)
        {
            result.Status = "failed";
            result.Error = error.Message;
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
        using var process = Process.Start(info) ?? throw new IOException($"Could not start {result.Executable}.");
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
            if (!process.HasExited)
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

    private static async Task SaveAsync(WorkflowReport report, string output)
    {
        var path = Path.Combine(output, "run.json");
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, report, WorkflowJson.Default.WorkflowReport);
        File.Move(temporary, path, overwrite: true);
    }
}
