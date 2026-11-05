namespace CMakeWorkflow.Client.Profiling;

public sealed record ProfileVariantOptions(
    string Name,
    string BuildDirectory,
    WorkflowToolchainBinding? Toolchain = null);
