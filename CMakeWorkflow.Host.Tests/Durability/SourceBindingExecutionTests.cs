using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Sources;

namespace CMakeWorkflow.Host.Tests.Durability;

// RID-specific executable test. This exercises actual Lore native storage,
// CMake configure, target dispatch, the Host ledger and fail-closed admission.
internal static class SourceBindingExecutionTests
{
    internal static async Task RunAsync(string parent)
    {
        // The caller already creates a unique parent; avoid long Windows paths.
        string workspace = Path.Combine(parent, "w");
        string source = Path.Combine(workspace, "source");
        string manifest = Path.Combine(workspace, "workflow.json");
        string output = Path.Combine(workspace, "results");
        string liveBuild = Path.Combine(workspace, "legacy-build");
        _ = Directory.CreateDirectory(source);
        _ = Directory.CreateDirectory(liveBuild);
        string storePath = SourceStorePaths.ForWorkspace(BuildTreeOwnership.CanonicalPath(workspace));
        string? previousGenerator = Environment.GetEnvironmentVariable("CMAKE_GENERATOR");
        // All three CI images install Ninja. Pin a single-config backend so this
        // fixture makes no assumptions about the platform's default generator.
        Environment.SetEnvironmentVariable("CMAKE_GENERATOR", "Ninja");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"),
                "cmake_minimum_required(VERSION 3.26)\nproject(source_binding LANGUAGES CXX)\n" +
                "add_executable(probe main.cpp)\n");
            string cpp = Path.Combine(source, "main.cpp");
            await File.WriteAllTextAsync(cpp, "#include <cstdio>\nint main(){ std::puts(\"captured\"); return 0; }\n");
            byte[] manifestBytes = Encoding.UTF8.GetBytes("""
                {"schemaVersion":1,
                 "contexts":{"only":{"buildDirectory":"legacy-build","sourceDirectory":"source","mode":"managed","generator":"Ninja"}},
                 "nodes":[
                   {"id":"configure","kind":"configure","context":"only"},
                   {"id":"compile","kind":"build","context":"only","target":"probe","dependsOn":["configure"]},
                   {"id":"execute","kind":"run","from":"compile","dependsOn":["compile"]}
                 ]}
                """);
            await File.WriteAllBytesAsync(manifest, manifestBytes);
            await File.WriteAllTextAsync(Path.Combine(liveBuild, "CMakeCache.txt"),
                "stale cache must never be adopted\n");
            string hash = Convert.ToHexString(SHA256.HashData(manifestBytes));
            LoreSnapshotStore lore = new(storePath);
            string id = lore.Capture(source, ["CMakeLists.txt", "main.cpp"]);
            WorkflowStartRequest request = new("source-bound", manifest, hash, 1, output)
            {
                SourceSnapshotId = id
            };

            using (DurableAcceptanceStore db = new(workspace))
            {
                _ = db.Accept(request);
            }
            // An accepted request must use immutable Lore bytes, not the files
            // currently present on disk (nor the old preconfigured build tree).
            await File.WriteAllTextAsync(cpp, "#include <cstdio>\nint main(){ std::puts(\"live\"); return 0; }\n");
            using (DurableExecutionCoordinator coordinator = new(CancellationToken.None))
            {
                OperationReference accepted = coordinator.Start(workspace, request);
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
                    db.GetDispatchCheckpoint(accepted.OperationId) is null)
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
            using (System.Text.Json.JsonDocument report = System.Text.Json.JsonDocument.Parse(
                       await File.ReadAllBytesAsync(Path.Combine(output, "run.json"))))
            {
                if (report.RootElement.GetProperty("sourceSnapshotId").GetString() != id)
                {
                    throw new UnreachableException("Runner report lost immutable Lore identity.");
                }
            }
            using (CMakeWorkflow.History.Storage.HistoryStore history = new(Path.Combine(workspace, "history-test.sqlite")))
            {
                _ = history.Import(output, recursive: false);
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

            // The source store must fail closed before AuthorizeDispatch.
            string missingOutput = Path.Combine(workspace, "missing-results");
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
