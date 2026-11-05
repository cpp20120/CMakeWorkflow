using System.ComponentModel;
using System.Diagnostics;

namespace CMakeWorkflow;

// Owns all CMake-specific process preparation and result projection. The backend
// receives a complete start specification and never discovers domain context.
internal static class CMakeProcessExecution
{
    public static async Task<int> RunAsync(NodeResult result,
        IReadOnlyDictionary<string, string> environment, string output, CancellationToken ct,
        Action<TimeSpan>? heartbeat)
    {
        var launcher = result.ExecutionConditions.GetValueOrDefault("cpuAffinityLauncher");
        var info = new ProcessStartInfo(launcher ?? result.Executable!)
        {
            UseShellExecute = false, WorkingDirectory = result.WorkingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        if (launcher is not null)
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(result.ExecutionConditions["cpuSetRequested"]);
            info.ArgumentList.Add(result.Executable!);
        }

        foreach (var argument in result.Arguments) info.ArgumentList.Add(argument);
        foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        BuildTreeOwnership.PropagateTo(info);

        // Keep path evaluation and file creation in the original order: stdout,
        // stderr, then the pre-start cancellation check.
        await using var stdout = File.Create(Path.Combine(output, result.Stdout!));
        await using var stderr = File.Create(Path.Combine(output, result.Stderr!));
        ct.ThrowIfCancellationRequested();

        var outcome = await WorkflowProcessBackend.RunAsync(new(info, stdout, stderr), ct,
            heartbeat, elapsed => result.ProcessDurationSeconds = elapsed.TotalSeconds);
        if (outcome.StartError is Win32Exception { NativeErrorCode: 2 or 3 } error)
            throw new MissingWorkflowToolException(result.Executable!, error);
        if (outcome.StartError is IOException ioError)
            throw new MissingWorkflowToolException(result.Executable!, ioError);

        return outcome.ExitCode ?? throw new InvalidOperationException("Process backend returned no exit code.");
    }
}
