using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed record PlanDiagnostic(string Severity, string Message);

public sealed record PlannedNode(
    WorkflowNode Definition,
    CMakeTargetRef Target,
    string WorkingDirectory,
    IReadOnlyList<string> Dependencies,
    RequestedBuildContext Requested,
    ObservedBuildContext Observed,
    IReadOnlyList<PlanDiagnostic> Diagnostics,
    WorkflowToolchainBinding? Toolchain = null);

public sealed record WorkflowPlan(
    string File,
    string ManifestHash,
    IReadOnlyList<PlannedNode> Nodes);

public static class WorkflowPlanner
{
    public static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static async Task<WorkflowPlan> LoadAsync(string file, IReadOnlyList<string> only,
        string? controllerDirectory, CancellationToken ct, Action<string>? readGuard = null)
    {
        file = Path.GetFullPath(file);
        readGuard?.Invoke(file);
        var bytes = await System.IO.File.ReadAllBytesAsync(file, ct);
        return await LoadSnapshotAsync(file, bytes, only, controllerDirectory, ct, readGuard);
    }

    // Parse a durably accepted manifest snapshot while preserving its ORIGINAL path
    // as the base for all relative CMake source/build/working directories.
    // The snapshot bytes must be verified by the durable acceptance store before use.
    public static async Task<WorkflowPlan> LoadSnapshotAsync(string originalManifestPath, byte[] snapshot,
        IReadOnlyList<string> only, string? controllerDirectory, CancellationToken ct,
        Action<string>? readGuard = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var file = Path.GetFullPath(originalManifestPath);
        readGuard?.Invoke(file);
        ct.ThrowIfCancellationRequested();
        var bytes = snapshot;
        var document = JsonSerializer.Deserialize(bytes, WorkflowJson.Default.WorkflowDocument)
                       ?? throw new ArgumentException("Empty workflow document.");
        if (document.SchemaVersion is not (1 or 2))
            throw new ArgumentException("Workflow schemaVersion must be 1 or 2.");
        if (document.Contexts is null || document.Nodes is null || document.Nodes.Count == 0)
            throw new ArgumentException("Workflow requires contexts and a nonempty nodes array.");

        var nodes = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var node in document.Nodes)
        {
            if (node is null || !IsIdentifier(node.Id) || !nodes.TryAdd(node.Id, node))
                throw new ArgumentException(
                    "Node IDs must be unique nonempty identifiers (letters, digits, '.', '_', '-').");
            ValidateNode(node);
        }

