using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.Client.Experiments;
using CMakeWorkflow.Client.Transport;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.History;
using Microsoft.Data.Sqlite;
using CMakeWorkflowClient = CMakeWorkflow.Client.CMakeWorkflowClient;

namespace CMakeWorkflow.Host.Tests.Transport;

internal static partial class SshNetLiveLifecycleTests
{
    private static async Task BinaryExperimentAsync(Probe probe, SshNetLaunchOptions options, string controller,
        string faultExecutable, string store, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        ct = deadline.Token;
        string workspace = options.Workspace + "/exp-" + Guid.NewGuid().ToString("N")[..8];
        string endpoint = workspace + "/host.sock";
        string marker = workspace + "/fault";
        string effects = workspace + "/effects";
        string remote = Quote(options.RemoteExecutable);
        string environment = "CMAKEWORKFLOW_TOOL_STORE=" + Quote(store);
        DirectoryInfo scratch = Directory.CreateTempSubdirectory("cmwf-native-experiment-");
        int pid = 0;
        string planId = "";
        _ = probe.Run("mkdir -m 700 -- " + Quote(workspace));
        try
        {
            string project = Directory.CreateDirectory(Path.Combine(scratch.FullName, "repo")).FullName;
            await File.WriteAllTextAsync(Path.Combine(project, "CMakeLists.txt"), """
                cmake_minimum_required(VERSION 3.26)
                project(native_remote LANGUAGES CXX)
                find_package(cxxopts CONFIG REQUIRED)
                add_executable(benchmark main.cpp)
                target_link_libraries(benchmark PRIVATE cxxopts::cxxopts)
                enable_testing()
                add_test(NAME package_consumer COMMAND benchmark)
                """, ct);
            await File.WriteAllTextAsync(Path.Combine(project, "main.cpp"), """
                #include <cxxopts.hpp>
                #include <fstream>
                #include <iostream>
                int main(int argc, char** argv) {
                    cxxopts::Options options("benchmark", "binary provisioning receipt");
                    std::cout << options.help();
                    if (argc > 1) {
                        std::ofstream stream(argv[1], std::ios::app);
                        stream << "timing\n";
                        if (!stream) return 2;
                    }
                    return 0;
                }
                """, ct);
            await File.WriteAllTextAsync(Path.Combine(project, "vcpkg.json"), """
                {"builtin-baseline":"84bab45d415d22042bd0b9081aea57f362da3f35","dependencies":["cxxopts"]}
                """, ct);

            _ = await RunExperimentCommandAsync("git", ["-C", project, "init", "-q"], scratch, true, ct);
            _ = await RunExperimentCommandAsync("git", ["-C", project, "add", "."], scratch, true, ct);
            _ = await RunExperimentCommandAsync("git", ["-C", project, "-c", "user.name=Fixture", "-c",
                "user.email=fixture@example.invalid", "commit", "-qm", "Native Git workflow"], scratch, true, ct);
            string commit = (await RunExperimentCommandAsync("git", ["-C", project, "rev-parse", "HEAD"], scratch, true, ct)).Trim();

            _ = probe.Run(remote + " workflow init-binary " + Quote(workspace));
            string toolingPlan = workspace + "/tools.capnp";
            string tooling = " --profile linux-clang --profile vcpkg --tool-policy ManagedOnly --binary-plan " + Quote(toolingPlan);
            string reviewed = probe.Run("env " + environment + " " + remote + " tooling plan" + tooling + " 2>&1");
            string toolingIdentity = LineValue(reviewed, "Binary plan approval: ");
            _ = probe.Run("env " + environment + " " + remote + " tooling setup" + tooling +
                " --yes --plan-sha " + Quote(toolingIdentity), TimeSpan.FromMinutes(8));

            string lockOptions = " --binary --profile linux-clang --generator 'Ninja Multi-Config' --json";
            string resolution = probe.Run("env " + environment + " " + remote + " tooling resolve" + lockOptions);
            using JsonDocument resolved = JsonDocument.Parse(resolution);
            string lockId = resolved.RootElement.GetProperty("environmentSha256").GetString()
                ?? throw new InvalidDataException("Managed environment did not resolve.");
            _ = probe.Run("env " + environment + " " + remote + " tooling lock" + lockOptions +
                " --approve-sha " + Quote(lockId));

            pid = StartPersistentHost(probe, workspace, endpoint, faultExecutable, environment +
                " CMAKEWORKFLOW_TEST_FAULT=after-checkpoint CMAKEWORKFLOW_TEST_NODE=timing_002 CMAKEWORKFLOW_TEST_MARKER=" + Quote(marker));
            string planned = await RunExperimentCommandAsync(controller, ["experiment", "plan", "--repo", new Uri(project).AbsoluteUri,
                "--ref", commit, "--host", options.Host, "--workspace", workspace, "--endpoint", endpoint,
                "--user", options.Username, "--port", options.Port.ToString(CultureInfo.InvariantCulture),
                "--key-file", options.PrivateKeyPath, "--host-key-sha256", options.HostKeySha256,
                "--remote-executable", options.RemoteExecutable, "--binary", "--managed-clang", "--config", "Release",
                "--environment-lock", lockId, "--vcpkg", "--benchmark-target", "benchmark", "--repeats", "3",
                "--benchmark-arg", effects], scratch, true, ct);
            planId = LineValue(planned, "Plan ID ");
            string controllerPlan = Path.Combine(scratch.FullName, "plans", planId, "plan.json");
            PreparedExperimentPlan approved = await ExperimentService.ReadPlanAsync(controllerPlan, planId, ct);
            Check(approved.BinaryWorkflow && approved.Commit == commit, "Controller lost native Git approval context.");
            string legacyId = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(controllerPlan, ct)));
            string legacyPlan = Directory.CreateDirectory(Path.Combine(scratch.FullName, "plans", legacyId)).FullName;
            File.Copy(controllerPlan, Path.Combine(legacyPlan, "plan.json"));
            _ = await RunExperimentCommandAsync(controller, ["experiment", "run", "--plan", legacyId, "--approve"], scratch, false, ct);

