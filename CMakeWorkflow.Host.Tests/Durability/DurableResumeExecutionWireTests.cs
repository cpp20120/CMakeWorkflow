using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CMakeWorkflow;
using CMakeWorkflow.CMake.Execution;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Host;
using StreamJsonRpc;

internal static class DurableResumeExecutionWireTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok)
        {
            throw new UnreachableException(message);
        }
    }

    public static async Task RunAsync(string endpoint, string parent, CancellationToken ct)
    {
        string root = Path.Combine(parent, "resume wire " + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "src");
        string build = Path.Combine(root, "build");
        string run = Path.Combine(root, "run");
        _ = Directory.CreateDirectory(source);
        _ = Directory.CreateDirectory(build);
        foreach (string? id in new[] { "a", "b" })
        {
            await File.WriteAllTextAsync(Path.Combine(source, id + ".cmake"),
                $"file(APPEND \"${{CMAKE_CURRENT_LIST_DIR}}/effects.txt\" \"{id}\\n\")\n", CancellationToken.None);
        }

        await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"),
            "cmake_minimum_required(VERSION 3.26)\nproject(resume_wire NONE)\n" +
            "add_custom_target(a COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/a.cmake\")\n" +
            "add_custom_target(b COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/b.cmake\")\n", CancellationToken.None);
        string query = Path.Combine(build, ".cmake", "api", "v1", "query");
        _ = Directory.CreateDirectory(query);
        await File.WriteAllTextAsync(Path.Combine(query, "codemodel-v2"), "", CancellationToken.None);
        ProcessStartInfo psi = new("cmake")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (string? arg in new[] { "-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" })
        {
            psi.ArgumentList.Add(arg);
        }

        using (Process configure = Process.Start(psi) ?? throw new UnreachableException("Cannot configure CMake fixture"))
        {
            Task<string> stderr = configure.StandardError.ReadToEndAsync(ct);
            Task<string> stdout = configure.StandardOutput.ReadToEndAsync(ct);
            await configure.WaitForExitAsync(ct);
            Check(configure.ExitCode == 0, "CMake configure failed: " + await stderr + await stdout);
        }

        JsonNode doc = JsonNode.Parse("""
                                 {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
                                  "nodes":[{"id":"a","kind":"invoke","context":"w","target":"a"},
                                           {"id":"b","kind":"invoke","context":"w","target":"b","dependsOn":["a"]}]}
                                 """)!;
        doc["contexts"]!["w"]!["buildDirectory"] = build;
        string manifest = Path.Combine(root, "workflow.json");
        byte[] content = Encoding.UTF8.GetBytes(doc.ToJsonString());
        await File.WriteAllBytesAsync(manifest, content, ct);
        string sha = Convert.ToHexString(SHA256.HashData(content));
        WorkflowPlan plan = await WorkflowPlanner.LoadSnapshotAsync(manifest, content, [], null, ct);
        Check(plan.Nodes.All(n => n.Diagnostics.All(d => d.Severity != "Error")), "Resume fixture preflight failed");

        // Build a real partial runner checkpoint: A succeeds; B reaches a durable
        // write-ahead checkpoint but is intentionally stopped before dispatch.
        // This fixture simulates an operator's verified no-effect classification.
        int dispatchCount = 0;
        _ = await WorkflowRunner.RunAsync(plan, 1, run, ct, beforeExternalDispatch: (_, _) =>
        {
            return Interlocked.Increment(ref dispatchCount) == 2
                ? throw new IOException("Injected pre-dispatch failure for B")
                : Task.CompletedTask;
        });
        string marker = Path.Combine(source, "effects.txt");
        Check(File.Exists(marker) && (await File.ReadAllLinesAsync(marker, ct)).SequenceEqual(["a"]),
            "Pre-recovery checkpoint did not record exactly one external A effect");
        string reportPath = Path.Combine(run, "run.json");
        WorkflowReport report = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(reportPath, ct),
            WorkflowJson.Default.WorkflowReport) ?? throw new UnreachableException("Missing partial report");
        Check(report.Nodes.Single(n => n.Id == "a").Status == "succeeded", "A must be succeeded");
        NodeResult pending = report.Nodes.Single(n => n.Id == "b");
        // The runner conservatively quarantines a write-ahead "running" node as
        // outcome_unknown when the pre-dispatch hook throws. The hook itself
        // proves B never reached ExecuteAsync in THIS deterministic fixture.
        Check(pending.Status == "outcome_unknown",
            "Runner must quarantine the interrupted B dispatch admission");
        string operationId;
        using (DurableAcceptanceStore store = new(root))
        {
            operationId = store.Accept("resume-e2e", manifest, sha, 1, run).OperationId;
            store.ReserveResultsDirectory(operationId, run);
            _ = store.AuthorizeDispatch(operationId);
            // Reconstruct the existing runner dispatch binding while B is still
            // in its write-ahead running state (before operator reconciliation).
            pending.Status = "running";
            pending.Error = null;
            report.Status = "running";
            report.Error = null;
            report.FinishedAt = null;
            await File.WriteAllBytesAsync(reportPath,
                JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport), ct);
            _ = store.ConfirmRunnerCheckpoint(operationId, run);
            pending.Status = "needs_attention";
            pending.Error = "Operator verified B did not execute";
            report.Status = "needs_attention";
            report.Error = null;
            report.FinishedAt = null;
            await File.WriteAllBytesAsync(reportPath,
                JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport), ct);
            Check(store.Reconcile(operationId, 0, "needs_attention", "Verified B had no external effect").Revision == 1,
                "Reconcile failed");
        }

        using (Stream wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc rpc = HostConnection.Create(wire))
        {
            IHostRpc client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            _ = await client.InitializeAsync(new(1, root), ct);
            Check((await client.WorkflowResumeAsync(new(operationId, 1, true), ct)).Revision == 2,
                "Resume CAS admission failed");
            Check((await client.WorkflowResumeExecuteAsync(new(operationId, 2, true), ct)).Revision == 2,
                "Resume execution RPC rejected admitted operation");
            // Retry over the same connection while running must not re-dispatch.
        }

        using (Stream wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc rpc = HostConnection.Create(wire))
        {
            IHostRpc client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            _ = await client.InitializeAsync(new(1, root), ct);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            while (true)
            {
                OperationReference current = await client.OperationGetAsync(new(operationId), timeout.Token);
                if (current.State == "succeeded")
                {
                    break;
                }

                Check(current.State == "dispatch_authorized", "Unexpected resumed state: " + current.State);
                await Task.Delay(50, timeout.Token);
            }
        }

        Check((await File.ReadAllLinesAsync(marker, ct)).SequenceEqual(["a", "b"]),
            "Resume duplicated completed A or lost B");
        using (DurableAcceptanceStore store = new(root))
        {
            Check(store.GetFinalization(operationId) is { State: "succeeded", ExitCode: 0 },
                "Resume terminal report not durable");
            try
            {
                _ = store.ClaimResumeAttempt(operationId, run);
                throw new UnreachableException("A second resume attempt was accepted");
            }
            catch (InvalidOperationException)
            {
            }
        }

        Console.WriteLine(
            "Workflow/resumeExecute E2E passed: partial DAG, reconnect, single attempt, completed-node skip and terminal finalization.");
    }
}