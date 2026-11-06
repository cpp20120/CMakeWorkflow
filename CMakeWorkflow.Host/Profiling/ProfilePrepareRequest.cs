using System.Diagnostics.CodeAnalysis;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Profiling;
#pragma warning restore IDE0130

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record ProfilePrepareRequest(
    string RequestId,
    string ResultsDirectory,
    string Target,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<ProfileVariantRequest> Variants,
    int Repeats = 3,
    double TimeoutSeconds = 300,
    string Backend = "auto",
    string Mode = "cpu",
    string? Configuration = null,
    string? SourceDirectory = null,
    string? ConfigurePreset = null,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[]? Arguments = null
#pragma warning restore CA1819
);
