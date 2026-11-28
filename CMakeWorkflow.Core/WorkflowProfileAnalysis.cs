namespace CMakeWorkflow.Core;

/// <summary>Measurements derived from a capture and bound by its terminal evidence.</summary>
public sealed record WorkflowProfileMeasurement(string Name, string Unit, double Value, string? Variant, string Method);

public sealed record WorkflowProfileAnalysis(string Status, IReadOnlyList<WorkflowProfileMeasurement> Measurements);
