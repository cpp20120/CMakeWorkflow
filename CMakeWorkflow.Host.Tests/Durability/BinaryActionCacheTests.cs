using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Transport;
using CMakeWorkflow.Host.Workflows;
using StreamJsonRpc;

namespace CMakeWorkflow.Host.Tests.Durability;

internal static partial class BinaryWorkflowWireTests
{
    internal static async Task VerifyActionCacheAsync(string cli, string? previous = null, string? legacyProducer = null,
        string? importFault = null)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(3));
        CancellationToken ct = deadline.Token;
        DirectoryInfo temp = Directory.CreateTempSubdirectory("cmwf-cache-");
        string? prior = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_ARTIFACT_CAS");
        try
        {
            string root = temp.FullName;
            string cas = Path.Combine(root, "cas");
            if (previous is not null)
            {
                await RunCommandAsync(cli, ["cas", cas, "init-indexed"], ct);
                await RunCommandRejectedAsync(previous, ["cas", cas, "gc", "--apply"], ct);
            }
            string legacyCas = importFault is null ? cas : Path.Combine(root, "legacy-cas");
            Environment.SetEnvironmentVariable("CMAKEWORKFLOW_ARTIFACT_CAS", legacyCas);
            string source = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
            string output = Path.Combine(root, "result.txt");
            string input = Path.Combine(root, "input.txt");
            string effects = Path.Combine(root, "effects.txt");
            await File.WriteAllTextAsync(Path.Combine(source, "CMakeLists.txt"), """
                cmake_minimum_required(VERSION 3.26)
                project(action_identity LANGUAGES CXX)
                add_executable(worker worker.cpp)
                """, ct);
            await File.WriteAllTextAsync(Path.Combine(source, "worker.cpp"), """
                #include <cstdlib>
                #include <fstream>
                #include <string>
                int main(int argc, char** argv) {
                    if (argc != 5) return 2;
                    std::ofstream marker(argv[4], std::ios::app);
                    marker << "ran\n";
                    std::ifstream input(argv[1]);
                    std::string line;
                    std::getline(input, line);
                    std::ofstream output(argv[2]);
                    output << line << argv[3] << std::getenv("CMWF_FIXTURE_MODE");
                    return marker && input && output ? 0 : 3;
                }
                """, ct);
            await File.WriteAllTextAsync(input, "first", ct);
            if (legacyProducer is null)
            {
                BinaryWorkflowSubmission.InitializeWorkspace(root);
            }
            else
            {
                await RunCommandAsync(legacyProducer, ["workflow", "init-binary", root], ct);
            }
            WorkflowDocument workflow = new()
            {
                SchemaVersion = 2,
                Contexts = { ["tree"] = new()
                {
                    SourceDirectory = "src", BuildDirectory = "build", Mode = "managed", Generator = "Ninja"
                } },
                Nodes =
                [
                    new() { Id = "configure", Kind = "configure", Context = "tree" },
                    new() { Id = "worker", Kind = "build", Context = "tree", Target = "worker", DependsOn = ["configure"] },
                    new()
                    {
                        Id = "cached", Kind = "run", From = "worker", ActionCache = true,
                        Inputs = [new() { Path = "input.txt" }],
                        Outputs = [new() { Name = "result", Path = "result.txt", Fresh = true }],
                        Arguments = [input, output, "A", effects],
                        Environment = { ["CMWF_FIXTURE_MODE"] = "clean" }
                    }
                ]
            };
            if (importFault is not null)
            {
                // The actual child environment is part of the historical key. Keep it
                // identical while the two Hosts use different metadata store locations.
                workflow.Nodes[^1].Environment["CMAKEWORKFLOW_ARTIFACT_CAS"] = legacyCas;
            }

            int attempt = 0;
            NodeResult legacy;
            using (Peer producer = await Peer.StartAsync(legacyProducer ?? cli, root, ct))
            using (JsonRpc producerConnection = HostConnection.Create(await LocalTransport.ConnectAsync(producer.Endpoint, ct)))
            {
                IHostRpc producerRpc = await ConnectAsync(producerConnection, root, ct);
                legacy = await RunAsync(producerRpc, binary: false, selected: [], "succeeded");
            }
            Require(legacy.ActionCacheHit == false, "Cold legacy action unexpectedly hit the cache.");
            if (importFault is not null)
            {
                string reference = Path.Combine(legacyCas, "actions", legacy.ActionDigest + ".json");
                byte[] exactReference = await File.ReadAllBytesAsync(reference, ct);
                string[] import = ["cas", cas, "import-actions", legacyCas, "--key-policy", "1"];
                string marker = Path.Combine(root, "import-before-commit");
                ProcessStartInfo imports = StartInfo(importFault, import);
                imports.Environment["CMAKEWORKFLOW_TEST_FAULT"] = "action-import-before-commit";
                imports.Environment["CMAKEWORKFLOW_TEST_MARKER"] = marker;
                _ = imports.Environment.Remove("CMAKEWORKFLOW_TEST_NODE");
                using (Process interrupted = Process.Start(imports) ?? throw new UnreachableException("Could not start cached action import."))
                {
                    try
                    {
                        await WaitForAsync(() => Task.FromResult(interrupted.HasExited || File.Exists(marker)), ct);
                        Require(!interrupted.HasExited, "Import exited before publishing its staged blobs.");
                    }
                    finally
                    {
                        if (!interrupted.HasExited)
                        {
                            interrupted.Kill(entireProcessTree: true);
                        }
                        await interrupted.WaitForExitAsync(CancellationToken.None);
                    }
                }
                using (Microsoft.Data.Sqlite.SqliteConnection index = new(
                    "Data Source=" + Path.Combine(cas, "actions", "index.sqlite") + ";Mode=ReadOnly"))
                {
                    await index.OpenAsync(ct);
                    using Microsoft.Data.Sqlite.SqliteCommand rows = index.CreateCommand();
                    rows.CommandText = "SELECT count(*) FROM action_results;";
                    Require((long)(await rows.ExecuteScalarAsync(ct))! == 0, "Interrupted import published cache metadata.");
                }
                await RunCommandAsync(cli, ["cas", cas, "gc", "--apply"], ct);
                await Task.WhenAll(RunCommandAsync(cli, import, ct), RunCommandAsync(cli, import, ct));
                await RunCommandRejectedAsync(cli, ["cas", cas, "import-actions", legacyCas, "--key-policy", "2"], ct);
                Require((await File.ReadAllBytesAsync(reference, ct)).SequenceEqual(exactReference), "Import mutated the legacy reference.");
                Environment.SetEnvironmentVariable("CMAKEWORKFLOW_ARTIFACT_CAS", cas);
            }

            using Peer host = await Peer.StartAsync(cli, root, ct);
            using JsonRpc connection = HostConnection.Create(await LocalTransport.ConnectAsync(host.Endpoint, ct));
            IHostRpc rpc = await ConnectAsync(connection, root, ct);
            // Managed configure is needed once. Reuse the same provenanced tree and
            // select the build and run nodes in subsequent attempts.
            workflow.Nodes[1].DependsOn.Clear();
            NodeResult same = await RunAsync(rpc, binary: true, selected: ["cached"], "succeeded");
            Require(same.ActionCacheHit == true && same.ActionDigest == legacy.ActionDigest,
                "Equivalent binary input did not reuse a verified legacy cache result.");
            workflow.Nodes[^1].TimeoutSeconds = 30;
            NodeResult policyOnly = await RunAsync(rpc, binary: true, selected: ["cached"], "succeeded");
            Require(policyOnly.ActionCacheHit == true && policyOnly.ActionDigest == same.ActionDigest,
                "Workflow-only declaration changed an equivalent action key.");

            workflow.Nodes[^1].Arguments[2] = "B";
            NodeResult argument = await RunAsync(rpc, binary: true, selected: ["cached"], "succeeded");
            workflow.Nodes[^1].Environment["CMWF_FIXTURE_MODE"] = "different";
            NodeResult variable = await RunAsync(rpc, binary: true, selected: ["cached"], "succeeded");
            await File.WriteAllTextAsync(input, "second", ct);
            NodeResult changedInput = await RunAsync(rpc, binary: true, selected: ["cached"], "succeeded");
            Require(new[] { legacy.ActionDigest, argument.ActionDigest, variable.ActionDigest, changedInput.ActionDigest }.Distinct().Count() == 4 &&
                argument.ActionCacheHit == false && variable.ActionCacheHit == false && changedInput.ActionCacheHit == false,
                "Effective arguments, environment or declared input bytes reused an unrelated action.");
            Require(await File.ReadAllTextAsync(output, ct) == "secondBdifferent", "Action output differs from the executed inputs.");

            string digest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(output, ct)));
            string blob = Path.Combine(cas, "blobs", digest);
            await File.WriteAllTextAsync(blob, "corrupt", ct);
            _ = await RunAsync(rpc, binary: true, selected: ["cached"], "failed");
            File.Delete(blob);
            _ = await RunAsync(rpc, binary: true, selected: ["cached"], "failed");
            Require((await File.ReadAllLinesAsync(effects, ct)).Length == 4 && !File.Exists(output),
                "A corrupted or missing cache blob launched work or published an unverified output.");
            if (previous is not null)
            {
                string index = Path.Combine(cas, "actions", "index.sqlite");
                Require(File.Exists(index) && !Directory.EnumerateFiles(Path.Combine(cas, "actions"), "*.json").Any(),
                    "Indexed Action Cache wrote a legacy reference.");
                await RunCommandAsync(cli, ["cas", cas, "gc", "--apply"], ct);
                string legacyBlob = Path.Combine(cas, "blobs", Convert.ToHexStringLower(SHA256.HashData("firstAclean"u8)));
                Require(File.Exists(legacyBlob), "GC removed a retained indexed action.");
                await RunCommandAsync(cli, ["cas", cas, "drop-action", legacy.ActionDigest!], ct);
                await RunCommandAsync(cli, ["cas", cas, "gc", "--apply"], ct);
                Require(!File.Exists(legacyBlob), "GC retained an evicted indexed action.");
                File.Move(index, index + ".unavailable");
                await RunCommandRejectedAsync(cli, ["cas", cas, "gc", "--apply"], ct);
            }

            async Task<NodeResult> RunAsync(IHostRpc target, bool binary, IReadOnlyList<string> selected, string expected)
            {
                File.Delete(output);
                string id = "action-" + (++attempt).ToString(CultureInfo.InvariantCulture);
                string manifest = Path.Combine(root, binary ? "action.capnp" : "action.json");
                byte[] frame = binary ? CapnpWire.Frame(CapnpWire.Canonicalize(ProductionWorkflowCodec.Encode(workflow)))
                    : JsonSerializer.SerializeToUtf8Bytes(workflow, WorkflowDefinitionJson.Default.WorkflowDocument);
                await File.WriteAllBytesAsync(manifest, frame, ct);
                string sha = Convert.ToHexString(SHA256.HashData(frame));
                string results = Path.Combine(root, id);
                OperationReference accepted = binary
                    ? await target.ExecutionStartAsync(new(BinaryWorkflowSubmission.Domain, id, manifest, sha, 1, results)
                    {
                        Only = selected
                    }, ct)
                    : await target.WorkflowStartAsync(new(id, manifest, sha, 1, results), ct);
                await WaitForAsync(async () => (await target.OperationGetAsync(new(accepted.OperationId), ct)).State
                    is "succeeded" or "failed" or "canceled", ct);
                WorkflowReportSnapshot report = await target.OperationReportAsync(new(accepted.OperationId), ct);
                NodeResult result = report.Report.Nodes.Single(node => node.Id == "cached");
                Require(result.Status == expected, "Unexpected action status: " + result.Error);
                return result;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CMAKEWORKFLOW_ARTIFACT_CAS", prior);
            temp.Delete(recursive: true);
        }

        Console.WriteLine("Native action identity, legacy reuse, effective input misses, corrupt and missing blob refusal passed.");
    }
}
