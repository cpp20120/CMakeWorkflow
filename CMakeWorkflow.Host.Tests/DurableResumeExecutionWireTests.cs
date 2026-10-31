using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CMakeWorkflow;
using CMakeWorkflow.Host;

internal static class DurableResumeExecutionWireTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); }

    public static async Task RunAsync(string endpoint, string parent, CancellationToken ct)
    {
        var root = Path.Combine(parent, "resume wire " + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "src");
        var build = Path.Combine(root, "build");
        var run = Path.Combine(root, "run");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(build);
        foreach (var id in new[] { "a", "b" })
            File.WriteAllText(Path.Combine(source, id + ".cmake"),
                $"file(APPEND \"${{CMAKE_CURRENT_LIST_DIR}}/effects.txt\" \"{id}\\n\")\n");
        File.WriteAllText(Path.Combine(source, "CMakeLists.txt"),
            "cmake_minimum_required(VERSION 3.26)\nproject(resume_wire NONE)\n" +
            "add_custom_target(a COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/a.cmake\")\n" +
            "add_custom_target(b COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/b.cmake\")\n");
        var query = Path.Combine(build, ".cmake", "api", "v1", "query");
        Directory.CreateDirectory(query);
        File.WriteAllText(Path.Combine(query, "codemodel-v2"), "");
        var psi = new ProcessStartInfo("cmake") { UseShellExecute = false,
            RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" })
            psi.ArgumentList.Add(arg);
        using (var configure = Process.Start(psi) ?? throw new Exception("Cannot configure CMake fixture"))
        {
            var stderr = configure.StandardError.ReadToEndAsync(ct);
            var stdout = configure.StandardOutput.ReadToEndAsync(ct);
            await configure.WaitForExitAsync(ct);
            Check(configure.ExitCode == 0, "CMake configure failed: " + await stderr + await stdout);
        }
        var doc = JsonNode.Parse("""
            {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
             "nodes":[{"id":"a","kind":"invoke","context":"w","target":"a"},
                      {"id":"b","kind":"invoke","context":"w","target":"b","dependsOn":["a"]}]}
            """)!;
        doc["contexts"]!["w"]!["buildDirectory"] = build;
        var manifest = Path.Combine(root, "workflow.json");
        var content = Encoding.UTF8.GetBytes(doc.ToJsonString());
        await File.WriteAllBytesAsync(manifest, content, ct);
        var sha = Convert.ToHexString(SHA256.HashData(content));
        var plan = await WorkflowPlanner.LoadSnapshotAsync(manifest, content, [], null, ct);
        Check(plan.Nodes.All(n => n.Diagnostics.All(d => d.Severity != "Error")), "Resume fixture preflight failed");

        // Build a real partial runner checkpoint: A succeeds; B reaches a durable
        // write-ahead checkpoint but is intentionally stopped before dispatch.
        // This fixture simulates an operator's verified no-effect classification.
        var dispatchCount = 0;
        await WorkflowRunner.RunAsync(plan, 1, run, ct, beforeExternalDispatch: (_, _) =>
        {
            if (Interlocked.Increment(ref dispatchCount) == 2)
                throw new IOException("Injected pre-dispatch failure for B");
            return Task.CompletedTask;
        });
        var marker = Path.Combine(source, "effects.txt");
        Check(File.Exists(marker) && File.ReadAllLines(marker).SequenceEqual(["a"]),
            "Pre-recovery checkpoint did not record exactly one external A effect");
        var reportPath = Path.Combine(run, "run.json");
        var report = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(reportPath, ct),
            WorkflowJson.Default.WorkflowReport) ?? throw new Exception("Missing partial report");
        Check(report.Nodes.Single(n => n.Id == "a").Status == "succeeded", "A must be succeeded");
        var pending = report.Nodes.Single(n => n.Id == "b");
        // The runner conservatively quarantines a write-ahead "running" node as
        // outcome_unknown when the pre-dispatch hook throws. The hook itself
        // proves B never reached ExecuteAsync in THIS deterministic fixture.
        Check(pending.Status == "outcome_unknown",
            "Runner must quarantine the interrupted B dispatch admission");
        string operationId;
        using (var store = new DurableAcceptanceStore(root))
        {
            operationId = store.Accept("resume-e2e", manifest, sha, 1, run).OperationId;
            store.ReserveResultsDirectory(operationId, run);
            store.AuthorizeDispatch(operationId);
            // Reconstruct the existing runner dispatch binding while B is still
            // in its write-ahead running state (before operator reconciliation).
            pending.Status = "running";
            pending.Error = null;
            report.Status = "running";
            report.Error = null;
            report.FinishedAt = null;
            await File.WriteAllBytesAsync(reportPath,
                JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport), ct);
            store.ConfirmRunnerCheckpoint(operationId, run);
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
        using (var wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (var rpc = HostConnection.Create(wire))
        {
            var client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            await client.InitializeAsync(new(1, root), ct);
            Check((await client.WorkflowResumeAsync(new(operationId, 1, true), ct)).Revision == 2,
                "Resume CAS admission failed");
            Check((await client.WorkflowResumeExecuteAsync(new(operationId, 2, true), ct)).Revision == 2,
                "Resume execution RPC rejected admitted operation");
            // Retry over the same connection while running must not re-dispatch.
        }
        using (var wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (var rpc = HostConnection.Create(wire))
        {
            var client = rpc.Attach<IHostRpc>();
            rpc.StartListening();
            await client.InitializeAsync(new(1, root), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            while (true)
            {
                var current = await client.OperationGetAsync(new(operationId), timeout.Token);
                if (current.State == "succeeded") break;
                Check(current.State == "dispatch_authorized", "Unexpected resumed state: " + current.State);
                await Task.Delay(50, timeout.Token);
            }
        }
        Check(File.ReadAllLines(marker).SequenceEqual(["a", "b"]),
            "Resume duplicated completed A or lost B");
        using (var store = new DurableAcceptanceStore(root))
        {
            Check(store.GetFinalization(operationId) is { State: "succeeded", ExitCode: 0 },
                "Resume terminal report not durable");
            try { store.ClaimResumeAttempt(operationId, run); throw new Exception("A second resume attempt was accepted"); }
            catch (InvalidOperationException) { }
        }
        Console.WriteLine("Workflow/resumeExecute E2E passed: partial DAG, reconnect, single attempt, completed-node skip and terminal finalization.");
    }
}
