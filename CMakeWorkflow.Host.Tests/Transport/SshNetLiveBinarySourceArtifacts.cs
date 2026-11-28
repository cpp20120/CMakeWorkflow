using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.Client.Transport;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Sources;
using CMakeWorkflowClient = CMakeWorkflow.Client.CMakeWorkflowClient;

namespace CMakeWorkflow.Host.Tests.Transport;

internal static partial class SshNetLiveLifecycleTests
{
    private static async Task BinarySourceArtifactsAsync(Probe probe, SshNetLaunchOptions options,
        string faultExecutable, CancellationToken ct)
    {
        string workspace = options.Workspace + "/mapped-" + Guid.NewGuid().ToString("N")[..8];
        string endpoint = workspace + "/host.sock";
        string marker = workspace + "/fault";
        string effects = workspace + "/effects";
        string cas = workspace + "/cas";
        string remote = Quote(options.RemoteExecutable);
        string environment = "CMAKEWORKFLOW_ARTIFACT_CAS=" + Quote(cas);
        string requestId = "mapped-source-ssh";
        DirectoryInfo scratch = Directory.CreateTempSubdirectory("cmwf-mapped-ssh-");
        int pid = 0;
        _ = probe.Run("mkdir -m 700 -- " + Quote(workspace));
        try
        {
            Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
            {
                ["CMakeLists.txt"] = """
                    cmake_minimum_required(VERSION 3.26)
                    project(mapped_source LANGUAGES CXX)
                    add_executable(worker main.cpp)
                    enable_testing()
                    add_test(NAME smoke COMMAND worker)
                    """u8.ToArray(),
                ["CMakePresets.json"] = """
                    {"version":3,"configurePresets":[{"name":"fixture","generator":"Ninja"}]}
                    """u8.ToArray(),
                ["main.cpp"] = """
                    #include <fstream>
                    #include <string>
                    int main(int argc, char** argv) {
                        if (argc == 1) return 0;
                        if (argc != 5) return 2;
                        std::ifstream input(argv[1]);
                        std::ofstream output(argv[2]);
                        std::ofstream effects(argv[3], std::ios::app);
                        std::string line;
                        std::getline(input, line);
                        output << line;
                        effects << argv[4] << '\n';
                        return input && output && effects ? 0 : 3;
                    }
                    """u8.ToArray(),
                ["message.txt"] = "captured bytes\n"u8.ToArray()
            };
            string archive = Path.Combine(scratch.FullName, "source.zip");
            await using (ZipArchive zip = await ZipFile.OpenAsync(archive, ZipArchiveMode.Create, ct))
            {
                foreach ((string path, byte[] content) in files)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(path);
                    entry.ExternalAttributes = ExperimentSourceBundle.RegularFile;
                    await using Stream output = await entry.OpenAsync(ct);
                    await output.WriteAsync(content, ct);
                }
            }

            string snapshot = LoreSnapshotStore.GetSnapshotId([.. files.Select(file =>
                new SnapshotFile(file.Key, Convert.ToHexString(SHA256.HashData(file.Value)), file.Value.LongLength))]);
            WorkflowDocument workflow = new()
            {
                SchemaVersion = 2,
                Contexts = { ["tree"] = new()
                {
                    SourceDirectory = "source", BuildDirectory = "build", Mode = "managed", ConfigurePreset = "fixture"
                } },
                Nodes =
                [
                    new() { Id = "configure", Kind = "configure", Context = "tree" },
                    new() { Id = "worker", Kind = "build", Context = "tree", Target = "worker", DependsOn = ["configure"] },
                    new() { Id = "test", Kind = "ctest", Context = "tree", DependsOn = ["worker"] },
                    CopyNode("a", "test"),
                    CopyNode("b", "a")
                ]
            };
            byte[] frame = CapnpWire.Frame(CapnpWire.Canonicalize(ProductionWorkflowCodec.Encode(workflow)));
            string manifestSha = Convert.ToHexString(SHA256.HashData(frame));
            byte[] zipBytes = await File.ReadAllBytesAsync(archive, ct);
            ExperimentStageRequest request = new(Convert.ToHexString(SHA256.HashData("mapped-source-ssh"u8)),
                snapshot, manifestSha, "", [])
            {
                BinaryManifest = frame,
                Bundle = new(Convert.ToHexString(SHA256.HashData(zipBytes)), zipBytes.LongLength, files.Count)
            };
            SshNetHostOptions persistent = new(options.Host, options.Username, options.PrivateKeyPath,
                options.HostKeySha256, endpoint, workspace)
            {
                RemoteExecutable = options.RemoteExecutable,
                Port = options.Port
            };
            _ = probe.Run(remote + " workflow init-binary " + Quote(workspace) + " && " +
                remote + " cas " + Quote(cas) + " init-indexed");
            pid = StartPersistentHost(probe, workspace, endpoint, faultExecutable, environment +
                " CMAKEWORKFLOW_TEST_FAULT=after-checkpoint CMAKEWORKFLOW_TEST_NODE=b CMAKEWORKFLOW_TEST_MARKER=" + Quote(marker));

            string operation;
            ExperimentStageReceipt staged;
            await using (CMakeWorkflowClient client = await CMakeWorkflowClient.ConnectSshNetAsync(persistent, ct))
            {
                staged = await client.Experiments.StageAsync(request, archive, ct);
                string stage = Path.GetDirectoryName(staged.Manifest)!;
                probe.UploadText(stage + "/source/message.txt", "changed live stage\n");
                WorkflowOperation accepted = await client.Executions.StartAsync(new("cmake-capnp-v1", requestId,
                    staged.Manifest, manifestSha, 1, workspace + "/results")
                {
                    SourceSnapshotId = snapshot
                }, ct);
                operation = accepted.OperationId;
            }

            for (int i = 0; i < 120 && !probe.Exists(marker); i++)
            {
                await Task.Delay(250, ct);
            }
            Check(probe.Exists(marker) && probe.Read(effects) == "a\n", "Mapped workflow missed the B pre-launch boundary.");
            _ = probe.Run("kill -KILL " + pid.ToString(CultureInfo.InvariantCulture));
            await probe.WaitForExitAsync(pid, ct);
            pid = 0;
            _ = probe.Run("rm -- " + Quote(staged.Manifest) + " " + Quote(endpoint));
            using (JsonDocument layout = JsonDocument.Parse(probe.Run(remote + " workflow storage-inspect " + Quote(workspace))))
            {
                string sourceStore = layout.RootElement.GetProperty("sourceStore").GetString()
                    ?? throw new InvalidDataException("Storage inspection did not find the source store.");
                _ = probe.Run(CopyAndRestore(workspace) + " && " + CopyAndRestore(sourceStore));
            }
            _ = probe.Run(remote + " cas " + Quote(workspace + "/.cmakeworkflow/execution-documents") + " gc --apply" +
                " && " + remote + " cas " + Quote(cas) + " gc --apply");
            pid = StartPersistentHost(probe, workspace, endpoint, options.RemoteExecutable, environment);

            await using CMakeWorkflowClient recovered = await CMakeWorkflowClient.ConnectSshNetAsync(persistent, ct);
            Check((await recovered.Operations.FindAsync(requestId, ct)).OperationId == operation, "Mapped execution identity changed.");
            WorkflowOperation handle = recovered.Operations.GetHandle(operation);
            RecoverySnapshot checkpoint = await handle.InspectRecoveryAsync(ct);
            RecoverySnapshot reconciled = await handle.ReconcileBinaryRetryAsync(checkpoint.Revision, "b",
                checkpoint.CheckpointSha256 ?? throw new InvalidDataException("Missing mapped checkpoint."),
                "Verified B stopped at the injected pre-launch boundary.", confirmedQuiescent: true, ct);
            RecoverySnapshot admitted = await handle.AdmitResumeAsync(reconciled.Revision, confirmedQuiescent: true, ct);
            _ = await handle.ExecuteResumeAsync(admitted.Revision, ct);
            Check((await handle.WaitForCompletionAsync(ct: ct)).State == "succeeded" && probe.Read(effects) == "a\nb\n",
                "Mapped recovery failed or repeated completed work.");
            foreach (OperationArtifactInfo artifact in (await handle.ListArtifactsAsync(ct)).Artifacts.Where(a => a.Kind == "output"))
            {
                string destination = Path.Combine(scratch.FullName, artifact.NodeId + ".txt");
                _ = await handle.DownloadArtifactAsync(artifact, destination, ct: ct);
                Check(await File.ReadAllTextAsync(destination, ct) == "captured bytes", "Mapped output differs from Lore input.");
            }
            Check(File.Exists(Path.Combine(scratch.FullName, "a.txt")) && File.Exists(Path.Combine(scratch.FullName, "b.txt")),
                "Mapped outputs were missing from the verified Host catalog.");
            Check(probe.Read(Path.GetDirectoryName(staged.Manifest)! + "/source/message.txt") == "changed live stage\n",
                "Execution modified the stage source.");
            Check(probe.Read(workspace + "/results/manifest-artifacts/evidence/b.txt") == "captured bytes",
                "Mapped output did not land under the operation's result root.");
        }
        catch
        {
            if (probe.Exists(workspace + "/host.log"))
            {
                await Console.Error.WriteLineAsync(probe.Read(workspace + "/host.log"));
            }
            if (probe.Exists(workspace + "/results/run.json"))
            {
                await Console.Error.WriteLineAsync(probe.Read(workspace + "/results/run.json"));
            }
            string configureError = workspace + "/results/configure.stderr.log";
            if (probe.Exists(configureError))
            {
                await Console.Error.WriteLineAsync(probe.Read(configureError));
            }
            throw;
        }
        finally
        {
            if (probe.Alive(pid))
            {
                _ = probe.Run("kill -TERM " + pid.ToString(CultureInfo.InvariantCulture));
                await probe.WaitForExitAsync(pid, CancellationToken.None);
            }
            _ = probe.Run("rm -rf -- " + Quote(workspace));
            scratch.Delete(recursive: true);
        }

        WorkflowNode CopyNode(string id, string previous) => new()
        {
            Id = id,
            Kind = "run",
            From = "worker",
            DependsOn = [previous],
            ActionCache = true,
            Inputs = [new() { Path = "source/message.txt" }],
            Outputs = [new() { Name = "mapped", Path = "evidence/" + id + ".txt", Fresh = true }],
            Arguments = ["@input:0", "@output:mapped", effects, id]
        };
    }

    private static string CopyAndRestore(string path)
    {
        string backup = Quote(path + ".backup");
        string original = Quote(path + ".original");
        string destination = Quote(path);
        return "test ! -e " + backup + " && test ! -e " + original +
               " && cp -a -- " + destination + " " + backup + " && diff -r -- " + destination + " " + backup +
               " && mv -T -- " + destination + " " + original + " && { mv -T -- " + backup + " " + destination +
               " || { mv -T -- " + original + " " + destination + "; false; }; } && rm -rf -- " + original;
    }
}
