using System.Diagnostics;
using System.Text;

namespace CMakeWorkflow.Profiling.Analysis;

internal static class PerfScript
{
    internal static async Task<T> RunAsync<T>(ProcessStartInfo start,
        Func<StreamReader, CancellationToken, Task<T>> parse, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using Process process = Process.Start(start) ?? throw new IOException("Could not start perf script.");
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<string> stderr = ReadErrorsAsync(process.StandardError, stop.Token);
        try
        {
            T result = await parse(process.StandardOutput, ct);
            await process.WaitForExitAsync(ct);
            string errors = await stderr;
            return process.ExitCode == 0
                ? result
                : throw new IOException($"perf script failed ({process.ExitCode}): {errors}");
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (process.HasExited) { }

            await stop.CancelAsync();
            await process.WaitForExitAsync(CancellationToken.None);
            try { _ = await stderr; }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
            throw;
        }
    }

    private static async Task<string> ReadErrorsAsync(StreamReader reader, CancellationToken ct)
    {
        const int limit = 16384;
        char[] buffer = new char[4096];
        StringBuilder saved = new();
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            if (saved.Length < limit)
            {
                _ = saved.Append(buffer, 0, Math.Min(read, limit - saved.Length));
            }
        }

        return saved.ToString();
    }
}
