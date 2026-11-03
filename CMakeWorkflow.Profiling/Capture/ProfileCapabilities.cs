using CMakeWorkflow;

namespace CMakeWorkflow.Profiling;

public sealed record ProfileCapabilities(
    string Backend,
    IReadOnlyList<string> Modes,
    string EventSource,
    string LaunchModel,
    string StartGuarantee,
    string Attribution,
    IReadOnlyList<string> RawFormats);
