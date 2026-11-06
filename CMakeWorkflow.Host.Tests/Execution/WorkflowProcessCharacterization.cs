using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Execution;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Toolchains;

namespace CMakeWorkflow.Host.Tests.Execution;

internal static class WorkflowProcessCharacterization
{
    private static readonly string[] ExplicitOwnedTreeNames =
    [
        "forged tree"
    ];
    private static readonly string TestAssembly = typeof(WorkflowProcessCharacterization).Assembly.Location;
    private static readonly string DotNet = Environment.ProcessPath
                                            ?? throw new InvalidOperationException("The .NET host path is unavailable.");

    public static async Task RunAsync(string root)
    {
        string? previousInherited = Environment.GetEnvironmentVariable("PROCESS_CHARACTERIZATION_INHERITED");
        Environment.SetEnvironmentVariable("PROCESS_CHARACTERIZATION_INHERITED", "inherited value");
        try
        {
            await StartArgumentsStreamsAndEnvironment(root);
            await OwnershipEnvironmentAndNestedProtection(root);
            await LargeConcurrentOutput(root);
            await NonzeroExit(root);
            await MissingExecutable(root);
            await FailedLogOpenPreventsStart(root);
            await NullStderrPathStillCreatesStdoutFirst(root);
            await PreCanceledCallCreatesLogsButDoesNotStart(root);
            await CancellationKillsParentAndDescendant(root);
        }
        finally { Environment.SetEnvironmentVariable("PROCESS_CHARACTERIZATION_INHERITED", previousInherited); }
        Console.WriteLine("Original workflow process dispatcher characterization passed.");
    }

