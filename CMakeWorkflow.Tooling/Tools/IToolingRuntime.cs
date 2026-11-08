namespace CMakeWorkflow.Tooling.Tools;

public interface IToolingRuntime
{
    ToolPlatform Platform { get; }
    bool IsAdministrator { get; }
    string? Resolve(string command, string installDirectory);

    Task<ToolCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken ct);
}
