using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Sources;

namespace CMakeWorkflow.Host.Tests.Durability;

// RID-specific executable test. This exercises actual Lore native storage,
// CMake configure, target dispatch, the Host ledger and fail-closed admission.
internal static class SourceBindingExecutionTests
{
    internal static async Task RunAsync(string parent, bool binary = false)
    {
        // The caller already creates a unique parent; avoid long Windows paths.
        string workspace = Path.Combine(parent, "w");
        string source = Path.Combine(workspace, "source");
        string manifest = Path.Combine(workspace, binary ? "workflow.capnp" : "workflow.json");
        string output = Path.Combine(workspace, "results");
        string liveBuild = Path.Combine(workspace, "legacy-build");
        _ = Directory.CreateDirectory(source);
        _ = Directory.CreateDirectory(liveBuild);
        string storePath = SourceStorePaths.ForWorkspace(BuildTreeOwnership.CanonicalPath(workspace));
        string? previousGenerator = Environment.GetEnvironmentVariable("CMAKE_GENERATOR");
        string? previousCas = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_ARTIFACT_CAS");
        // All three CI images install Ninja. Pin a single-config backend so this
        // fixture makes no assumptions about the platform's default generator.
        Environment.SetEnvironmentVariable("CMAKE_GENERATOR", "Ninja");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"),
                "cmake_minimum_required(VERSION 3.26)\nproject(source_binding LANGUAGES CXX)\n" +
                "add_executable(probe main.cpp)\nenable_testing()\nadd_test(NAME probe COMMAND probe)\n");
            string cpp = Path.Combine(source, "main.cpp");
            await File.WriteAllTextAsync(cpp, """
                #include <cstdio>
                #include <fstream>
                #include <string>
                int main(int argc, char** argv) {
                    std::puts("captured");
                    if (argc == 1) return 0;
                    std::ifstream input(argv[1]);
                    std::string line;
                    std::getline(input, line);
                    std::ofstream output(argv[2]);
                    output << line;
                    return input && output ? 0 : 2;
                }
                """);
            string input = Path.Combine(source, "message.txt");
            await File.WriteAllTextAsync(input, "snapshot data");
            byte[] manifestBytes = Encoding.UTF8.GetBytes("""
                {"schemaVersion":1,
                 "contexts":{"only":{"buildDirectory":"legacy-build","sourceDirectory":"source","mode":"managed","generator":"Ninja"}},
                 "nodes":[
                   {"id":"configure","kind":"configure","context":"only"},
                   {"id":"compile","kind":"build","context":"only","target":"probe","dependsOn":["configure"]},
                   {"id":"inspect","kind":"inspect","context":"only","dependsOn":["configure"]},
                   {"id":"test","kind":"ctest","context":"only","dependsOn":["compile"]},
                   {"id":"execute","kind":"run","from":"compile","dependsOn":["compile"]}
                 ]}
                """);
            WorkflowDocument document = JsonSerializer.Deserialize(manifestBytes, WorkflowDefinitionJson.Default.WorkflowDocument)!;
            if (binary)
            {
                Environment.SetEnvironmentVariable("CMAKEWORKFLOW_ARTIFACT_CAS", Path.Combine(workspace, "action-cache"));
                document.SchemaVersion = 2;
                WorkflowNode execute = document.Nodes[^1];
                execute.ActionCache = true;
                execute.Inputs.Add(new() { Path = "source/message.txt" });
                execute.Outputs.Add(new() { Name = "mapped", Path = "publish/result.txt", Fresh = true });
                execute.Arguments.AddRange(["@input:0", "@output:mapped"]);
            }
            byte[] wire = binary
                ? CapnpWire.Frame(CapnpWire.Canonicalize(ProductionWorkflowCodec.Encode(document)))
                : manifestBytes;
            await File.WriteAllBytesAsync(manifest, wire);
            await File.WriteAllTextAsync(Path.Combine(liveBuild, "CMakeCache.txt"),
                "stale cache must never be adopted\n");
            string hash = Convert.ToHexString(SHA256.HashData(wire));
            LoreSnapshotStore lore = new(storePath);
            string id = await lore.CaptureAsync(source, ["CMakeLists.txt", "main.cpp", "message.txt"]);
            WorkflowStartRequest request = new("source-bound", manifest, hash, 1, output)
            {
                SourceSnapshotId = id
            };
            ExecutionStartRequest binaryRequest = new(BinaryWorkflowSubmission.Domain, "source-bound", manifest, hash, 1, output)
            {
                SourceSnapshotId = id
            };
            if (binary)
            {
                BinaryWorkflowSubmission.InitializeWorkspace(workspace);
                _ = SourceBoundArtifactLayout.Validate(document, manifest, source, await lore.VerifyAsync(id, CancellationToken.None));
            }

