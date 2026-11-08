using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Host.Provisioning;

public sealed record DoctorProgress(string OperationId, int Sequence, string Stage, ToolStatus? Tool);
