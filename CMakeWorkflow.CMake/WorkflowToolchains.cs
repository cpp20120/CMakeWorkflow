using System.Text.Json.Serialization;

namespace CMakeWorkflow;

// A request for toolchain binding, deliberately independent of Windows discovery DTOs.
// The Host (or CLI) resolves IDs afresh against its own inventory. Never trust client paths.
public sealed record WorkflowToolchainBinding(
    [property: JsonPropertyName("cmakeCandidateId")] string? CMakeCandidateId = null,
    string? NinjaCandidateId = null,
    string? CompilerCandidateId = null, string? SdkCandidateId = null,
    string? EnvironmentProfileId = null, string Generator = "Ninja",
    string HostArchitecture = "x64", string TargetArchitecture = "x64");

// Keep the environment in memory; never put inherited variables or tokens in run.json.
public sealed record WorkflowToolchainRuntime(string CMake, string? Ninja, string Compiler,
    string? Sdk, string? EnvironmentProfileId, string Generator, string Fingerprint,
    IReadOnlyDictionary<string, string> Variables);

public sealed record WorkflowToolchainIdentity(string BuildDirectory, string CMake,
    string? Ninja, string Compiler, string? Sdk, string? EnvironmentProfileId,
    string Generator, string Fingerprint);

public interface IWorkflowToolchainProvider
{
    Task<WorkflowToolchainRuntime> ResolveAsync(WorkflowToolchainBinding binding, CancellationToken ct);
}

public static class WorkflowToolchainAdmission
{
    public static async Task<IReadOnlyDictionary<string, WorkflowToolchainRuntime>> ResolveAsync(
        WorkflowPlan plan, IWorkflowToolchainProvider? provider, CancellationToken ct)
    {
        var selected = new Dictionary<string, WorkflowToolchainRuntime>(WorkflowPlanner.PathComparer);
        foreach (var tree in plan.Nodes.Where(n => n.Definition.Kind != "inspect" && n.Toolchain is not null)
                     .GroupBy(n => n.Target.BuildDirectory, WorkflowPlanner.PathComparer))
        {
            if (provider is null)
                throw new InvalidOperationException("An explicit toolchain requires a platform execution environment provider.");
            var binding = tree.First().Toolchain!;
            if (tree.Any(x => x.Requested.ConfigurePreset is not null))
                throw new InvalidOperationException("Selected toolchain cannot override configurePreset; use a separately configured tree.");
            if (tree.Any(x => x.Toolchain != binding))
                throw new InvalidDataException("One build tree has conflicting toolchain bindings.");
            ct.ThrowIfCancellationRequested();
            var resolved = await provider.ResolveAsync(binding, ct);
            if (resolved.Generator != binding.Generator || string.IsNullOrWhiteSpace(resolved.Fingerprint)
                || !Path.IsPathFullyQualified(resolved.CMake) || !Path.IsPathFullyQualified(resolved.Compiler)
                || resolved.Ninja is not null && !Path.IsPathFullyQualified(resolved.Ninja))
                throw new InvalidDataException("Toolchain provider returned an invalid or incompatible selection.");
            selected.Add(tree.Key, resolved);
        }
        return selected;
    }

    public static List<WorkflowToolchainIdentity> Identities(
        IReadOnlyDictionary<string, WorkflowToolchainRuntime> selected) =>
        selected.OrderBy(p => p.Key, WorkflowPlanner.PathComparer).Select(pair =>
            new WorkflowToolchainIdentity(pair.Key, pair.Value.CMake, pair.Value.Ninja,
                pair.Value.Compiler, pair.Value.Sdk, pair.Value.EnvironmentProfileId,
                pair.Value.Generator, pair.Value.Fingerprint)).ToList();

    public static void RequireSameIdentity(IReadOnlyList<WorkflowToolchainIdentity> recorded,
        IReadOnlyDictionary<string, WorkflowToolchainRuntime> selected)
    {
        var current = Identities(selected);
        if (recorded.Count != current.Count || recorded.Where((a, i) => a != current[i]).Any())
            throw new InvalidDataException("Toolchain/environment changed since the checkpoint. Resume is unsafe; reconcile explicitly.");
    }

    // An existing configured CMake tree cannot be silently rebound to a different compiler,
    // generator or Ninja executable, including when somebody edited the tree after preflight.
    public static void ValidateBuildTree(string buildDirectory, WorkflowToolchainRuntime selected)
    {
        var cache = Path.Combine(buildDirectory, "CMakeCache.txt");
        if (!File.Exists(cache)) return;
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(cache))
        {
            if (line.Length == 0 || line[0] is '/' or '#') continue;
            var split = line.IndexOf('=');
            var colon = line.IndexOf(':');
            if (split > colon && colon > 0) properties[line[..colon]] = line[(split + 1)..];
        }
        if (properties.TryGetValue("CMAKE_GENERATOR", out var generator) && generator != selected.Generator)
            throw new InvalidOperationException("Configured generator differs from selected toolchain.");
        static bool EqualPath(string observed, string selected) =>
            WorkflowPlanner.PathComparer.Equals(Path.GetFullPath(observed), Path.GetFullPath(selected));
        if (properties.TryGetValue("CMAKE_CXX_COMPILER", out var compiler) && !EqualPath(compiler, selected.Compiler))
            throw new InvalidOperationException("CMake cache compiler differs from selected toolchain; use a new build tree.");
        if (selected.Ninja is not null && properties.TryGetValue("CMAKE_MAKE_PROGRAM", out var ninja)
            && !EqualPath(ninja, selected.Ninja))
            throw new InvalidOperationException("CMake cache build tool differs from selected Ninja; use a new build tree.");
    }
}
