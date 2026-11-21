using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Sources;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    /// <summary>Checks the staged experiment and plans, installs or verifies its pinned dependencies.</summary>
    public Task<ExperimentDependencyResult> ExperimentDependenciesAsync(ExperimentDependencyRequest request,
        CancellationToken ct) => ApplicationCallAsync(request, async (paths, token) =>
    {
        if (!OperatingSystem.IsLinux() || !IsStageHash(request.ExperimentId) || !IsStageHash(request.SourceSnapshotId) ||
            !IsStageHash(request.ManifestSha256) || (request.VerifyInstalled && request.ApprovalSha256 is not null))
        {
            throw new ArgumentException("Invalid experiment dependency request.");
        }
        ManagedEnvironmentLocks.RequireDigest(request.PlanSha256);
        if (request.ApprovalSha256 is { } approval &&
            !string.Equals(approval, request.PlanSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The dependency approval SHA-256 differs from the approved experiment plan.");
        }
        string stage = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "experiments", request.ExperimentId);
        string manifest = RequireStagePath(paths, Path.Combine(stage, "workflow.json"));
        string source = RequireStagePath(paths, Path.Combine(stage, "source"));
        string binding = RequireStagePath(paths, Path.Combine(stage, "source.sha256"));
        paths.Read(manifest);
        paths.Read(binding);
        byte[] workflowBytes = await File.ReadAllBytesAsync(manifest, token);
        if (Convert.ToHexString(SHA256.HashData(workflowBytes)) != request.ManifestSha256 ||
            await File.ReadAllTextAsync(binding, token) != request.SourceSnapshotId)
        {
            throw new InvalidDataException("Staged experiment identity differs from the dependency request.");
        }

        LoreSnapshotStore lore = new(SourceStorePaths.ForWorkspace(paths.WorkspaceDirectory));
        SnapshotManifest snapshot = await lore.InspectAsync(request.SourceSnapshotId, token);
        await WorkflowSourceSnapshotExecution.VerifyExistingMaterializationAsync(snapshot, source, token);
        VcpkgProjectPlan plan = await VcpkgProjects.PlanSnapshotAsync(source, request.Commit, request.SourceSnapshotId, token);
        byte[] bytes = VcpkgProjects.Serialize(plan);
        WorkflowDocument workflow = JsonSerializer.Deserialize(workflowBytes, WorkflowJson.Default.WorkflowDocument)
            ?? throw new InvalidDataException("Missing staged workflow.");
        if (ManagedEnvironmentLocks.Hash(bytes) != request.PlanSha256 || workflow.Contexts.Count != 1 ||
            workflow.Contexts.Values.Single().Toolchain is not { EnvironmentProfileId: "linux-managed-clang" } tooling ||
            tooling.VcpkgProjectSha256 != request.PlanSha256 || tooling.EnvironmentLockSha256 != plan.EnvironmentSha256)
        {
            throw new InvalidDataException("Staged workflow does not bind the approved dependency and environment plan.");
        }

        if (request.ApprovalSha256 is not null)
        {
            await VcpkgProjects.InstallAsync(plan, request.PlanSha256, source, token);
        }
        ManagedVcpkgInstallation? installed = request.VerifyInstalled || request.ApprovalSha256 is not null
            ? await VcpkgProjects.ResolveAsync(request.PlanSha256, request.SourceSnapshotId, source, token) : null;
        return new ExperimentDependencyResult(request.PlanSha256, Encoding.UTF8.GetString(bytes), installed?.PackageTreeSha256)
        {
            DownloadTreeSha256 = installed?.DownloadTreeSha256
        };
    }, ct);
    private static async Task ValidateSourceDependenciesAsync(WorkspaceReadPaths paths, WorkflowStartRequest request,
        CancellationToken ct)
    {
        paths.Read(request.Manifest);
        byte[] bytes = await File.ReadAllBytesAsync(request.Manifest, ct);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), request.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("stale_manifest");
        }

        // The package check uses the stage source only after matching it against the approved Lore snapshot.
        using JsonDocument json = JsonDocument.Parse(bytes);
        if (!json.RootElement.TryGetProperty("contexts", out JsonElement contexts) ||
            contexts.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (JsonProperty context in contexts.EnumerateObject())
        {
            if (context.Value.ValueKind != JsonValueKind.Object ||
                !context.Value.TryGetProperty("toolchain", out JsonElement toolchain) ||
                toolchain.ValueKind != JsonValueKind.Object ||
                !toolchain.TryGetProperty("vcpkgProjectSha256", out JsonElement dependency) ||
                dependency.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string root = (context.Value.TryGetProperty("sourceDirectory", out JsonElement directory) &&
                directory.ValueKind == JsonValueKind.String ? directory.GetString() : null)
                ?? throw new ApplicationRefusal("dependencies_invalid", "Source-bound vcpkg requires an explicit source directory.");
            try
            {
                string source = paths.Require(Path.GetFullPath(root, Path.GetDirectoryName(request.Manifest)!));
                string snapshotId = request.SourceSnapshotId!.ToUpperInvariant();
                LoreSnapshotStore lore = new(SourceStorePaths.ForWorkspace(paths.WorkspaceDirectory));
                await WorkflowSourceSnapshotExecution.VerifyExistingMaterializationAsync(
                    await lore.InspectAsync(snapshotId, ct), source, ct);
                string digest = dependency.GetString()!;
                string environment = await VcpkgProjects.VerifySnapshotInstallationAsync(digest, snapshotId, source, ct);
                if (Str(toolchain, "environmentLockSha256") != environment ||
                    Str(toolchain, "environmentProfileId") != "linux-managed-clang" ||
                    Str(toolchain, "hostArchitecture") is not (null or "x64") ||
                    Str(toolchain, "targetArchitecture") is not (null or "x64") ||
                    Str(toolchain, "generator") is not (null or "Ninja" or "Ninja Multi-Config"))
                {
                    throw new InvalidDataException("Workflow vcpkg binding differs from the approved managed environment.");
                }
            }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException)
            {
                throw new ApplicationRefusal("dependencies_invalid", error.Message);
            }
        }
    }
    private static string? Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

}