            using (DurableAcceptanceStore db = new(workspace))
            {
                if (binary)
                {
                    SnapshotManifest snapshot = await lore.VerifyAsync(id, CancellationToken.None);
                    WorkflowNode execute = document.Nodes[^1];
                    string originalPath = execute.Inputs[0].Path!;
                    execute.Inputs[0].Path = "../source/message.txt";
                    await RejectArtifactLayoutAsync(db, binaryRequest, document, "traversal-input");
                    execute.Inputs[0].Path = "source/missing.txt";
                    await RejectArtifactLayoutAsync(db, binaryRequest, document, "missing-input");
                    execute.Inputs[0].Path = originalPath;
                    string originalOutput = execute.Outputs[0].Path;
                    execute.Outputs[0].Path = "source/message.txt";
                    await RejectArtifactLayoutAsync(db, binaryRequest, document, "aliased-output");
                    execute.Outputs[0].Path = originalOutput;
                    _ = SourceBoundArtifactLayout.Validate(document, manifest, source, snapshot);
                    await File.WriteAllBytesAsync(manifest, wire);
                    _ = await db.AcceptBinaryAsync(binaryRequest, CancellationToken.None);
                    await ExpectRefusalAsync(() => db.AcceptBinaryAsync(
                        binaryRequest with { SourceSnapshotId = new string('0', 64) }, CancellationToken.None),
                        "Conflicting source snapshot reused durable acceptance.");
                    File.Delete(manifest);
                }
                else
                {
                    _ = db.Accept(request);
                }
            }
            // An accepted request must use immutable Lore bytes, not the files
            // currently present on disk (nor the old preconfigured build tree).
            await File.WriteAllTextAsync(cpp, "#include <cstdio>\nint main(){ std::puts(\"live\"); return 0; }\n");
            await File.WriteAllTextAsync(input, "live data");
            using (DurableExecutionCoordinator coordinator = new(CancellationToken.None))
            {
                OperationReference accepted = binary
                    ? await BinaryWorkflowSubmission.StartAsync(coordinator, workspace, binaryRequest)
                    : coordinator.Start(workspace, request);
                if (accepted.State != "accepted_not_dispatched")
                {
                    throw new UnreachableException("Source request was not durably accepted.");
                }
                await coordinator.DrainAsync();
            }

            string operation;
            using (DurableAcceptanceStore db = new(workspace, readOnly: true))
            {
                AcceptedWorkflow accepted = db.Find("source-bound")
                    ?? throw new UnreachableException("Source operation disappeared.");

                if (accepted.State != "succeeded" ||
                    db.GetSourceSnapshotId(accepted.OperationId) != id ||
                    db.GetDispatchCheckpoint(accepted.OperationId) is null ||
                    (binary && db.ReadBinaryInput(accepted.OperationId) is null))
                {
                    string[] logFiles =
                    [
                        "configure.stdout.log",
                        "configure.stderr.log"
                    ];

                    foreach (string name in logFiles)
                    {
                        string path = Path.Combine(output, name);

                        if (File.Exists(path))
                        {
                            await Console.Error.WriteLineAsync($"===== {name} =====");
                            await Console.Error.WriteLineAsync(
                                await File.ReadAllTextAsync(path));
                        }
                    }

                    await Console.Error.WriteLineAsync(
                        $"Source workspace: {workspace.Length} chars; {workspace}");

                    throw new UnreachableException(
                        "Source execution was not finalized with durable dispatch evidence.");
                }

                operation = accepted.OperationId;
            }