            string plannedDeps = await RunExperimentCommandAsync(controller, ["experiment", "deps", "plan", planId], scratch, true, ct);
            string dependency = LineValue(plannedDeps, "Dependency plan SHA-256 ");
            _ = await RunExperimentCommandAsync(controller, ["experiment", "run", "--plan", planId, "--approve",
                "--wait-seconds", "0"], scratch, false, ct);
            _ = await RunExperimentCommandAsync(controller, ["experiment", "deps", "apply", planId, "--approve",
                "--plan-sha", new string('0', 64)], scratch, false, ct);
            string lockPath = store + "/manifests/" + lockId + ".capnp";
            string[] apply = ["experiment", "deps", "apply", planId, "--approve", "--plan-sha", dependency];
            await RejectTamperedAsync(lockPath, apply);
            string[] applied = await Task.WhenAll(
                RunExperimentCommandAsync(controller, apply, scratch, true, ct),
                RunExperimentCommandAsync(controller, apply, scratch, true, ct));
            string receipt = LineValue(applied[0], "Binary provisioning receipt identity ");
            Check(LineValue(applied[1], "Binary provisioning receipt identity ") == receipt,
                "Concurrent native provisioning did not preserve the first verified receipt.");
            string installation = store + "/vcpkg/projects/" + dependency;
            string stage = workspace + "/.cmakeworkflow/experiments/" + planId;
            _ = probe.Run("test -f " + Quote(installation + "/receipt.capnp") +
                " && test ! -e " + Quote(installation + "/receipt.json") +
                " && test -f " + Quote(store + "/manifests/" + lockId + ".capnp") +
                " && test ! -e " + Quote(store + "/manifests/" + lockId + ".json") +
                " && test -f " + Quote(stage + "/workflow.capnp") +
                " && test ! -e " + Quote(stage + "/workflow.json"));
            await RejectTamperedAsync(installation + "/receipt.capnp",
                ["experiment", "run", "--plan", planId, "--approve", "--wait-seconds", "0"]);

