using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CMakeWorkflow;

public static class ProcessRunner
{
    public static async Task RunAsync(string file, IReadOnlyList<string> args, string workingDirectory,
        CancellationToken ct)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            // Inherit standard streams: this allows normal IDE/terminal logs and avoids pipe deadlocks.
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        BuildTreeOwnership.PropagateTo(info);

        Process started;
        try
        {
            started = Process.Start(info)
                      ?? throw new MissingWorkflowToolException(file, new IOException("Process.Start returned null."));
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        {
            throw new MissingWorkflowToolException(file, error);
        }

        using var process = started;
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Process '{file}' failed with exit code {process.ExitCode}.");
    }
}