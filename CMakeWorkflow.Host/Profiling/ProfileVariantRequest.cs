using CMakeWorkflow.Contracts.Workflows;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Profiling;
#pragma warning restore IDE0130

public sealed record ProfileVariantRequest(
    string Name,
    string BuildDirectory,
    WorkflowToolchainBinding? Toolchain = null);
