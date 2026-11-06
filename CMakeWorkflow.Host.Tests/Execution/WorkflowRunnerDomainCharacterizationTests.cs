using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CMakeWorkflow.CMake.Execution;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core;
using CMakeWorkflow.CMake.Serialization;

namespace CMakeWorkflow.Host.Tests.Execution;

internal static class WorkflowRunnerDomainCharacterizationTests
{
    public static async Task RunAsync(string root)
    {
        string fixture = Path.Combine(root, "domain-runner");
        string source = Directory.CreateDirectory(Path.Combine(fixture, "source")).FullName;
        string build = Path.Combine(fixture, "build");
        string observerSource = Directory.CreateDirectory(Path.Combine(fixture, "observer-source")).FullName;
        string observerBuild = Directory.CreateDirectory(Path.Combine(fixture, "observer-build")).FullName;
        string continueConfigure = Path.Combine(observerBuild, "continue-configure");
        string manifest = Path.Combine(fixture, "workflow.json");
        string output = Path.Combine(fixture, "results");
        _ = Directory.CreateDirectory(Path.Combine(build, ".cmake", "api", "v1", "query",
            "client-cmake-workflow"));
        await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"), """
                                                                             cmake_minimum_required(VERSION 3.26)
                                                                             project(WorkflowDomainFixture LANGUAGES CXX)
                                                                             add_executable(worker main.cpp)
                                                                             """);
        await File.WriteAllTextAsync(Path.Combine(source, "main.cpp"), "int main() { return 0; }\n");
        await File.WriteAllTextAsync(Path.Combine(observerSource, "CMakeLists.txt"), """
            cmake_minimum_required(VERSION 3.26)
            project(WorkflowObserver NONE)
            execute_process(COMMAND "${CMAKE_COMMAND}" "-DMARKER=${CMAKE_BINARY_DIR}/continue-configure"
                -P "${CMAKE_SOURCE_DIR}/wait.cmake")
            """);
        await File.WriteAllTextAsync(Path.Combine(observerSource, "wait.cmake"), """
            while(NOT EXISTS "${MARKER}")
              execute_process(COMMAND "${CMAKE_COMMAND}" -E sleep 0.05)
            endwhile()
            """);
        string query = Path.Combine(build, ".cmake", "api", "v1", "query", "client-cmake-workflow");
        foreach (string? kind in new[] { "codemodel-v2", "cache-v2", "toolchains-v1" })
        {
            await File.WriteAllTextAsync(Path.Combine(query, kind), "");
        }

        await RunCommand("cmake", ["-S", source, "-B", build], fixture);

        WorkflowDocument workflow = new()
        {
            SchemaVersion = 2,
            Contexts = new Dictionary<string, WorkflowContext>
            {
                ["worker"] = new()
                {
                    BuildDirectory = "build",
                    SourceDirectory = "source",
                    Mode = "adopt",
                    ProvenancePolicy = "allowUnverified"
                },
                ["observer"] = new()
                {
                    BuildDirectory = "observer-build",
                    SourceDirectory = "observer-source",
                    Mode = "managed"
                }
            },
            Nodes =
            [
                new WorkflowNode { Id = "build", Kind = "build", Context = "worker", Target = "worker" },
                new WorkflowNode
                {
                    Id = "profile", Kind = "profile", From = "build", WorkingDirectory = "source",
                    Arguments = ["alpha", "", "a;b"], Environment = new() { ["PROFILE_VALUE"] = "fixture" },
                    Options = new() { ["capture"] = "stable" }
                },
                new WorkflowNode
                {
                    Id = "configure-observer", Kind = "configure", Context = "observer", DependsOn = ["build"]
                },
                new WorkflowNode
                {
                    Id = "inspect-observer", Kind = "inspect", Context = "observer",
                    DependsOn = ["configure-observer"]
                }
            ]
        };
        await File.WriteAllBytesAsync(manifest,
            JsonSerializer.SerializeToUtf8Bytes(workflow, WorkflowJson.Default.WorkflowDocument));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(120));
        WorkflowPlan plan = await WorkflowPlanner.LoadAsync(manifest, [], null, deadline.Token);
        Check(plan.Nodes.Select(node => node.Definition.Id).SequenceEqual(
                ["build", "profile", "configure-observer", "inspect-observer"]),
            "Public planner returned an unexpected stable node order.");

        BlockingProfileExecutor profile = new();
        List<string[]> gates = [];
        Task<int> runTask = WorkflowRunner.RunAsync(plan, 2, output, deadline.Token, profile,
            beforeExternalDispatch: async (directory, token) =>
            {
                WorkflowReport report = await ReadReportAsync(Path.Combine(directory, "run.json"), token);
                gates.Add([.. report.Nodes.Select(node => node.Id + ":" + node.Status)]);
            });

        try
        {
            await profile.Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await File.WriteAllTextAsync(continueConfigure, "continue");
            WorkflowOperationRequest request = profile.Request ?? throw new UnreachableException("Profile extension request was not captured.");
            Check(request.Kind == "profile" && request.NodeId == "profile"
                                            && request.Executable is not null && File.Exists(request.Executable)
                                            && request.Arguments.SequenceEqual(["alpha", "", "a;b"])
                                            && request.WorkingDirectory == source
                                            && request.Environment.TryGetValue("PROFILE_VALUE", out string? env) && env == "fixture"
                                            && request.Options.TryGetValue("capture", out string? option) && option == "stable"
                                            && request.OutputDirectory == output,
                "Fake profile extension received a changed or incomplete execution request.");

            WorkflowReport during = await WaitForProfileAndInspectAsync(Path.Combine(output, "run.json"), deadline.Token);
            NodeResult profileNode = during.Nodes.Single(node => node.Id == "profile");
            Check(profileNode.Status == "running" && profileNode.Executable is null
                                                  && profileNode.ExitCode is null && profileNode.Arguments.Count == 0
                                                  && profileNode.ExperimentResult == "NotRun" && profileNode.Outputs.Count == 0,
                "A blocked worker mutated the scheduler-owned persisted NodeResult.");

            profile.Release();
            Check(await runTask.WaitAsync(TimeSpan.FromSeconds(30)) == 0,
                "WorkflowRunner did not report success after fake profile completion.");
            WorkflowReport final = await ReadReportAsync(Path.Combine(output, "run.json"), deadline.Token);
            AssertFinalProjection(final, request);
            Check(gates.Count == 3
                  && gates[0].SequenceEqual([
                      "build:running", "profile:pending", "configure-observer:pending", "inspect-observer:pending"])
                  && gates[1].SequenceEqual([
                      "build:succeeded", "profile:running", "configure-observer:running", "inspect-observer:pending"])
                  && gates[2].SequenceEqual([
                      "build:succeeded", "profile:running", "configure-observer:succeeded", "inspect-observer:running"]),
                "External dispatch did not follow the durable scheduler checkpoint boundary.");
        }
        finally
        {
            profile.Release();
            try { _ = await runTask.WaitAsync(TimeSpan.FromSeconds(10)); }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
            catch (Exception) { }
#pragma warning restore CA1031

        }

        await CharacterizeMissingToolAsync(build, fixture, deadline.Token);
        Console.WriteLine("Public WorkflowRunner domain characterization passed.");
    }

    private static void AssertFinalProjection(WorkflowReport report, WorkflowOperationRequest request)
    {
        Check(report.Status == "succeeded"
              && report.Nodes.Select(node => node.Id).SequenceEqual(
                  ["build", "profile", "configure-observer", "inspect-observer"]),
            "Workflow report status or plan-ordered node identity changed.");
        NodeResult build = report.Nodes[0];
        NodeResult profile = report.Nodes[1];
        NodeResult configure = report.Nodes[2];
        NodeResult inspect = report.Nodes[3];
        Check(build.Status == "succeeded" && build.ExitCode == 0
                                          && build.BuildVerification?.Verification == "BackendSucceeded"
                                          && build.Artifact is { Materialization: "VerifiedAfterBuild" },
            "Build result or verified artifact projection changed.");
        Check(profile.Status == "succeeded" && profile.ExitCode == 0
                                            && profile.ExperimentResult == "captured"
                                            && profile.Error == "fixture capture complete"
                                            && profile.Outputs.OrderBy(pair => pair.Key).SequenceEqual(new Dictionary<string, string>
                                            {
                                                ["backend"] = "fixture-backend",
                                                ["eventSource"] = "synthetic-source",
                                                ["sampleCount"] = "7"
                                            }.OrderBy(pair => pair.Key))
                                            && profile.ExecutionConditions.GetValueOrDefault("profilerBackend") == "fixture-backend"
                                            && profile.ExecutionConditions.GetValueOrDefault("profilerEventSource") == "synthetic-source"
                                            && profile.Artifact is { Materialization: "VerifiedAgainstBuild" } artifact
                                            && artifact.Executable == request.Executable
                                            && artifact.Sha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.Executable))),
            "Profile operation result/evidence projection changed.");
        Check(configure.Status == "succeeded" && configure.Configure?.State == "Succeeded"
                                              && inspect.Status == "succeeded" && inspect.ObservedContext is not null
                                              && report.ManifestHash.Length == 64,
            "Concurrent independent inspect projection or within-run manifest binding changed.");

        JsonNode? actualProjection = JsonSerializer.SerializeToNode(new
        {
            status = report.Status,
            nodes = report.Nodes.Select(node => new
            {
                id = node.Id,
                kind = node.Kind,
                status = node.Status,
                exitCode = node.ExitCode,
                error = node.Error,
                experimentResult = node.ExperimentResult,
                buildVerification = node.BuildVerification?.Verification,
                artifactMaterialization = node.Artifact?.Materialization,
                artifactSha256 = node.Artifact?.Sha256,
                outputs = node.Outputs.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value),
                profilerBackend = node.ExecutionConditions.GetValueOrDefault("profilerBackend"),
                profilerEventSource = node.ExecutionConditions.GetValueOrDefault("profilerEventSource")
            })
        });
        JsonNode? expectedProjection = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            status = "succeeded",
            nodes = new object[]
            {
                new
                {
                    id = "build", kind = "build", status = "succeeded", exitCode = (int?)0,
                    error = (string?)null, experimentResult = "NotRun", buildVerification = "BackendSucceeded",
                    artifactMaterialization = "VerifiedAfterBuild", artifactSha256 = build.Artifact!.Sha256,
                    outputs = new Dictionary<string, string>(), profilerBackend = (string?)null,
                    profilerEventSource = (string?)null
                },
                new
                {
                    id = "profile", kind = "profile", status = "succeeded", exitCode = (int?)0,
                    error = "fixture capture complete", experimentResult = "captured",
                    buildVerification = (string?)null, artifactMaterialization = "VerifiedAgainstBuild",
                    artifactSha256 = profile.Artifact!.Sha256,
                    outputs = new Dictionary<string, string>
                    {
                        ["backend"] = "fixture-backend", ["eventSource"] = "synthetic-source",
                        ["sampleCount"] = "7"
                    }, profilerBackend = "fixture-backend", profilerEventSource = "synthetic-source"
                },
                new
                {
                    id = "configure-observer", kind = "configure", status = "succeeded", exitCode = (int?)0,
                    error = (string?)null, experimentResult = "NotRun", buildVerification = (string?)null,
                    artifactMaterialization = (string?)null, artifactSha256 = (string?)null,
                    outputs = new Dictionary<string, string>(), profilerBackend = (string?)null,
                    profilerEventSource = (string?)null
                },
                new
                {
                    id = "inspect-observer", kind = "inspect", status = "succeeded", exitCode = (int?)null,
                    error = (string?)null, experimentResult = "NotRun", buildVerification = (string?)null,
                    artifactMaterialization = (string?)null, artifactSha256 = (string?)null,
                    outputs = new Dictionary<string, string>(), profilerBackend = (string?)null,
                    profilerEventSource = (string?)null
                }
            }
        }));
        Check(JsonNode.DeepEquals(actualProjection, expectedProjection),
            "Stable report projection changed (timings, durations and temporary paths are excluded).");
    }

    private static async Task CharacterizeMissingToolAsync(string build, string fixture, CancellationToken ct)
    {
        string index = Directory.GetFiles(Path.Combine(build, ".cmake", "api", "v1", "reply"), "index-*.json")
            .OrderByDescending(path => Path.GetFileName(path)[6..], StringComparer.Ordinal).First();
        JsonNode indexJson = JsonNode.Parse(await File.ReadAllTextAsync(index, ct))!;
        string missingCtest = Path.Combine(fixture, "missing-ctest-executable");
        indexJson["cmake"]!["paths"]!["ctest"] = missingCtest;
        await File.WriteAllTextAsync(index, indexJson.ToJsonString(), ct);

        string missingManifest = Path.Combine(fixture, "missing-tool.json");
        WorkflowDocument missingWorkflow = new()
        {
            SchemaVersion = 2,
            Contexts = new Dictionary<string, WorkflowContext>
            {
                ["worker"] = new()
                {
                    BuildDirectory = "build",
                    SourceDirectory = "source",
                    Mode = "adopt",
                    ProvenancePolicy = "allowUnverified"
                }
            },
            Nodes =
            [
                new WorkflowNode { Id = "build", Kind = "build", Context = "worker", Target = "worker" },
                new WorkflowNode
                {
                    Id = "ctest", Kind = "ctest", Context = "worker", DependsOn = ["build"]
                }
            ]
        };
        await File.WriteAllBytesAsync(missingManifest,
            JsonSerializer.SerializeToUtf8Bytes(missingWorkflow, WorkflowJson.Default.WorkflowDocument), ct);
        WorkflowPlan plan = await WorkflowPlanner.LoadAsync(missingManifest, [], null, ct);
        string output = Path.Combine(fixture, "missing-tool-results");
        Check(await WorkflowRunner.RunAsync(plan, 1, output, ct) == 3,
            "Unavailable CTest executable did not produce needs_attention.");
        WorkflowReport report = await ReadReportAsync(Path.Combine(output, "run.json"), ct);
        NodeResult node = report.Nodes.Single(result => result.Id == "ctest");
        Check(report.Status == "needs_attention" && node.Status == "needs_attention"
                                                 && node.ExperimentResult == "NotRun"
                                                 && node.Error?.Contains(missingCtest, StringComparison.Ordinal) == true
                                                 && report.Findings.Any(finding => finding.Code == "TOOL_EXECUTABLE_MISSING"
                                                     && finding.NodeId == "ctest"),
            "Missing-tool status, error, or finding projection changed.");
    }

    private static async Task<WorkflowReport> WaitForProfileAndInspectAsync(string path, CancellationToken ct)
    {
        Stopwatch until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromSeconds(10))
        {
            ct.ThrowIfCancellationRequested();
            WorkflowReport report = await ReadReportAsync(path, ct);
            if (report.Nodes.Single(node => node.Id == "profile").Status == "running"
                && report.Nodes.Single(node => node.Id == "configure-observer").Status == "succeeded"
                && report.Nodes.Single(node => node.Id == "inspect-observer").Status == "succeeded")
            {
                return report;
            }

            await Task.Delay(20, ct);
        }
        throw new TimeoutException("Independent inspect did not complete while profile extension was blocked.");
    }

    private static async Task<WorkflowReport> ReadReportAsync(string path, CancellationToken ct) =>
        JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path, ct), WorkflowJson.Default.WorkflowReport)
        ?? throw new InvalidDataException("WorkflowRunner did not persist a valid report.");

    private static async Task RunCommand(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new UnreachableException($"Could not start {executable}.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            throw;
        }
        string details = await stdout + await stderr;
        Check(process.ExitCode == 0, $"{executable} failed ({process.ExitCode}): {details}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new UnreachableException(message);
        }
    }

    private sealed class BlockingProfileExecutor : IWorkflowOperationExecutor
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkflowOperationRequest? Request { get; private set; }
        public bool Supports(string kind) => kind == "profile";

        public async Task<WorkflowOperationResult> ExecuteAsync(WorkflowOperationRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            _ = Started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new(0, "captured", new Dictionary<string, string>
            {
                ["backend"] = "fixture-backend",
                ["eventSource"] = "synthetic-source",
                ["sampleCount"] = "7"
            }, "fixture capture complete");
        }

        public void Release() => release.TrySetResult();
    }
}