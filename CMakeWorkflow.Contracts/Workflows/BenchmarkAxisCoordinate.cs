namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>One resolved coordinate in a benchmark matrix.</summary>
/// <param name="Name">Recipe axis name.</param>
/// <param name="Flag">Literal argument flag associated with the axis.</param>
/// <param name="Value">Literal value passed to the benchmark.</param>
/// <param name="Role">Optional charting role declared by the recipe.</param>
public sealed record BenchmarkAxisCoordinate(string Name, string Flag, string Value, string? Role);
