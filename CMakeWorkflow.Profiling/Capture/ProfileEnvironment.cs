using CMakeWorkflow;

namespace CMakeWorkflow.Profiling;

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