using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public static class WorkflowToolchainAdmission
{
    public static async Task<IReadOnlyDictionary<string, WorkflowToolchainRuntime>> ResolveAsync(
        WorkflowPlan plan, IWorkflowToolchainProvider? provider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Dictionary<string, WorkflowToolchainRuntime> selected = new(WorkflowPlanner.PathComparer);
        foreach (IGrouping<string, PlannedNode> tree in plan.Nodes.Where(n => n.Definition.Kind != "inspect" && n.Toolchain is not null)
                     .GroupBy(n => n.Target.BuildDirectory, WorkflowPlanner.PathComparer))
        {
            if (provider is null)
            {
                throw new InvalidOperationException(
                    "An explicit toolchain requires a platform execution environment provider.");
            }

            WorkflowToolchainBinding binding = tree.First().Toolchain!;
            if (tree.Any(x => x.Requested.ConfigurePreset is not null))
            {
                throw new InvalidOperationException(
                    "Selected toolchain cannot override configurePreset; use a separately configured tree.");
            }

            if (tree.Any(x => x.Toolchain != binding))
            {
                throw new InvalidDataException("One build tree has conflicting toolchain bindings.");
            }

            ct.ThrowIfCancellationRequested();
            WorkflowToolchainRuntime resolved = await provider.ResolveAsync(binding, ct);
            if (resolved.Generator != binding.Generator || string.IsNullOrWhiteSpace(resolved.Fingerprint)
                                                        || !Path.IsPathFullyQualified(resolved.CMake) ||
                                                        !Path.IsPathFullyQualified(resolved.Compiler)
                                                        || (resolved.Ninja is not null &&
                                                        !Path.IsPathFullyQualified(resolved.Ninja)))
            {
                throw new InvalidDataException("Toolchain provider returned an invalid or incompatible selection.");
            }

            selected.Add(tree.Key, resolved);
        }

        return selected;
    }

    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    public static List<WorkflowToolchainIdentity> Identities(
        IReadOnlyDictionary<string, WorkflowToolchainRuntime> selected) =>
        [.. selected.OrderBy(p => p.Key, WorkflowPlanner.PathComparer).Select(pair =>
            new WorkflowToolchainIdentity(pair.Key, pair.Value.CMake, pair.Value.Ninja,
                pair.Value.Compiler, pair.Value.Sdk, pair.Value.EnvironmentProfileId,
                pair.Value.Generator, pair.Value.Fingerprint))];

    public static void RequireSameIdentity(IReadOnlyList<WorkflowToolchainIdentity> recorded,
        IReadOnlyDictionary<string, WorkflowToolchainRuntime> selected)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        List<WorkflowToolchainIdentity> current = Identities(selected);
        if (recorded.Count != current.Count || recorded.Where((a, i) => a != current[i]).Any())
        {
            throw new InvalidDataException(
                "Toolchain/environment changed since the checkpoint. Resume is unsafe; reconcile explicitly.");
        }
    }

    // An existing configured CMake tree cannot be silently rebound to a different compiler,
    // generator or Ninja executable, including when somebody edited the tree after preflight.
    public static void ValidateBuildTree(string buildDirectory, WorkflowToolchainRuntime selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        string cache = Path.Combine(buildDirectory, "CMakeCache.txt");
        if (!File.Exists(cache))
        {
            return;
        }

        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(cache))
        {
            if (line.Length == 0 || line[0] is '/' or '#')
            {
                continue;
            }

            int split = line.IndexOf('=', StringComparison.Ordinal);
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (split > colon && colon > 0)
            {
                properties[line[..colon]] = line[(split + 1)..];
            }
        }

        if (properties.TryGetValue("CMAKE_GENERATOR", out string? generator) && generator != selected.Generator)
        {
            throw new InvalidOperationException("Configured generator differs from selected toolchain.");
        }

        static bool EqualPath(string observed, string selected) =>
            WorkflowPlanner.PathComparer.Equals(Path.GetFullPath(observed), Path.GetFullPath(selected));

        if (properties.TryGetValue("CMAKE_CXX_COMPILER", out string? compiler) && !EqualPath(compiler, selected.Compiler))
        {
            throw new InvalidOperationException(
                "CMake cache compiler differs from selected toolchain; use a new build tree.");
        }

        if (selected.Ninja is not null && properties.TryGetValue("CMAKE_MAKE_PROGRAM", out string? ninja)
                                       && !EqualPath(ninja, selected.Ninja))
        {
            throw new InvalidOperationException(
                "CMake cache build tool differs from selected Ninja; use a new build tree.");
        }
    }
}