#:project ../../CMakeWorkflow.Developer/CMakeWorkflow.Developer.csproj
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.History;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Developer;

if (args.Length is not (2 or 6))
{
    await Console.Error.WriteLineAsync("Usage: campaign.cs PROJECT REQUEST_ID [APPROVED_SHA BASELINE_RUN|this BASELINE_VARIANT CANDIDATE_VARIANT]");
    return 2;
}

using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(5));
CancellationToken ct = deadline.Token;
EngineeringProject project = await EngineeringProject.OpenAsync(new(args[0]), ct);
string[] kits = ["linux-gcc", "linux-system-clang"];
ConfigurationSelection[] selections = [.. from kit in project.Kits
    where kits.Contains(kit.Id, StringComparer.Ordinal)
    orderby Array.IndexOf(kits, kit.Id)
    from profile in project.Profiles
    where profile == "release"
    select new ConfigurationSelection { Kit = kit.Id, Profile = profile }];
foreach (ConfigurationSelection selection in selections)
{
    ProjectInspection inspection = await project.InspectAsync(selection, ct);
    Console.WriteLine($"{selection.Kit}/{selection.Profile} SDK={inspection.SdkLock.Sha256} Kit={inspection.Kit?.Lock.Sha256} config={inspection.Configuration?.Lock.Sha256}");
    foreach (DeveloperDiagnostic diagnostic in inspection.Diagnostics)
    {
        Console.WriteLine($"  {diagnostic.Code} {diagnostic.Message} {diagnostic.Remediation}");
    }
}

DeveloperOperationRequest request = project.Operation(DeveloperOperationKind.Benchmark, new() { Profile = "release" }) with
{
    KitMatrix = [.. selections.Select(selection => selection.Kit!)],
    Repeats = 5
};
DeveloperOperationPlan plan = await DeveloperLifecycle.PlanOperationAsync(request, ct);
Console.WriteLine($"Manifest SHA-256 {plan.ManifestSha256}");
foreach (ProjectVariant variant in plan.Variants)
{
    Console.WriteLine($"Variant {variant.Name} {variant.Id} ({variant.BuildDirectory})");
}
if (args.Length == 2)
{
    return 0;
}
if (args[2] != plan.ManifestSha256)
{
    await Console.Error.WriteLineAsync("Engineering evidence changed. Review the new plan digest.");
    return 2;
}

await using LocalDeveloperHost host = await LocalDeveloperHost.OpenAsync(project.Project.Directory, ct);
CMakeWorkflowClient client = host.Client;
string destination = ".cmakeworkflow/campaign-results/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(args[1])));
PreparedWorkflow prepared = await ReviewedEngineeringWorkflow.PrepareAsync(client, request, plan, destination, ct: ct);
WorkflowPreflightResult preflight = await ReviewedEngineeringWorkflow.PreflightAsync(client, prepared,
    new(HistoryPolicyMode.Default), ct: ct);
if (!preflight.Valid)
{
    foreach (WorkflowPreflightDiagnostic diagnostic in preflight.Nodes.SelectMany(node => node.Diagnostics))
    {
        await Console.Error.WriteLineAsync(diagnostic.Message);
    }
    return 2;
}

Console.WriteLine($"Request ID {args[1]}");
WorkflowOperation operation = await client.Workflows.StartAsync(preflight.CreateStartOptions(args[1]), ct);
if ((await host.WaitForOperationAsync(operation.OperationId, ct)).Operation.State != "succeeded")
{
    return 1;
}
HistoryIndexStatus status = await operation.WaitForHistoryAsync(ct: ct);
if (status.State != HistoryIndexState.Indexed)
{
    Console.WriteLine($"History {status.State} {status.DiagnosticCode}");
    return 2;
}

const string metric = "elapsed";
OperationHistoryRuns receipt = await client.History.GetOperationRunsAsync(operation.OperationId, ct);
string candidateRun = receipt.Runs.Single().RunId;
string baselineRun = args[3] == "this" ? candidateRun : args[3];
Console.WriteLine($"Operation {operation.OperationId} History run {candidateRun}");
IReadOnlyList<HistoryMeasurement> measurements = await operation.GetMeasurementsAsync(metric, ct: ct);
foreach (MeasurementSummary group in measurements.Summarize())
{
    Console.WriteLine($"{group.Series.Variant} n={group.Statistics.Count} median={group.Statistics.Median} {group.Series.Unit} environment={group.Series.Environment}");
}
string? baselineEnvironment = (await client.History.QueryAsync(new(receipt.DatabasePath, baselineRun, metric, args[4]), ct))
    .Select(row => row.Environment).Distinct().SingleOrDefault();
string? candidateEnvironment = measurements.Where(row => row.Variant == args[5])
    .Select(row => row.Environment).Distinct().SingleOrDefault();
bool intentional = args[3] == "this" && baselineEnvironment is not null && candidateEnvironment is not null && args[4] != args[5];
List<HistoryDifference> differences = await client.History.CompareAsync(new(receipt.DatabasePath, baselineRun, candidateRun)
{
    Selection = new(metric)
    {
        BaselineVariant = args[4],
        CandidateVariant = args[5],
        BaselineEnvironment = baselineEnvironment,
        CandidateEnvironment = candidateEnvironment,
        AllowDifferentBuildEnvironments = intentional
    }
}, ct);
RegressionDecision decision = RegressionGate.Evaluate(differences.SingleOrDefault(), new(MetricDirection.LowerIsBetter, 10)
{
    AllowIntentionalBuildDifferences = intentional
});
Console.WriteLine($"Gate {decision.Outcome} {decision.Code} {decision.ObservedRegression}");
return decision.Outcome switch { RegressionOutcome.Passed => 0, RegressionOutcome.Failed => 1, RegressionOutcome.Inconclusive => 2, _ => throw new InvalidOperationException() };
