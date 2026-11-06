using System.Text.Json.Serialization;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Recipes;
#pragma warning restore IDE0130

/// <summary>Build once, then execute filtered CTest groups.</summary>
/// <remarks>
/// Kind is test-matrix. Requires 1..64 uniquely named groups. Parallel groups depend on the build; sequential groups form a chain.
/// The DTO itself performs no I/O or validation; recipe builders enforce these requirements.
/// </remarks>
/// <param name="Kind">Exact recipe kind discriminator.</param>
/// <param name="SchemaVersion">Recipe schema version; must be 1, independently of workflow schema version.</param>
/// <param name="BuildDirectory">Absolute build-tree path on the execution host.</param>
/// <param name="Target">CMake target name.</param>
/// <param name="Configuration">CMake configuration, or null for the build-tree default.</param>
/// <param name="Groups">Non-null group array with unique names and non-null entries.</param>
/// <param name="ExecutionOrder">Explicit ordering policy; see the recipe remarks for allowed values.</param>
/// <param name="TimeoutSeconds">Positive finite per-execution timeout in seconds, at most 86400.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TestMatrixRecipeV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string BuildDirectory,
    [property: JsonRequired] string Target,
    string? Configuration,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
[property: JsonRequired] TestMatrixGroupV1[] Groups
#pragma warning restore CA1819
,
    [property: JsonRequired] string ExecutionOrder,
    [property: JsonRequired] double TimeoutSeconds);