    private static async Task StartArgumentsStreamsAndEnvironment(string root)
    {
        string directory = NewDirectory(root, "arguments");
        string workingDirectory = Directory.CreateDirectory(Path.Combine(directory, "working directory")).FullName;
        string reportPath = Path.Combine(directory, "worker report.json");
        NodeResult result = Result(workingDirectory, [TestAssembly, "--workflow-process-worker", "inspect", reportPath,
            "plain", "value with spaces", "", "semi;colon", "quoted\"value"]);
        result.EnvironmentOverrideHash = "domain-owned-node-value";

        int exitCode = await WorkflowProcessDispatcher.RunAsync(result,
            new Dictionary<string, string> { ["PROCESS_CHARACTERIZATION_EXPLICIT"] = "explicit value" },
            directory, CancellationToken.None);

        Check(exitCode == 0, "Normal child exit code changed.");
        Check(result.ProcessDurationSeconds is > 0, "Successful child duration was not recorded.");
        JsonElement details = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath)).RootElement;
        Check(details.GetProperty("workingDirectory").GetString() == workingDirectory,
            "Child working directory changed.");
        Check(string.IsNullOrEmpty(details.GetProperty("stdin").GetString()), "Child stdin was not closed immediately.");
        Check(details.GetProperty("explicitValue").GetString() == "explicit value",
            "Explicit child environment was lost.");
        Check(details.GetProperty("inherited").GetString() == "inherited value",
            "Inherited child environment was lost.");
        Check(details.GetProperty("arguments").EnumerateArray().Select(x => x.GetString())
                .SequenceEqual(["plain", "value with spaces", "", "semi;colon", "quoted\"value"]),
            "Child argument boundaries changed.");
        Check(await File.ReadAllTextAsync(Path.Combine(directory, "logs", "node.stdout.log"))
              == "stdout:plain" + Environment.NewLine, "Child stdout file changed.");
        Check(await File.ReadAllTextAsync(Path.Combine(directory, "logs", "node.stderr.log"))
              == "stderr:plain" + Environment.NewLine, "Child stderr file changed.");
        Pass("workflow child argv, working directory, stdin EOF, inherited/explicit environment and stream files");
    }

    private static async Task OwnershipEnvironmentAndNestedProtection(string root)
    {
        string directory = NewDirectory(root, "ownership");
        string inheritedTree = Directory.CreateDirectory(Path.Combine(directory, "inherited tree")).FullName;
        string ownedTree = Directory.CreateDirectory(Path.Combine(directory, "owned tree")).FullName;
        string ambientController = Directory.CreateDirectory(Path.Combine(directory, "ambient controller")).FullName;
        string explicitController = Directory.CreateDirectory(Path.Combine(directory, "explicit controller")).FullName;
        string? previousInheritedTrees = Environment.GetEnvironmentVariable("CMAKE_WORKFLOW_OWNED_TREES");
        string? previousController = Environment.GetEnvironmentVariable("CMAKE_WORKFLOW_CONTROLLER_BUILD_DIR");
        string? previousMarker = Environment.GetEnvironmentVariable("PROCESS_CHARACTERIZATION_INHERITED");
        Environment.SetEnvironmentVariable("CMAKE_WORKFLOW_OWNED_TREES", JsonSerializer.Serialize(new[] { inheritedTree }));
        Environment.SetEnvironmentVariable("CMAKE_WORKFLOW_CONTROLLER_BUILD_DIR", ambientController);
        Environment.SetEnvironmentVariable("PROCESS_CHARACTERIZATION_INHERITED", "inherited value");
        try
        {
            using BuildTreeOwnership ownership = await BuildTreeOwnership.AcquireAsync([ownedTree], CancellationToken.None);
            string explicitOutput = NewDirectory(directory, "explicit");
            NodeResult overridden = Result(directory,
            [TestAssembly, "--workflow-process-worker", "ownership",
                Path.Combine(directory, "explicit report.json"), ownedTree, explicitController]);
            int exitCode = await WorkflowProcessDispatcher.RunAsync(overridden,
                new Dictionary<string, string>
                {
                    ["CMAKE_WORKFLOW_OWNED_TREES"] = JsonSerializer.Serialize(ExplicitOwnedTreeNames),
                    ["CMAKE_WORKFLOW_CONTROLLER_BUILD_DIR"] = explicitController,
                    ["PROCESS_CHARACTERIZATION_EXPLICIT"] = "explicit value"
                }, explicitOutput, CancellationToken.None);
            Check(exitCode == 0, "Child with explicit controller environment failed.");
            using (JsonDocument details = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "explicit report.json"))))
            {
                JsonElement rootElement = details.RootElement;
                Check(rootElement.GetProperty("ownedTrees").EnumerateArray().Select(x => x.GetString())
                        .SequenceEqual([inheritedTree, ownedTree]),
                    "Owned-tree propagation did not override the supplied list with inherited and active trees.");
                Check(rootElement.GetProperty("controller").GetString() == explicitController,
                    "Explicit controller environment did not override the inherited value.");
                Check(rootElement.GetProperty("inherited").GetString() == "inherited value"
                      && rootElement.GetProperty("explicitValue").GetString() == "explicit value",
                    "Inherited or explicit ordinary environment was lost.");
                Check(rootElement.GetProperty("ownedRejection").GetString()?.Contains("already owned", StringComparison.Ordinal) == true,
                    "Inherited active-tree protection did not reach the child process.");
                Check(rootElement.GetProperty("controllerRejection").GetString()?.Contains("identical", StringComparison.Ordinal) == true,
                    "Explicit nested-controller protection did not reach the child process.");
            }

            NodeResult ambient = Result(directory,
            [TestAssembly, "--workflow-process-worker", "ownership",
                Path.Combine(directory, "ambient report.json"), ownedTree, ambientController]);
            string ambientOutput = NewDirectory(directory, "ambient");
            exitCode = await WorkflowProcessDispatcher.RunAsync(ambient,
                new Dictionary<string, string>(), ambientOutput, CancellationToken.None);
            Check(exitCode == 0, "Child with inherited controller environment failed.");
            using JsonDocument ambientDetails = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "ambient report.json")));
            Check(ambientDetails.RootElement.GetProperty("controller").GetString() == ambientController
                  && ambientDetails.RootElement.GetProperty("controllerRejection").GetString()?.Contains("identical", StringComparison.Ordinal) == true,
                "Ambient controller rejection was not inherited by the child process.");
            Pass("inherited tree propagation, explicit environment precedence and child nested-tree rejection");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CMAKE_WORKFLOW_OWNED_TREES", previousInheritedTrees);
            Environment.SetEnvironmentVariable("CMAKE_WORKFLOW_CONTROLLER_BUILD_DIR", previousController);
            Environment.SetEnvironmentVariable("PROCESS_CHARACTERIZATION_INHERITED", previousMarker);
        }
    }

    private static async Task LargeConcurrentOutput(string root)
    {
        string directory = NewDirectory(root, "large-output");
        NodeResult result = Result(directory, [TestAssembly, "--workflow-process-worker", "flood"]);
        int exitCode = await WorkflowProcessDispatcher.RunAsync(result,
            new Dictionary<string, string>(), directory, CancellationToken.None);
        byte[] stdout = await File.ReadAllBytesAsync(Path.Combine(directory, "logs", "node.stdout.log"));
        byte[] stderr = await File.ReadAllBytesAsync(Path.Combine(directory, "logs", "node.stderr.log"));
        Check(exitCode == 0 && stdout.Length > 200_000 && stderr.Length > 200_000,
            "Concurrent stdout/stderr streams were truncated or blocked.");
        Check(Encoding.UTF8.GetString(stdout).Split(Environment.NewLine).Length == 20_001
              && Encoding.UTF8.GetString(stderr).Split(Environment.NewLine).Length == 20_001,
            "Concurrent stdout/stderr line counts changed.");
        Pass("concurrent large stdout and stderr file redirection");
    }

    private static async Task NonzeroExit(string root)
    {
        string directory = NewDirectory(root, "nonzero");
        NodeResult result = Result(directory, [TestAssembly, "--workflow-process-worker", "nonzero"]);
        int exitCode = await WorkflowProcessDispatcher.RunAsync(result,
            new Dictionary<string, string>(), directory, CancellationToken.None);
        Check(exitCode == 23 && result.ProcessDurationSeconds is > 0,
            "Nonzero child exit code or duration projection changed.");
        Pass("nonzero child exit code and duration projection");
    }

    private static async Task MissingExecutable(string root)
    {
        string directory = NewDirectory(root, "missing-executable");
        string executable = Path.Combine(directory, "missing tool");
        NodeResult result = Result(directory, []);
        result.Executable = executable;

        MissingWorkflowToolException error = await Throws<MissingWorkflowToolException>(() => WorkflowProcessDispatcher.RunAsync(result,
            new Dictionary<string, string>(), directory, CancellationToken.None));
        Check(error.Executable == executable && error.Message ==
            $"Required executable '{executable}' could not be started.",
            "Missing executable error projection changed.");
        Check(error.InnerException is System.ComponentModel.Win32Exception or IOException,
            "Missing executable did not preserve its native start error.");
        Check(File.Exists(Path.Combine(directory, "logs", "node.stdout.log"))
              && File.Exists(Path.Combine(directory, "logs", "node.stderr.log")),
            "Missing executable was detected before opening the old stream destinations.");
        Check(result.ProcessDurationSeconds is null, "Failed process start recorded a duration.");
        Pass("missing executable error mapping and pre-start log creation");
    }

    private static async Task FailedLogOpenPreventsStart(string root)
    {
        string directory = NewDirectory(root, "failed-log-open");
        string marker = Path.Combine(directory, "started");
        NodeResult result = Result(directory,
            [TestAssembly, "--workflow-process-worker", "touch", marker]);
        string stdoutPath = Path.Combine(directory, result.Stdout!);
        result.Stderr = Path.Combine("logs", "missing", "node.stderr.log");
        string stderrPath = Path.Combine(directory, result.Stderr);
        await File.WriteAllTextAsync(stdoutPath, "truncate first");

        await ThrowsIOException(() => WorkflowProcessDispatcher.RunAsync(result,
            new Dictionary<string, string>(), directory, CancellationToken.None));

        Check(File.Exists(stdoutPath) && new FileInfo(stdoutPath).Length == 0,
            "Stdout was not created/truncated before stderr open failed.");
        Check(!Directory.Exists(Path.GetDirectoryName(stderrPath)!) && !File.Exists(marker),
            "Stderr open failure started the child or changed the obstruction.");
        Pass("stdout creation precedes stderr open and process start");
    }

    private static async Task NullStderrPathStillCreatesStdoutFirst(string root)
    {
        string directory = NewDirectory(root, "null-stderr");
        string marker = Path.Combine(directory, "started");
        NodeResult result = Result(directory, [TestAssembly, "--workflow-process-worker", "touch", marker]);
        await File.WriteAllTextAsync(Path.Combine(directory, result.Stdout!), "truncate first");
        result.Stderr = null;

        _ = await Throws<ArgumentNullException>(() => WorkflowProcessDispatcher.RunAsync(result,
            new Dictionary<string, string>(), directory, CancellationToken.None));

        Check(new FileInfo(Path.Combine(directory, result.Stdout!)).Length == 0 && !File.Exists(marker),
            "A null stderr destination changed stdout creation order or started the child.");
        Pass("null stderr destination preserves stdout-first evaluation");
    }

    private static async Task PreCanceledCallCreatesLogsButDoesNotStart(string root)
    {
        string directory = NewDirectory(root, "pre-canceled");
        string marker = Path.Combine(directory, "started");
        NodeResult result = Result(directory, [TestAssembly, "--workflow-process-worker", "touch", marker]);
        await File.WriteAllTextAsync(Path.Combine(directory, result.Stdout!), "old stdout");
        await File.WriteAllTextAsync(Path.Combine(directory, result.Stderr!), "old stderr");
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        _ = await Throws<OperationCanceledException>(() => WorkflowProcessDispatcher.RunAsync(result,
            new Dictionary<string, string>(), directory, cancellation.Token));

        Check(new FileInfo(Path.Combine(directory, result.Stdout!)).Length == 0
              && new FileInfo(Path.Combine(directory, result.Stderr!)).Length == 0,
            "Pre-start cancellation did not leave opened log files empty.");
        Check(!File.Exists(marker) && result.ProcessDurationSeconds is null,
            "Pre-start cancellation launched a child or recorded a process duration.");
        Pass("pre-start cancellation after creating and truncating stream files");
    }

    private static async Task CancellationKillsParentAndDescendant(string root)
    {
        string directory = NewDirectory(root, "cancel-parent-child");
        string pidPath = Path.Combine(directory, "process-ids.txt");
        string childReady = Path.Combine(directory, "child-ready.txt");
        NodeResult result = Result(directory,
            [TestAssembly, "--workflow-process-worker", "spawn", pidPath, childReady]);
        using CancellationTokenSource cancellation = new();
        Task<int> execution = WorkflowProcessDispatcher.RunAsync(result,
            new Dictionary<string, string>(), directory, cancellation.Token);
        int parentPid = 0, childPid = 0;
        try
        {
            await WaitUntil(() =>
            {
                if (!File.Exists(pidPath) || !File.Exists(childReady))
                {
                    return false;
                }

                string[] ids = File.ReadAllText(pidPath).Split('|');
                return ids.Length == 2 && int.TryParse(ids[0], out parentPid)
                                       && int.TryParse(ids[1], out childPid);
            }, TimeSpan.FromSeconds(20), "Process worker did not start its descendant.");

            Check(IsRunning(parentPid) && IsRunning(childPid),
                "Fixture parent and child were not running before cancellation.");
            await cancellation.CancelAsync();
            _ = await Throws<OperationCanceledException>(() => execution);
            Check(result.ProcessDurationSeconds is > 0,
                "Canceled process duration was not recorded before cleanup.");
            await WaitUntil(() => !IsRunning(parentPid) && !IsRunning(childPid),
                TimeSpan.FromSeconds(10), "Cancellation left the process parent or descendant running.");
            Pass("cancellation kills the workflow parent and its running descendant");
        }
        finally
        {
            await cancellation.CancelAsync();
            try { _ = await execution; }
            catch (OperationCanceledException) { }
            StopProcess(parentPid);
            StopProcess(childPid);
        }
    }

    private static NodeResult Result(string directory, IReadOnlyList<string> arguments) => new()
    {
        Id = "node",
        Kind = "invoke",
        Executable = DotNet,
        Arguments = [.. arguments],
        WorkingDirectory = directory,
        Stdout = Path.Combine("logs", "node.stdout.log"),
        Stderr = Path.Combine("logs", "node.stderr.log")
    };

    private static string NewDirectory(string root, string name)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        _ = Directory.CreateDirectory(Path.Combine(directory, "logs"));
        return directory;
    }

    private static async Task WaitUntil(Func<bool> ready, TimeSpan timeout, string message)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed >= timeout)
            {
                throw new UnreachableException(message);
            }

            await Task.Delay(20);
        }
    }

    private static bool IsRunning(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            string stat = Path.Combine("/proc", processId.ToString(System.Globalization.CultureInfo.CurrentCulture), "stat");
            if (!File.Exists(stat))
            {
                return false;
            }

            string details = File.ReadAllText(stat);
            int stateOffset = details.LastIndexOf(')') + 2;
            return stateOffset > 1 && details[stateOffset] != 'Z';
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void StopProcess(int processId)
    {
        if (!IsRunning(processId))
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(5_000);
        }
        catch (ArgumentException)
        {
        }
    }

    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T error)
        {
            return error;
        }

        throw new UnreachableException("Expected " + typeof(T).Name + ".");
    }

    private static async Task ThrowsIOException(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (IOException)
        {
            return;
        }

        throw new UnreachableException("Expected IOException.");
    }

    private static void Pass(string test) => Console.WriteLine("PASS process characterization: " + test);
    private static void Check(bool valid, string message)
    {
        if (!valid)
        {
            throw new UnreachableException(message);
        }
    }
}