        var dependencies = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var node in document.Nodes)
        {
            var deps = node.DependsOn.ToList();
            foreach (var input in node.Inputs)
            {
                if (input.From is null) continue;
                if (!nodes.TryGetValue(input.From, out var producer)
                    || !producer.Outputs.Any(output => output.Name == input.Artifact))
                    throw new ArgumentException($"{node.Id}: artifact reference must name a declared producer output.");
                deps.Add(input.From);
            }

            if (node.Kind is "run" or "profile")
            {
                if (node.From is null || !nodes.TryGetValue(node.From, out var source) || source.Kind != "build")
                    throw new ArgumentException($"{node.Id}: from must refer to a build node.");
                deps.Add(node.From);
            }
            else if (node.Context is null || !document.Contexts.ContainsKey(node.Context))
                throw new ArgumentException($"{node.Id}: unknown context '{node.Context}'.");

            foreach (var dep in deps)
                if (!nodes.ContainsKey(dep))
                    throw new ArgumentException($"{node.Id}: unknown dependency '{dep}'.");
            dependencies[node.Id] = deps.Distinct(StringComparer.Ordinal).ToArray();
        }

        // Validate the whole graph, including unselected branches, before any execution.
        var ordered = new List<WorkflowNode>();
        var remaining = document.Nodes.ToList();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(n => dependencies[n.Id].All(visited.Contains)).ToArray();
            if (ready.Length == 0)
                throw new ArgumentException(
                    $"Workflow contains a dependency cycle: {string.Join(", ", remaining.Select(n => n.Id))}");
            foreach (var node in ready)
            {
                ordered.Add(node);
                visited.Add(node.Id);
                remaining.Remove(node);
            }
        }

        var selected = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(only.Count == 0 ? nodes.Keys : only);
        while (pending.TryPop(out var id))
        {
            if (!nodes.ContainsKey(id)) throw new ArgumentException($"Unknown selected node '{id}'.");
            if (!selected.Add(id)) continue;
            foreach (var dep in dependencies[id]) pending.Push(dep);
        }

        var directory = Path.GetDirectoryName(file)!;
        foreach (var node in ordered.Where(n => selected.Contains(n.Id)))
        {
            foreach (var input in node.Inputs.Where(x => x.Path is not null))
                readGuard?.Invoke(WorkflowArtifacts.ManifestPath(directory, input.Path!));
            foreach (var declared in node.Outputs.Where(x => x.Root == "manifest"))
                readGuard?.Invoke(WorkflowArtifacts.ManifestPath(directory, declared.Path));
        }

        var contexts =
            new Dictionary<string, (RequestedBuildContext Request, ObservedBuildContext Observed)>(StringComparer
                .Ordinal);
        var plan = new List<PlannedNode>();
        foreach (var node in ordered.Where(n => selected.Contains(n.Id)))
        {
            var owner = node.Kind is "run" or "profile" ? nodes[node.From!] : node;
            var contextName = owner.Context!;
            if (!contexts.TryGetValue(contextName, out var context))
            {
                var spec = document.Contexts[contextName];
                if (spec is null || string.IsNullOrWhiteSpace(spec.BuildDirectory))
                    throw new ArgumentException($"{contextName}: buildDirectory is required.");
                if (spec.ConfigurePreset is not null && (string.IsNullOrWhiteSpace(spec.ConfigurePreset)
                                                         || spec.ConfigurePreset.StartsWith('-') ||
                                                         spec.ConfigurePreset.Contains('\0')))
                    throw new ArgumentException($"{contextName}: configurePreset must be a literal preset name.");
                if (spec.ProvenancePolicy is not ("strict" or "allowUnverified"))
                    throw new ArgumentException($"{contextName}: provenancePolicy must be strict or allowUnverified.");
                if (spec.Mode is not (null or "observe" or "adopt" or "managed"))
                    throw new ArgumentException($"{contextName}: mode must be observe, adopt or managed.");
                if (spec.Toolchain is { } selectedToolchain && spec.Generator is { } identity
                                                            && !string.Equals(selectedToolchain.Generator, identity,
                                                                StringComparison.Ordinal))
                    throw new ArgumentException(
                        $"{contextName}: toolchain generator disagrees with context.generator.");
                var path = BuildTreeOwnership.CanonicalPath(Path.IsPathFullyQualified(spec.BuildDirectory)
                    ? spec.BuildDirectory
                    : Path.Combine(directory, spec.BuildDirectory));
                var source = spec.SourceDirectory is null
                    ? null
                    : BuildTreeOwnership.CanonicalPath(
                        Path.IsPathFullyQualified(spec.SourceDirectory)
                            ? spec.SourceDirectory
                            : Path.Combine(directory, spec.SourceDirectory));
                readGuard?.Invoke(path);
                if (source is not null) readGuard?.Invoke(source);
                var observed = await CMakeInspection.InspectAsync(path, ct, readGuard);
                var hasConfigure = ordered.Any(n =>
                    selected.Contains(n.Id) && n.Kind == "configure" && n.Context == contextName);
                // A configure barrier opts into Managed. An unmanaged tree is
                // observation-only until mode: adopt is explicitly declared;
                // AllowUnverified changes the provenance policy, not that boundary.
                var mode = spec.Mode ?? (hasConfigure || observed.ManagedConfigure is not null
                    ? "managed"
                    : "observe");
                context = (new RequestedBuildContext(path, spec.Configuration, spec.ConfigurePreset, source,
                    spec.ProvenancePolicy, mode, spec.Generator), observed);
                contexts.Add(contextName, context);
            }

            var target = new CMakeTargetRef(context.Request.BuildDirectory, owner.Target ?? "",
                context.Request.Configuration);
            if (node.Kind != "inspect") BuildTreeOwnership.RejectController(target.BuildDirectory, controllerDirectory);
            var ancestors = Ancestors(node.Id, dependencies);
            var deferred = plan.Any(n => ancestors.Contains(n.Definition.Id) && n.Definition.Kind == "configure"
                                                                             && PathComparer.Equals(
                                                                                 n.Target.BuildDirectory,
                                                                                 target.BuildDirectory));
            var diagnostics = new List<PlanDiagnostic>();
            diagnostics.Add(new("Observed",
                $"Session mode={context.Request.Mode}; policy={context.Request.ProvenancePolicy}; generation={context.Observed.IndexFile ?? "Unknown"}; provenance={context.Observed.PresetState}."));
            foreach (var message in context.Observed.Diagnostics) diagnostics.Add(new("Unverified", message));
            if (node.Kind == "inspect")
            {
                // Observe reports known conflicts, but absence of evidence stays
                // Unknown rather than forcing configure or a mutation lease.
                if (context.Request.SourceDirectory is { } requestedSource && context.Observed.SourceDirectory is
                                                                               { } observedSource
                                                                           && !PathComparer.Equals(requestedSource,
                                                                               BuildTreeOwnership.CanonicalPath(
                                                                                   observedSource)))
                    diagnostics.Add(new("Error",
                        "Requested source directory disagrees with the observed build context."));
                if (context.Request.Generator is { } requestedGenerator && context.Observed.Generator is
                                                                            { } observedGenerator
                                                                        && requestedGenerator != observedGenerator)
                    diagnostics.Add(new("Error", "Requested generator disagrees with the observed build context."));
                if (context.Request.Configuration is { } requestedConfiguration &&
                    context.Observed.Configurations.Count > 0
                    && !context.Observed.Configurations.Contains(requestedConfiguration, StringComparer.Ordinal))
                    diagnostics.Add(new("Error", "Requested configuration disagrees with the observed build context."));
            }

            if (context.Request.Mode == "adopt")
                diagnostics.Add(new("Unverified",
                    "Explicit adoption: preset provenance is not verified; observed source/configuration conflicts still block execution."));
            if (node.Kind == "configure")
            {
                if (context.Request.Mode != "managed")
                    diagnostics.Add(new("Error",
                        "Explicit configure requires mode: managed (omit mode to infer it from a configure node)."));
                if (context.Request.SourceDirectory is null || !Directory.Exists(context.Request.SourceDirectory))
                    diagnostics.Add(new("Error", "Configure requires an existing context.sourceDirectory."));
                diagnostics.Add(new("Runtime",
                    "Explicit CMake configure may execute project commands and change the build tree. Preset interpretation belongs to CMake."));
            }
            else if (node.Kind != "inspect")
            {
                try
                {
                    BuildContextSession.ValidateAdmission(context.Request, context.Observed);
                    target = CMakeInspection.Resolve(BuildContextSession.ResolutionRequest(context.Request),
                        context.Observed, target.Target);
                    if (node.Kind is "build" or "run" or "profile")
                    {
                        var observedTarget = CMakeInspection.RequireTarget(context.Observed, target,
                            node.Kind is "run" or "profile");
                        diagnostics.Add(new("Observed",
                            $"Target type: {observedTarget.Type} ({observedTarget.Evidence}); native dependencies/custom commands remain CMake-owned."));
                    }
                }
                catch (InvalidOperationException error)
                {
                    diagnostics.Add(new(deferred ? "Runtime" : "Error", error.Message));
                }

                if (deferred)
                    diagnostics.Add(new("Runtime",
                        "Re-inspect after the configure prerequisite; current observations do not predict its result."));
                diagnostics.Add(new("Runtime", node.Kind == "invoke"
                    ? "Intentional CMake target execution: may run arbitrary commands, fuzz campaigns or benchmarks."
                    : node.Kind == "build"
                        ? "CMake/backend verifies native rebuilding at runtime; no IsTargetUpToDate claim."
                        : "Execution result and artifact availability are determined at runtime."));
            }

            var workingDirectory = node.WorkingDirectory is null
                ? context.Request.BuildDirectory
                : BuildTreeOwnership.CanonicalPath(Path.IsPathFullyQualified(node.WorkingDirectory)
                    ? node.WorkingDirectory
                    : Path.Combine(directory, node.WorkingDirectory));
            if (node.WorkingDirectory is not null && !Directory.Exists(workingDirectory))
                diagnostics.Add(new("Runtime", "Working directory must exist when execution starts."));
            foreach (var input in node.Inputs)
                diagnostics.Add(new("Runtime",
                    $"Declared input {(input.Path ?? input.From + ":" + input.Artifact)}: content identity is checked before and after this node"
                    + (input.Snapshot ? "; a verified copy is retained in the run directory." : ".")));
            foreach (var output in node.Outputs)
                diagnostics.Add(new("Runtime",
                    $"Declared output {output.Name}: existence and content identity are checked after this node"
                    + (output.Fresh
                        ? "; pre-existing output is rejected."
                        : "; no freshness is inferred from existence.")));
            if (node.Constraints?.CpuSet is { } cpus)
                diagnostics.Add(new(OperatingSystem.IsLinux() ? "Runtime" : "Error",
                    OperatingSystem.IsLinux()
                        ? $"Required CPU set {cpus}: Linux taskset admission and allowed CPUs are checked before dispatch."
                        : "Required CPU affinity cannot be guaranteed on this host."));
            if (node.Constraints?.Profiler is { } profiler)
                diagnostics.Add(new("Runtime",
                    $"Required profiler {profiler.Backend}/{profiler.Mode}: the profiling extension must confirm capabilities before capture."));
            plan.Add(new PlannedNode(node, target, workingDirectory, dependencies[node.Id],
                context.Request, context.Observed, diagnostics, document.Contexts[contextName].Toolchain));
        }

        foreach (var tree in plan.Where(n => n.Definition.Kind != "inspect")
                     .GroupBy(n => n.Target.BuildDirectory, PathComparer))
        {
            if (tree.Select(n => (n.Requested.Mode, n.Requested.ProvenancePolicy)).Distinct().Count() != 1)
                throw new ArgumentException(
                    "Aliases of one physical build tree must use the same session mode and provenancePolicy.");
            if (tree.Select(n => n.Toolchain).Distinct().Count() != 1)
                throw new ArgumentException(
                    "Aliases of one physical build tree must use the same toolchain selection.");
        }

        // CTest does not build its executables. Require a declared build prerequisite.
        foreach (var node in plan.Where(n => n.Definition.Kind == "ctest"))
        {
            var ancestors = Ancestors(node.Definition.Id, dependencies);
            if (!plan.Any(n => ancestors.Contains(n.Definition.Id) && n.Definition.Kind is "build" or "invoke"
                                                                   && PathComparer.Equals(n.Target.BuildDirectory,
                                                                       node.Target.BuildDirectory)
                                                                   && n.Target.Configuration ==
                                                                   node.Target.Configuration))
                throw new ArgumentException(
                    $"{node.Definition.Id}: ctest requires a build ancestor or explicit invoke prerequisite in the same build tree and configuration.");
        }

        return new WorkflowPlan(file, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), plan);
    }

    private static HashSet<string> Ancestors(string node, IReadOnlyDictionary<string, string[]> dependencies)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(dependencies[node]);
        while (pending.TryPop(out var id))
            if (result.Add(id))
                foreach (var dep in dependencies[id])
                    pending.Push(dep);
        return result;
    }

    private static bool IsIdentifier(string? value) => !string.IsNullOrEmpty(value)
                                                       && value is not "." and not ".."
                                                       && value.All(c =>
                                                           char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static void ValidateNode(WorkflowNode n)
    {
        if (n.Kind is not ("inspect" or "configure" or "build" or "invoke" or "run" or "profile" or "ctest"))
            throw new ArgumentException($"{n.Id}: unknown workflow node kind.");
        if (n.DependsOn is null || n.Arguments is null || n.Environment is null || n.Resources is null ||
            n.Options is null
            || n.DependsOn.Any(string.IsNullOrWhiteSpace) || n.Arguments.Any(a => a is null || a.Contains('\0'))
            || n.Resources.Any(string.IsNullOrWhiteSpace)
            || n.Options.Any(e => string.IsNullOrWhiteSpace(e.Key) || e.Value is null || e.Value.Contains('\0'))
            || n.Environment.Any(e => string.IsNullOrEmpty(e.Key) || e.Key.Contains('=') || e.Key.Contains('\0')
                                      || e.Value is null || e.Value.Contains('\0')))
            throw new ArgumentException($"{n.Id}: invalid dependencies, arguments, environment or resources.");
        if (n.TimeoutSeconds is { } seconds && (!double.IsFinite(seconds) || seconds <= 0 || seconds > 86400 * 30))
            throw new ArgumentException($"{n.Id}: timeoutSeconds must be positive and at most 30 days.");
        if (n.Kind is "build" or "invoke" && (string.IsNullOrWhiteSpace(n.Target) || n.Target.StartsWith('-')
                || n.Target.Contains('\0') || n.From is not null || n.Arguments.Count > 0
                || n.WorkingDirectory is not null || n.Environment.Count > 0))
            throw new ArgumentException(
                $"{n.Id}: build/invoke requires target and context; run-only fields are not accepted.");
        if (n.Kind is "inspect" or "configure" && (n.Target is not null || n.From is not null || n.Arguments.Count > 0
                                                   || n.WorkingDirectory is not null || n.Environment.Count > 0))
            throw new ArgumentException(
                $"{n.Id}: inspect/configure uses context only; no execution recipe or configure flags are inferred.");
        if (n.Kind is "run" or "profile" && (n.Context is not null || n.Target is not null))
            throw new ArgumentException($"{n.Id}: {n.Kind} inherits target and context through from.");
        if (n.HarnessDirectory is not null)
        {
            if (n.Kind != "invoke" || string.IsNullOrWhiteSpace(n.HarnessDirectory)
                                   || Path.IsPathRooted(n.HarnessDirectory) || n.HarnessDirectory.Contains('\0')
                                   || n.HarnessDirectory.Split(['/', '\\']).Any(p => p is ".." or "." or ""))
                throw new ArgumentException(
                    $"{n.Id}: harnessDirectory requires an invoke node and a relative directory without traversal.");
        }

        if (n.Kind != "profile" && n.Options.Count > 0)
            throw new ArgumentException($"{n.Id}: options are only valid for profile.");
        if (n.Kind == "ctest" && (n.Target is not null || n.From is not null || n.Arguments.Count > 0))
            throw new ArgumentException(
                $"{n.Id}: ctest uses context, testRegex and labelRegex, not target/from/arguments.");
        if (n.Kind != "ctest" && (n.TestRegex is not null || n.LabelRegex is not null))
            throw new ArgumentException($"{n.Id}: testRegex and labelRegex are only valid for ctest.");
        if (n.Inputs is null || n.Outputs is null || n.Inputs.Any(x => x is null)
            || n.Outputs.Any(x => x is null))
            throw new ArgumentException($"{n.Id}: invalid artifact declarations.");
        if (n.Kind == "inspect" && n.Outputs.Count != 0)
            throw new ArgumentException($"{n.Id}: inspect cannot produce declared artifacts.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in n.Inputs)
        {
            if (input.Path is null && input.From is null || input.Path is not null
                                                         && (string.IsNullOrWhiteSpace(input.Path) ||
                                                             input.Path.Contains('\0'))
                                                         || input.From is not null && (!IsIdentifier(input.From) ||
                                                             !IsIdentifier(input.Artifact))
                                                         || input.From is null && input.Artifact is not null
                                                         || input.Sha256 is not null &&
                                                         !WorkflowArtifacts.ValidSha256(input.Sha256))
                throw new ArgumentException($"{n.Id}: invalid input artifact declaration.");
        }

        foreach (var output in n.Outputs)
        {
            if (!IsIdentifier(output.Name) || !names.Add(output.Name)
                                           || string.IsNullOrWhiteSpace(output.Path) || output.Path.Contains('\0')
                                           || output.Root is not ("manifest" or "run")
                                           || output.Root == "run" && (Path.IsPathRooted(output.Path)
                                                                       || output.Path.Split(['/', '\\'])
                                                                           .Any(x => x is "" or "." or "..")))
                throw new ArgumentException($"{n.Id}: invalid output artifact declaration.");
        }

        if (n.Constraints is { } requirements)
        {
            if (n.Kind == "inspect" && (requirements.CpuSet is not null || requirements.Profiler is not null))
                throw new ArgumentException($"{n.Id}: inspect has no execution to constrain.");
            if (requirements.CpuSet is not null && n.Kind is not ("run" or "profile"))
                throw new ArgumentException(
                    $"{n.Id}: required CPU affinity is supported only for run and profile execution.");
            if (requirements.CpuSet is { } cpuSet && !WorkflowExecutionAdmission.ValidCpuSet(cpuSet))
                throw new ArgumentException($"{n.Id}: invalid required CPU set.");
            if (requirements.Profiler is { } profiler && (n.Kind != "profile"
                                                          || string.IsNullOrWhiteSpace(profiler.Backend) ||
                                                          profiler.Backend == "auto"
                                                          || string.IsNullOrWhiteSpace(profiler.Mode)
                                                          || profiler.EventSource is { Length: 0 }
                                                          || n.Options.GetValueOrDefault("backend", "auto") !=
                                                          profiler.Backend
                                                          || n.Options.GetValueOrDefault("mode", "cpu") !=
                                                          profiler.Mode))
                throw new ArgumentException($"{n.Id}: required profiler must match an explicit profile backend/mode.");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WorkflowDocument))]
[JsonSerializable(typeof(WorkflowReport))]
[JsonSerializable(typeof(WorkflowToolchainIdentity))]
[JsonSerializable(typeof(WorkflowToolchainBinding))]
[JsonSerializable(typeof(NodeResult))]
[JsonSerializable(typeof(ObservedBuildContext))]
[JsonSerializable(typeof(ConfigureProvenance))]
[JsonSerializable(typeof(List<string>))]
public partial class WorkflowJson : JsonSerializerContext;