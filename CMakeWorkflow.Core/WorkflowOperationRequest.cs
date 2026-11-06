using CMakeWorkflow.Contracts.Workflows;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Core;
#pragma warning restore IDE0130

// Optional operation executed by the existing workflow DAG. The runner verifies
// the built executable before calling this interface; backends own capture.
/// <summary>Admitted extension-node inputs with Host-local paths and verified executable identity.</summary>
/// <remarks>
/// Read-only collection interfaces are borrowed inputs, not permission to mutate underlying values.
/// The runner owns checkpoint/admission; the backend owns capture and child cleanup.
/// </remarks>
/// <param name="Kind">Extension node kind.</param>
/// <param name="Executable">Verified built executable path.</param>
/// <param name="Arguments">Literal executable arguments.</param>
/// <param name="WorkingDirectory">Resolved Host working directory.</param>
/// <param name="Environment">Environment overrides.</param>
/// <param name="Options">Extension options; time values use units declared by their option names.</param>
/// <param name="OutputDirectory">Host output directory for extension artifacts.</param>
/// <param name="NodeId">Owning DAG node ID.</param>
/// <param name="CpuSet">Required CPU set, or null for no explicit affinity requirement.</param>
/// <param name="RequiredProfiler">Required profiler capability evidence, or null.</param>
/// <param name="CpuAffinityLauncher">Resolved affinity launcher path, or null when not needed/available.</param>
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
