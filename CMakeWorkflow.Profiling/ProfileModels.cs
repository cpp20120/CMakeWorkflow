using CMakeWorkflow;

namespace CMakeWorkflow.Profiling;

public sealed record ProfileCapabilities(
    string Backend,
    IReadOnlyList<string> Modes,
    string EventSource,
    string LaunchModel,
    string StartGuarantee,
    string Attribution,
    IReadOnlyList<string> RawFormats);

public sealed record ProfileRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string OutputDirectory,
    string CaptureId,
    string Mode,
    TimeSpan Timeout);

public sealed record ProfileResult(
    string Backend,
    string Mode,
    int ExitCode,
    string RawTrace,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string CaptureStatus,
    ProfileCapabilities Capabilities,
    string Output,
    int? TargetProcessId = null,
    DateTimeOffset? TargetStartedAt = null,
    DateTimeOffset? TargetFinishedAt = null);

public interface IProfilerBackend
{
    ProfileCapabilities Capabilities { get; }
    Task<ProfileResult> CaptureAsync(ProfileRequest request, CancellationToken cancellationToken);
}

public interface IProfileProcess
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken);
}

public sealed class ProfileProcess : IProfileProcess
{
    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
        CommandExecution.RunAsync(executable, arguments, workingDirectory, timeout, environment, cancellationToken);
}

internal static class ProfileEnvironment
{
    public static Dictionary<string, string?> Copy(IReadOnlyDictionary<string, string> input) =>
        input.ToDictionary(x => x.Key, x => (string?)x.Value, StringComparer.Ordinal);

    public static void RequireSuccess(CommandResult result, string operation)
    {
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"{operation} failed: {result.Output}");
    }
}
