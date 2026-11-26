using System.Diagnostics;
using System.Globalization;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

namespace CMakeWorkflow.IntegrationTests.Execution;

internal static class ScriptCancellationTests
{
    private const string PidPrefix = "author child pid ";

    internal static async Task<int> RunAsync(string executable, string script)
    {
        Check(OperatingSystem.IsLinux(), "Script cancellation checks require Linux");
        ProcessStartInfo start = new(Path.GetFullPath(executable))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string[] arguments = ["script", "compose", Path.GetFullPath(script), "--", "timeout"];
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process cli = Process.Start(start) ?? throw new UnreachableException("Could not start script compose");
        Task<string> stdout = cli.StandardOutput.ReadToEndAsync();
        int author = 0;
        try
        {
            string? line = await cli.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Check(line?.StartsWith(PidPrefix, StringComparison.Ordinal) == true, $"Expected author PID, got: {line}");
            Check(int.TryParse(line.AsSpan(PidPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out author) &&
                author > 0, $"Invalid author PID: {line}");

            Task<string> stderr = cli.StandardError.ReadToEndAsync();
            Check(SendInterrupt(cli.Id), "Could not interrupt script compose");
            await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            string[] outputs = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
            Check(cli.ExitCode == 130, $"Expected exit 130, got {cli.ExitCode}\n{outputs[0]}{outputs[1]}");
            Check(!IsProcessRunning(author), $"Authoring child {author} is still running");
            Pass("script Ctrl+C and child termination");
            return 0;
        }
        finally
        {
            if (!cli.HasExited)
            {
                cli.Kill(entireProcessTree: true);
                await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (author > 0)
            {
                KillProcessIfRunning(author);
            }
        }
    }
}
