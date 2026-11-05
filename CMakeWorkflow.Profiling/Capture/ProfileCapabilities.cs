#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling;
#pragma warning restore IDE0130

public sealed record ProfileCapabilities(
    string Backend,
    IReadOnlyList<string> Modes,
    string EventSource,
    string LaunchModel,
    string StartGuarantee,
    string Attribution,
    IReadOnlyList<string> RawFormats);
