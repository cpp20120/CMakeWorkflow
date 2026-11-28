using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core.Persistence;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Transport;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Storage.Documents;
using Microsoft.Data.Sqlite;
using StreamJsonRpc;

namespace CMakeWorkflow.Host.Tests.Durability;

internal static partial class BinaryWorkflowWireTests
{
    internal static async Task RunAsync(string cli, string faultCli, string? oldCli, string? inlineCli,
        string? previousDocumentCli, string? layoutCli, string? journalCli, string? reportCli, string? checkpointCli,
        string? processCli)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        CancellationToken ct = timeout.Token;
        DirectoryInfo temp = Directory.CreateTempSubdirectory("cmwf-binary-");
        try
        {
            string root = temp.FullName;
            string source = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
            string build = Directory.CreateDirectory(Path.Combine(root, "build")).FullName;
            string run = Path.Combine(root, "run");
            string effects = Path.Combine(source, "effects.txt");
            foreach (string id in new[] { "a", "b" })
            {
                await File.WriteAllTextAsync(Path.Combine(source, id + ".cmake"),
                    $"file(APPEND \"${{CMAKE_CURRENT_LIST_DIR}}/effects.txt\" \"{id}\\n\")\n", ct);
            }

            await File.AppendAllTextAsync(Path.Combine(source, "b.cmake"),
                "file(WRITE \"${CMAKE_CURRENT_LIST_DIR}/../run/effect-b.txt\" \"recovered\")\n", ct);
            await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"), """
                cmake_minimum_required(VERSION 3.26)
                project(binary_recovery NONE)
                add_custom_target(a COMMAND "${CMAKE_COMMAND}" -P "${CMAKE_CURRENT_SOURCE_DIR}/a.cmake")
                add_custom_target(b COMMAND "${CMAKE_COMMAND}" -P "${CMAKE_CURRENT_SOURCE_DIR}/b.cmake")
                """, ct);
            string query = Directory.CreateDirectory(Path.Combine(build, ".cmake", "api", "v1", "query")).FullName;
            await File.WriteAllTextAsync(Path.Combine(query, "codemodel-v2"), "", ct);
            await RunCommandAsync("cmake", ["-S", source, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug"], ct);

            BinaryWorkflowSubmission.InitializeWorkspace(root);
            WorkflowDocument workflow = new()
            {
                SchemaVersion = 2,
                Contexts = { ["tree"] = new() { BuildDirectory = build, Mode = "adopt" } },
                Nodes =
                [
                    new() { Id = "a", Kind = "invoke", Context = "tree", Target = "a" },
                    new()
                    {
                        Id = "b", Kind = "invoke", Context = "tree", Target = "b", DependsOn = ["a"],
                        Outputs = [new() { Name = "effect", Root = "run", Path = "effect-b.txt" }]
                    }
                ]
            };
            string manifest = Path.Combine(root, "workflow.capnp");
            byte[] frame = CapnpWire.Frame(CapnpWire.Canonicalize(ProductionWorkflowCodec.Encode(workflow)));
            string sha = Convert.ToHexString(SHA256.HashData(frame));
            await ComparePlansAsync(workflow, frame, manifest, sha, ct);
            string marker = Path.Combine(root, "fault.marker");
            ExecutionStartRequest request = new(BinaryWorkflowSubmission.Domain, "native-binary-retry", manifest, sha, 1, run)
            {
                HistoryPolicy = new(HistoryPolicyMode.Default)
            };

            string operation;
            string checkpoint;
            long interruptedEventSequence;
            using (Peer interrupted = await Peer.StartAsync(faultCli, root, ct, marker))
            {
                using JsonRpc connection = HostConnection.Create(await LocalTransport.ConnectAsync(interrupted.Endpoint, ct));
                IHostRpc rpc = await ConnectAsync(connection, root, ct);
                WorkflowManifestPreparation stage = new(".", "run", sha, ReadOnlyMemory<byte>.Empty);
                Require(await rpc.PrepareBinaryManifestAsync(stage, ct) is null, "A new frame probe unexpectedly succeeded.");
                await RejectAsync(() => rpc.PrepareBinaryManifestAsync(stage with { ManifestBytes = new byte[frame.Length] }, ct));
                WorkflowManifestPreparationReceipt prepared = await rpc.PrepareBinaryManifestAsync(
                    stage with { ManifestBytes = frame }, ct) ?? throw new InvalidOperationException("Binary publication failed.");
                Require(await rpc.PrepareBinaryManifestAsync(stage, ct) == prepared, "Staged bytes did not survive verification.");
                manifest = prepared.Manifest;
                request = request with { Manifest = manifest };
                await RejectAsync(() => rpc.ExecutionStartAsync(
                    request with { ManifestSha256 = new string('0', 64) }, ct));
                using JsonRpc concurrent = HostConnection.Create(await LocalTransport.ConnectAsync(interrupted.Endpoint, ct));
                IHostRpc second = await ConnectAsync(concurrent, root, ct);
                OperationReference[] accepted = await Task.WhenAll(rpc.ExecutionStartAsync(request, ct),
                    second.ExecutionStartAsync(request, ct));
                operation = accepted[0].OperationId;
                Require(accepted[1].OperationId == operation, "Concurrent admission created another operation.");
                // Prove a retry remains independent of the staging source path after acceptance.
                File.Delete(manifest);
                Require((await rpc.ExecutionStartAsync(request, ct)).OperationId == operation, "Request replay created another operation.");
                await RejectAsync(() => rpc.ExecutionStartAsync(request with { Jobs = 2 }, ct));
                await RejectAsync(() => rpc.WorkflowStartAsync(new(request.ClientRequestId, manifest, sha, 1, run), ct));

                await WaitForAsync(() => Task.FromResult(File.Exists(marker)), ct);
                OperationEventsPage interruptedEvents = await rpc.OperationEventsAsync(new(operation, 0, 20), ct);
                interruptedEventSequence = interruptedEvents.NextSequence;
                Require(interruptedEvents.Events.Count == 4 &&
                    interruptedEvents.Events[^1] is { NodeId: "b", Type: "node_started" } &&
                    !File.Exists(Path.Combine(run, "events.jsonl")), "The running B intent was not captured in the native event stream.");
                await RunCommandAsync(cli, ["cas", WorkflowLedgerPath.Documents(root), "gc", "--apply"], ct);
                await interrupted.KillAsync(ct);
                if (!OperatingSystem.IsWindows())
                {
                    File.Delete(interrupted.Endpoint);
                }
                Require((await File.ReadAllLinesAsync(effects, ct)).SequenceEqual(["a"]), "Fault did not precede B's effect.");
                using DurableAcceptanceStore ledger = new(root, readOnly: true);
                checkpoint = ledger.InspectRecovery(operation).CheckpointSha256
                    ?? throw new InvalidOperationException("The interrupted checkpoint was not durably bound.");
                BoundNativeCheckpoint saved = ledger.ReadNativeCheckpoint(operation);
                Require(saved.Digest == checkpoint && saved.State.Report.Nodes.Last().Status == "running" &&
                    saved.State.EventCursor < interruptedEventSequence && !File.Exists(Path.Combine(run, "run.json")),
                    "The write-ahead intent was not bound to native checkpoint bytes.");
                AssertBinaryOnly(root, operation);
            }

            RehearseOfflineRestore(root);
            string cas = WorkflowLedgerPath.Documents(root);
            await RunCommandAsync(cli, ["cas", cas, "gc", "--apply"], ct);
            if (oldCli is not null)
            {
                await CheckOldHostAsync(oldCli, root, ct);
            }
            if (inlineCli is not null)
            {
                await CheckOldHostAsync(inlineCli, root, ct);
                await RunCommandRejectedAsync(inlineCli, ["cas", cas, "gc", "--apply"], ct);
                await VerifyPreviousWorkspaceAsync(inlineCli, cli, CreateWorkspace(root, "inline"), epoch: 2, ct);
            }
            if (previousDocumentCli is not null)
            {
                await CheckOldHostAsync(previousDocumentCli, root, ct);
                await VerifyPreviousWorkspaceAsync(previousDocumentCli, cli, CreateWorkspace(root, "previous"), epoch: 3, ct);
            }
            if (layoutCli is not null)
            {
                await CheckOldHostAsync(layoutCli, root, ct);
                await VerifyPreviousWorkspaceAsync(layoutCli, cli, CreateWorkspace(root, "layout"), epoch: 4, ct);
            }

            if (journalCli is not null)
            {
                await CheckOldHostAsync(journalCli, root, ct);
                await RunCommandRejectedAsync(journalCli, ["cas", cas, "gc", "--apply"], ct);
                await VerifyPreviousWorkspaceAsync(journalCli, cli, CreateWorkspace(root, "journal"), epoch: 5, ct);
            }

            if (reportCli is not null)
            {
                await CheckOldHostAsync(reportCli, root, ct);
                await VerifyPreviousWorkspaceAsync(reportCli, cli, CreateWorkspace(root, "report"), epoch: 6, ct);
            }

            if (checkpointCli is not null)
            {
                await CheckOldHostAsync(checkpointCli, root, ct);
                await VerifyPreviousWorkspaceAsync(checkpointCli, cli, CreateWorkspace(root, "checkpoint"), epoch: 7, ct);
            }

            if (processCli is not null)
            {
                await CheckOldHostAsync(processCli, root, ct);
                await VerifyPreviousWorkspaceAsync(processCli, cli, CreateWorkspace(root, "epoch8"), epoch: 8, ct);
            }


            DocumentStore documents = await DocumentStore.OpenReportStoreAsync(cas, ct);
            DocumentId planId = (await ReadAuthorizationAsync(root, operation, ct)).Document;
            string planBlob;
            using (DocumentSession plan = await documents.OpenAsync(planId, ct: ct))
            {
                Require(plan.Descriptor.Codec == DocumentCodec.ExecutionPlanCapnp && plan.Verification.CanonicalSegment,
                    "GC lost the authorized plan document.");
                planBlob = Path.Combine(cas, "blobs", plan.Descriptor.Blob.Value);
            }

            using (Peer restarted = await Peer.StartAsync(cli, root, ct))
            {
                using JsonRpc connection = HostConnection.Create(await LocalTransport.ConnectAsync(restarted.Endpoint, ct));
                IHostRpc rpc = await ConnectAsync(connection, root, ct);
                Require((await rpc.OperationFindAsync(new(request.ClientRequestId), ct)).OperationId == operation,
                    "Request ID no longer resolves after restart.");
                OperationEventsPage page = await rpc.OperationEventsAsync(new(operation, 0, 2), ct);
                Require(page.HasMore && page.NextSequence == 2 &&
                    (await rpc.OperationEventsAsync(new(operation, 2, 2), ct)).NextSequence == interruptedEventSequence,
                    "Offline restore failed to preserve the ordered event stream and cursor.");
                Require((await rpc.ExecutionStartAsync(request, ct)).State == "dispatch_authorized",
                    "Retry implicitly redispatched an authorized operation.");
                await RejectAsync(() => rpc.WorkflowResumeExecuteAsync(new(operation, 2, true), ct));
                await RejectAsync(() => rpc.RecoveryReconcileAsync(new(operation, 0, "needs_attention", "Verified B not started", true)
                {
                    RetryBinaryNode = "b",
                    ExpectedCheckpointSha256 = new string('0', 64)
                }, ct));

                RecoveryInspectionView reviewed = await rpc.RecoveryReconcileAsync(
                    new(operation, 0, "needs_attention", "Verified B did not start at the injected boundary.", true)
                    {
                        RetryBinaryNode = "b",
                        ExpectedCheckpointSha256 = checkpoint
                    }, ct);
                Require(reviewed.Revision == 1, "Binary reconciliation did not commit.");
                RecoveryInspectionView admitted = await rpc.WorkflowResumeAsync(new(operation, 1, true), ct);
                Require(admitted.Revision == 2, "Binary resume was not admitted.");
                string authorized = (await ReadAuthorizationAsync(root, operation, ct)).Identity;
                await SetBindingIdentityAsync(root, operation, new string('0', 64), ct);
                try
                {
                    await RejectAsync(() => rpc.WorkflowResumeExecuteAsync(new(operation, 2, true), ct));
                }
                finally
                {
                    await SetBindingIdentityAsync(root, operation, authorized, ct);
                }

                string unavailable = planBlob + ".unavailable";
                File.Move(planBlob, unavailable);
                try
                {
                    await RejectAsync(() => rpc.WorkflowResumeExecuteAsync(new(operation, 2, true), ct));
                }
                finally
                {
                    File.Move(unavailable, planBlob);
                }

                _ = await rpc.WorkflowResumeExecuteAsync(new(operation, 2, true), ct);
                await WaitForAsync(async () =>
                    (await rpc.OperationGetAsync(new(operation), ct)).State == "succeeded", ct);
                await WaitForAsync(async () =>
                    (await rpc.HistoryIndexStatusAsync(new(operation), ct)).State == HistoryIndexState.Indexed, ct);
                WorkflowReportSnapshot report = await rpc.OperationReportAsync(new(operation), ct);
                Require(report.Report.ManifestHash == sha && report.Report.Nodes.All(n => n.Status == "succeeded"),
                    "Verified report lost binary manifest provenance.");
                OperationEventsPage resumedEvents = await rpc.OperationEventsAsync(new(operation, interruptedEventSequence, 20), ct);
                Require(resumedEvents.Events[0] is { Type: "workflow_resumed" } &&
                    resumedEvents.Events[0].Sequence == interruptedEventSequence + 1 &&
                    resumedEvents.Events[^1] is { Type: "workflow_finished", Status: "succeeded" } &&
                    resumedEvents.Events.All(e => e.NodeId != "a"),
                    "Native event replay missed recovery, discontinuity or completed-node suppression.");
                OperationArtifactList artifacts = await rpc.OperationArtifactListAsync(new(operation), ct);
                Require(artifacts.Artifacts.Any(artifact => artifact.Kind == "output")
                    && await File.ReadAllTextAsync(Path.Combine(run, "effect-b.txt"), ct) == "recovered",
                    "Recovered artifact is absent from the verified operation catalog.");
                await VerifyIndexedArtifactsAsync(cli, restarted.Endpoint, root, operation, ct);
            }

            Require((await File.ReadAllLinesAsync(effects, ct)).SequenceEqual(["a", "b"]),
                "Recovery replayed A or failed to launch B.");
            AssertBinaryOnly(root, operation);
            await VerifyEmptySnapshotAsync(root, operation, ct);
            await RunCommandAsync(cli, ["workflow", "binary-info", root, operation], ct);
            await RunCommandAsync(cli, ["workflow", "binary-export-json", root, operation], ct);
            await RunCommandAsync(cli, ["cas", cas, "gc", "--apply"], ct);
            using (DurableAcceptanceStore ledger = new(root, readOnly: true))
            {
                BinaryWorkflowInput input = ledger.ReadBinaryInput(operation) ?? throw new InvalidDataException("Missing input after GC.");
                using DocumentSession retained = await documents.OpenAsync(input.Document.Id, ct: ct);
                Require(retained.Descriptor == input.Document && !retained.Descriptor.Legacy.HasValue, "GC lost the binary input.");
            }

            Console.WriteLine("Binary RPC publication, concurrent admission, active GC, plan retention, Host termination and recovery passed.");
            if (oldCli is not null || inlineCli is not null || previousDocumentCli is not null || layoutCli is not null || journalCli is not null || reportCli is not null)
            {
                Console.WriteLine("Supplied older executable fences passed.");
            }
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static void RehearseOfflineRestore(string workspace)
    {
        string archive = workspace + ".backup";
        string original = workspace + ".original";
        try
        {
            _ = OperatingSystem.IsWindows() ? Directory.CreateDirectory(archive)
                : Directory.CreateDirectory(archive, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (string directory in Directory.EnumerateDirectories(workspace, "*", SearchOption.AllDirectories))
            {
                _ = Directory.CreateDirectory(Path.Combine(archive, Path.GetRelativePath(workspace, directory)));
            }
            foreach (string file in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(archive, Path.GetRelativePath(workspace, file)));
            }

            // All clients and the killed Host are closed. Restore to the same path
            // because dispatch binds absolute CMake and result paths.
            Directory.Move(workspace, original);
            try
            {
                Directory.Move(archive, workspace);
            }
            catch
            {
                Directory.Move(original, workspace);
                throw;
            }
            Directory.Delete(original, recursive: true);
        }
        finally
        {
            if (Directory.Exists(archive))
            {
                Directory.Delete(archive, recursive: true);
            }
        }
    }

    private static async Task VerifyIndexedArtifactsAsync(string cli, string endpoint, string workspace, string operation,
        CancellationToken ct)
    {
        string stash = Path.Combine(workspace, "indexed-stash");
        await RunCommandAsync(cli, ["cas", stash, "init-indexed"], ct);
        string[] import = ["remote", "stash", "--transport", "local", "--endpoint", endpoint,
            "--workspace", workspace, "--operation", operation, "--store", stash];
        await Task.WhenAll(RunCommandAsync(cli, import, ct), RunCommandAsync(cli, import, ct));
        ArtifactStoreFiles files = new(stash);
        CasReferenceIndex.ReceiptReference before;
        using (FileStream lease = await files.OpenLeaseAsync(false, ct))
        {
            using CasReferenceIndex index = new(files);
            before = index.FindReceipt(operation) ?? throw new InvalidDataException("Native artifact manifest is absent.");
            Require(before.Pinned && File.Exists(files.Blob(before.Blob)) &&
                !Directory.EnumerateFiles(files.Operations, "*.json").Any(),
                "Artifact stash wrote legacy metadata or did not retain its manifest.");
        }

        string destination = Path.Combine(workspace, "downloaded");
        await RunCommandAsync(cli, ["cas", stash, "gc", "--apply"], ct);
        await RunCommandAsync(cli, ["cas", stash, "fetch", operation, "output:b:0", destination], ct);
        Require(await File.ReadAllTextAsync(destination, ct) == "recovered", "Fetched native artifact bytes differ.");
        await RunCommandAsync(cli, ["cas", stash, "unpin", operation], ct);
        await RunCommandAsync(cli, ["cas", stash, "gc", "--apply"], ct);
        await RunCommandRejectedAsync(cli, ["cas", stash, "pin", operation], ct);
        using (FileStream lease = await files.OpenLeaseAsync(false, ct))
        {
            using CasReferenceIndex index = new(files);
            Require(index.FindReceipt(operation) == before with { Pinned = false } && File.Exists(files.Blob(before.Blob)),
                "Unpin changed the immutable manifest or removed its identity.");
        }

        string blob = files.Blob(before.Blob);
        await File.WriteAllTextAsync(blob, "corrupt", ct);
        await RunCommandRejectedAsync(cli, ["cas", stash, "gc", "--apply"], ct);
    }

    private static async Task VerifyPreviousWorkspaceAsync(string previous, string current, string workspace, int epoch,
        CancellationToken ct)
    {
        string source = Directory.CreateDirectory(Path.Combine(workspace, "src")).FullName;
        string build = Path.Combine(workspace, "b");
        await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"), """
            cmake_minimum_required(VERSION 3.26)
            project(inline_workspace NONE)
            """, ct);
        await RunCommandAsync("cmake", ["-S", source, "-B", build, "-G", "Ninja"], ct);
        WorkflowDocument workflow = new()
        {
            SchemaVersion = 2,
            Contexts = { ["tree"] = new() { BuildDirectory = "b", Mode = "observe", ProvenancePolicy = "allowUnverified" } },
            Nodes = [new() { Id = "observe", Kind = "inspect", Context = "tree" }]
        };
        string framePath = Path.Combine(workspace, "manifest.capnp");
        byte[] frame = CapnpWire.Frame(CapnpWire.Canonicalize(ProductionWorkflowCodec.Encode(workflow)));
        await File.WriteAllBytesAsync(framePath, frame, ct);
        await RunCommandAsync(previous, ["workflow", "init-binary", workspace], ct);
        await RunCommandAsync(previous, ["workflow", "run-binary", workspace, framePath,
            Convert.ToHexString(SHA256.HashData(frame)), "inline-inquiry", Path.Combine(workspace, "run")], ct);
        using DurableAcceptanceStore ledger = new(workspace, readOnly: true);
        AcceptedWorkflow accepted = ledger.Find("inline-inquiry")
            ?? throw new InvalidDataException("Prior Host did not produce an inline authorized operation.");
        Require(ledger.NativeFormatEpoch == epoch && accepted.State == "succeeded",
            "Reading an earlier workspace changed its operation or ledger epoch.");
        await RunCommandAsync(current, ["workflow", "binary-info", workspace, accepted.OperationId], ct);
        using Peer observer = await Peer.StartAsync(current, workspace, ct);
        using JsonRpc connection = HostConnection.Create(await LocalTransport.ConnectAsync(observer.Endpoint, ct));
        IHostRpc rpc = await ConnectAsync(connection, workspace, ct);
        OperationEventsPage events = await rpc.OperationEventsAsync(new(accepted.OperationId, 0, 10), ct);
        Require(events.Events.Any(item => item.Type == "workflow_finished"),
            "Current Host could not replay an earlier event stream.");
    }

    internal static async Task VerifyAllKindsAsync(string cli)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("This integration fixture uses Linux perf and clang-format.");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        CancellationToken ct = timeout.Token;
        DirectoryInfo temp = Directory.CreateTempSubdirectory("cmwf-kinds-");
        try
        {
            Dictionary<string, string> legacyOutcomes = await ExecuteAllKindsAsync(cli,
                CreateWorkspace(temp.FullName, "legacy"), binary: false, profileHistoryFaultCli: null, ct);
            Dictionary<string, string> binaryOutcomes = await ExecuteAllKindsAsync(cli,
                CreateWorkspace(temp.FullName, "binary"), binary: true, profileHistoryFaultCli: null, ct);
            Require(legacyOutcomes.OrderBy(pair => pair.Key).SequenceEqual(binaryOutcomes.OrderBy(pair => pair.Key)),
                "Execution outcomes differ between workflow codecs.");
            Console.WriteLine("All nine operation kinds passed in legacy and native execution, including perf and clang-format.");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    internal static async Task VerifyNativeProfileHistoryAsync(string cli, string faultCli)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("This fixture uses Linux perf.");
        }

        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(3));
        DirectoryInfo temp = Directory.CreateTempSubdirectory("cmwf-native-profile-");
        try
        {
            _ = await ExecuteAllKindsAsync(cli, temp.FullName, binary: true, faultCli, deadline.Token);
            Console.WriteLine("Native profile History recovered with missing and corrupted JSON, verified retained metrics and rejected a corrupt report.");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task<Dictionary<string, string>> ExecuteAllKindsAsync(string cli, string root, bool binary,
        string? profileHistoryFaultCli, CancellationToken ct)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "s")).FullName;
        string snapshot = Path.Combine(root, "snapshot");
        File.Copy("/usr/bin/true", snapshot);
        File.SetUnixFileMode(snapshot, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string snapshotSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(snapshot, ct)));
        await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"), """
            cmake_minimum_required(VERSION 3.26)
            project(operation_coverage LANGUAGES CXX)
            enable_testing()
            add_executable(probe main.cpp)
            add_custom_target(check_graph COMMAND "${CMAKE_COMMAND}" -E echo dag)
            add_test(NAME smoke COMMAND probe)
            """, ct);
        string cpp = Path.Combine(source, "main.cpp");
        await File.WriteAllTextAsync(cpp, "int main() { return 0; }\n", ct);
        await RunCommandAsync("clang-format", ["-i", cpp], ct);

        WorkflowDocument document = new()
        {
            SchemaVersion = 2,
            Contexts = { ["managed"] = new()
            {
                SourceDirectory = "s", BuildDirectory = "build", Mode = "managed", Generator = "Ninja"
            } },
            Nodes =
            [
                new() { Id = "configure", Kind = "configure", Context = "managed" },
                new() { Id = "inspect", Kind = "inspect", Context = "managed", DependsOn = ["configure"] },
                new() { Id = "format", Kind = "format_check", Context = "managed", Arguments = ["cpp"] },
                new() { Id = "build", Kind = "build", Context = "managed", Target = "probe", DependsOn = ["inspect", "format"] },
                new() { Id = "test", Kind = "ctest", Context = "managed", TestRegex = "smoke", DependsOn = ["build"] },
                new() { Id = "invoke", Kind = "invoke", Context = "managed", Target = "check_graph", DependsOn = ["build"] },
                new() { Id = "run", Kind = "run", From = "build", DependsOn = ["test", "invoke"] },
                new()
                {
                    Id = "profile", Kind = "profile", From = "build", DependsOn = ["run"],
                    Options = { ["backend"] = "perf", ["mode"] = "counters" }
                },
                new()
                {
                    Id = "snapshot", Kind = "profile_snapshot", SnapshotExecutable = snapshot,
                    SnapshotSha256 = snapshotSha, WorkingDirectory = root, DependsOn = ["profile"],
                    Options = { ["backend"] = "perf", ["mode"] = "counters" }
                }
            ]
        };
        if (binary)
        {
            BinaryWorkflowSubmission.InitializeWorkspace(root);
        }
        byte[] bytes = binary ? CapnpWire.Frame(CapnpWire.Canonicalize(ProductionWorkflowCodec.Encode(document)))
            : JsonSerializer.SerializeToUtf8Bytes(document, WorkflowDefinitionJson.Default.WorkflowDocument);
        string manifest = Path.Combine(root, binary ? "workflow.capnp" : "workflow.json");
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        string run = Path.Combine(root, "run");
        await File.WriteAllBytesAsync(manifest, bytes, ct);
        string? marker = profileHistoryFaultCli is null ? null : Path.Combine(root, "history-import");
        string historyDatabase = Path.Combine(root, "history.sqlite");
        using Peer host = await Peer.StartAsync(profileHistoryFaultCli ?? cli, root, ct,
            marker, "before-native-history-import", node: null);
        using JsonRpc connection = HostConnection.Create(await LocalTransport.ConnectAsync(host.Endpoint, ct));
        IHostRpc rpc = await ConnectAsync(connection, root, ct);
        string[] selection = ["snapshot"];
        OperationReference operation = binary
            ? await rpc.ExecutionStartAsync(new(BinaryWorkflowSubmission.Domain, "kinds", manifest, sha, 2, run)
            {
                Only = selection,
                HistoryPolicy = marker is null ? null : new(HistoryPolicyMode.ExplicitDatabase, historyDatabase)
            }, ct)
            : await rpc.WorkflowStartAsync(new("kinds", manifest, sha, 2, run) { Only = selection }, ct);
        await WaitForAsync(async () => (await rpc.OperationGetAsync(new(operation.OperationId), ct)).State
            is "succeeded" or "failed" or "canceled", ct);
        WorkflowReportSnapshot report = await rpc.OperationReportAsync(new(operation.OperationId), ct);
        Require(report.Report.ManifestHash == sha && report.Report.Nodes.Count == 9 &&
            report.Report.Nodes.All(node => node.Status == "succeeded"),
            "Operation coverage workflow failed: " + string.Join(", ", report.Report.Nodes.Select(node =>
                $"{node.Id}={node.Status} {node.Error}")));
        Require(File.Exists(Path.Combine(run, "traces", "raw", "profile", "run.perf-stat.csv")) &&
            File.Exists(Path.Combine(run, "traces", "raw", "snapshot", "run.perf-stat.csv")),
            "The production perf backend did not publish both captures.");
        if (binary)
        {
            await VerifyEmptySnapshotAsync(root, operation.OperationId, ct);
            Require(report.Report.Nodes.Where(node => node.Kind is "profile" or "profile_snapshot")
                .All(node => node.Profile is { Backend: "perf", Mode: "counters", RawTraceSha256.Length: 64 }),
                "Native execution lost structured profiler capture evidence.");
        }

        if (marker is not null)
        {
            await WaitForAsync(() => Task.FromResult(File.Exists(marker)), ct);
            OperationArtifactList artifacts = await rpc.OperationArtifactListAsync(new(operation.OperationId), ct);
            await host.KillAsync(ct);
            foreach (string path in Directory.GetFiles(run, "*.results.json"))
            {
                File.Delete(path);
            }
            await File.WriteAllTextAsync(Path.Combine(run, "analysis.json"), "invalid analysis", ct);
            await File.WriteAllTextAsync(Path.Combine(run, "counter-comparison.json"), "{\"schemaVersion\":100}", ct);

            string blob = Path.Combine(WorkflowLedgerPath.Documents(root), "blobs", artifacts.NativeReportBlobId!);
            byte[] original = await File.ReadAllBytesAsync(blob, ct);
            if (Environment.GetEnvironmentVariable("CMWF_TEST_CAPNP") is { } capnp &&
                Environment.GetEnvironmentVariable("CMWF_TEST_PROFILE_SCHEMA") is { } schema)
            {
                await VerifyOfficialFrameAsync(capnp, schema, blob, ct);
            }
            await File.WriteAllBytesAsync(blob, [1, 2, 3], ct);
            using Peer restarted = await Peer.StartAsync(cli, root, ct);
            using JsonRpc remote = HostConnection.Create(await LocalTransport.ConnectAsync(restarted.Endpoint, ct));
            IHostRpc reader = await ConnectAsync(remote, root, ct);
            await WaitForAsync(async () =>
            {
                HistoryIndexState state = (await reader.HistoryRetryIndexAsync(new(operation.OperationId), ct)).State;
                Require(state != HistoryIndexState.Indexed, "History indexed corrupt native capture evidence.");
                return state == HistoryIndexState.Failed;
            }, ct);
            await File.WriteAllBytesAsync(blob, original, ct);
            await WaitForAsync(async () => (await reader.HistoryRetryIndexAsync(new(operation.OperationId), ct)).State ==
                HistoryIndexState.Indexed, ct);
            using SqliteConnection history = new(new SqliteConnectionStringBuilder
            {
                DataSource = historyDatabase,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            await history.OpenAsync(ct);
            using SqliteCommand query = history.CreateCommand();
            query.CommandText = """
                SELECT count(DISTINCT node_id) FROM measurements WHERE source='execution-report.capnp' AND method='perf stat';
                """;
            Require((long)(await query.ExecuteScalarAsync(ct))! == 2,
                "History did not store both typed perf counter captures.");
        }

        return report.Report.Nodes.ToDictionary(node => node.Kind, node => node.Status, StringComparer.Ordinal);
    }

    internal static async Task MeasureAsync(string current, string archived)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        CancellationToken ct = timeout.Token;
        DirectoryInfo temp = Directory.CreateTempSubdirectory("cmwf-binary-measure-");
        try
        {
            await MeasureCaseAsync("archive-enabled legacy", archived, CreateWorkspace(temp.FullName, "old"), binary: false, ct);
            await MeasureCaseAsync("current legacy", current, CreateWorkspace(temp.FullName, "legacy"), binary: false, ct);
            await MeasureCaseAsync("current binary", current, CreateWorkspace(temp.FullName, "binary"), binary: true, ct);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static string CreateWorkspace(string parent, string name)
    {
        string workspace = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(workspace, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return workspace;
    }

    private static async Task MeasureCaseAsync(string name, string cli, string workspace, bool binary, CancellationToken ct)
    {
        string build = Directory.CreateDirectory(Path.Combine(workspace, "build")).FullName;
        string source = Directory.CreateDirectory(Path.Combine(workspace, "src")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"), """
            cmake_minimum_required(VERSION 3.26)
            project(probe NONE)
            """, ct);
        await RunCommandAsync("cmake", ["-S", source, "-B", build, "-G", "Ninja"], ct);
        WorkflowDocument doc = new()
        {
            SchemaVersion = 2,
            Contexts = { ["tree"] = new() { BuildDirectory = build, Mode = "observe", ProvenancePolicy = "allowUnverified" } },
            Nodes = [new() { Id = "inspect", Kind = "inspect", Context = "tree" }]
        };
        if (binary)
        {
            BinaryWorkflowSubmission.InitializeWorkspace(workspace);
        }

        byte[] bytes = binary ? CapnpWire.Frame(CapnpWire.Canonicalize(ProductionWorkflowCodec.Encode(doc)))
            : JsonSerializer.SerializeToUtf8Bytes(doc, WorkflowDefinitionJson.Default.WorkflowDocument);
        string input = Path.Combine(workspace, binary ? "measure.capnp" : "measure.json");
        List<double> encodeTimes = [], decodeTimes = [];
        List<long> codecAllocations = [];
        for (int i = 0; i < 21; i++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            byte[] encoded = binary ? CapnpWire.Frame(CapnpWire.Canonicalize(ProductionWorkflowCodec.Encode(doc)))
                : JsonSerializer.SerializeToUtf8Bytes(doc, WorkflowDefinitionJson.Default.WorkflowDocument);
            double encodedUs = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            start = Stopwatch.GetTimestamp();
            _ = binary ? ProductionWorkflowCodec.Decode(CapnpWire.Unframe(encoded))
                : JsonSerializer.Deserialize(encoded, WorkflowDefinitionJson.Default.WorkflowDocument);
            if (i >= 2)
            {
                encodeTimes.Add(encodedUs);
                decodeTimes.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
                codecAllocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
            }
        }

        await File.WriteAllBytesAsync(input, bytes, ct);
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        using Peer host = await Peer.StartAsync(cli, workspace, ct);
        using JsonRpc connection = HostConnection.Create(await LocalTransport.ConnectAsync(host.Endpoint, ct));
        IHostRpc rpc = await ConnectAsync(connection, workspace, ct);
        List<double> admission = [], total = [], indexed = [];
        HistoryPolicy? history = name.StartsWith("current ", StringComparison.Ordinal)
            ? new(HistoryPolicyMode.ExplicitDatabase, Path.Combine(workspace, "measure-history.sqlite"))
            : null;
        for (int i = 0; i < 9; i++)
        {
            string requestId = "measure-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string run = Path.Combine(workspace, requestId);
            long start = Stopwatch.GetTimestamp();
            OperationReference operation = binary
                ? await rpc.ExecutionStartAsync(new(BinaryWorkflowSubmission.Domain, requestId, input, sha, 1, run)
                { HistoryPolicy = history }, ct)
                : await rpc.WorkflowStartAsync(new(requestId, input, sha, 1, run)
                { HistoryPolicy = history }, ct);
            double accepted = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            await WaitForAsync(async () => (await rpc.OperationGetAsync(new(operation.OperationId), ct)).State == "succeeded", ct);
            double observedTerminal = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (history is not null)
            {
                await WaitForAsync(async () => (await rpc.HistoryIndexStatusAsync(new(operation.OperationId), ct)).State ==
                    HistoryIndexState.Indexed, ct);
            }

            if (i >= 2)
            {
                admission.Add(accepted);
                total.Add(observedTerminal);
                if (history is not null)
                {
                    indexed.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
            }
        }

        long persisted = Directory.EnumerateFiles(Path.Combine(workspace, ".cmakeworkflow"), "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        Console.WriteLine(FormattableString.Invariant(
            $"{name} manifest_bytes={bytes.Length} ledger_and_cas_bytes={persisted} admission_ms={admission.Order().ElementAt(3):F3} terminal_ms={total.Order().ElementAt(3):F3} samples=7"));
        Console.WriteLine(FormattableString.Invariant(
            $"{name} managed_encode_us={encodeTimes.Order().ElementAt(9):F3} managed_decode_us={decodeTimes.Order().ElementAt(9):F3} codec_allocated_bytes={codecAllocations.Order().ElementAt(9)} host_peak_rss_bytes={host.PeakWorkingSetBytes} codec_samples=19"));
        if (indexed.Count > 0)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"{name} indexed_from_submit_ms={indexed.Order().ElementAt(3):F3} history_bytes={new FileInfo(history!.DatabasePath!).Length}"));
        }
    }

    private static async Task VerifyEmptySnapshotAsync(string root, string operation, CancellationToken ct)
    {
        await using SqliteConnection sql = OpenLedger(root);
        await sql.OpenAsync(ct);
        using SqliteCommand query = sql.CreateCommand();
        query.CommandText = "SELECT length(snapshot) FROM accepted_workflows WHERE operation_id=$operation;";
        _ = query.Parameters.AddWithValue("$operation", operation);
        Require(await query.ExecuteScalarAsync(ct) is 0L, "A native operation persisted legacy snapshot bytes.");
    }

    private static async Task<StoredPlanAuthorization> ReadAuthorizationAsync(string root, string operation, CancellationToken ct)
    {
        await using SqliteConnection sql = OpenLedger(root);
        await sql.OpenAsync(ct);
        using SqliteCommand query = sql.CreateCommand();
        query.CommandText = "SELECT plan_document_id,identity FROM workflow_binary_authorizations WHERE operation_id=$operation;";
        _ = query.Parameters.AddWithValue("$operation", operation);
        using SqliteDataReader row = await query.ExecuteReaderAsync(ct);
        return await row.ReadAsync(ct) ? new(new(row.GetString(0)), row.GetString(1))
            : throw new InvalidOperationException("Missing authorization binding.");
    }

    private static async Task SetBindingIdentityAsync(string root, string operation, string identity, CancellationToken ct)
    {
        await using SqliteConnection sql = OpenLedger(root);
        await sql.OpenAsync(ct);
        using SqliteCommand update = sql.CreateCommand();
        update.CommandText = "UPDATE workflow_binary_authorizations SET identity=$identity WHERE operation_id=$operation;";
        _ = update.Parameters.AddWithValue("$operation", operation);
        _ = update.Parameters.AddWithValue("$identity", identity);
        Require(await update.ExecuteNonQueryAsync(ct) == 1, "Authorization fixture did not change.");
    }

    private static SqliteConnection OpenLedger(string root) => new(new SqliteConnectionStringBuilder
    {
        DataSource = WorkflowLedgerPath.Resolve(root),
        Pooling = false
    }.ToString());

    private static async Task ComparePlansAsync(WorkflowDocument doc, byte[] frame, string path, string sha, CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(doc, WorkflowDefinitionJson.Default.WorkflowDocument);
        WorkflowPlan legacy = await WorkflowPlanner.LoadSnapshotAsync(path, json, [], null, ct);
        WorkflowDocument decoded = ProductionWorkflowCodec.Decode(CapnpWire.Unframe(frame));
        WorkflowPlan binary = await WorkflowPlanner.LoadDocumentAsync(path, decoded, sha, [], null, ct);
        Require(legacy.ManifestHash != binary.ManifestHash && legacy.Nodes.Count == binary.Nodes.Count,
            "Differential planner did not preserve format separation.");
        for (int i = 0; i < legacy.Nodes.Count; i++)
        {
            PlannedNode left = legacy.Nodes[i];
            PlannedNode right = binary.Nodes[i];
            Require(left.Target == right.Target && left.Requested == right.Requested &&
                left.Definition.Kind == right.Definition.Kind && left.Dependencies.SequenceEqual(right.Dependencies),
                "Binary planning changed a resolved node.");
        }
    }

    private static void AssertBinaryOnly(string workspace, string operation)
    {
        using DurableAcceptanceStore ledger = new(workspace, readOnly: true);
        Require(ledger.ReadBinaryInput(operation) is { Document.Legacy: null }, "The accepted input is not native binary.");
        try
        {
            _ = ledger.GetSnapshot(operation);
            throw new UnreachableException("Binary operation exposed a legacy snapshot.");
        }
        catch (InvalidOperationException error) when (error.Message == "Native binary workflows have no legacy snapshot.")
        {
        }
    }

    private static async Task CheckOldHostAsync(string cli, string parent, CancellationToken ct)
    {
        string workspace = Directory.CreateDirectory(Path.Combine(parent, "version-fence")).FullName;
        BinaryWorkflowSubmission.InitializeWorkspace(workspace);
        string manifest = Path.Combine(workspace, "unrelated-legacy.json");
        byte[] bytes = """
            {"schemaVersion":1,"contexts":{"tree":{"buildDirectory":"build","mode":"observe","provenancePolicy":"allowUnverified"}},
             "nodes":[{"id":"inspect","kind":"inspect","context":"tree"}]}
            """u8.ToArray();
        await File.WriteAllBytesAsync(manifest, bytes, ct);
        using Peer old = await Peer.StartAsync(cli, workspace, ct);
        using JsonRpc connection = HostConnection.Create(await LocalTransport.ConnectAsync(old.Endpoint, ct));
        IHostRpc rpc = await ConnectAsync(connection, workspace, ct);
        await RejectAsync(() => rpc.WorkflowStartAsync(new("old-host-cannot-write", manifest,
            Convert.ToHexString(SHA256.HashData(bytes)), 1, workspace), ct));
        using DurableAcceptanceStore ledger = new(workspace, readOnly: true);
        Require(ledger.Find("old-host-cannot-write") is null, "Old Host modified a fenced ledger.");
    }

    private static async Task RejectAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (RemoteInvocationException)
        {
            return;
        }

        throw new UnreachableException("An incompatible or conflicting RPC was accepted.");
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        while (!await condition())
        {
            await Task.Delay(30, ct);
        }
    }

    private static Task RunCommandAsync(string executable, string[] args, CancellationToken ct) =>
        RunCommandExpectAsync(executable, args, expectSuccess: true, ct);

    private static Task RunCommandRejectedAsync(string executable, string[] args, CancellationToken ct) =>
        RunCommandExpectAsync(executable, args, expectSuccess: false, ct);

    private static async Task RunCommandExpectAsync(string executable, string[] args, bool expectSuccess, CancellationToken ct)
    {
        using Process child = Process.Start(StartInfo(executable, args))
            ?? throw new InvalidOperationException("Command could not start.");
        Task<string> stderr = child.StandardError.ReadToEndAsync(ct);
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(ct);
        await child.WaitForExitAsync(ct);
        if (child.ExitCode == 0 != expectSuccess)
        {
            throw new InvalidOperationException(await stderr + await stdout);
        }
    }

    private static ProcessStartInfo StartInfo(string executable, string[] args)
    {
        ProcessStartInfo start = new(executable) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return start;
    }

    private static void Require(bool predicate, string message)
    {
        if (!predicate)
        {
            throw new UnreachableException(message);
        }
    }

    private static async Task<IHostRpc> ConnectAsync(JsonRpc connection, string workspace, CancellationToken ct)
    {
        IHostRpc rpc = connection.Attach<IHostRpc>();
        connection.StartListening();
        _ = await rpc.InitializeAsync(new(1, workspace), ct);
        return rpc;
    }

    private sealed class Peer(Process process, string endpoint) : IDisposable
    {
        internal string Endpoint { get; } = endpoint;

        internal long PeakWorkingSetBytes
        {
            get
            {
                process.Refresh();
                return process.PeakWorkingSet64;
            }
        }

        internal static async Task<Peer> StartAsync(string cli, string workspace, CancellationToken ct,
            string? marker = null, string fault = "after-checkpoint", string? node = "b")
        {
            string name = "h" + Guid.NewGuid().ToString("N")[..8];
            string endpoint = OperatingSystem.IsWindows()
                ? @"\\.\pipe\cmakeworkflow-" + name
                : Path.Combine(Path.GetFileName(workspace) == "version-fence" ? Path.GetDirectoryName(workspace)! : workspace, name);
            ProcessStartInfo start = StartInfo(Path.GetFullPath(cli), ["host", "serve", "--endpoint", endpoint]);
            if (marker is not null)
            {
                start.Environment["CMAKEWORKFLOW_TEST_FAULT"] = fault;
                start.Environment["CMAKEWORKFLOW_TEST_NODE"] = node;
                start.Environment["CMAKEWORKFLOW_TEST_MARKER"] = marker;
            }

            Process process = Process.Start(start) ?? throw new InvalidOperationException("Host could not start.");
            try
            {
                string? ready = await process.StandardError.ReadLineAsync(ct);
                Require(ready?.StartsWith("[Host] Listening: ", StringComparison.Ordinal) == true,
                    "Host did not listen: " + ready);
                // Keep both pipes drained throughout execution.
                _ = process.StandardError.ReadToEndAsync(ct);
                _ = process.StandardOutput.ReadToEndAsync(ct);
                return new Peer(process, endpoint);
            }
            catch
            {
                Stop(process);
                process.Dispose();
                throw;
            }
        }

        internal async Task KillAsync(CancellationToken ct)
        {
            Stop(process);
            await process.WaitForExitAsync(ct);
        }

        public void Dispose()
        {
            Stop(process);
            process.Dispose();
        }

        private static void Stop(Process process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }

    }
}
