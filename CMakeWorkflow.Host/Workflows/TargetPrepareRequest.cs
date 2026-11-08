using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Host.Workflows;

// Prepared native actions reuse the durable workflow engine and its operation API.
public sealed record TargetPrepareRequest(
    string RequestId,
    string ResultsDirectory,
    string Action,
    string BuildDirectory,
    string? Target = null,
    string? SecondTarget = null,
    string? Configuration = null,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[]? Arguments = null
#pragma warning restore CA1819
,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[]? SecondArguments = null
#pragma warning restore CA1819
,
    WorkflowToolchainBinding? Toolchain = null);
