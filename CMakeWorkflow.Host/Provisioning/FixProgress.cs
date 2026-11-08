namespace CMakeWorkflow.Host.Provisioning;

public sealed record FixProgress(string OperationId, int Sequence, string Stage, string? Tool, string? Message);
