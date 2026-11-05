using System.Diagnostics;
using CMakeWorkflow;

internal static class WorkflowProcessBackendTests
{
    public static async Task RunAsync(string root)
    {
        var outputDirectory = Directory.CreateDirectory(Path.Combine(root, "neutral-backend")).FullName;
        var stdoutPath = Path.Combine(outputDirectory, "stdout.bin");
        var stderrPath = Path.Combine(outputDirectory, "stderr.bin");
        var startInfo = new ProcessStartInfo(Environment.ProcessPath
            ?? throw new InvalidOperationException("The .NET host path is unavailable."))
        {
            UseShellExecute = false,
            WorkingDirectory = outputDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     typeof(WorkflowProcessBackendTests).Assembly.Location,
                     "--workflow-process-worker", "inspect", Path.Combine(outputDirectory, "report.json"), "direct"
                 }) startInfo.ArgumentList.Add(argument);

        TimeSpan? duration = null;
        WorkflowProcessBackend.Outcome outcome;
        await using (var stdout = File.Create(stdoutPath))
        await using (var stderr = File.Create(stderrPath))
        {
            outcome = await WorkflowProcessBackend.RunAsync(
                new WorkflowProcessBackend.Specification(startInfo, stdout, stderr), CancellationToken.None,
                null, elapsed => duration = elapsed);
        }
        if (outcome.StartError is not null || outcome.ExitCode != 0 || duration is null
            || duration <= TimeSpan.Zero)
            throw new Exception("Neutral process backend did not return a completed child result.");
        if (await File.ReadAllTextAsync(stdoutPath) != "stdout:direct" + Environment.NewLine
            || await File.ReadAllTextAsync(stderrPath) != "stderr:direct" + Environment.NewLine)
            throw new Exception("Neutral process backend did not drain both prepared output streams.");

        var missing = new ProcessStartInfo(Path.Combine(outputDirectory, "missing executable"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        await using var missingStdout = new MemoryStream();
        await using var missingStderr = new MemoryStream();
        var failed = await WorkflowProcessBackend.RunAsync(
            new WorkflowProcessBackend.Specification(missing, missingStdout, missingStderr),
            CancellationToken.None, null, _ => throw new Exception("Failed start reported a duration."));
        if (failed.ExitCode is not null || failed.StartError is not System.ComponentModel.Win32Exception and not IOException)
            throw new Exception("Neutral backend did not return the original process-start failure.");

        var exitedMarker = Path.Combine(outputDirectory, "quiet-child-exited");
        var quietStart = new ProcessStartInfo(Environment.ProcessPath
            ?? throw new InvalidOperationException("The .NET host path is unavailable."))
        {
            UseShellExecute = false,
            WorkingDirectory = outputDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     typeof(WorkflowProcessBackendTests).Assembly.Location,
                     "--workflow-process-worker", "quiet-pid", exitedMarker
                 }) quietStart.ArgumentList.Add(argument);
        await using var quietStdout = new QuietChildExitBarrier(exitedMarker);
        await using var quietStderr = new MemoryStream();
        TimeSpan? quietDuration = null;
        var quietExecution = Task.Run(() => WorkflowProcessBackend.RunAsync(
            new WorkflowProcessBackend.Specification(quietStart, quietStdout, quietStderr),
            CancellationToken.None, null, elapsed => quietDuration = elapsed));
        var quietOutcome = await quietExecution.WaitAsync(TimeSpan.FromSeconds(10));
        if (quietOutcome.StartError is not null || quietOutcome.ExitCode != 0 || quietDuration is null
            || quietStdout.Length != 0 || quietStderr.Length != 0)
            throw new Exception("Immediate empty child did not complete both redirected pumps cleanly.");

        Console.WriteLine("Neutral workflow process backend direct checks passed.");
    }

    private sealed class QuietChildExitBarrier(string pidPath) : MemoryStream
    {
        public override bool CanWrite
        {
            get
            {
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(8))
                {
                    if (File.Exists(pidPath) && int.TryParse(File.ReadAllText(pidPath), out var pid))
                    {
                        try
                        {
                            using var child = Process.GetProcessById(pid);
                            if (child.HasExited) return base.CanWrite;
                        }
                        catch (ArgumentException) { return base.CanWrite; }
                    }
                    Thread.Sleep(10);
                }
                throw new TimeoutException("Quiet process did not exit before stdout pump setup.");
            }
        }
    }
}
