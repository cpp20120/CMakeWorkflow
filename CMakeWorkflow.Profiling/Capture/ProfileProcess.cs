using CMakeWorkflow;

namespace CMakeWorkflow.Profiling;

public sealed class ProfileProcess : IProfileProcess
{
    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
        CommandExecution.RunAsync(executable, arguments, workingDirectory, timeout, environment, cancellationToken);
}
