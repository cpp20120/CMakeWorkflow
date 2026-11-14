using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Client.Protocol;
using CMakeWorkflow.Client.Provisioning;
using CMakeWorkflow.Client.Workflows;
using StreamJsonRpc;
using CMakeWorkflowClient = CMakeWorkflow.Client.CMakeWorkflowClient;

namespace CMakeWorkflow.Host.Tests.Application;

internal static class P3ApiTests
{
    private static void Check(bool valid, string text)
    {
        if (!valid)
        {
            throw new System.Diagnostics.UnreachableException("P3 API: " + text);
        }
    }

    internal static async Task RunAsync(string endpoint, string workspace, string manifest,
        string digest, int nodeCount, CancellationToken ct)
    {
        await using CMakeWorkflowClient sdk = await CMakeWorkflowClient.ConnectAsync(new(endpoint, workspace), ct);
        Check(MemoryExtensions.Contains(sdk.Host.Capabilities, "workflow/preflight") &&
              MemoryExtensions.Contains(sdk.Host.Capabilities, "tooling/doctor") &&
              MemoryExtensions.Contains(sdk.Host.Capabilities, "tooling/planFix") &&
              MemoryExtensions.Contains(sdk.Host.Capabilities, "tooling/applyFix"), "advertised capabilities");

        string nonexistentOutput = Path.Combine(workspace, "p3-preflight-output");
        WorkflowPreflightResult result = await sdk.Workflows.PreflightAsync(
            new(manifest, digest, 2, nonexistentOutput), ct);
        Check(result.SchemaVersion == 1 && result.Jobs == 2 &&
              result.ManifestSha256 == digest && result.Nodes.Count == nodeCount,
            "manifest snapshot and typed plan");
        Check(!Directory.Exists(nonexistentOutput), "preflight created output directory");
        Check(result.Nodes.All(n => !string.IsNullOrWhiteSpace(n.Id) &&
                                    n.Dependencies is not null && n.Resources is not null &&
                                    !string.IsNullOrWhiteSpace(n.BuildDirectory)),
            "preflight node DTOs");
        if (result.Valid)
        {
            StartWorkflowOptions request = result.CreateStartOptions("p3-" + Guid.NewGuid().ToString("N"));
            Check(request.Manifest == result.Manifest && request.ManifestSha256 == digest &&
                  request.ResultsDirectory == nonexistentOutput && request.Jobs == 2,
                "explicit start handoff");
        }
        else
        {
            try
            {
                _ = result.CreateStartOptions("should-not-start");
                throw new System.Diagnostics.UnreachableException("Invalid preflight result was accepted for start");
            }
            catch (InvalidOperationException) { }
        }
        Check(!Directory.Exists(nonexistentOutput), "create start options performed dispatch");

        BuildContextObservation observed = await sdk.Targets.InspectContextAsync(result.Nodes[0].BuildDirectory, ct);
        Check(observed.BuildDirectory == result.Nodes[0].BuildDirectory &&
              observed.Targets.Any(target => target.Name == "worker"), "typed build context round trip");
        TargetResolution resolution = await sdk.Targets.ResolveAsync(observed.BuildDirectory, "worker", ct: ct);
        Check(resolution.SchemaVersion == 1 && resolution.Target == "worker" &&
              Path.IsPathFullyQualified(resolution.Executable), "typed target resolution round trip");

        string selectedId = result.Nodes.Last().Id;
        WorkflowPreflightResult selected = await sdk.Workflows.PreflightAsync(
            new WorkflowPreflightOptions(manifest, digest, 2, nonexistentOutput)
            {
                Only = [selectedId, selectedId],
                HistoryPolicy = new(HistoryPolicyMode.Default)
            }, ct);
        Check(selected.Only is { Count: 1 } && selected.Only[0] == selectedId &&
              selected.Nodes.Any(node => node.Id == selectedId) &&
              selected.Nodes.Count <= result.Nodes.Count,
            "selected node preflight round trip");
        if (selected.Valid)
        {
            StartWorkflowOptions start = selected.CreateStartOptions("p3-selected");
            Check(start.Only is { Count: 1 } && start.Only[0] == selectedId &&
                  start.HistoryPolicy?.Mode == HistoryPolicyMode.Default,
                "selected node start handoff");
        }

        bool refused = false;
        try
        {
            _ = await sdk.Workflows.PreflightAsync(
                new(manifest, new string('0', 64), 2, nonexistentOutput), ct);
        }
        catch (CMakeWorkflowRpcRefusalException ex) when (ex.Code == "manifest_changed")
        {
            refused = true;
        }
        Check(refused, "expected stable manifest_changed refusal");
        Check(!Directory.Exists(nonexistentOutput), "digest refusal created output directory");

        refused = false;
        try
        {
            _ = await sdk.Workflows.PreflightAsync(new(manifest, digest, 2,
                Path.Combine(Path.GetDirectoryName(workspace)!, "outside-preflight")), ct);
        }
        catch (CMakeWorkflowRpcRefusalException ex) when (ex.Code == "path_not_allowed")
        {
            refused = true;
        }
        Check(refused, "outside workspace was not rejected");

        // Doctor and PlanFix inspect only; never call ApplyFix for a real plan
        // during an API smoke test (it may install packages on an actual host).
        HostToolingPlan doctor = await sdk.Provisioning.DoctorAsync(new(
            "p3-doctor-" + Guid.NewGuid().ToString("N"), ["cmake"]), ct);
        Check(doctor.SchemaVersion == 1 && doctor.Profiles.Contains("cmake") &&
              doctor.Tools is not null, "Doctor typed serialization");
        HostFixPlan plan = await sdk.Provisioning.PlanFixAsync(new(["cmake"],
            PackageManager: HostPackageManagerKind.None), ct);
        Check(plan.SchemaVersion == 1 && !string.IsNullOrWhiteSpace(plan.PlanId) &&
              plan.Plan?.PackageManager == HostPackageManagerKind.None,
            "Fix-plan typed serialization and enum converter");
        // The SDK can invoke applyFix, but a nonexistent plan must fail before
        // any installation. The Host's own FixTests cover real one-shot application.
        bool invalidPlanRejected = false;
        try
        {
            _ = await sdk.Provisioning.ApplyFixAsync(new("p3-apply-refusal", "no-such-plan"), ct);
        }
        catch (RemoteInvocationException) { invalidPlanRejected = true; }
        catch (CMakeWorkflowRpcRefusalException) { invalidPlanRejected = true; }
        Check(invalidPlanRejected, "invalid plan was accepted for installation");
        Check(!Directory.Exists(nonexistentOutput), "Doctor/Fix inspection dispatched workflow");
        Console.WriteLine("P3 API wire checks passed: preflight, digest, isolation, Doctor, planFix, explicit apply.");
    }
}