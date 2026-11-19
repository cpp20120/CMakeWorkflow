namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>Resource observations for one actual process execution.</summary>
/// <remarks>
/// Peaks and CPU totals are sampled estimates. Sampling can miss short-lived descendants.
/// Linux includes descendants visible beneath the launched process. Other platforms observe the direct process.
/// Missing values mean no usable observation. The scope and method must accompany comparisons.
/// </remarks>
/// <param name="SchemaVersion">Resource evidence format version.</param>
/// <param name="Scope">Observed process membership.</param>
/// <param name="Method">Collector and measurement semantics.</param>
/// <param name="SampleIntervalMilliseconds">Requested sampling interval.</param>
/// <param name="Samples">Number of usable observations.</param>
/// <param name="PeakRssBytes">Largest observed resident set in bytes.</param>
/// <param name="CpuSeconds">Largest observed cumulative user and kernel CPU time in seconds.</param>
public sealed record ProcessResourceUsage(int SchemaVersion, string Scope, string Method,
    int SampleIntervalMilliseconds, int Samples, long? PeakRssBytes, double? CpuSeconds);
