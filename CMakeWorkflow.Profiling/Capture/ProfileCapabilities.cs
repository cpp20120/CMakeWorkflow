// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Capture;
#pragma warning restore IDE0130

public sealed record ProfileCapabilities(
    string Backend,
    IReadOnlyList<string> Modes,
    string EventSource,
    string LaunchModel,
    string StartGuarantee,
    string Attribution,
    IReadOnlyList<string> RawFormats);
