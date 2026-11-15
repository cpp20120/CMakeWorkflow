using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Execution;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Sources;

namespace CMakeWorkflow.Host.Tests.Durability;

// Real CMake execution, operator-admitted recovery, no implicit repeat of effects.
internal static class SourceBindingRecoveryTests
{
    internal static async Task RunAsync(string parent)
    {
        string workspace = Path.Combine(parent, "sr-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(workspace, "src");
        string output = Path.Combine(workspace, "result");
        string manifest = Path.Combine(workspace, "workflow.json");
        _ = Directory.CreateDirectory(source);
        string storePath = SourceStorePaths.ForWorkspace(BuildTreeOwnership.CanonicalPath(workspace));
        string? previousGenerator = Environment.GetEnvironmentVariable("CMAKE_GENERATOR");
        Environment.SetEnvironmentVariable("CMAKE_GENERATOR", "Ninja");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"),
                "cmake_minimum_required(VERSION 3.26)\nproject(source_resume NONE)\n" +
                "add_custom_target(a COMMAND \"${CMAKE_COMMAND}\" -DOUT=${CMAKE_CURRENT_BINARY_DIR}/effects.txt -P \"${CMAKE_CURRENT_SOURCE_DIR}/a.cmake\")\n" +
                "add_custom_target(b COMMAND \"${CMAKE_COMMAND}\" -DOUT=${CMAKE_CURRENT_BINARY_DIR}/effects.txt -P \"${CMAKE_CURRENT_SOURCE_DIR}/b.cmake\")\n");
            await File.WriteAllTextAsync(Path.Combine(source, "a.cmake"),
                "file(APPEND \"${OUT}\" \"a\\n\")\n");
            await File.WriteAllTextAsync(Path.Combine(source, "b.cmake"),
                "file(APPEND \"${OUT}\" \"b\\n\")\n");
            byte[] manifestBytes = Encoding.UTF8.GetBytes("""
                {"schemaVersion":1,
                 "contexts":{"w":{"buildDirectory":"ignored","sourceDirectory":"src","mode":"managed","generator":"Ninja"}},
                 "nodes":[
                   {"id":"configure","kind":"configure","context":"w"},
                   {"id":"a","kind":"invoke","context":"w","target":"a","dependsOn":["configure"]},
                   {"id":"b","kind":"invoke","context":"w","target":"b","dependsOn":["a"]}
                 ]}
                """);
            await File.WriteAllBytesAsync(manifest, manifestBytes);
            string hash = Convert.ToHexString(SHA256.HashData(manifestBytes));
            LoreSnapshotStore lore = new(storePath);
            string sourceId = lore.Capture(source, ["CMakeLists.txt", "a.cmake", "b.cmake"]);
            WorkflowStartRequest request = new("source-resume", manifest, hash, 1, output)
            {
                SourceSnapshotId = sourceId
            };
            string operation;
            using (DurableAcceptanceStore db = new(workspace))
            {
                operation = db.Accept(request).OperationId;
            }
            WorkspaceReadPaths paths = new(workspace);
            WorkflowPlan plan = await WorkflowSourceSnapshotExecution.PlanAsync(paths, operation, manifest,
                output, manifestBytes, sourceId, [], CancellationToken.None);
            string build = Path.Combine(workspace, ".cw", operation, "b", "0");
            string effects = Path.Combine(build, "effects.txt");
            // Stop at B's checkpoint *before* invoking B. The first two dispatches
            // were configure and target A; only A has an external effect.
            int dispatches = 0;
            _ = await WorkflowRunner.RunAsync(plan, 1, output, CancellationToken.None,
                sourceSnapshotId: sourceId,
                beforeExternalDispatch: (_, _) =>
                    Interlocked.Increment(ref dispatches) == 3
                        ? throw new IOException("Injected pre-dispatch stop for B")
                        : Task.CompletedTask);
            if (!File.Exists(effects) ||
                !(await File.ReadAllLinesAsync(effects)).SequenceEqual(["a"]))
            {
                throw new UnreachableException("Partial source-bound run did not execute exactly A.");
            }
            string reportPath = Path.Combine(output, "run.json");
            WorkflowReport report = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(reportPath),
                WorkflowJson.Default.WorkflowReport)
                ?? throw new UnreachableException("Partial source-bound report absent.");
            NodeResult pending = report.Nodes.Single(n => n.Id == "b");
            if (pending.Status != "outcome_unknown" || report.SourceSnapshotId != sourceId)
            {
                throw new UnreachableException("Interrupted source-bound checkpoint was not quarantined.");
            }
            using (DurableAcceptanceStore db = new(workspace))
            {
                db.ReserveResultsDirectory(operation, output);
                _ = db.AuthorizeDispatch(operation);
                pending.Status = "running";
                pending.Error = null;
                report.Status = "running";
                report.Error = null;
                report.FinishedAt = null;
                report.SourceSnapshotId = null;
                await File.WriteAllBytesAsync(reportPath,
                    JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport));
                try
                {
                    _ = db.ConfirmRunnerCheckpoint(operation, output);
                    throw new UnreachableException("Dispatch accepted a checkpoint without its source binding.");
                }
                catch (InvalidDataException error) when (error.Message == "source_snapshot_dispatch_binding_mismatch")
                {
                }

