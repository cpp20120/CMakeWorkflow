using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Client.Experiments;

/// <summary>Inputs for planning a pinned Git experiment and its remote workflow.</summary>
/// <remarks>Omit ConfigurePreset to use managed Linux Clang with an explicit Configuration.</remarks>
public sealed record ExperimentPlanOptions(string Repository, string Revision, ExperimentHost Host)
{
    public string? ConfigurePreset { get; init; }
    public string? Configuration { get; init; }
    public IReadOnlyList<string> BuildTargets { get; init; } = [];
    public string? BenchmarkTarget { get; init; }
    public int Repeats { get; init; } = 3;
    public IReadOnlyList<string> BenchmarkArguments { get; init; } = [];
    public bool Profile { get; init; }
    public bool Vcpkg { get; init; }
    public ExperimentPlacement? Placement { get; init; }
}
