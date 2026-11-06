using CMakeWorkflow.Tooling.Toolchains;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Toolchains;
#pragma warning restore IDE0130

public sealed record ToolchainResolveRequest(ToolchainSelection Selection);
