using System.ComponentModel;
using System.Diagnostics;
using CMakeWorkflow.Core.Processes;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

// Owns all CMake-specific process preparation and result projection. The backend
// receives a complete start specification and never discovers domain context.
internal static class CMakeProcessExecution
{
    public static async Task<int> RunAsync(NodeResult result,
        IReadOnlyDictionary<string, string> environment, string output, Action<TimeSpan>? heartbeat, CancellationToken ct)
    {
        string? launcher = result.ExecutionConditions.GetValueOrDefault("cpuAffinityLauncher");
        ProcessStartInfo info = new(launcher ?? result.Executable!)
        {
            UseShellExecute = false,
            WorkingDirectory = result.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        if (launcher is not null)
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(result.ExecutionConditions["cpuSetRequested"]);
            info.ArgumentList.Add(result.Executable!);
        }

        foreach (string argument in result.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (KeyValuePair<string, string> pair in environment)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        BuildTreeOwnership.PropagateTo(info);

        // Keep path evaluation and file creation in the original order: stdout,
        // stderr, then the pre-start cancellation check.
        await using FileStream stdout = File.Create(Path.Combine(output, result.Stdout!));
        await using FileStream stderr = File.Create(Path.Combine(output, result.Stderr!));
        ct.ThrowIfCancellationRequested();

        WorkflowProcessBackend.Outcome outcome = await WorkflowProcessBackend.RunAsync(new(info, stdout, stderr), heartbeat, elapsed => result.ProcessDurationSeconds = elapsed.TotalSeconds, ct: ct);
        return outcome.StartError is Win32Exception { NativeErrorCode: 2 or 3 } error
            ? throw new MissingWorkflowToolException(result.Executable!, error)
            : outcome.StartError is IOException ioError
            ? throw new MissingWorkflowToolException(result.Executable!, ioError)
            : outcome.ExitCode ?? throw new InvalidOperationException("Process backend returned no exit code.");
    }
}
