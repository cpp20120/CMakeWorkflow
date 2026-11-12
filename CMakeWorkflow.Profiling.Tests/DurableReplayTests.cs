using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.CMake.Artifacts;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Execution;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core;
using CMakeWorkflow.Profiling.Replay;

namespace CMakeWorkflow.Profiling.Tests;

internal static class DurableReplayTests
{
    private sealed class RecordingExecutor : IWorkflowOperationExecutor
    {
        public List<string> Executed { get; } = [];
        public bool Supports(string kind) => kind == "profile";
        public Task<WorkflowOperationResult> ExecuteAsync(WorkflowOperationRequest request,
            CancellationToken cancellationToken)
        {
            Executed.Add(request.NodeId);
            return Task.FromResult(new WorkflowOperationResult(0, "complete",
                new Dictionary<string, string>()));
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidDataException(message);
        }
    }

    public static async Task RunAsync(string root)
    {
        string source = Path.Combine(root, "durable-replay");
        _ = Directory.CreateDirectory(source);
        string[] runs = [Path.Combine(source, "baseline"), Path.Combine(source, "candidate")];
        for (int i = 0; i < runs.Length; i++)
        {
            _ = Directory.CreateDirectory(runs[i]);
            string executable = Path.Combine(runs[i], "worker");
            await File.WriteAllTextAsync(executable, "fixture" + i);
            string sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable)));
            WorkflowReport report = new()
            {
                Status = "succeeded",
                Nodes = [new NodeResult
                {
                    Id = "benchmark", Kind = "run", Status = "succeeded",
                    EnvironmentOverrideHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                    WorkingDirectory = runs[i],
                    Arguments = ["--case", "sample"],
                    Artifact = new ArtifactReference("build",
                        new CMakeTargetRef(runs[i], "worker", null), executable, sha, "VerifiedAfterBuild")
                }]
            };
            await File.WriteAllBytesAsync(Path.Combine(runs[i], "run.json"),
                JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport));
        }

        string definitions = Path.Combine(source, "definitions");
        _ = Directory.CreateDirectory(definitions);
        WorkflowDocument manifest = await ProfileReplayCommand.PrepareWorkflowAsync(
            new ProfileReplayOptions(runs[0], runs[1], "benchmark", Backend: "auto",
                ResultsDirectory: definitions), null, CancellationToken.None);
        int expected = OperatingSystem.IsLinux() ? 6 : OperatingSystem.IsWindows() ? 4 : 2;
        Check(manifest.Contexts.Count == 0 && manifest.Nodes.Count == expected &&
              manifest.Nodes.All(x => x.Kind == "profile_snapshot" && x.SnapshotSha256 is { Length: 64 }),
            "Replay DAG must contain buildless verified captures.");
        string manifestPath = Path.Combine(definitions, "replay.json");
        await File.WriteAllBytesAsync(manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(manifest, WorkflowJson.Default.WorkflowDocument));
        WorkflowPlan plan = await WorkflowPlanner.LoadAsync(manifestPath, [], null, CancellationToken.None);
        Check(plan.Nodes.Count == expected && plan.Nodes.All(x => x.Diagnostics.All(d => d.Severity != "Error")),
            "Replay graph did not pass planner validation.");
        RecordingExecutor capture = new();
        string output = Path.Combine(source, "executed");
        int code = await WorkflowRunner.RunAsync(plan, 4, output, CancellationToken.None, capture);
        Check(code == 0 && capture.Executed.SequenceEqual(manifest.Nodes.Select(x => x.Id)),
            "Replay passes must execute sequentially through the existing scheduler.");
        WorkflowReport saved = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(Path.Combine(output, "run.json")),
            WorkflowJson.Default.WorkflowReport)!;
        Check(saved.Status == "succeeded" && saved.Nodes.All(x => x.Artifact?.Materialization == "VerifiedSnapshot"),
            "Replay captures must persist immutable executable evidence.");

        // An interrupted checkpoint is uncertain evidence, not permission to
        // replay the first profiler pass. The existing runner must quarantine it.
        string uncertainOutput = Path.Combine(source, "interrupted");
        _ = Directory.CreateDirectory(uncertainOutput);
        saved.Status = "running";
        saved.RecoveryPhase = "running";
        saved.FinishedAt = null;
        for (int i = 0; i < saved.Nodes.Count; i++)
        {
            saved.Nodes[i].Status = i == 0 ? "running" : "pending";
        }
        await File.WriteAllBytesAsync(Path.Combine(uncertainOutput, "run.json"),
            JsonSerializer.SerializeToUtf8Bytes(saved, WorkflowJson.Default.WorkflowReport));
        RecordingExecutor afterDisconnect = new();
        int resumeCode = await WorkflowRunner.RunAsync(plan, 1, uncertainOutput,
            CancellationToken.None, afterDisconnect, resume: true);
        Check(resumeCode == 4 && afterDisconnect.Executed.Count == 0,
            "Uncertain capture was automatically dispatched after reconnect.");
        WorkflowReport quarantined = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(Path.Combine(uncertainOutput, "run.json")),
            WorkflowJson.Default.WorkflowReport)!;
        Check(quarantined.Status == "outcome_unknown" && quarantined.Nodes[0].Status == "outcome_unknown",
            "Interrupted capture must be marked outcome_unknown.");

        await File.AppendAllTextAsync(manifest.Nodes[0].SnapshotExecutable!, "tampered");
        string brokenOutput = Path.Combine(source, "tampered");
        RecordingExecutor tamperedCapture = new();
        code = await WorkflowRunner.RunAsync(plan, 1, brokenOutput, CancellationToken.None, tamperedCapture);
        Check(code == 1 && tamperedCapture.Executed.Count == 0, "Changed snapshot executable must fail before dispatch.");
        saved = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(Path.Combine(brokenOutput, "run.json")),
            WorkflowJson.Default.WorkflowReport)!;
        Check(saved.Nodes[0].Status == "failed", "Tampering was not persisted as failure.");
    }
}
