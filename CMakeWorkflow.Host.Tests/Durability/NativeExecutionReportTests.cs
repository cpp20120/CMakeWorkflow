using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core.Persistence;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Transport;
using CMakeWorkflow.Host.Workflows;
using StreamJsonRpc;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Host.Tests.Durability;

internal static partial class BinaryWorkflowWireTests
{
    internal static async Task VerifyNativeReportsAsync(string cli, string faultCli, string capnp, string schema)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        CancellationToken ct = timeout.Token;
        DirectoryInfo fixture = Directory.CreateTempSubdirectory("cmwf-report-");
        try
        {
            string root = fixture.FullName;
            VerifyReportWireFields(root);
            string src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
            string build = Path.Combine(root, "build");
            string effect = Path.Combine(root, "effects.txt");
            await File.WriteAllTextAsync(Path.Combine(src, "CMakeLists.txt"), """
                cmake_minimum_required(VERSION 3.26)
                project(terminal_evidence NONE)
                add_custom_target(pass COMMAND "${CMAKE_COMMAND}" -P "${CMAKE_CURRENT_SOURCE_DIR}/effect.cmake")
                add_custom_target(fail COMMAND "${CMAKE_COMMAND}" -E false)
                add_custom_target(wait COMMAND "${CMAKE_COMMAND}" -E sleep 30)
                """, ct);
            await File.WriteAllTextAsync(Path.Combine(src, "effect.cmake"), """
                file(APPEND "${CMAKE_CURRENT_LIST_DIR}/../effects.txt" "ran\n")
                """, ct);
            string query = Directory.CreateDirectory(Path.Combine(build, ".cmake", "api", "v1", "query")).FullName;
            await File.WriteAllTextAsync(Path.Combine(query, "codemodel-v2"), "", ct);
            await RunCommandAsync("cmake", ["-S", src, "-B", build, "-G", "Ninja"], ct);
            BinaryWorkflowSubmission.InitializeWorkspace(root);
            ExecutionStartRequest success = await RequestAsync("success", "pass");
            ExecutionStartRequest failure = await RequestAsync("failure", "fail");
            ExecutionStartRequest cancellation = await RequestAsync("cancellation", "wait");
            string marker = Path.Combine(root, "faulted");
            string operation;
            using (Peer stopped = await Peer.StartAsync(faultCli, root, ct, marker, "before-terminal-commit", node: null))
            using (JsonRpc connection = HostConnection.Create(await LocalTransport.ConnectAsync(stopped.Endpoint, ct)))
            {
                IHostRpc rpc = await ConnectAsync(connection, root, ct);
                operation = (await rpc.ExecutionStartAsync(success, ct)).OperationId;
                await WaitForAsync(() => Task.FromResult(File.Exists(marker)), ct);
                Require((await rpc.OperationGetAsync(new(operation), ct)).State == "dispatch_authorized",
                    "Staged terminal evidence became visible before the ledger transaction.");
                await RejectAsync(() => rpc.OperationReportAsync(new(operation), ct));
                using (DurableAcceptanceStore ledger = new(root, readOnly: true))
                {
                    BoundNativeCheckpoint checkpoint = ledger.ReadNativeCheckpoint(operation);
                    byte[] checkpointFrame = NativeExecutionReportCodec.EncodeCheckpoint(checkpoint.State);
                    Require(Convert.ToHexString(SHA256.HashData(checkpointFrame)) == checkpoint.Digest,
                        "Persisted checkpoint differs from its canonical native model.");
                    string checkpointFile = Path.Combine(root, "checkpoint-oracle.capnp");
                    await File.WriteAllBytesAsync(checkpointFile, checkpointFrame, ct);
                    await VerifyOfficialFrameAsync(capnp, schema, checkpointFile, ct, rootType: "ExecutionCheckpoint");
                    Require(!File.Exists(Path.Combine(success.ResultsDirectory, "run.json")),
                        "Native checkpoint unexpectedly required a JSON file.");
                }
                await stopped.KillAsync(ct);
            }

            File.Delete(Path.Combine(success.ResultsDirectory, "run.json"));
            File.Delete(success.Manifest);
            string cas = WorkflowLedgerPath.Documents(root);
            await RunCommandAsync(cli, ["cas", cas, "gc", "--apply"], ct);

            using Peer current = await Peer.StartAsync(cli, root, ct);
            using JsonRpc connected = HostConnection.Create(await LocalTransport.ConnectAsync(current.Endpoint, ct));
            IHostRpc client = await ConnectAsync(connected, root, ct);
            OperationReference replay = await client.ExecutionStartAsync(success, ct);
            Require(replay.OperationId == operation && replay.State == "succeeded",
                "Replaying the exact accepted request failed to commit the retained terminal report.");
            Require(await File.ReadAllTextAsync(effect, ct) == "ran\n", "Retry replayed the external effect.");
            Require((await client.ExecutionStartAsync(success, ct)).OperationId == operation,
                "Duplicate finalization changed operation identity.");
            WorkflowReportSnapshot report = await client.OperationReportAsync(new(operation), ct);
            OperationArtifactList artifacts = await client.OperationArtifactListAsync(new(operation), ct);
            Require(report.Report.Status == "succeeded" && artifacts.ReportSha256 == report.Revision &&
                artifacts.NativeReportDocumentId is not null && artifacts.NativeReportBlobId is not null,
                "Public report and artifact views lost retained native provenance.");
            OperationArtifactInfo projection = artifacts.Artifacts.Single(a => a.ArtifactId == "report");
            OperationArtifactChunk chunk = await client.OperationArtifactReadAsync(new(operation, "report", 0,
                checked((int)projection.Size), projection.Size, projection.LastWriteUtcTicks), ct);
            Require(Convert.ToHexString(SHA256.HashData(chunk.Content)) == report.Revision && !File.Exists(Path.Combine(success.ResultsDirectory, "run.json")),
                "Public JSON projection required a persisted report.");
            string blob = Path.Combine(cas, "blobs", artifacts.NativeReportBlobId!);
            await VerifyOfficialFrameAsync(capnp, schema, blob, ct);

            string stash = Path.Combine(root, "stash");
            await RunCommandAsync(cli, ["cas", stash, "init-indexed"], ct);
            await RunCommandAsync(cli, ["remote", "stash", "--transport", "local", "--endpoint", current.Endpoint,
                "--workspace", root, "--operation", operation, "--store", stash], ct);
            ArtifactStoreFiles artifactFiles = new(stash);
            using (FileStream lease = await artifactFiles.OpenLeaseAsync(false, ct))
            {
                using CasReferenceIndex index = new(artifactFiles);
                CasReferenceIndex.ReceiptReference indexed = index.FindReceipt(operation)
                    ?? throw new InvalidDataException("Indexed stash did not publish a manifest.");
                string artifactSchema = Path.Combine(Path.GetDirectoryName(schema)!, "artifactEvidence.capnp");
                await VerifyOfficialFrameAsync(capnp, artifactSchema, artifactFiles.Blob(indexed.Blob), ct,
                    rootType: "ArtifactManifest");
            }
            byte[] retainedFrame = await File.ReadAllBytesAsync(artifactFiles.Blob(artifacts.NativeReportBlobId!), ct);
            Require(Convert.ToHexStringLower(SHA256.HashData(retainedFrame)) == artifacts.NativeReportBlobId,
                "Indexed stash did not retain exact native report bytes.");

            string retained = blob + ".saved";
            File.Move(blob, retained);
            try
            {
                await RejectAsync(() => client.OperationReportAsync(new(operation), ct));
                await File.WriteAllTextAsync(blob, "corrupt", ct);
                await RejectAsync(() => client.OperationArtifactListAsync(new(operation), ct));
            }
            finally
            {
                File.Move(retained, blob, overwrite: true);
            }

            string failedId = (await client.ExecutionStartAsync(failure, ct)).OperationId;
            await WaitForAsync(async () => (await client.OperationGetAsync(new(failedId), ct)).State == "failed", ct);
            WorkflowReportSnapshot failed = await client.OperationReportAsync(new(failedId), ct);
            Require(failed.Report.Status == "failed" && failed.Report.Nodes.Single().ExitCode != 0,
                "Failed operation lost native exit evidence.");

            string canceledId = (await client.ExecutionStartAsync(cancellation, ct)).OperationId;
            await WaitForAsync(async () => (await client.OperationEventsAsync(new(canceledId), ct)).Events
                .Any(item => item.Type == "node_started"), ct);
            _ = await client.OperationCancelAsync(new(canceledId, 0), ct);
            await WaitForAsync(async () => (await client.OperationGetAsync(new(canceledId), ct)).State == "canceled", ct);
            OperationArtifactList canceledCatalog = await client.OperationArtifactListAsync(new(canceledId), ct);
            string canceledBlob = Path.Combine(cas, "blobs", canceledCatalog.NativeReportBlobId!);
            NativeExecutionReport canceled = NativeExecutionReportCodec.Decode(await File.ReadAllBytesAsync(canceledBlob, ct));
            Require(canceled.ExitCode == 130 && canceled.OperatorCancellationRequestedAt is not null,
                "Canceled operation lost native cancellation provenance.");

            ExecutionStartRequest beforePublication = await RequestAsync("before-publication", "pass");
            string unpublishedMarker = Path.Combine(root, "report-unpublished");
            string unpublished;
            using (Peer stopped = await Peer.StartAsync(faultCli, root, ct, unpublishedMarker, "before-report-publication", node: null))
            using (JsonRpc connection = HostConnection.Create(await LocalTransport.ConnectAsync(stopped.Endpoint, ct)))
            {
                IHostRpc rpc = await ConnectAsync(connection, root, ct);
                unpublished = (await rpc.ExecutionStartAsync(beforePublication, ct)).OperationId;
                await WaitForAsync(() => Task.FromResult(File.Exists(unpublishedMarker)), ct);
                await stopped.KillAsync(ct);
            }

            OperationReference pending = await client.ExecutionStartAsync(beforePublication, ct);
            Require(pending.OperationId == unpublished && pending.State == "dispatch_authorized" &&
                await File.ReadAllTextAsync(effect, ct) == "ran\nran\n",
                "An interrupted report publication completed falsely or repeated the executed workload.");
            await RejectAsync(() => client.OperationReportAsync(new(unpublished), ct));

            if (Environment.GetEnvironmentVariable("CMWF_TEST_NATIVE_HISTORY") == "1")
            {
                ExecutionStartRequest historyRequest = await RequestAsync("native-history", "pass");
                await VerifyNativeHistoryAsync(root, cas, historyRequest, faultCli, client, ct);
            }

            async Task<ExecutionStartRequest> RequestAsync(string key, string target)
            {
                WorkflowDocument workflow = new()
                {
                    SchemaVersion = 2,
                    Contexts = { ["tree"] = new() { BuildDirectory = build, Mode = "adopt" } },
                    Nodes = [new() { Id = "invoke", Kind = "invoke", Context = "tree", Target = target }]
                };
                byte[] frame = CapnpWire.Frame(CapnpWire.Canonicalize(ProductionWorkflowCodec.Encode(workflow)));
                string manifest = Path.Combine(root, key + ".capnp");
                await File.WriteAllBytesAsync(manifest, frame, ct);
                return new(BinaryWorkflowSubmission.Domain, key, manifest, Convert.ToHexString(SHA256.HashData(frame)),
                    1, Path.Combine(root, key))
                {
                    HistoryPolicy = new(HistoryPolicyMode.Disabled)
                };
            }
        }
        finally
        {
            fixture.Delete(recursive: true);
        }

