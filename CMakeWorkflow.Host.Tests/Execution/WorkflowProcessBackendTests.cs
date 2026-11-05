using System.Diagnostics;
using CMakeWorkflow.Core.Processes;

internal static class WorkflowProcessBackendTests
{
    public static async Task RunAsync(string root)
    {
        string outputDirectory = Directory.CreateDirectory(Path.Combine(root, "neutral-backend")).FullName;
        string stdoutPath = Path.Combine(outputDirectory, "stdout.bin");
        string stderrPath = Path.Combine(outputDirectory, "stderr.bin");
        ProcessStartInfo startInfo = new(Environment.ProcessPath
            ?? throw new InvalidOperationException("The .NET host path is unavailable."))
        {
            UseShellExecute = false,
            WorkingDirectory = outputDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string? argument in new[]
                 {
                     typeof(WorkflowProcessBackendTests).Assembly.Location,
                     "--workflow-process-worker", "inspect", Path.Combine(outputDirectory, "report.json"), "direct"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        TimeSpan? duration = null;
        WorkflowProcessBackend.Outcome outcome;
        await using (FileStream stdout = File.Create(stdoutPath))
        await using (FileStream stderr = File.Create(stderrPath))
        {
            outcome = await WorkflowProcessBackend.RunAsync(
                new WorkflowProcessBackend.Specification(startInfo, stdout, stderr), null, elapsed => duration = elapsed, ct: CancellationToken.None);
        }
        if (outcome.StartError is not null || outcome.ExitCode != 0 || duration is null
            || duration <= TimeSpan.Zero)
        {
            throw new UnreachableException("Neutral process backend did not return a completed child result.");
        }

        if (await File.ReadAllTextAsync(stdoutPath) != "stdout:direct" + Environment.NewLine
            || await File.ReadAllTextAsync(stderrPath) != "stderr:direct" + Environment.NewLine)
        {
            throw new UnreachableException("Neutral process backend did not drain both prepared output streams.");
        }

        ProcessStartInfo missing = new(Path.Combine(outputDirectory, "missing executable"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        await using MemoryStream missingStdout = new();
        await using MemoryStream missingStderr = new();
        WorkflowProcessBackend.Outcome failed = await WorkflowProcessBackend.RunAsync(
            new WorkflowProcessBackend.Specification(missing, missingStdout, missingStderr),
null, _ => throw new UnreachableException("Failed start reported a duration."), ct: CancellationToken.None);
        if (failed.ExitCode is not null || failed.StartError is not System.ComponentModel.Win32Exception and not IOException)
        {
            throw new UnreachableException("Neutral backend did not return the original process-start failure.");
        }

        string exitedMarker = Path.Combine(outputDirectory, "quiet-child-exited");
        ProcessStartInfo quietStart = new(Environment.ProcessPath
            ?? throw new InvalidOperationException("The .NET host path is unavailable."))
        {
            UseShellExecute = false,
            WorkingDirectory = outputDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string? argument in new[]
                 {
                     typeof(WorkflowProcessBackendTests).Assembly.Location,
                     "--workflow-process-worker", "quiet-pid", exitedMarker
                 })
        {
            quietStart.ArgumentList.Add(argument);
        }

        await using QuietChildExitBarrier quietStdout = new(exitedMarker);
        await using MemoryStream quietStderr = new();
        TimeSpan? quietDuration = null;
        Task<WorkflowProcessBackend.Outcome> quietExecution = Task.Run(() => WorkflowProcessBackend.RunAsync(
            new WorkflowProcessBackend.Specification(quietStart, quietStdout, quietStderr),
null, elapsed => quietDuration = elapsed, ct: CancellationToken.None));
        WorkflowProcessBackend.Outcome quietOutcome = await quietExecution.WaitAsync(TimeSpan.FromSeconds(10));
        if (quietOutcome.StartError is not null || quietOutcome.ExitCode != 0 || quietDuration is null
            || quietStdout.Length != 0 || quietStderr.Length != 0)
        {
            throw new UnreachableException("Immediate empty child did not complete both redirected pumps cleanly.");
        }

        Console.WriteLine("Neutral workflow process backend direct checks passed.");
    }

    private sealed class QuietChildExitBarrier(string pidPath) : MemoryStream
    {
        public override bool CanWrite
        {
            get
            {
                Stopwatch timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(8))
                {
                    if (File.Exists(pidPath) && int.TryParse(File.ReadAllText(pidPath), out int pid))
                    {
                        try
                        {
                            using Process child = Process.GetProcessById(pid);
                            if (child.HasExited)
                            {
                                return base.CanWrite;
                            }
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
