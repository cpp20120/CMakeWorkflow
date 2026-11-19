using System.Text.Json.Serialization;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Contracts.Recipes;

// Canonical wire and manifest recipe definitions. No runtime or I/O dependency.
// Recipe versioning is independent of WorkflowDocument.schemaVersion.
/// <summary>Build followed by a serial chain of timing runs.</summary>
/// <remarks>
/// Kind is benchmark. Repeats is 1..1000. Select a source directory and preset to configure a managed build tree.
/// The DTO itself performs no I/O or validation; recipe builders enforce these requirements.
/// </remarks>
/// <param name="Kind">Exact recipe kind discriminator.</param>
/// <param name="SchemaVersion">Recipe schema version; must be 1, independently of workflow schema version.</param>
/// <param name="BuildDirectory">Absolute build-tree path on the execution host.</param>
/// <param name="Target">CMake target name.</param>
/// <param name="Configuration">CMake configuration, or null for the build-tree default.</param>
/// <param name="Repeats">Number of timing repetitions.</param>
/// <param name="TimeoutSeconds">Positive finite per-execution timeout in seconds, at most 86400.</param>
/// <param name="Arguments">Literal argument strings without NUL; the array and its elements must be non-null.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BenchmarkRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string BuildDirectory,
    [property: JsonRequired] string Target,
    string? Configuration,
    [property: JsonRequired] int Repeats,
    [property: JsonRequired] double TimeoutSeconds,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] string[] Arguments
#pragma warning restore CA1819
)
{
    /// <summary>Optional numeric metrics from each timing run's JSON standard output.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenchmarkMetrics? Metrics { get; init; }

    /// <summary>Optional compiler cache profile resolved on the execution host.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompilerCacheProfile { get; init; }

    /// <summary>Source directory for an explicit configure step using the named preset.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceDirectory { get; init; }

    /// <summary>Configure preset for a managed benchmark tree.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfigurePreset { get; init; }
}
