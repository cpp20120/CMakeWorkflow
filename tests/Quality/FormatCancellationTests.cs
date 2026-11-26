using System.Diagnostics;
using System.Globalization;
using CMakeWorkflow.IntegrationTests.Support;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

namespace CMakeWorkflow.IntegrationTests.Quality;

internal static class FormatCancellationTests
{
    internal static async Task RunAsync()
    {
        Check(OperatingSystem.IsLinux(), "Formatter cancellation checks require Linux");
        using TempTree temp = new();
        _ = Write(Path.Combine(temp.Path, "CMakeLists.txt"), "project(Cancellation NONE)\n");

        await CheckCleanupAsync(temp.Path, timeout: 4, expectedExit: 2, expectedStatus: "Failed");
        await CheckCleanupAsync(temp.Path, timeout: 30, expectedExit: 130, expectedStatus: "Cancelled");
        Pass("formatter timeout, cancellation and child process cleanup");
    }

    private static async Task CheckCleanupAsync(string root, int timeout, int expectedExit, string expectedStatus)
    {
        string marker = Path.Combine(root, FormattableString.Invariant($"processes-{timeout}"));
        ProcessStartInfo start = new(Cli)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string[] arguments = ["format", "--project", root, "--language", "cmake", "--json", "--timeout-seconds",
            timeout.ToString(CultureInfo.InvariantCulture), "--executable", QualityToolProbe.AppHost];
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment[QualityToolProbe.ModeVariable] = "formatter";
        start.Environment[QualityToolProbe.MarkerVariable] = marker;

        using Process cli = Process.Start(start) ?? throw new UnreachableException("Could not start formatter test CLI");
        Task<string> stdout = cli.StandardOutput.ReadToEndAsync();
        Task<string> stderr = cli.StandardError.ReadToEndAsync();
        int[] pids = [];
        try
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!File.Exists(marker))
            {
                Check(!cli.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(10), "Formatter never started its child");
                await Task.Delay(20);
            }
            string processIds = await File.ReadAllTextAsync(marker);
            pids = [.. processIds.Split(' ').Select(pid => int.Parse(pid, CultureInfo.InvariantCulture))];
            Check(pids.Length == 2, "Expected formatter and child process IDs");
            if (expectedExit == 130)
            {
                Check(SendInterrupt(cli.Id), "Could not interrupt formatter CLI");
            }

            await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            string[] outputs = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
            Check(cli.ExitCode == expectedExit && S(J(outputs[0])["status"]) == expectedStatus, outputs[0] + outputs[1]);
            Check(pids.All(pid => !IsProcessRunning(pid)), $"Formatter process tree survived: {string.Join(' ', pids)}");
        }
        finally
        {
            if (!cli.HasExited)
            {
                cli.Kill(entireProcessTree: true);
                await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            foreach (int pid in pids)
            {
                KillProcessIfRunning(pid);
            }
        }
    }
}