            string launched = await RunExperimentCommandAsync(controller, ["experiment", "run", "--plan", planId, "--approve",
                "--wait-seconds", "0"], scratch, true, ct);
            string[] launch = launched.Split('\n').Single(line => line.Contains("Host request ", StringComparison.Ordinal))
                .Split("Host request ", StringSplitOptions.None)[1].TrimEnd('.').Split(", operation ", StringSplitOptions.None);
            string requestId = launch[0];
            string operationId = launch[1];
            Check(operationId is { Length: 32 } && operationId.All(Uri.IsHexDigit), "CLI did not persist a child operation.");
            for (int i = 0; i < 120 && !probe.Exists(marker); i++)
            {
                await Task.Delay(500, ct);
            }
            Check(probe.Exists(marker) && probe.Read(effects) == "timing\n",
                "Managed experiment failed to reach the checkpoint after the first timed invocation.");

            string interrupted = pid.ToString(CultureInfo.InvariantCulture);
            _ = probe.Run("kill -KILL " + interrupted);
            await probe.WaitForExitAsync(pid, ct);
            pid = 0;
            _ = probe.Run("rm -- " + Quote(stage + "/workflow.capnp") + " " + Quote(endpoint) +
                " && " + remote + " cas " + Quote(workspace + "/.cmakeworkflow/execution-documents") + " gc --apply");
            pid = StartPersistentHost(probe, workspace, endpoint, options.RemoteExecutable, environment);

            await using (CMakeWorkflowClient resumed = await approved.Host.ConnectAsync(ct))
            {
                Check(resumed.Host.ProcessId == pid &&
                    (await resumed.Operations.FindAsync(requestId, ct)).OperationId == operationId,
                    "Restarted Host did not recover the original experiment operation.");
                WorkflowOperation operation = resumed.Operations.GetHandle(operationId);
                RecoverySnapshot checkpoint = await operation.InspectRecoveryAsync(ct);
                RecoverySnapshot reconciled = await operation.ReconcileBinaryRetryAsync(checkpoint.Revision, "timing_002",
                    checkpoint.CheckpointSha256 ?? throw new InvalidDataException("Missing experiment checkpoint."),
                    "The second benchmark did not launch at the injected checkpoint.", confirmedQuiescent: true, ct);
                RecoverySnapshot admitted = await operation.AdmitResumeAsync(reconciled.Revision, confirmedQuiescent: true, ct);
                _ = await operation.ExecuteResumeAsync(admitted.Revision, ct);
                Check((await operation.WaitForCompletionAsync(ct: ct)).State == "succeeded", "Native experiment resume failed.");
                Check((await resumed.History.WaitForIndexAsync(operationId, ct: ct)).State == HistoryIndexState.Indexed,
                    "Native experiment history was not indexed.");
            }

