using Microsoft.Data.Sqlite;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Protocol;
using CMakeWorkflow.History.Storage;
using CMakeWorkflow.Host.History;
using CMakeWorkflow.Host.Sessions;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CMakeWorkflow.Host.Transport;
using CMakeWorkflow.Host.Tests.Transport;
using CMakeWorkflow.Host.Workflows;
using StreamJsonRpc;
using WorkflowPreflightOptions = CMakeWorkflow.Client.Workflows.WorkflowPreflightOptions;
using WorkflowOperation = CMakeWorkflow.Client.Workflows.WorkflowOperation;

namespace CMakeWorkflow.Host.Tests.Durability;

internal static class DurableWorkflowWireTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new UnreachableException(message);
        }
    }

    public static async Task RunAsync(string endpoint, string parent, CancellationToken ct)
    {
        string workspace = Path.Combine(parent, "wire durable " + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(workspace, "source");
        string build = Path.Combine(workspace, "build");
        string output = Path.Combine(workspace, "execution");
        _ = Directory.CreateDirectory(source);
        _ = Directory.CreateDirectory(build);
        string marker = Path.Combine(source, "effects.txt");
        await File.WriteAllTextAsync(Path.Combine(source, "effect.cmake"),
            "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"started\\n\")\n" +
            "execute_process(COMMAND \"${CMAKE_COMMAND}\" -E sleep 2)\n" +
            "file(APPEND \"${CMAKE_CURRENT_LIST_DIR}/effects.txt\" \"finished\\n\")\n", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"),
            "cmake_minimum_required(VERSION 3.26)\nproject(durable_wire NONE)\n" +
            "add_custom_target(effect COMMAND \"${CMAKE_COMMAND}\" -P \"${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake\")\n" +
            "add_custom_target(fail COMMAND \"${CMAKE_COMMAND}\" -E false)\n", CancellationToken.None);
        string query = Path.Combine(build, ".cmake", "api", "v1", "query");
        _ = Directory.CreateDirectory(query);
        await File.WriteAllTextAsync(Path.Combine(query, "codemodel-v2"), "", CancellationToken.None);
        await ConfigureAsync(source, build, ct);

        JsonNode document = JsonNode.Parse("""
                                           {"schemaVersion":1,"contexts":{"w":{"buildDirectory":"placeholder","mode":"adopt"}},
                                            "nodes":[{"id":"effect","kind":"invoke","context":"w","target":"effect"}]}
                                           """)!;
        document["contexts"]!["w"]!["buildDirectory"] = build;
        string manifest = Path.Combine(workspace, "workflow.json");
        byte[] bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        await File.WriteAllBytesAsync(manifest, bytes, ct);
        WorkflowStartRequest request = new("wire-client-id", manifest,
            Convert.ToHexString(SHA256.HashData(bytes)), 1, output);

        string operationId;
        // Connection A goes away while the external command is executing.
        using (Stream wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc rpc = HostConnection.Create(wire))
        {
            Protocol.IHostRpc client = rpc.Attach<Protocol.IHostRpc>();
            rpc.StartListening();
            _ = await client.InitializeAsync(new(1, workspace), ct);
            OperationReference accepted = await client.WorkflowStartAsync(request, ct);
            operationId = accepted.OperationId;
            // The domain-qualified entry point must share the original durable
            // CMake identity and must not redispatch the same request.
            OperationReference composed = await client.ExecutionStartAsync(new ExecutionStartRequest(
                "cmake", request.ClientRequestId, request.Manifest, request.ManifestSha256,
                request.Jobs, request.ResultsDirectory), ct);
            Check(composed.OperationId == operationId && composed.Kind == "workflow",
                "Neutral Host composition changed CMake operation identity.");
            Check(accepted.ClientRequestId == request.ClientRequestId, "First RPC lost request identity.");
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker) || !(await ReadMarkerAsync(marker, wait.Token)).Contains("started"))
            {
                await Task.Delay(25, wait.Token);
            }

            Check(!(await ReadMarkerAsync(marker, wait.Token)).Contains("finished"),
                "Workflow completed before initiator disconnected; test is not exercising detached execution.");
        }

        // Connection B has a separate RPC session, bound to the same workspace.
        using (Stream wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc rpc = HostConnection.Create(wire))
        {
            Protocol.IHostRpc client = rpc.Attach<Protocol.IHostRpc>();
            rpc.StartListening();
            _ = await client.InitializeAsync(new(1, workspace), ct);
            OperationReference retry = await client.WorkflowStartAsync(request, ct);
            Check(retry.OperationId == operationId, "Reconnected retry created a second operation.");
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            OperationReference current;
            do
            {
                current = await client.OperationGetAsync(new(operationId), wait.Token);
                Check(current.OperationId == operationId, "Operation identity changed after disconnect.");
                if (current.State == "succeeded")
                {
                    break;
                }

                Check(current.State is "accepted_not_dispatched" or "dispatch_authorized",
                    "Unexpected nonterminal state: " + current.State);
                await Task.Delay(50, wait.Token);
            } while (true);

            OperationOverview overview = await client.OperationOverviewAsync(new(operationId), ct);
            Check(overview.IsFinalized && overview.Operation.Kind == "workflow" &&
                  overview.Nodes.Count == 1 && overview.Nodes[0].Id == "effect" &&
                  overview.Nodes[0].Status == "succeeded" && overview.ReportSha256 is { Length: 64 },
                "CMake domain-neutral terminal overview missing verified node state.");
            OperationReference found = await client.OperationFindAsync(new(request.ClientRequestId), ct);
            OperationReference terminalRetry = await client.WorkflowStartAsync(request, ct);
            Check(found.State == "succeeded" && terminalRetry.State == "succeeded"
                                             && terminalRetry.OperationId == operationId,
                "Terminal RPC retry/lookup inconsistency.");

            string[] lines = await ReadMarkerAsync(marker, wait.Token);
            Check(lines.SequenceEqual(["started", "finished"]),
                "Detached execution duplicated or lost the external effect.");
        }

        Check(File.Exists(Path.Combine(output, "run.json")), "Terminal runner report missing.");
        using (DurableAcceptanceStore store = new(workspace, readOnly: true))
        {
            Check(store.Get(operationId)?.State == "succeeded" &&
                  store.GetFinalization(operationId) is { State: "succeeded", ExitCode: 0 },
                "RPC terminal state was not durable.");
        }

        Console.WriteLine(
            "Workflow/start JSON-RPC lifecycle checks passed: disconnect, reconnect, terminal lookup and single dispatch.");
        await CheckDurableHistoryAsync(endpoint, workspace, manifest, request.ManifestSha256, marker, ct);
    }

    private static async Task CheckDurableHistoryAsync(string endpoint, string workspace, string manifest,
        string digest, string marker, CancellationToken ct)
    {
        WorkspaceReadPaths paths = new(workspace);
        string database = Path.Combine(workspace, "history.sqlite");
        HistoryPolicy policy = new(HistoryPolicyMode.ExplicitDatabase, database);
        WorkflowStartRequest request = new("history-crash", manifest, digest, 1, Path.Combine(workspace, "history-run"))
        {
            Only = ["effect"],
            HistoryPolicy = policy
        };

        AcceptedWorkflow accepted;
        using (DurableAcceptanceStore store = new(workspace))
        {
            accepted = store.Accept(request);
            Check(store.GetHistoryIndexStatus(accepted.OperationId).State == HistoryIndexState.AwaitingFinalization,
                "History intent was active before finalization.");
            Check(store.Accept(request).OperationId == accepted.OperationId, "History retry changed identity.");
            foreach (WorkflowStartRequest conflict in new[]
                     {
                         request with { HistoryPolicy = new(HistoryPolicyMode.Disabled) },
                         request with { HistoryPolicy = new(HistoryPolicyMode.ExplicitDatabase, database + ".other") },
                         request with { Only = [] }
                     })
            {
                try
                {
                    _ = store.Accept(conflict);
                    throw new UnreachableException("Changed history request was accepted.");
                }
                catch (InvalidOperationException error) when (error.Message == "request_id_conflict") { }
            }
        }

        _ = await DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, accepted.OperationId, request,
            accepted, null, ct);
        using (DurableAcceptanceStore store = new(workspace, readOnly: true))
        {
            Check(store.Get(accepted.OperationId)?.State == "succeeded" &&
                  store.GetHistoryIndexStatus(accepted.OperationId).State == HistoryIndexState.Pending &&
                  !File.Exists(database), "Finalization did not leave a durable pending intent.");
        }

        byte[] originalManifest = await File.ReadAllBytesAsync(manifest, ct);
        File.Delete(manifest);
        try
        {
            using DurableAcceptanceStore store = new(workspace);
            Check(store.Accept(request).OperationId == accepted.OperationId &&
                  store.GetHistoryIndexStatus(accepted.OperationId).DatabasePath == database,
                "Removing the original manifest changed a previously accepted History policy.");
        }
        finally
        {
            await File.WriteAllBytesAsync(manifest, originalManifest, CancellationToken.None);
        }

        WorkflowStartRequest interrupted = request with
        {
            ClientRequestId = "history-before-finalization",
            ResultsDirectory = Path.Combine(workspace, "interrupted-history-run")
        };
        AcceptedWorkflow unfinished;
        using (DurableAcceptanceStore store = new(workspace))
        {
            unfinished = store.Accept(interrupted);
        }
        try
        {
            _ = await DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, unfinished.OperationId, interrupted,
                unfinished, (stage, _) => stage == "authorized"
                    ? throw new IOException("injected pre-finalization fault")
                    : Task.CompletedTask, ct);
            throw new UnreachableException("Missing pre-finalization fault.");
        }
        catch (IOException error) when (error.Message == "injected pre-finalization fault") { }

        // Interrupt after the History transaction commits, before ledger acknowledgement.
        try
        {
            _ = await DurableHistoryIndexer.IndexAsync(paths, accepted.OperationId, false,
                () => throw new IOException("injected post-commit fault"), ct);
            throw new UnreachableException("Missing post-commit fault.");
        }
        catch (IOException error) when (error.Message == "injected post-commit fault") { }
        using (HistoryStore history = new(database, readOnly: true))
        {
            Check(history.Runs(10, null, null, null, null, null, ct: ct).Count == 1, "History commit was lost.");
        }
        using (DurableAcceptanceStore store = new(workspace, readOnly: true))
        {
            Check(store.GetHistoryIndexStatus(accepted.OperationId).State == HistoryIndexState.Indexing,
                "An interrupted acknowledgement was falsely reported as indexed.");
        }

        string ledgerConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DurableHistoryIndexer.LedgerPath(paths),
            Pooling = false
        }.ToString();
        // Recreate the previous on-disk schema with an interrupted, unowned
        // Indexing row. Read-only inspection works before the additive migration.
        await using (SqliteConnection connection = new(ledgerConnectionString))
        {
            await connection.OpenAsync(ct);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE workflow_history_intents DROP COLUMN attempt_id;";
            _ = await command.ExecuteNonQueryAsync(ct);
        }
        using (DurableAcceptanceStore reader = new(workspace, readOnly: true))
        {
            Check(reader.GetHistoryIndexStatus(accepted.OperationId).State == HistoryIndexState.Indexing,
                "The older History schema could not expose an interrupted attempt.");
        }

        WorkflowStartRequest pending = request with
        {
            ClientRequestId = "history-pending-on-restart",
            ResultsDirectory = Path.Combine(workspace, "pending-history-run")
        };
        AcceptedWorkflow waiting;
        using (DurableAcceptanceStore store = new(workspace))
        {
            waiting = store.Accept(pending);
        }
        _ = await DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, waiting.OperationId, pending,
            waiting, null, ct);

        // Fault inside the finalization transaction, after inserting the terminal
        // row. The ledger must roll back both terminal visibility and activation.
        WorkflowStartRequest atomic = request with
        {
            ClientRequestId = "history-atomic-finalization",
            ResultsDirectory = Path.Combine(workspace, "atomic-history-run")
        };
        AcceptedWorkflow atomicAccepted;
        using (DurableAcceptanceStore store = new(workspace))
        {
            atomicAccepted = store.Accept(atomic);
        }

        await using (SqliteConnection connection = new(ledgerConnectionString))
        {
            await connection.OpenAsync(ct);
            await using SqliteCommand trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER reject_history_activation
                BEFORE UPDATE ON workflow_history_intents
                BEGIN SELECT RAISE(ABORT, 'injected history activation fault'); END;
                """;
            _ = await trigger.ExecuteNonQueryAsync(ct);
            try
            {
                _ = await DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, atomicAccepted.OperationId, atomic,
                    atomicAccepted, null, ct);
                throw new UnreachableException("Missing history activation fault.");
            }
            catch (SqliteException error) when (error.Message.Contains("injected history activation fault", StringComparison.Ordinal)) { }

            using DurableAcceptanceStore ledger = new(workspace);
            Check(ledger.GetFinalization(atomicAccepted.OperationId) is null &&
                  ledger.GetHistoryIndexStatus(atomicAccepted.OperationId).State == HistoryIndexState.AwaitingFinalization &&
                  !ledger.PendingHistoryOperations().Contains(atomicAccepted.OperationId),
                "A failed activation committed terminal state or made history runnable.");
            trigger.CommandText = "DROP TRIGGER reject_history_activation;";
            _ = await trigger.ExecuteNonQueryAsync(ct);
            _ = ledger.FinalizeExecution(atomicAccepted.OperationId, atomic.ResultsDirectory, 0);
            Check(ledger.GetFinalization(atomicAccepted.OperationId) is not null &&
                  ledger.PendingHistoryOperations().Contains(atomicAccepted.OperationId),
                "Finalization did not activate the existing intent.");
        }

        // Two ledger connections model an old worker and a takeover after its
        // lease was released. The state is Indexing both before and after takeover.
        using (DurableAcceptanceStore old = new(workspace))
        using (DurableAcceptanceStore takeover = new(workspace))
        {
            string oldAttempt = old.ClaimHistoryIndex(atomicAccepted.OperationId, retry: false)
                ?? throw new UnreachableException("Missing initial claim.");
            string newAttempt = takeover.ClaimHistoryIndex(atomicAccepted.OperationId, retry: false)
                ?? throw new UnreachableException("Missing recovery claim.");
            Check(oldAttempt != newAttempt &&
                  !old.CompleteHistoryIndex(atomicAccepted.OperationId, oldAttempt) &&
                  !old.CompleteHistoryIndex(atomicAccepted.OperationId, oldAttempt, "stale_failure") &&
                  takeover.GetHistoryIndexStatus(atomicAccepted.OperationId).State == HistoryIndexState.Indexing,
                "An obsolete attempt acknowledged or failed a newer claim.");
        }

        string reportPath = Path.Combine(atomic.ResultsDirectory, "run.json");
        byte[] finalReport = await File.ReadAllBytesAsync(reportPath, ct);
        await File.AppendAllTextAsync(reportPath, "\n", ct);
        // The import boundary also verifies the bytes it parses, even if an
        // earlier Host verification already succeeded before a replacement.
        using (HistoryStore history = new(database))
        {
            try
            {
                _ = history.ImportFinalizedWorkflow(atomic.ResultsDirectory,
                    Convert.ToHexString(SHA256.HashData(finalReport)), paths.Read);
                throw new UnreachableException("The history parser accepted a replaced report.");
            }
            catch (InvalidDataException error) when (error.Message == "Workflow report differs from durable finalization.") { }
        }
        HistoryIndexStatus changed = await DurableHistoryIndexer.IndexAsync(paths, atomicAccepted.OperationId, false, ct);
        Check(changed.State == HistoryIndexState.Failed && changed.DiagnosticCode == "report_changed",
            "History did not explicitly refuse a modified final report.");
        using (HistoryStore history = new(database, readOnly: true))
        {
            Check(history.Runs(10, null, null, null, null, null, ct: ct).Count == 1,
                "The modified report reached History.");
        }

        int dispatches = (await File.ReadAllLinesAsync(marker, ct)).Count(line => line == "started");
        // New session initialization sweeps the persisted intent. The retry endpoint
        // can also race with recovery and must share its indexing lease.
        await using TransportTestHost restarted = new();
        await restarted.ReadyAsync(ct);
        await using CMakeWorkflowClient sdk = await CMakeWorkflowClient.ConnectAsync(new(restarted.Endpoint, workspace), ct);
        await using CMakeWorkflowClient second = await CMakeWorkflowClient.ConnectAsync(new(endpoint, workspace), ct);
        _ = await Task.WhenAll(sdk.History.RetryIndexAsync(accepted.OperationId, ct),
            second.History.RetryIndexAsync(accepted.OperationId, ct));
        HistoryIndexStatus indexed;
        do
        {
            indexed = await sdk.History.GetIndexStatusAsync(accepted.OperationId, ct);
            if (indexed.State == HistoryIndexState.Indexed)
            {
                break;
            }

            Check(indexed.State is HistoryIndexState.Indexing or HistoryIndexState.Pending,
                "Unexpected History recovery state: " + indexed.State);
            await Task.Delay(30, ct);
        } while (true);
        while ((await sdk.History.GetIndexStatusAsync(waiting.OperationId, ct)).State != HistoryIndexState.Indexed)
        {
            await Task.Delay(30, ct);
        }
        Check(indexed.DatabasePath == database &&
              (await sdk.History.RunsAsync(new(database), ct)).Count == 2 &&
              (await File.ReadAllLinesAsync(marker, ct)).Count(line => line == "started") == dispatches,
            "Recovering History duplicated a run or dispatched a workflow node.");

        Check((await sdk.History.RetryIndexAsync(atomicAccepted.OperationId, ct)).DiagnosticCode == "report_changed" &&
              (await sdk.Operations.GetAsync(atomicAccepted.OperationId, ct)).State == "succeeded",
            "Retry accepted changed bytes or modified the terminal outcome.");
        await File.WriteAllBytesAsync(reportPath, finalReport, ct);
        Check((await sdk.History.RetryIndexAsync(atomicAccepted.OperationId, ct)).State == HistoryIndexState.Indexed &&
              (await sdk.History.RunsAsync(new(database), ct)).Count == 3 &&
              (await File.ReadAllLinesAsync(marker, ct)).Count(line => line == "started") == dispatches,
            "Restoring the exact final report did not permit index-only recovery.");

        Check((await sdk.History.GetIndexStatusAsync(unfinished.OperationId, ct)).State ==
              HistoryIndexState.AwaitingFinalization, "An unfinished operation was indexed after restart.");
        string legacyOperation = (await sdk.Operations.FindAsync("wire-client-id", ct)).OperationId;
        Check((await sdk.History.GetIndexStatusAsync(legacyOperation, ct)).State == HistoryIndexState.Disabled &&
              !File.Exists(Path.Combine(workspace, ".cmakeworkflow", "history.sqlite")),
            "A legacy request created a default History database.");

        string badDatabase = Path.Combine(workspace, "bad-history.sqlite");
        await File.WriteAllTextAsync(badDatabase, "this is not SQLite", ct);
        WorkflowOperation bad = await sdk.Workflows.StartAsync(new("history-failure", manifest, digest, 1,
            Path.Combine(workspace, "failed-history-run"))
        {
            Only = ["effect"],
            HistoryPolicy = new(HistoryPolicyMode.ExplicitDatabase, badDatabase)
        }, ct);
        HistoryIndexStatus failed;
        do
        {
            failed = await sdk.History.GetIndexStatusAsync(bad.OperationId, ct);
            if (failed.State == HistoryIndexState.Failed)
            {
                break;
            }

            await Task.Delay(30, ct);
        } while (true);
        Check(failed.DiagnosticCode == "history_database_error" &&
              (await sdk.Operations.GetAsync(bad.OperationId, ct)).State == "succeeded",
            "Indexing failure changed the successful workflow outcome.");
        File.Delete(badDatabase);
        Check((await sdk.History.RetryIndexAsync(bad.OperationId, ct)).State == HistoryIndexState.Indexed,
            "Index-only retry did not recover after database repair.");

        try
        {
            _ = await sdk.Workflows.StartAsync(new("outside-history", manifest, digest, 1,
                Path.Combine(workspace, "outside-history-run"))
            {
                HistoryPolicy = new(HistoryPolicyMode.ExplicitDatabase,
                    Path.Combine(Path.GetDirectoryName(workspace)!, "outside.sqlite"))
            }, ct);
            throw new UnreachableException("External History database path was accepted.");
        }
        catch (CMakeWorkflowRpcRefusalException error) when (error.Code == "path_not_allowed") { }

        if (!OperatingSystem.IsWindows())
        {
            string escapedSidecar = Path.Combine(workspace, "sidecar.sqlite");
            _ = File.CreateSymbolicLink(escapedSidecar + "-journal", Path.Combine(Path.GetDirectoryName(workspace)!, "outside.sqlite"));
            try
            {
                _ = await sdk.Workflows.PreflightAsync(new WorkflowPreflightOptions(
                    manifest, digest, 1, Path.Combine(workspace, "sidecar-run"))
                {
                    HistoryPolicy = new(HistoryPolicyMode.ExplicitDatabase, escapedSidecar)
                }, ct);
                throw new UnreachableException("External History sidecar was accepted.");
            }
            catch (CMakeWorkflowRpcRefusalException error) when (error.Code == "path_not_allowed") { }
        }

        JsonNode failure = JsonNode.Parse(originalManifest)!;
        failure["nodes"]![0]!["target"] = "fail";
        byte[] failureBytes = Encoding.UTF8.GetBytes(failure.ToJsonString());
        string failureManifest = Path.Combine(workspace, "failing-workflow.json");
        await File.WriteAllBytesAsync(failureManifest, failureBytes, ct);
        WorkflowOperation failedWorkflow = await sdk.Workflows.StartAsync(new("history-of-failed-workflow",
            failureManifest, Convert.ToHexString(SHA256.HashData(failureBytes)), 1, Path.Combine(workspace, "failed-run"))
        {
            HistoryPolicy = policy
        }, ct);
        while ((await sdk.History.GetIndexStatusAsync(failedWorkflow.OperationId, ct)).State != HistoryIndexState.Indexed)
        {
            await Task.Delay(30, ct);
        }
        Check((await sdk.Operations.GetAsync(failedWorkflow.OperationId, ct)).State == "failed" &&
              (await sdk.History.RunsAsync(new(database, Status: "failed"), ct)).Count == 1,
            "A valid failed finalization was not indexed with its original outcome.");

        Console.WriteLine("Durable History checks passed: identity, finalization, interrupted acknowledgement, concurrent recovery, isolated failure and retry.");
    }

    private static async Task<string[]> ReadMarkerAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await File.ReadAllLinesAsync(path, ct);
            }
            // CMake file(APPEND) may briefly deny concurrent readers on Windows.
            // Only ERROR_SHARING_VIOLATION is transient; all other failures propagate.
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 32)
            {
                await Task.Delay(25, ct);
            }
        }
    }

    private static async Task ConfigureAsync(string source, string build, CancellationToken ct)
    {
        ProcessStartInfo start = new("cmake")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string? arg in new[] { "-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" })
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start) ?? throw new UnreachableException("Cannot start CMake configure.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            throw new UnreachableException("Wire fixture CMake configure failed: " + await stdout + await stderr);
        }

        _ = await Task.WhenAll(stdout, stderr);
    }
}
