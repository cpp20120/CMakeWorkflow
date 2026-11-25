using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Developer;

using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(4));
CancellationToken ct = timeout.Token;
if (args is ["--plan", string directory])
{
    DeveloperOperationPlan matching = await DeveloperLifecycle.PlanOperationAsync(
        new(new(directory), DeveloperOperationKind.Test), ct);
    await Console.Out.WriteLineAsync(matching.ManifestSha256);
    return;
}

if (args is not ([] or ["--environment-lock", _]))
{
    throw new ArgumentException("Expected --plan DIRECTORY or --environment-lock SHA256.");
}
DirectoryInfo temporary = Directory.CreateTempSubdirectory("cmwf-p5-smoke-");
// Darwin's system temporary directory has a symlinked ancestor.
string parent = OperatingSystem.IsMacOS() && temporary.FullName.StartsWith("/var/", StringComparison.Ordinal)
    ? "/private" + temporary.FullName : temporary.FullName;
try
{
    CreatedProject created = await DeveloperLifecycle.CreateProjectAsync(new("runtime", "LifecycleSample", Path.Combine(parent, "project"))
    {
        EnvironmentLockSha256 = args.Length == 2 ? args[1] : null
    }, ct);
    string root = created.Directory;
    string nested = Directory.CreateDirectory(Path.Combine(root, "sub", "directory")).FullName;
    ProjectLocation location = new(nested);
    ProjectLocation exact = new(nested) { ProjectDirectory = root };
    Check((await DeveloperLifecycle.DiscoverProjectAsync(location, ct)).Directory == root, "nested discovery");
    Check((await DeveloperLifecycle.DiscoverProjectAsync(exact, ct)).Directory == root, "explicit discovery");
    ProjectInspection inspection = await DeveloperLifecycle.InspectProjectAsync(location, new(), ct);
    Check(inspection.Sdk.Id == "Native.Runtime" && inspection.Sdk.Version.Length != 0, "SDK resolution");
    Check(inspection.Configuration?.Variants is [{ Name: "release", Configuration: "Release" }], "default profile");
    Check((await DeveloperLifecycle.VerifyConfigurationAsync(root, new() { Profile = "debug" }, ct)).Variants
        is [{ Name: "debug", Configuration: "Debug" }], "debug profile");
    DeveloperOperationRequest test = new(location, DeveloperOperationKind.Test);
    DeveloperOperationPlan testPlan = await DeveloperLifecycle.PlanOperationAsync(test, ct);
    Check((await DeveloperLifecycle.PlanOperationAsync(test with { Location = exact }, ct)).ManifestSha256 ==
        testPlan.ManifestSha256, "deterministic manifest");
    Check(!File.Exists(Path.Combine(root, testPlan.ManifestName)) && !Directory.Exists(Path.Combine(root, "out")),
        "read-only planning");

    await File.WriteAllTextAsync(Path.Combine(root, "override.json"), """{"testing":false}""", ct);
    ConfigurationSelection changed = new() { Overrides = "override.json" };
    await RejectAsync(() => DeveloperLifecycle.PlanOperationAsync(test with { Configuration = changed }, ct), "approval required");
    ConfigurationPlan approval = await DeveloperLifecycle.PlanConfigurationAsync(root, changed, ct);
    await File.WriteAllTextAsync(Path.Combine(root, "override.json"), """{"testing":true}""", ct);
    await RejectAsync(() => DeveloperLifecycle.LockConfigurationAsync(root, changed, approval.Lock.Sha256, ct), "stale configuration approval");

    await using (LocalDeveloperHost host = await LocalDeveloperHost.OpenAsync(root, ct))
    {
        foreach (DeveloperOperationKind kind in new[] { DeveloperOperationKind.Build, DeveloperOperationKind.Test,
            DeveloperOperationKind.Benchmark, DeveloperOperationKind.Pack })
        {
            DeveloperOperationRequest operation = new(location, kind) { Repeats = 2 };
            DeveloperOperationPlan plan = await DeveloperLifecycle.PlanOperationAsync(operation, ct);
            DeveloperExecutionRequest submission = new(operation, plan.ManifestSha256, Guid.NewGuid().ToString("N"))
            {
                HistoryPolicy = new(HistoryPolicyMode.Disabled)
            };
            WorkflowOperation run = await DeveloperLifecycle.ExecuteOperationAsync(host.Client, submission, ct);
            OperationOverview overview = await host.WaitForOperationAsync(run.OperationId, ct);
            OperationSnapshot state = overview.Operation;
            OperationReportSnapshot report = await run.GetReportAsync(ct);
            if (state.State != "succeeded")
            {
                throw new InvalidOperationException(kind + " failed: " + report.Report.GetRawText());
            }
            Check(overview.IsFinalized && overview.Nodes.All(node => node.Status == "succeeded"), "structured node results");
            Check(report.Revision == overview.ReportSha256 && report.RunDirectory == overview.RunDirectory, "report identity");
            Check(string.Equals(state.ManifestSha256, plan.ManifestSha256, StringComparison.OrdinalIgnoreCase) && state.OperationId == run.OperationId, "admission identity");
            string manifest = Path.Combine(root, plan.ManifestName);
            Check(File.Exists(manifest) && (await File.ReadAllBytesAsync(manifest, ct)).AsSpan().SequenceEqual(plan.ManifestJson.Span),
                "digest-pinned recovery manifest");
            if (kind == DeveloperOperationKind.Build)
            {
                WorkflowOperation retry = await DeveloperLifecycle.ExecuteOperationAsync(host.Client, submission, ct);
                Check(retry.OperationId == run.OperationId, "idempotent start");
            }
            if (kind == DeveloperOperationKind.Pack)
            {
                string archive = Path.Combine(root, plan.Variants[0].BuildDirectory, "cmwf-package.zip");
                Check(File.Exists(archive) && new FileInfo(archive).Length > 0, "real package");
            }
        }

        string manifestPath = Path.Combine(root, testPlan.ManifestName);
        await File.AppendAllTextAsync(manifestPath, " ", ct);
        await RejectAsync(() => DeveloperLifecycle.PublishOperationManifestAsync(test, testPlan.ManifestSha256, ct), "tampered manifest");
        await File.WriteAllBytesAsync(manifestPath, testPlan.ManifestJson.ToArray(), ct);

        await File.WriteAllTextAsync(Path.Combine(root, "cancel.cmake"), """
            file(WRITE "${ready}" "")
            execute_process(COMMAND "${CMAKE_COMMAND}" -E sleep 60)
            """, ct);
        await File.AppendAllTextAsync(Path.Combine(root, "CMakeLists.txt"), """

            add_test(NAME p5_cancel COMMAND "${CMAKE_COMMAND}"
                "-Dready=${CMAKE_CURRENT_BINARY_DIR}/p5-cancel.ready" -P "${CMAKE_CURRENT_SOURCE_DIR}/cancel.cmake")
            """, ct);
        DeveloperOperationRequest cancel = test with { TestRegex = "^p5_cancel$" };
        DeveloperOperationPlan cancelPlan = await DeveloperLifecycle.PlanOperationAsync(cancel, ct);
        WorkflowOperation running = await DeveloperLifecycle.ExecuteOperationAsync(host.Client,
            new(cancel, cancelPlan.ManifestSha256, "cancel-test") { HistoryPolicy = new(HistoryPolicyMode.Disabled) }, ct);
        string ready = Path.Combine(root, cancelPlan.Variants[0].BuildDirectory, "p5-cancel.ready");
        while (!File.Exists(ready))
        {
            OperationOverview current = await running.GetOverviewAsync(ct);
            Check(!current.IsFinalized && current.IsScheduledOnHost == true, "cancel fixture is scheduled");
            await Task.Delay(25, ct);
        }
        CancellationReceipt receipt = await running.RequestCancellationAsync(0, ct);
        Check(receipt.Requested, "durable cancellation intent");
        Check((await host.WaitForOperationAsync(running.OperationId, ct)).Operation.State == "canceled", "terminal cancellation");
        Check((await running.GetReportAsync(ct)).Revision.Length == 64, "canceled report");
        Check((await running.InspectRecoveryAsync(ct)).OperationId == running.OperationId, "recovery identity");
    }

    await Console.Out.WriteLineAsync("Developer API smoke passed");
}
finally
{
    temporary.Delete(recursive: true);
}

static void Check(bool condition, string description)
{
    if (!condition)
    {
        throw new InvalidOperationException("Developer API smoke failed: " + description);
    }
}

static async Task RejectAsync(Func<Task> action, string description)
{
    try
    {
        await action();
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new InvalidOperationException("Expected rejection: " + description);
}
