using CMakeWorkflow;

namespace CMakeWorkflow.Profiling;

public interface IProfileProcess
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken);
}
