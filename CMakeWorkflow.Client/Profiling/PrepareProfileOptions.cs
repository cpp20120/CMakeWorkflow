using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Profiling;

[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.Profiling.PrepareProfileOptions(Variants) participates in CMakeWorkflow.Client.Profiling.ProfileService.PrepareAsync; retain these concrete collection parameter types.")]
public sealed record PrepareProfileOptions(
    string RequestId,
    string ResultsDirectory,
    string Target,
    [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.Profiling.PrepareProfileOptions.Variants (List<ProfileVariantOptions>) is exposed through CMakeWorkflow.Client.Profiling.ProfileService.PrepareAsync; retain this Client SDK property signature.")]
List<ProfileVariantOptions> Variants,
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
