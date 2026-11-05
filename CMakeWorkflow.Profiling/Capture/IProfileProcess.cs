#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling;
#pragma warning restore IDE0130

public interface IProfileProcess
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken);
}
