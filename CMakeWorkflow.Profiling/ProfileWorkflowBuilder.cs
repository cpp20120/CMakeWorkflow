using System.Globalization;
using System.Text.RegularExpressions;

namespace CMakeWorkflow.Profiling;

public sealed record ProfileVariant(string Name, string BuildDirectory);

// Single typed graph builder for the CLI and the local Host. It does no I/O and
// does not start a build or profiler. The caller owns persistence and admission.
public static class ProfileWorkflowBuilder
{
    public static WorkflowDocument Build(IReadOnlyList<ProfileVariant> variants, string target,
        string? sourceDirectory, string? configuration, string? configurePreset,
        int repeats, double timeoutSeconds, IReadOnlyList<string> arguments,
        string backend = "auto", string mode = "cpu")
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(arguments);
        if (variants.Count is < 1 or > 32) throw new ArgumentException("Profile requires 1..32 variants.");
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Profile target is required.");
        if (repeats is < 1 or > 100) throw new ArgumentException("--repeats must be between 1 and 100.");
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds is <= 0 or > 86400)
            throw new ArgumentException("--timeout-seconds must be positive and at most one day.");
        if (mode is not ("cpu" or "scheduler" or "counters" or "diagnostic"))
            throw new ArgumentException("--mode must be cpu, scheduler, counters or diagnostic.");
        if (backend is not ("auto" or "perf" or "etw" or "instruments"))
            throw new ArgumentException("Unknown profiler backend.");
        if (configurePreset is not null && (variants.Count != 1 || sourceDirectory is null))
            throw new ArgumentException("A configured profile requires one variant and a source directory.");
        if (variants.Any(v => !Regex.IsMatch(v.Name, "^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)
            || !Path.IsPathFullyQualified(v.BuildDirectory)))
            throw new ArgumentException("Profile variants require lowercase names and absolute build directories.");
        if (variants.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count() != variants.Count
            || variants.Select(v => Path.GetFullPath(v.BuildDirectory)).Distinct(WorkflowPlanner.PathComparer).Count() != variants.Count)
            throw new ArgumentException("Profile variants require unique names and build directories.");
        var timeout = timeoutSeconds.ToString(CultureInfo.InvariantCulture);
        var contexts = new Dictionary<string, WorkflowContext>(StringComparer.Ordinal);
        var nodes = new List<WorkflowNode>();
        foreach (var variant in variants)
        {
            contexts[variant.Name] = new WorkflowContext
            {
                BuildDirectory = variant.BuildDirectory, SourceDirectory = sourceDirectory,
                Configuration = configuration, ConfigurePreset = configurePreset,
                Mode = configurePreset is null ? "adopt" : "managed",
                ProvenancePolicy = configurePreset is null ? "allowUnverified" : "strict"
            };
            if (configurePreset is not null)
                nodes.Add(new WorkflowNode { Id = "configure", Kind = "configure", Context = variant.Name });
            nodes.Add(new WorkflowNode { Id = "build_" + variant.Name, Kind = "build",
                Context = variant.Name, Target = target,
                DependsOn = configurePreset is null ? [] : ["configure"] });
        }
        var allBuilds = nodes.Where(x => x.Kind == "build").Select(x => x.Id).ToList();
        var previous = nodes.Last().Id;
        var firstTiming = true;
        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            // Alternate variants between timing rounds to reduce thermal drift.
            var order = repeat % 2 == 1 ? variants : variants.Reverse();
            foreach (var variant in order)
            {
                var buildId = "build_" + variant.Name;
                var id = $"timing_{variant.Name}_{repeat}";
                nodes.Add(new WorkflowNode { Id = id, Kind = "run", From = buildId,
                    DependsOn = firstTiming ? allBuilds.ToList() : [previous],
                    Arguments = arguments.ToList(),
                    TimeoutSeconds = timeoutSeconds, Resources = ["measurement-host"] });
                previous = id;
                firstTiming = false;
            }
        }
        var diagnostic = mode == "diagnostic";
        if (OperatingSystem.IsLinux() && (backend is "auto" or "perf") && mode != "counters")
        {
            foreach (var variant in variants)
            {
                var id = "counters_" + variant.Name;
                nodes.Add(ProfileNode(id, "build_" + variant.Name, previous,
                    arguments, timeoutSeconds, timeout, backend, "counters"));
                previous = id;
            }
        }
        foreach (var variant in variants)
        {
            var buildId = "build_" + variant.Name;
            var captureId = (mode == "counters" ? "counters_" : "capture_") + variant.Name;
            nodes.Add(ProfileNode(captureId, buildId, previous, arguments, timeoutSeconds, timeout,
                backend, diagnostic ? "cpu" : mode));
            previous = captureId;
            if (diagnostic && (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()))
            {
                var schedulerId = "scheduler_" + variant.Name;
                nodes.Add(ProfileNode(schedulerId, buildId, previous, arguments,
                    timeoutSeconds, timeout, backend, "scheduler"));
                previous = schedulerId;
            }
        }
        return new WorkflowDocument { SchemaVersion = 2, Contexts = contexts, Nodes = nodes };
    }

    private static WorkflowNode ProfileNode(string id, string buildId, string after,
        IReadOnlyList<string> arguments, double seconds, string timeout, string backend, string mode) =>
        new() { Id = id, Kind = "profile", From = buildId, DependsOn = [after],
            Arguments = arguments.ToList(), TimeoutSeconds = seconds + 180,
            Exclusive = true, Resources = ["measurement-host"],
            Options = new Dictionary<string, string> { ["backend"] = backend,
                ["mode"] = mode, ["captureTimeoutSeconds"] = timeout } };
}
