using CMakeWorkflow.Core;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Capture;
#pragma warning restore IDE0130

internal static class ProfileEnvironment
{
    public static Dictionary<string, string?> Copy(IReadOnlyDictionary<string, string> input) =>
        input.ToDictionary(x => x.Key, x => (string?)x.Value, StringComparer.Ordinal);

    public static void RequireSuccess(CommandResult result, string operation)
    {
        if (result.TimedOut || result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{operation} failed: {result.Output}");
        }
    }
}