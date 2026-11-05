#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public interface IToolingRuntime
{
    ToolPlatform Platform { get; }
    bool IsAdministrator { get; }
    string? Resolve(string command, string installDirectory);

    Task<ToolCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken ct);
}
