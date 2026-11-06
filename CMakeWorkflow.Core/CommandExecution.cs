using System.Diagnostics;
using System.Security.Cryptography;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Core;
#pragma warning restore IDE0130

public static class CommandExecution
{
    public static async Task<CommandResult> RunAsync(
        string executable, IEnumerable<string> arguments, string workingDirectory,
        TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        ProcessStartInfo start = new(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach ((string? key, string? value) in environment)
            {
                start.Environment[key] = value;
            }
        }

        using Process process = Process.Start(start)
                            ?? throw new IOException($"Could not start {executable}.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
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

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            string outText = await stdout;
            string errText = await stderr;
            return new CommandResult(process.ExitCode, outText + errText, true, process.Id,
                outText, errText);
        }

        string standardOutput = await stdout;
        string standardError = await stderr;
        return new CommandResult(process.ExitCode, standardOutput + standardError, false,
            process.Id, standardOutput, standardError);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);

#pragma warning disable CA1308 // Lowercase values are part of the existing persisted hash, identifier, or command protocol.
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
#pragma warning restore CA1308

    }
}