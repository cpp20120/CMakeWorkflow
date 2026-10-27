using System.Diagnostics;
using System.Security.Cryptography;

namespace CMakeWorkflow;

public sealed record CommandResult(int ExitCode, string Output, bool TimedOut, int? ProcessId = null);

public static class CommandExecution
{
    public static async Task<CommandResult> RunAsync(
        string executable, IEnumerable<string> arguments, string workingDirectory,
        TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                start.Environment[key] = value;
        using var process = Process.Start(start)
            ?? throw new IOException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            if (cancellationToken.IsCancellationRequested) throw;
            return new CommandResult(process.ExitCode,
                await stdout + await stderr, true, process.Id);
        }
        return new CommandResult(process.ExitCode, await stdout + await stderr, false, process.Id);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
}
