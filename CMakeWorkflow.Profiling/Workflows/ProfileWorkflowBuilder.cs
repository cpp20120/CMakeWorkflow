using System.Globalization;
using System.Text.RegularExpressions;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling;
#pragma warning restore IDE0130

// Single typed graph builder for the CLI and the local Host. It does no I/O and
// does not start a build or profiler. The caller owns persistence and admission.
public static class ProfileWorkflowBuilder
{
    public static WorkflowDocument Build(IReadOnlyList<ProfileVariant> variants, string target,
        string? sourceDirectory, string? configuration, string? configurePreset,
        int repeats, double timeoutSeconds, IReadOnlyList<string> arguments,
        string backend = "auto", string mode = "cpu", string? capturePlatform = null)
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(arguments);
        if (variants.Count is < 1 or > 32)
        {
            throw new ArgumentException("Profile requires 1..32 variants.");
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("Profile target is required.");
        }

        if (repeats is < 1 or > 100)
        {
            throw new ArgumentException("--repeats must be between 1 and 100.");
        }

        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds is <= 0 or > 86400)
        {
            throw new ArgumentException("--timeout-seconds must be positive and at most one day.");
        }

        if (mode is not ("cpu" or "scheduler" or "counters" or "diagnostic"))
        {
            throw new ArgumentException("--mode must be cpu, scheduler, counters or diagnostic.");
        }

        if (backend is not ("auto" or "perf" or "etw" or "instruments"))
        {
            throw new ArgumentException("Unknown profiler backend.");
        }

        if (capturePlatform is not (null or "linux" or "windows" or "macos"))
        {
            throw new ArgumentException("Profile capture platform must be linux, windows or macos.");
        }

        if (capturePlatform is not null && backend != "auto" &&
            backend != (capturePlatform switch { "linux" => "perf", "windows" => "etw", _ => "instruments" }))
        {
            throw new ArgumentException("Profiler backend does not match the capture platform.");
        }

        if (configurePreset is not null && (variants.Count != 1 || sourceDirectory is null))
        {
            throw new ArgumentException("A configured profile requires one variant and a source directory.");
        }

        if (variants.Any(v => !Regex.IsMatch(v.Name, "^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)
                              || !Path.IsPathFullyQualified(v.BuildDirectory)))
        {
            throw new ArgumentException("Profile variants require lowercase names and absolute build directories.");
        }

        if (variants.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count() != variants.Count
            || variants.Select(v => Path.GetFullPath(v.BuildDirectory)).Distinct(WorkflowPlanner.PathComparer)
                .Count() != variants.Count)
        {
            throw new ArgumentException("Profile variants require unique names and build directories.");
        }

        string timeout = timeoutSeconds.ToString(CultureInfo.InvariantCulture);
        Dictionary<string, WorkflowContext> contexts = new(StringComparer.Ordinal);
        List<WorkflowNode> nodes = [];
        foreach (ProfileVariant variant in variants)
        {
            contexts[variant.Name] = new WorkflowContext
            {
                BuildDirectory = variant.BuildDirectory,
                SourceDirectory = sourceDirectory,
                Configuration = configuration,
                ConfigurePreset = configurePreset,
                Mode = configurePreset is null ? "adopt" : "managed",
                ProvenancePolicy = configurePreset is null ? "allowUnverified" : "strict"
            };
            if (configurePreset is not null)
            {
                nodes.Add(new WorkflowNode { Id = "configure", Kind = "configure", Context = variant.Name });
            }

            nodes.Add(new WorkflowNode
            {
                Id = "build_" + variant.Name,
                Kind = "build",
                Context = variant.Name,
                Target = target,
                DependsOn = configurePreset is null ? [] : ["configure"]
            });
        }

        List<string> allBuilds = [.. nodes.Where(x => x.Kind == "build").Select(x => x.Id)];
        string previous = nodes.Last().Id;
        bool firstTiming = true;
        for (int repeat = 1; repeat <= repeats; repeat++)
        {
            // Alternate variants between timing rounds to reduce thermal drift.
            IEnumerable<ProfileVariant> order = repeat % 2 == 1 ? variants : variants.Reverse();
            foreach (ProfileVariant? variant in order)
            {
                string buildId = "build_" + variant.Name;
                string id = $"timing_{variant.Name}_{repeat}";
                nodes.Add(new WorkflowNode
                {
                    Id = id,
                    Kind = "run",
                    From = buildId,
                    DependsOn = firstTiming ? [.. allBuilds] : [previous],
                    Arguments = [.. arguments],
                    TimeoutSeconds = timeoutSeconds,
                    Resources = ["measurement-host"]
                });
                previous = id;
                firstTiming = false;
            }
        }

        bool diagnostic = mode == "diagnostic";
        bool linux = capturePlatform is null ? OperatingSystem.IsLinux() : capturePlatform == "linux";
        bool windows = capturePlatform is null ? OperatingSystem.IsWindows() : capturePlatform == "windows";
        if (linux && (backend is "auto" or "perf") && mode != "counters")
        {
            foreach (ProfileVariant variant in variants)
            {
                string id = "counters_" + variant.Name;
                nodes.Add(ProfileNode(id, "build_" + variant.Name, previous,
                    arguments, timeoutSeconds, timeout, backend, "counters"));
                previous = id;
            }
        }

        foreach (ProfileVariant variant in variants)
        {
            string buildId = "build_" + variant.Name;
            string captureId = (mode == "counters" ? "counters_" : "capture_") + variant.Name;
            nodes.Add(ProfileNode(captureId, buildId, previous, arguments, timeoutSeconds, timeout,
                backend, diagnostic ? "cpu" : mode));
            previous = captureId;
            if (diagnostic && (linux || windows))
            {
                string schedulerId = "scheduler_" + variant.Name;
                nodes.Add(ProfileNode(schedulerId, buildId, previous, arguments,
                    timeoutSeconds, timeout, backend, "scheduler"));
                previous = schedulerId;
            }
        }

        return new WorkflowDocument { SchemaVersion = 2, Contexts = contexts, Nodes = nodes };
    }

    private static WorkflowNode ProfileNode(string id, string buildId, string after,
        IReadOnlyList<string> arguments, double seconds, string timeout, string backend, string mode) =>
        new()
        {
            Id = id,
            Kind = "profile",
            From = buildId,
            DependsOn = [after],
            Arguments = [.. arguments],
            TimeoutSeconds = seconds + 180,
            Exclusive = true,
            Resources = ["measurement-host"],
            Options = new Dictionary<string, string>
            {
                ["backend"] = backend,
                ["mode"] = mode,
                ["captureTimeoutSeconds"] = timeout
            }
        };
}