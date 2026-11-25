#:package CMakeWorkflow.Scripting@0.1.0-script.1
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Scripting;

if (args is not ([_] or [_, _] or [_, _, "--execute", _]))
{
    await Console.Error.WriteLineAsync("Usage: dotnet run --file workflow.cs -- WORKSPACE [ENDPOINT [--execute REQUEST_ID]]");
    return 2;
}

string workspace = Path.GetFullPath(args[0]);
Workflow workflow = Workflow.Define("native-smoke", ".");
ContextRef context = workflow.Context("debug", "build/gcc-debug", spec => spec.ConfigurePreset = "gcc-debug");
NodeRef configure = workflow.Configure("configure", context);
TargetBuildRef build = workflow.Build("build", context, "worker", [configure]);
NodeRef test = workflow.CTest("test", context, [build]);
WorkflowManifest manifest = workflow.Compose([configure, build, test]);

string path = await manifest.SaveAsync(workspace);
Console.WriteLine($"{manifest.Sha256} {path}");
if (args.Length == 1)
{
    return 0;
}

// This example connects to a local Host that can read the same workspace.
await using CMakeWorkflowClient client = await CMakeWorkflowClient.ConnectAsync(new(args[1], workspace));
WorkflowPreflightResult preflight = await client.Workflows.PreflightAsync(
    manifest.AtHost(path, Path.Combine(workspace, "scripting-results")));
foreach (WorkflowPreflightNode node in preflight.Nodes)
{
    Console.WriteLine($"{node.Id}: {node.Kind} after [{string.Join(", ", node.Dependencies)}]");
    foreach (WorkflowPreflightDiagnostic diagnostic in node.Diagnostics)
    {
        Console.WriteLine($"  {diagnostic.Severity}: {diagnostic.Message}");
    }
}

if (!preflight.Valid || args.Length != 4)
{
    return preflight.Valid ? 0 : 1;
}

WorkflowOperation run = await client.Workflows.StartAsync(preflight.CreateStartOptions(args[3]));
Console.WriteLine($"Operation {run.OperationId}");
OperationSnapshot status = await run.WaitForCompletionAsync();
OperationReportSnapshot report = await run.GetReportAsync();
Console.WriteLine($"{status.State} {report.RunDirectory}");
return status.State == "succeeded" ? 0 : 1;
