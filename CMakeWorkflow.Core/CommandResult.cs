#if EXPERIMENT_SDK
namespace CMakeWorkflow.Client.Experiments;
#else
namespace CMakeWorkflow.Core;
#endif

#if EXPERIMENT_SDK
internal
#else
public
#endif
sealed record CommandResult(
    int ExitCode,
    string Output,
    bool TimedOut,
    int? ProcessId = null,
    string? Stdout = null,
    string? Stderr = null);
