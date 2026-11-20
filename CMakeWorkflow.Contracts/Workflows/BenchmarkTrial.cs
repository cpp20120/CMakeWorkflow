namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>Benchmark matrix metadata for one generated run.</summary>
/// <param name="BaselineVariant">Name of the first variant in the recipe.</param>
/// <param name="ExecutionOrder">Scheduling policy declared by the recipe.</param>
/// <param name="Axes">Ordered coordinates of this run in the matrix.</param>
public sealed record BenchmarkTrial(string BaselineVariant, string ExecutionOrder,
    IReadOnlyList<BenchmarkAxisCoordinate> Axes);
