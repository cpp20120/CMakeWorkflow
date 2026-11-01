using System.Text.RegularExpressions;

namespace CMakeWorkflow;

public static class TargetWorkflowBuilder
{
    // Pure manifest construction: the Host validates paths and persists a
    // committed snapshot before workflow/start admits any external processes.
    public static WorkflowDocument Build(string action, string buildDirectory,
        string? target, string? secondTarget, string? configuration,
        IReadOnlyList<string>? arguments, IReadOnlyList<string>? secondArguments,
        WorkflowToolchainBinding? toolchain = null)
    {
        if (action is not ("build" or "invoke" or "run" or "sequence"))
            throw new ArgumentException("Unsupported target action.");
        if (!Path.IsPathFullyQualified(buildDirectory))
            throw new ArgumentException("Build directory must be absolute.");
        static void Validate(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant))
                throw new ArgumentException("Invalid CMake target name.");
        }
        Validate(target);
        if (action == "sequence") Validate(secondTarget);
        else if (secondTarget is not null || (secondArguments?.Count ?? 0) != 0)
            throw new ArgumentException("Second target is valid only for sequence.");
        if (arguments?.Any(x => x is null) == true || secondArguments?.Any(x => x is null) == true)
            throw new ArgumentException("Target arguments cannot contain null elements.");
        if ((action is "build" or "invoke") && (arguments?.Count ?? 0) != 0)
            throw new ArgumentException("Only run and sequence accept executable arguments.");
        var nodes = new List<WorkflowNode>();
        if (action is "build" or "invoke")
            nodes.Add(new WorkflowNode { Id = "target", Kind = action, Context = "build", Target = target });
        else
        {
            nodes.Add(new WorkflowNode { Id = "build_1", Kind = "build", Context = "build", Target = target });
            nodes.Add(new WorkflowNode { Id = "run_1", Kind = "run", From = "build_1",
                DependsOn = ["build_1"], Arguments = arguments?.ToList() ?? [] });
            if (action == "sequence")
            {
                nodes.Add(new WorkflowNode { Id = "build_2", Kind = "build", Context = "build", Target = secondTarget,
                    DependsOn = ["run_1"] });
                nodes.Add(new WorkflowNode { Id = "run_2", Kind = "run", From = "build_2",
                    DependsOn = ["build_2"], Arguments = secondArguments?.ToList() ?? [] });
            }
        }
        return new WorkflowDocument
        {
            SchemaVersion = 2,
            Contexts = new Dictionary<string, WorkflowContext>(StringComparer.Ordinal)
            { ["build"] = new() { BuildDirectory = buildDirectory, Configuration = configuration,
                Mode = "adopt", ProvenancePolicy = "allowUnverified", Toolchain = toolchain } },
            Nodes = nodes
        };
    }
}
