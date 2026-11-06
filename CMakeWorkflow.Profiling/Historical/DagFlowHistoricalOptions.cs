// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Historical;
#pragma warning restore IDE0130

/// <summary>Historical capture inputs; schedule randomization is independent of trial counts.</summary>
/// <remarks>
/// Omitted scheduling options retain the archived Python ordering. Seeds identify separate experiments,
/// not independent performance samples. Each experiment persists its complete schedule before measurements.
/// </remarks>
/// <param name="SourceDirectory">DagFlow stress-harness source tree; snapshotted by the existing capture adapter.</param>
/// <param name="OutputDirectory">Fresh experiment directory, or an existing matching directory for resume.</param>
/// <param name="TimingTrials">Timing repetitions per seed, 1..1000; independent of seed count.</param>
/// <param name="StatTrials">Counter repetitions per seed, 1..1000; independent of seed count.</param>
/// <param name="Jobs">Positive native build parallelism; measurement execution remains serial.</param>
/// <param name="Resume">Verify journal evidence and execute only saved, not-yet-admitted schedule entries.</param>
/// <param name="ShuffleAlgorithm">Exact supported PRNG ID; python-legacy preserves default archival ordering.</param>
/// <param name="Seeds">Distinct UInt32 ordering seeds, or null for 20260930; dotnet requires Int32 range. Each seed gets its own run.</param>
/// <param name="ScheduleStrategy">sequential, shuffle-global, random-interleaved, randomized-blocks or balanced-blocks.</param>
/// <param name="ShuffleScope">block or global; shuffle-global requires global. Global block strategies reorder whole blocks.</param>
public sealed record DagFlowHistoricalOptions(
    string SourceDirectory,
    string OutputDirectory,
    int TimingTrials = 5,
    int StatTrials = 3,
    int Jobs = 4,
    bool Resume = false,
    string ShuffleAlgorithm = "python-legacy",
    IReadOnlyList<uint>? Seeds = null,
    string ScheduleStrategy = "random-interleaved",
    string ShuffleScope = "block");
