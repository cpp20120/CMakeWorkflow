using System.Diagnostics;
using CMakeWorkflow.Host.Workflows;

namespace CMakeWorkflow.Host.Tests.Durability;

internal static partial class BinaryWorkflowWireTests
{
    internal static async Task VerifyScriptRecoveryAsync(string cli, string faultCli, string scriptingProject)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(3));
        CancellationToken ct = deadline.Token;
        DirectoryInfo fixture = Directory.CreateTempSubdirectory("cmwf-script-replay-");
        string root = fixture.FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "CMakeLists.txt"), """
                cmake_minimum_required(VERSION 3.26)
                project(script_replay NONE)
                add_custom_target(effect COMMAND "${CMAKE_COMMAND}" -P "${CMAKE_CURRENT_LIST_DIR}/effect.cmake")
                """, ct);
            await File.WriteAllTextAsync(Path.Combine(root, "CMakePresets.json"), """
                {"version":6,"configurePresets":[{"name":"test","generator":"Ninja","binaryDir":"${sourceDir}/build"}]}
                """, ct);
            await File.WriteAllTextAsync(Path.Combine(root, "effect.cmake"), """
                file(APPEND "${CMAKE_CURRENT_LIST_DIR}/effects.txt" "ran\n")
                """, ct);
            string script = Path.Combine(root, "workflow.cs");
            await File.WriteAllTextAsync(script, "#:project " + Path.GetFullPath(scriptingProject) + "\n" + """
                using CMakeWorkflow.Scripting;
                Workflow workflow = Workflow.Define("script-replay", ".");
                ContextRef context = workflow.Context("test", "build", setup => setup.ConfigurePreset = "test");
                NodeRef configure = workflow.Configure("configure", context);
                NodeRef effect = workflow.Invoke("effect", context, "effect", [configure]);
                return await WorkflowScript.ExportAsync(workflow.Compose([effect]));
                """, ct);
            await RunCommandAsync(cli, ["workflow", "init-binary", root], ct);
            string[] args = ["script", "run-binary", script, "--approve", "--workspace", root, "--no-history",
                "--request-id", "script-replay", "--results-dir", Path.Combine(root, "results")];
            string marker = Path.Combine(root, "terminal");
            ProcessStartInfo fault = StartInfo(faultCli, args);
            fault.Environment["CMAKEWORKFLOW_TEST_FAULT"] = "before-terminal-commit";
            fault.Environment["CMAKEWORKFLOW_TEST_MARKER"] = marker;
            _ = fault.Environment.Remove("CMAKEWORKFLOW_TEST_NODE");
            using (Process process = Process.Start(fault) ?? throw new UnreachableException("Could not start fault script."))
            {
                Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
                try
                {
                    await WaitForAsync(() => Task.FromResult(process.HasExited || File.Exists(marker)), ct);
                    if (process.HasExited)
                    {
                        throw new UnreachableException("Script exited before terminal publication boundary. " + await stderr + await stdout);
                    }
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }

                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }

            string operation = ReadOperation(root, "dispatch_authorized");
            await RunCommandAsync(cli, args, ct);
            await RunCommandAsync(cli, args, ct);
            Require(ReadOperation(root, "succeeded") == operation, "Script replay changed operation identity.");
            Require((await File.ReadAllLinesAsync(Path.Combine(root, "effects.txt"), ct)).SequenceEqual(["ran"]),
                "A stopped script controller repeated an external effect.");
            Require(!File.Exists(Path.Combine(root, "results", "run.json")) &&
                !Directory.EnumerateFiles(root, ".cmwf-workflow-*.json").Any(), "Native script persisted a legacy JSON manifest or report.");
            Console.WriteLine("Script run-binary recovered a published report under the same request and ran the effect once.");
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }

    private static string ReadOperation(string workspace, string expectedState)
    {
        using DurableAcceptanceStore store = new(workspace, readOnly: true);
        AcceptedWorkflow operation = store.Find("script-replay") ??
            throw new InvalidDataException("Missing script operation.");
        Require(operation.State == expectedState, "Unexpected script operation state.");
        return operation.OperationId;
    }
}
