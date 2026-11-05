#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

// Optional operation executed by the existing workflow DAG. The runner verifies
// the built executable before calling this interface; backends own capture.
public sealed record WorkflowOperationRequest(
    string Kind,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> Options,
    string OutputDirectory,
    string NodeId,
    string? CpuSet = null,
    WorkflowProfilerRequirement? RequiredProfiler = null,
    string? CpuAffinityLauncher = null);