        Console.WriteLine("Native terminal report publication, kill/retry, artifact and JSON projection, corruption refusal and official canonicalization passed.");
    }

    private static async Task VerifyNativeHistoryAsync(string root, string cas, ExecutionStartRequest nativeRequest,
        string faultCli, IHostRpc current, CancellationToken ct)
    {
        HistoryPolicy history = new(HistoryPolicyMode.Default);
        nativeRequest = nativeRequest with { HistoryPolicy = history };
        byte[] frame = await File.ReadAllBytesAsync(nativeRequest.Manifest, ct);
        WorkflowDocument document = ProductionWorkflowCodec.Decode(CapnpWire.Unframe(frame));
        byte[] legacy = JsonSerializer.SerializeToUtf8Bytes(document, WorkflowDefinitionJson.Default.WorkflowDocument);
        string legacyPath = Path.Combine(root, "history.json");
        await File.WriteAllBytesAsync(legacyPath, legacy, ct);
        string legacyId = (await current.WorkflowStartAsync(new("legacy-history", legacyPath,
            Convert.ToHexString(SHA256.HashData(legacy)), 1, Path.Combine(root, "legacy-history"))
        {
            HistoryPolicy = history
        }, ct)).OperationId;
        await WaitForAsync(async () => (await current.HistoryIndexStatusAsync(new(legacyId), ct)).State ==
            HistoryIndexState.Indexed, ct);

        string marker = Path.Combine(root, "history-committed");
        string operation;
        string raw;
        using (Peer faulted = await Peer.StartAsync(faultCli, root, ct, marker, "after-native-history-commit", node: null))
        using (JsonRpc connection = HostConnection.Create(await LocalTransport.ConnectAsync(faulted.Endpoint, ct)))
        {
            IHostRpc rpc = await ConnectAsync(connection, root, ct);
            operation = (await rpc.ExecutionStartAsync(nativeRequest, ct)).OperationId;
            await WaitForAsync(() => Task.FromResult(File.Exists(marker)), ct);
            Require((await rpc.HistoryIndexStatusAsync(new(operation), ct)).State == HistoryIndexState.Indexing,
                "History acknowledgement committed before the crash boundary.");
            OperationArtifactList catalog = await rpc.OperationArtifactListAsync(new(operation), ct);
            raw = Path.Combine(cas, "blobs", catalog.NativeReportBlobId!);
            await faulted.KillAsync(ct);
        }

        File.Delete(Path.Combine(nativeRequest.ResultsDirectory, "run.json"));
        string backup = raw + ".backup";
        File.Move(raw, backup);
        try
        {
            Require((await current.HistoryRetryIndexAsync(new(operation), ct)).State == HistoryIndexState.Failed &&
                (await current.OperationGetAsync(new(operation), ct)).State == "succeeded",
                "Failed History verification changed execution completion.");
        }
        finally
        {
            File.Move(backup, raw);
        }

        Require((await current.HistoryRetryIndexAsync(new(operation), ct)).State == HistoryIndexState.Indexed,
            "History did not retry the same native evidence without a JSON report.");
        string database = Path.Combine(root, ".cmakeworkflow", "history.sqlite");
        await using SqliteConnection sql = new(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await sql.OpenAsync(ct);
        await using SqliteCommand query = sql.CreateCommand();
        query.CommandText = """
            SELECT count(*) FROM runs AS run JOIN workflow_native_sources AS source ON source.run_id=run.id
            WHERE source.operation_id=$operation AND source.blob_id=$blob;
            """;
        _ = query.Parameters.AddWithValue("$operation", operation);
        _ = query.Parameters.AddWithValue("$blob", Path.GetFileName(raw));
        Require((long?)await query.ExecuteScalarAsync(ct) == 1, "History lost or duplicated the typed evidence link.");
        query.CommandText = "SELECT count(*) FROM runs;";
        Require((long?)await query.ExecuteScalarAsync(ct) == 2, "Native and legacy History runs did not coexist.");
        Console.WriteLine("Native History commit/ack interruption, missing evidence refusal, retry and mixed-format rows passed.");
    }

    private static void VerifyReportWireFields(string directory)
    {
        string digest = new('1', 64);
        DateTimeOffset time = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(3.5));
        WorkflowReport report = new()
        {
            Workflow = Path.Combine(directory, "input.capnp"),
            ManifestHash = digest,
            StartedAt = time,
            FinishedAt = time.AddSeconds(1),
            Status = "succeeded",
            Nodes =
            [
                new()
                {
                    Id = "trial", Kind = "run", Status = "succeeded", ProcessDurationSeconds = -0.0,
                    BenchmarkTrial = new("base", "sequential", [new("dataset", "--data", "a", null)]),
                    Metrics = [new("latency", "ms", "lower", "latency", 0.1)],
                    ProcessResources = new(1, "child", "sampled", 20, 1, null, 0.0),
                    ActionCacheHit = false, Error = "", Outputs = { ["second"] = "b", ["first"] = "a" }
                }
            ]
        };
        NativeExecutionReport input = new("workspace", new string('a', 32), digest, digest, digest, digest, directory, 0, report);
        byte[] encoded = NativeExecutionReportCodec.Encode(input);
        NativeExecutionReport decoded = NativeExecutionReportCodec.Decode(encoded);
        NodeResult node = decoded.Report.Nodes.Single();
        Require(decoded.Report.StartedAt.EqualsExact(time) &&
            BitConverter.DoubleToInt64Bits(node.ProcessDurationSeconds!.Value) == BitConverter.DoubleToInt64Bits(-0.0) &&
            node.DurationSeconds is null && node.ProcessResources is { PeakRssBytes: null, CpuSeconds: 0 } &&
            node is { ActionCacheHit: false, Error: "" } && node.Outputs.Keys.SequenceEqual(["second", "first"]) &&
            node.Metrics!.Single().Value == 0.1 && node.BenchmarkTrial!.Axes[0].Role is null &&
            encoded.AsSpan().SequenceEqual(NativeExecutionReportCodec.Encode(decoded)),
            "Native encoding changed ordered, nullable or numeric report evidence.");
    }

    private static async Task VerifyOfficialFrameAsync(string cli, string schema, string blob, CancellationToken ct,
        string rootType = "ExecutionReport")
    {
        byte[] bytes = await File.ReadAllBytesAsync(blob, ct);
        byte[] text = await ConvertAsync("binary:text", bytes);
        byte[] canonical = await ConvertAsync("text:canonical", text);
        Require(bytes.AsSpan(8).SequenceEqual(canonical), "Official Cap'n Proto canonical report differs.");

        async Task<byte[]> ConvertAsync(string conversion, byte[] input)
        {
            ProcessStartInfo info = StartInfo(cli, ["convert", conversion, schema, rootType]);
            info.RedirectStandardInput = true;
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("Official Cap'n Proto did not launch.");
            Task<string> error = process.StandardError.ReadToEndAsync(ct);
            using MemoryStream output = new();
            await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(output, ct), WriteAsync());
            await process.WaitForExitAsync(ct);
            Require(process.ExitCode == 0, await error);
            return output.ToArray();

            async Task WriteAsync()
            {
                await using Stream stream = process.StandardInput.BaseStream;
                await stream.WriteAsync(input, ct);
            }
        }
    }
}