internal static class WorkflowProcessTestWorker
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("Worker mode is required.");
        }

        switch (args[0])
        {
            case "inspect":
                {
                    if (args.Length < 2)
                    {
                        throw new ArgumentException("Inspection report path is required.");
                    }

                    string standardInput = await Console.In.ReadToEndAsync();
                    var document = new
                    {
                        workingDirectory = Environment.CurrentDirectory,
                        stdin = standardInput,
                        arguments = args[2..],
                        inherited = Environment.GetEnvironmentVariable("PROCESS_CHARACTERIZATION_INHERITED"),
                        explicitValue = Environment.GetEnvironmentVariable("PROCESS_CHARACTERIZATION_EXPLICIT"),
                        ownedTrees = Environment.GetEnvironmentVariable("CMAKE_WORKFLOW_OWNED_TREES"),
                        controller = Environment.GetEnvironmentVariable("CMAKE_WORKFLOW_CONTROLLER_BUILD_DIR")
                    };
                    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(document));
                    await Console.Out.WriteLineAsync("stdout:" + (args.Length > 2 ? args[2] : ""));
                    await Console.Error.WriteLineAsync("stderr:" + (args.Length > 2 ? args[2] : ""));
                    return 0;
                }
            case "ownership":
                {
                    static string? Rejection(string path)
                    {
                        try { BuildTreeOwnership.RejectController(path, null); }
                        catch (InvalidOperationException error) { return error.Message; }
                        return null;
                    }

                    var details = new
                    {
                        controller = Environment.GetEnvironmentVariable("CMAKE_WORKFLOW_CONTROLLER_BUILD_DIR"),
                        ownedTrees = Environment.GetEnvironmentVariable("CMAKE_WORKFLOW_OWNED_TREES") is { } raw
                            ? JsonDocument.Parse(raw).RootElement.EnumerateArray().Select(x => x.GetString()).ToArray()
                            : [],
                        inherited = Environment.GetEnvironmentVariable("PROCESS_CHARACTERIZATION_INHERITED"),
                        explicitValue = Environment.GetEnvironmentVariable("PROCESS_CHARACTERIZATION_EXPLICIT"),
                        controllerRejection = Rejection(args[3]),
                        ownedRejection = Rejection(args[2])
                    };
                    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(details));
                    return 0;
                }
            case "flood":
                {
                    Task stdout = Task.Run(() =>
                    {
                        for (int index = 0; index < 20_000; index++)
                        {
                            Console.Out.WriteLine($"stdout-{index:D5}");
                        }
                    });
                    Task stderr = Task.Run(() =>
                    {
                        for (int index = 0; index < 20_000; index++)
                        {
                            Console.Error.WriteLine($"stderr-{index:D5}");
                        }
                    });
                    await Task.WhenAll(stdout, stderr);
                    return 0;
                }
            case "nonzero":
                return 23;
            case "touch":
                await File.WriteAllTextAsync(args[1], "started");
                return 0;
            case "append-delay":
                await File.AppendAllTextAsync(args[1], "started\n");
                await Task.Delay(850);
                await File.AppendAllTextAsync(args[1], "finished\n");
                return 0;
            case "exit-output":
                await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.CurrentCulture));
                await Console.Out.WriteLineAsync("gated output");
                return 0;
            case "quiet-pid":
                await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.CurrentCulture));
                return 0;
            case "wait":
                await File.WriteAllTextAsync(args[1], "ready");
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            case "wait-pid":
                await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.CurrentCulture));
                await File.WriteAllTextAsync(args[2], "ready");
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            case "spawn":
                {
                    ProcessStartInfo childInfo = new(Environment.ProcessPath
                                                     ?? throw new InvalidOperationException("The .NET host path is unavailable."))
                    { UseShellExecute = false };
                    foreach (string? argument in new[]
                             {
                             typeof(WorkflowProcessTestWorker).Assembly.Location,
                             "--workflow-process-worker", "wait", args[2]
                         })
                    {
                        childInfo.ArgumentList.Add(argument);
                    }

                    using Process child = Process.Start(childInfo) ?? throw new UnreachableException("Cannot start worker child.");
                    await File.WriteAllTextAsync(args[1], Environment.ProcessId + "|" + child.Id);
                    await child.WaitForExitAsync();
                    return child.ExitCode;
                }
            case "spawn-flood":
                {
                    ProcessStartInfo childInfo = new(Environment.ProcessPath
                                                     ?? throw new InvalidOperationException("The .NET host path is unavailable."))
                    { UseShellExecute = false };
                    foreach (string? argument in new[]
                             {
                             typeof(WorkflowProcessTestWorker).Assembly.Location,
                             "--workflow-process-worker", "wait", args[2]
                         })
                    {
                        childInfo.ArgumentList.Add(argument);
                    }

                    using Process child = Process.Start(childInfo) ?? throw new UnreachableException("Cannot start worker child.");
                    await File.WriteAllTextAsync(args[1], Environment.ProcessId + "|" + child.Id);
                    while (true)
                    {
                        await Console.Out.WriteLineAsync("stream-output-0123456789");
                        await Console.Error.WriteLineAsync("stream-error-0123456789");
                    }
                }
            default:
                throw new ArgumentException("Unknown workflow process worker mode: " + args[0]);
        }
    }

}