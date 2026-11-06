using System.Text.Json.Serialization;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Recipes;
#pragma warning restore IDE0130

// Canonical wire and manifest recipe definitions. No runtime or I/O dependency.
// Recipe versioning is independent of WorkflowDocument.schemaVersion.
/// <summary>Build followed by a serial chain of timing runs.</summary>
/// <remarks>
/// Kind is benchmark. Repeats is 1..1000. The existing build tree is adopted with allowUnverified provenance.
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
);
