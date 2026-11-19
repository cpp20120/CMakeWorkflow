namespace CMakeWorkflow.CMake.Reporting;

/// <summary>A numeric result extracted from a declared property in a timing run's JSON stdout.</summary>
public sealed record BenchmarkMetricSample(string Name, string Unit, string Direction, string Path, double Value);