                report.SourceSnapshotId = sourceId;
                await File.WriteAllBytesAsync(reportPath,
                    JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport));
                _ = db.ConfirmRunnerCheckpoint(operation, output);
                pending.Status = "needs_attention";
                pending.Error = "Operator verified B was never dispatched";
                report.Status = "needs_attention";
                await File.WriteAllBytesAsync(reportPath,
                    JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport));
                if (db.Reconcile(operation, 0, "needs_attention", "B was not dispatched").Revision != 1 ||
                    db.AdmitResume(operation, 1).Revision != 2)
                {
                    throw new UnreachableException("Source-bound recovery admission failed.");
                }
            }
            // Live edits are not authoritative during resume.
            await File.WriteAllTextAsync(Path.Combine(source, "b.cmake"),
                "file(APPEND \"${OUT}\" \"live\\n\")\n");
            Directory.Delete(source, recursive: true);
            string materializedB = Path.Combine(workspace, ".cw", operation, "s", "b.cmake");
            await File.WriteAllTextAsync(materializedB, "tampered");
            try
            {
                _ = await DurableWorkflowExecutor.ExecuteResumedAsync(workspace, operation, CancellationToken.None);
                throw new UnreachableException("Resume accepted modified source bytes.");
            }
            catch (InvalidDataException)
            {
                // Verification must fail before ClaimResumeAttempt.
            }
            await File.WriteAllTextAsync(materializedB, "file(APPEND \"${OUT}\" \"b\\n\")\n");
            // Missing physical Lore bindings must also fail before the one-shot
            // resume claim even when materialized source bytes still match.
            string binding = Path.Combine(storePath, "manifests", sourceId + ".lore.json");
            byte[] bindingBytes = await File.ReadAllBytesAsync(binding);
            File.Delete(binding);
            try
            {
                _ = await DurableWorkflowExecutor.ExecuteResumedAsync(workspace, operation, CancellationToken.None);
                throw new UnreachableException("Resume accepted missing Lore bindings.");
            }
            catch (FileNotFoundException)
            {
            }
            await File.WriteAllBytesAsync(binding, bindingBytes);
            int code = await DurableWorkflowExecutor.ExecuteResumedAsync(workspace, operation,
                CancellationToken.None);
            if (code != 0 || !(await File.ReadAllLinesAsync(effects)).SequenceEqual(["a", "b"]))
            {
                throw new UnreachableException("Recovery duplicated A or failed to execute the captured B.");
            }
            using (DurableAcceptanceStore db = new(workspace))
            {
                if (db.GetFinalization(operation) is not { State: "succeeded", ExitCode: 0 })
                {
                    throw new UnreachableException("Source-bound resume did not finalize durably.");
                }
                try
                {
                    _ = db.ClaimResumeAttempt(operation, output);
                    throw new UnreachableException("Duplicate source-bound resume attempt accepted.");
                }
                catch (InvalidOperationException)
                {
                }
            }
            await Console.Out.WriteLineAsync(
                "Source P4.3: recovered captured CMake state, skipped completed A, executed only B, blocked tampering and repeat resume.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CMAKE_GENERATOR", previousGenerator);
            if (Directory.Exists(storePath))
            {
                Directory.Delete(storePath, recursive: true);
            }
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }
}
