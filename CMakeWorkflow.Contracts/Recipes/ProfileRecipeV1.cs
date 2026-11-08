using System.Text.Json.Serialization;

namespace CMakeWorkflow.Contracts.Recipes;

/// <summary>Build and time variants, then capture profiler evidence.</summary>
/// <remarks>
/// Kind is profile. Requires 1..32 variants and 1..100 repetitions. Timing runs wait for all builds and are serialized.
/// A configure preset requires exactly one variant and an absolute source directory; otherwise existing trees are adopted.
/// The DTO itself performs no I/O or validation; recipe builders enforce these requirements.
/// </remarks>
/// <param name="Kind">Exact recipe kind discriminator.</param>
/// <param name="SchemaVersion">Recipe schema version; must be 1, independently of workflow schema version.</param>
/// <param name="Variants">Non-null variant array; entries must be non-null with unique names and distinct absolute build directories.</param>
/// <param name="Target">CMake target name.</param>
/// <param name="Configuration">CMake configuration, or null for the build-tree default.</param>
/// <param name="Repeats">Number of timing repetitions.</param>
/// <param name="TimeoutSeconds">Positive finite per-execution timeout in seconds, at most 86400.</param>
/// <param name="Arguments">Literal argument strings without NUL; the array and its elements must be non-null.</param>
/// <param name="Backend">auto, perf, etw or instruments; explicit backend must match Platform.</param>
/// <param name="Mode">cpu, scheduler, counters or diagnostic.</param>
/// <param name="Platform">Capture host platform: linux, windows or macos.</param>
/// <param name="SourceDirectory">Absolute source-tree path, or null when no explicit source is supplied.</param>
/// <param name="ConfigurePreset">Configure preset, or null to adopt existing trees.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] ProfileVariant[] Variants
#pragma warning restore CA1819
,
    [property: JsonRequired] string Target,
    string? Configuration,
    [property: JsonRequired] int Repeats,
    [property: JsonRequired] double TimeoutSeconds,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] string[] Arguments
#pragma warning restore CA1819
,
    [property: JsonRequired] string Backend,
    [property: JsonRequired] string Mode,
    [property: JsonRequired] string Platform,
    string? SourceDirectory = null,
    string? ConfigurePreset = null);