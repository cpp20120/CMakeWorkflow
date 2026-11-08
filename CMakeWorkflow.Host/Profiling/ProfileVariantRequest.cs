using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Host.Profiling;

public sealed record ProfileVariantRequest(
    string Name,
    string BuildDirectory,
    WorkflowToolchainBinding? Toolchain = null);
