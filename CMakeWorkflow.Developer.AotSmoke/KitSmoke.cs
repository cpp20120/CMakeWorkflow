using System.Globalization;
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Transport;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Developer;
using CMakeWorkflow.Scripting;

internal static class KitSmoke
{
    internal static DeveloperOperationRequest Test(string directory, string? kit = null) =>
        new(new(directory), DeveloperOperationKind.Test) { Configuration = new() { Kit = kit } };

    internal static async Task ExerciseAsync(string id, CancellationToken ct)
    {
        DirectoryInfo parent = Directory.CreateTempSubdirectory("cmwf-kit-");
        try
        {
            string path = OperatingSystem.IsMacOS() && parent.FullName.StartsWith("/var/", StringComparison.Ordinal)
                ? "/private" + parent.FullName : parent.FullName;
            string root = (await DeveloperLifecycle.CreateProjectAsync(new("runtime", "KitSample", Path.Combine(path, "project")), ct)).Directory;
            string sdk = (await DeveloperLifecycle.VerifySdkAsync(root, ct)).Sha256;
            string legacy = (await DeveloperLifecycle.VerifyConfigurationAsync(root, new(), ct)).Lock.Sha256;
            Check((await DeveloperLifecycle.ListKitsAsync(root, ct)).Any(kit => kit.Id == id), "catalog");
            await DeveloperLifecycle.SelectKitAsync(root, id, ct);
            ProjectInspection restore = await DeveloperLifecycle.PlanRestoreAsync(new(new(root)), ct);
            Check(restore.SelectedKit is { Origin: KitSelectionOrigin.Active } && restore.Kit is { Approved: false } &&
                restore.Diagnostics.Any(diagnostic => diagnostic.Code == DeveloperDiagnosticCode.KitLockApprovalRequired), "selection and diagnostics");
            EngineeringKitPlan kit = restore.Kit!;
            _ = await DeveloperLifecycle.LockKitAsync(root, id, kit.Lock.Sha256, ct);
            Check((await DeveloperLifecycle.VerifyKitAsync(root, id, ct)).Approved, "Kit approval");
            ConfigurationSelection selection = new() { Kit = id };
            ConfigurationPlan configuration = await DeveloperLifecycle.PlanConfigurationAsync(root, selection, ct);
            _ = await DeveloperLifecycle.LockConfigurationAsync(root, selection, configuration.Lock.Sha256, ct);
            DeveloperOperationRequest operation = Test(root);
            DeveloperOperationPlan plan = await DeveloperLifecycle.PlanOperationAsync(operation, ct);
            Check(plan.KitLockSha256 == kit.Lock.Sha256 && plan.ConfigurationLock.Sha256 != legacy, "variant evidence");
            await DeveloperLifecycle.ClearKitSelectionAsync(root, ct);
            Check((await DeveloperLifecycle.VerifyConfigurationAsync(root, new(), ct)).Lock.Sha256 == legacy, "v1 evidence");
            Check((await DeveloperLifecycle.VerifySdkAsync(root, ct)).Sha256 == sdk, "SDK evidence");

            WorkflowManifest snapshot = await ReviewedEngineeringWorkflow.MaterializeAsync(operation, plan, ct);
            Check(snapshot.Sha256 == plan.ManifestSha256 &&
                snapshot.CopyBytes().AsSpan().SequenceEqual(plan.ManifestJson.Span), "exact Scripting snapshot");
            string configurationPath = Path.Combine(root, configuration.Lock.Path);
            byte[] approved = await File.ReadAllBytesAsync(configurationPath, ct);
            try
            {
                await File.AppendAllTextAsync(configurationPath, "\n", ct);
                try
                {
                    _ = await ReviewedEngineeringWorkflow.MaterializeAsync(operation, plan, ct);
                    throw new InvalidOperationException("Changed configuration evidence was accepted.");
                }
                catch (InvalidDataException) { }
            }
            finally
            {
                await File.WriteAllBytesAsync(configurationPath, approved, ct);
            }

            await using LocalDeveloperHost host = await LocalDeveloperHost.OpenAsync(root, ct);
            PreparedWorkflow prepared = await ReviewedEngineeringWorkflow.PrepareAsync(host.Client, operation, plan,
                "out/preflight-only", ct: ct);
            Check(prepared.ManifestSha256 == plan.ManifestSha256 &&
                (await ReviewedEngineeringWorkflow.PreflightAsync(host.Client, prepared, ct: ct)).Valid,
                "reviewed Kit manifest survives changing active selection");
            WorkflowOperation run = await DeveloperLifecycle.ExecuteOperationAsync(host.Client,
                new(operation, plan.ManifestSha256, "kit-smoke")
                {
                    ReviewedPlan = plan,
                    HistoryPolicy = new(HistoryPolicyMode.Disabled)
                }, ct);
            OperationOverview overview = await host.WaitForOperationAsync(run.OperationId, ct);
            Check(overview.IsFinalized && overview.Operation.State == "succeeded" &&
                overview.Nodes.Any(node => node.Kind == "ctest" && node.Status == "succeeded"), "build and CTest");
            OperationReportSnapshot report = await run.GetReportAsync(ct);
            Check(report.Report.GetProperty("nodes").EnumerateArray().Any(node =>
                node.TryGetProperty("kitLockSha256", out System.Text.Json.JsonElement digest) && digest.GetString() == kit.Lock.Sha256),
                "report Kit identity");
            await Console.Out.WriteLineAsync("Kit API smoke passed: " + id);
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    internal static async Task RunRemoteAsync(string local, string remote, string requestId, CancellationToken ct)
    {
        DeveloperOperationRequest request = Test(local, "linux-gcc");
        DeveloperOperationPlan plan = await DeveloperLifecycle.PlanOperationAsync(request, ct);
        static string Required(string suffix) => Environment.GetEnvironmentVariable("CMWF_SSHNET_" + suffix) ??
            throw new InvalidOperationException("Missing SSH fixture setting " + suffix);
        SshNetHostOptions connection = new(Required("HOST"), Required("USER"), Required("KEY"), Required("PIN"),
            Required("ENDPOINT"), remote)
        {
            Port = int.Parse(Required("PORT"), CultureInfo.InvariantCulture),
            RemoteExecutable = Required("EXE")
        };
        await using CMakeWorkflowClient client = await CMakeWorkflowClient.ConnectSshNetAsync(connection, ct);
        PreparedWorkflow staged = await ReviewedEngineeringWorkflow.PrepareAsync(client, request, plan,
            remote + "/out/" + requestId, ct: ct);
        WorkflowPreflightResult preflight = await ReviewedEngineeringWorkflow.PreflightAsync(client, staged,
            new(HistoryPolicyMode.Disabled), ct: ct);
        WorkflowOperation operation = await client.Workflows.StartAsync(preflight.CreateStartOptions(requestId), ct);
        OperationSnapshot snapshot = await operation.WaitForCompletionAsync(ct: ct);
        Check(snapshot.State == "succeeded", "remote build and test");
        OperationReportSnapshot report = await operation.GetReportAsync(ct);
        Check(report.Report.GetProperty("toolchains")[0].GetProperty("environmentProfileId").GetString() == "linux-ambient-gcc",
            "remote Host compiler");
        await Console.Out.WriteLineAsync("Kit SSH smoke passed");
    }

    internal static async Task RecoverAsync(string directory, string operationId, CancellationToken ct)
    {
        await using LocalDeveloperHost host = await LocalDeveloperHost.OpenAsync(directory, ct);
        WorkflowOperation operation = host.Client.Operations.GetHandle(operationId);
        RecoverySnapshot before = await operation.InspectRecoveryAsync(ct);
        Check(before.CheckpointSha256 is not null && before.TerminalState is null, "interrupted Kit checkpoint");
        RecoverySnapshot reviewed = await operation.ReconcileAsync(before.Revision, "needs_attention",
            "The test stopped the Host at the pre-dispatch fault barrier.", true, ct);
        RecoverySnapshot admitted = await operation.AdmitResumeAsync(reviewed.Revision, true, ct);
        _ = await operation.ExecuteResumeAsync(admitted.Revision, ct);
        OperationOverview outcome = await host.WaitForOperationAsync(operationId, ct);
        Check(outcome.IsFinalized && outcome.Operation.State == "succeeded", "resumed Kit operation");
        OperationReportSnapshot report = await operation.GetReportAsync(ct);
        Check(report.Report.GetProperty("toolchains")[0].GetProperty("environmentProfileId").GetString() == "linux-ambient-gcc",
            "original Kit after active selection change");
        await Console.Out.WriteLineAsync("Kit recovery smoke passed");
    }

    private static void Check(bool result, string description)
    {
        if (!result)
        {
            throw new InvalidOperationException("Kit smoke failed: " + description);
        }
    }
}