            string isolatedRoot = Path.Combine(workspace, ".cw", operation);
            string isolatedSource = Path.Combine(isolatedRoot, "s");
            using (JsonDocument report = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(Path.Combine(output, "run.json"))))
            {
                if (report.RootElement.GetProperty("sourceSnapshotId").GetString() != id)
                {
                    throw new UnreachableException("Runner report lost immutable Lore identity.");
                }
                if (binary)
                {
                    JsonElement executed = report.RootElement.GetProperty("nodes").EnumerateArray()
                        .Single(node => node.GetProperty("id").GetString() == "execute");
                    if (executed.GetProperty("actionCacheHit").GetBoolean() ||
                        string.IsNullOrEmpty(executed.GetProperty("actionDigest").GetString()))
                    {
                        throw new UnreachableException("Mapped action did not publish a cache result.");
                    }
                }
            }
            if (binary)
            {
                string mapped = Path.Combine(output, "manifest-artifacts", "publish", "result.txt");
                OperationArtifactList artifacts = await OperationArtifactReader.ListAsync(
                    new(workspace), operation, CancellationToken.None);
                if (await File.ReadAllTextAsync(mapped) != "snapshot data" ||
                    Directory.Exists(Path.Combine(workspace, "publish")) ||
                    !artifacts.Artifacts.Any(item => item.RelativePath == "manifest-artifacts/publish/result.txt"))
                {
                    throw new UnreachableException("Mapped manifest output lost snapshot bytes or escaped the results directory.");
                }
            }
            using (CMakeWorkflow.History.Storage.HistoryStore history = new(Path.Combine(workspace, "history-test.sqlite")))
            {
                _ = await history.ImportAsync(output, recursive: false);
                if (history.Runs(10, null, null, null, null, null).Single().SourceSnapshotId != id)
                {
                    throw new UnreachableException("History index lost immutable Lore identity.");
                }
            }
            string stdout = await File.ReadAllTextAsync(Path.Combine(output, "execute.stdout.log"));
            if (!stdout.Contains("captured", StringComparison.Ordinal) ||
                stdout.Contains("live", StringComparison.Ordinal) ||
                !(await File.ReadAllTextAsync(Path.Combine(isolatedSource, "main.cpp")))
                    .Contains("captured", StringComparison.Ordinal) ||
                !(await File.ReadAllTextAsync(cpp)).Contains("live", StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(liveBuild, "CMakeCache.txt")))
            {
                throw new UnreachableException("Native executable read live source or reused an existing build tree.");
            }

            // A restarted Host may reuse a materialized snapshot only if all its bytes
            // still match Lore; a corrupt source must never be used for resume.
            await File.WriteAllTextAsync(Path.Combine(isolatedSource, "main.cpp"), "tampered");
            try
            {
                CMakeWorkflow.Host.Sessions.WorkspaceReadPaths paths = new(workspace);
                _ = binary
                    ? await WorkflowSourceSnapshotExecution.PlanAsync(paths, operation, manifest, output, document,
                        hash, id, [], CancellationToken.None, resume: true, allowManifestArtifacts: true)
                    : await WorkflowSourceSnapshotExecution.PlanAsync(paths, operation, manifest, output,
                        manifestBytes, id, [], CancellationToken.None, resume: true);
                throw new UnreachableException("Tampered materialization was accepted on resume.");
            }
            catch (InvalidDataException)
            {
            }

            // The source store must fail closed before AuthorizeDispatch.
            string missingOutput = Path.Combine(workspace, "missing-results");
            if (binary)
            {
                await File.WriteAllBytesAsync(manifest, wire);
                using DurableAcceptanceStore db = new(workspace);
                await ExpectRefusalAsync(() => db.AcceptBinaryAsync(binaryRequest with
                {
                    ClientRequestId = "missing-source",
                    SourceSnapshotId = new string('0', 64),
                    ResultsDirectory = missingOutput
                }, CancellationToken.None), "Missing Lore snapshot reached binary admission.");
                if (db.Find("missing-source") is not null || Directory.Exists(missingOutput))
                {
                    throw new UnreachableException("Missing Lore snapshot left binary execution state.");
                }
                Console.WriteLine("Binary Lore admission, selection graph, immutable execution and fail-closed source checks passed.");
                return;
            }
            WorkflowStartRequest missing = new("missing-source", manifest, hash, 1, missingOutput)
            {
                SourceSnapshotId = new string('0', 64)
            };
            using (DurableExecutionCoordinator coordinator = new(CancellationToken.None))
            {
                _ = coordinator.Start(workspace, missing);
                await coordinator.DrainAsync();
            }
            using (DurableAcceptanceStore db = new(workspace, readOnly: true))
            {
                AcceptedWorkflow failed = db.Find("missing-source")
                    ?? throw new UnreachableException("Missing-source admission disappeared.");
                if (failed.State != "accepted_not_dispatched" ||
                    db.GetDispatchCheckpoint(failed.OperationId) is not null ||
                    File.Exists(Path.Combine(missingOutput, "run.json")))
                {
                    throw new UnreachableException("Missing Lore content reached dispatch.");
                }
            }
            Console.WriteLine("Source P4.2: captured C++ compiled and ran, fresh build tree, live-edit isolation, fail-closed dispatch passed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CMAKE_GENERATOR", previousGenerator);
            Environment.SetEnvironmentVariable("CMAKEWORKFLOW_ARTIFACT_CAS", previousCas);
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

    private static async Task RejectArtifactLayoutAsync(DurableAcceptanceStore ledger, ExecutionStartRequest valid,
        WorkflowDocument document, string clientRequestId)
    {
        byte[] frame = CapnpWire.Frame(CapnpWire.Canonicalize(ProductionWorkflowCodec.Encode(document)));
        await File.WriteAllBytesAsync(valid.Manifest, frame);
        await ExpectRefusalAsync(() => ledger.AcceptBinaryAsync(valid with
        {
            ClientRequestId = clientRequestId,
            ManifestSha256 = Convert.ToHexString(SHA256.HashData(frame))
        }, CancellationToken.None), "Unsafe artifact layout reached native admission.");
        if (ledger.Find(clientRequestId) is not null)
        {
            throw new UnreachableException("Unsafe artifact layout created an accepted operation.");
        }
    }

    private static async Task ExpectRefusalAsync(Func<Task<AcceptedWorkflow>> action, string failure)
    {
        try
        {
            _ = await action();
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or FileNotFoundException)
        {
            return;
        }

        throw new UnreachableException(failure);
    }
}
