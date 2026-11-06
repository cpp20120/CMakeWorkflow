using CMakeWorkflow.Tooling.Tools;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Provisioning;
#pragma warning restore IDE0130

public sealed record DoctorProgress(string OperationId, int Sequence, string Stage, ToolStatus? Tool);