            _ = await RunExperimentCommandAsync(controller, ["experiment", "report", planId, "--wait-seconds", "30"], scratch, true, ct);
            using JsonDocument report = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(scratch.FullName, "plans", planId, "result.json"), ct));
            JsonElement run = report.RootElement.GetProperty("run");
            Check(probe.Read(effects) == "timing\ntiming\ntiming\n" &&
                run.GetProperty("sourceSnapshotId").GetString() == approved.SourceSnapshotId &&
                run.GetProperty("nodes").EnumerateArray().Any(node => node.GetProperty("kind").GetString() == "ctest"
                    && node.GetProperty("status").GetString() == "succeeded"), "Native recovery lost work or source provenance.");
            string artifactId = report.RootElement.GetProperty("artifacts").EnumerateArray().Single(artifact =>
                artifact.TryGetProperty("nodeId", out JsonElement node) && node.GetString() == "timing_001" &&
                artifact.GetProperty("kind").GetString() == "stdout").GetProperty("artifactId").GetString()!;
            string downloaded = Path.Combine(scratch.FullName, "benchmark.txt");
            _ = await RunExperimentCommandAsync(controller, ["experiment", "download", planId, "--artifact", artifactId,
                "--output", downloaded], scratch, true, ct);
            Check((await File.ReadAllTextAsync(downloaded, ct)).Contains("binary provisioning receipt", StringComparison.Ordinal),
                "Downloaded artifact lost the approved vcpkg workload output.");
            await VerifyBinaryExperimentLedgerAsync(probe, workspace, scratch.FullName, operationId, ct);
            Console.WriteLine($"Native Git experiment {planId} recovered {operationId}, verified receipt {receipt}.");
        }
        catch
        {
            string log = workspace + "/host.log";
            if (probe.Exists(log))
            {
                await Console.Error.WriteLineAsync(probe.Read(log));
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

        async Task RejectTamperedAsync(string path, string[] arguments)
        {
            string backup = path + ".original";
            _ = probe.Run("cp -- " + Quote(path) + " " + Quote(backup) + " && printf x >>" + Quote(path));
            try
            {
                _ = await RunExperimentCommandAsync(controller, arguments, scratch, false, ct);
            }
            finally
            {
                _ = probe.Run("mv -- " + Quote(backup) + " " + Quote(path));
            }
        }
    }

    private static async Task VerifyBinaryExperimentLedgerAsync(Probe probe, string workspace, string local,
        string operation, CancellationToken ct)
    {
        string remoteLedger = workspace + "/.cmakeworkflow/operations.sqlite/execution-v2.sqlite";
        string path = Path.Combine(local, "ledger.sqlite");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(probe.Run("base64 -w0 -- " + Quote(remoteLedger))), ct);
        await using SqliteConnection db = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await db.OpenAsync(ct);
        using SqliteCommand query = db.CreateCommand();
        query.CommandText = """
            SELECT b.results_directory, r.blob_id FROM accepted_workflows AS a
            JOIN workflow_binary_inputs AS b USING(operation_id)
            JOIN workflow_binary_authorizations AS d USING(operation_id)
            JOIN workflow_native_checkpoints AS c USING(operation_id)
            JOIN workflow_terminal_reports AS r USING(operation_id)
            WHERE a.operation_id=$operation AND length(a.snapshot)=0 AND d.identity_policy=2
                AND length(d.plan_document_id)=64 AND c.generation>0 AND length(r.document_id)=64;
            """;
        _ = query.Parameters.AddWithValue("$operation", operation);
        await using SqliteDataReader evidence = await query.ExecuteReaderAsync(ct);
        Check(await evidence.ReadAsync(ct), "Remote experiment did not retain native checkpoints, authorization and terminal report.");
        string results = evidence.GetString(0);
        string report = evidence.GetString(1);
        Check(!probe.Exists(results + "/run.json") && probe.Exists(workspace + "/.cmakeworkflow/execution-documents/blobs/" + report),
            "Remote experiment used a persisted JSON report or was missing its canonical report blob.");
    }

    private static string LineValue(string output, string prefix) =>
        output.Split('\n').Single(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..].Trim();

    private static async Task<string> RunExperimentCommandAsync(string executable, string[] arguments, DirectoryInfo scratch,
        bool success, CancellationToken ct)
    {
        ProcessStartInfo start = new(executable) { RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["CMAKEWORKFLOW_EXPERIMENT_STORE"] = Path.Combine(scratch.FullName, "plans");
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not launch " + executable);
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = child.StandardError.ReadToEndAsync(ct);
        try
        {
            await child.WaitForExitAsync(ct);
            string output = await stdout;
            Check(child.ExitCode == 0 == success, string.Join(' ', arguments) + "\n" + output + await stderr);
            return output;
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }
}
