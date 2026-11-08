using CMakeWorkflow.Core;

namespace CMakeWorkflow.Profiling.Capture;

public interface IProfileProcess
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken);
}
