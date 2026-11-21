using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Client.Protocol;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Client.Experiments;

/// <summary>Runs and observes a prepared Git experiment on the connected Linux Host.</summary>
public sealed class ExperimentService(CMakeWorkflowClient client)
{
    /// <summary>Inspects CPU capacity and optionally resolves the installed managed Clang toolchain.</summary>
    public Task<ExperimentHostResources> InspectResourcesAsync(bool managedClang = false, CancellationToken ct = default)
    {
        client.RequireCapability("experiment/resources");
        return RpcRefusals.TranslateAsync(() => client.Proxy.GetExperimentResourcesAsync(new(managedClang), ct));
    }

    /// <summary>Rechecks the architecture, minimum CPU capacity and managed toolchain of a selected plan.</summary>
    public async Task VerifyPlacementAsync(ExperimentPlacement placement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            ExperimentHostResources expected = placement.Resources;
            ExperimentHostResources current = await InspectResourcesAsync(
                expected.ManagedToolchainFingerprint is not null, deadline.Token);
            if (current.Architecture != expected.Architecture || current.AvailableProcessors < placement.MinProcessors ||
                current.ManagedToolchainFingerprint != expected.ManagedToolchainFingerprint)
            {
                throw new InvalidOperationException($"Host {placement.Name} no longer meets the approved resource requirements. Create a new plan.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out checking resources for Host {placement.Name}.");
        }
    }

    /// <summary>Opens a schema-v2 controller plan and checks it against the caller-approved ID.</summary>
    public static async Task<PreparedExperimentPlan> ReadPlanAsync(string planPath, string approvedId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planPath);
        string fullPath = Path.GetFullPath(planPath);
        byte[] bytes = await File.ReadAllBytesAsync(fullPath, ct);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != approvedId)
        {
            throw new InvalidDataException("Experiment plan does not match its approved SHA-256.");
        }
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 2)
        {
            throw new InvalidDataException("The SDK requires a schema-v2 experiment plan.");
        }
        ExperimentSourceBundle bundle = root.GetProperty("bundle").Deserialize(ClientJson.Default.ExperimentSourceBundle)
            ?? throw new InvalidDataException("Missing experiment source bundle.");
        ExperimentStageRequest stage = new(approvedId, RequiredString(root, "sourceSnapshotId"),
            RequiredString(root, "manifestSha256"), RequiredString(root, "manifest"), [])
        {
            Bundle = bundle
        };
        ExperimentPlacement? placement = root.TryGetProperty("placement", out JsonElement selection)
            ? selection.Deserialize(ClientJson.Default.ExperimentPlacement)
            : null;
        return new(RequiredString(root, "repository"), RequiredString(root, "commit"),
            RequiredString(root.GetProperty("host"), "workspace"),
            Path.Combine(Path.GetDirectoryName(fullPath)!, "source.zip"), stage, placement,
            root.TryGetProperty("dependencyPlanSha256", out JsonElement dependencies) ? dependencies.GetString() : null);
    }

    /// <summary>Stages the approved source and inspects or explicitly applies its dependency plan.</summary>
    public async Task<ExperimentDependencyResult> PrepareDependenciesAsync(PreparedExperimentPlan plan,
        string? approvalSha256 = null, CancellationToken ct = default)
    {
        ValidateWorkspace(plan);
        client.RequireCapability("experiment/dependencies");
        if (plan.DependencyPlanSha256 is null)
        {
            throw new InvalidOperationException("This experiment does not opt into vcpkg.");
        }
        if (plan.Placement is not null)
        {
            await VerifyPlacementAsync(plan.Placement, ct);
        }
        _ = await StageAsync(plan.Stage, plan.ArchivePath, ct);
        return await DependenciesAsync(plan, approvalSha256, false, ct);
    }

    /// <summary>Checks that the staged plan has a valid installed package receipt.</summary>
    public async Task VerifyDependenciesAsync(PreparedExperimentPlan plan, CancellationToken ct = default)
    {
        ValidateWorkspace(plan);
        _ = await DependenciesAsync(plan, null, true, ct);
    }

    private async Task<ExperimentDependencyResult> DependenciesAsync(PreparedExperimentPlan plan, string? approval,
        bool verify, CancellationToken ct)
    {
        client.RequireCapability("experiment/dependencies");
        string expected = plan.DependencyPlanSha256 ??
            throw new InvalidOperationException("This experiment does not opt into vcpkg.");
        ExperimentDependencyResult result = await RpcRefusals.TranslateAsync(() => client.Proxy.ExperimentDependenciesAsync(
            new(plan.Id, plan.Commit, plan.SourceSnapshotId, plan.ManifestSha256, expected, approval, verify), ct));
        return result.PlanSha256 == expected &&
               Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.Plan))) == expected && (!(verify || approval is not null) || result.InstalledTreeSha256 is not null)
            ? result : throw new InvalidDataException("Host dependency evidence differs from the experiment plan.");
    }

    /// <summary>Stages and explicitly authorizes execution of the prepared plan on the connected Host.</summary>
    /// <remarks>Call only on an isolated Host the caller has selected. Use FindAsync after an uncertain admission.</remarks>
    public async Task<WorkflowOperation> StartApprovedAsync(PreparedExperimentPlan plan, CancellationToken ct = default)
    {
        ValidateWorkspace(plan);
        if (plan.Placement is not null)
        {
            await VerifyPlacementAsync(plan.Placement, ct);
        }
        string manifest = plan.Workspace + $"/.cmakeworkflow/experiments/{plan.Id}/workflow.json";
        ExperimentStageReceipt staged = await StageAsync(plan.Stage, plan.ArchivePath, ct);
        if (plan.DependencyPlanSha256 is not null)
        {
            await VerifyDependenciesAsync(plan, ct);
        }
        StartWorkflowOptions options = new(plan.RequestId, manifest, plan.ManifestSha256, 1,
            plan.Workspace + $"/.cmakeworkflow/experiment-sdk-results/{plan.Id}")
        {
            SourceSnapshotId = plan.SourceSnapshotId,
            HistoryPolicy = new(HistoryPolicyMode.Default)
        };
        return staged == new ExperimentStageReceipt(manifest, plan.ManifestSha256, plan.SourceSnapshotId)
            ? await client.Workflows.StartAsync(options, ct)
            : throw new InvalidDataException("Host staged identity does not match the approved plan.");
    }

    /// <summary>Finds the operation for a durable SDK request after reconnection without starting execution.</summary>
    public async Task<WorkflowOperation> FindAsync(PreparedExperimentPlan plan, CancellationToken ct = default)
    {
        ValidateWorkspace(plan);
        OperationSnapshot operation = await client.Operations.FindAsync(plan.RequestId, ct);
        return string.Equals(operation.ManifestSha256, plan.ManifestSha256, StringComparison.OrdinalIgnoreCase)
            ? client.Operations.GetHandle(operation.OperationId)
            : throw new InvalidDataException("Recovered operation does not match the approved plan.");
    }

    private void ValidateWorkspace(PreparedExperimentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (client.Host.WorkspaceDirectory != plan.Workspace)
        {
            throw new InvalidDataException("Initialized Host workspace differs from the approved plan.");
        }
    }

    private static string RequiredString(JsonElement element, string property) => element.GetProperty(property).GetString()
        ?? throw new InvalidDataException($"Missing experiment plan property {property}.");

    /// <summary>Uploads and publishes a SHA-256-bound source archive and workflow manifest.</summary>
    /// <remarks>Call again with the same inputs after reconnecting to resume an interrupted upload.</remarks>
    public async Task<ExperimentStageReceipt> StageAsync(ExperimentStageRequest request, string archivePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ExperimentSourceBundle bundle = request.Bundle ?? throw new ArgumentException("Source bundle metadata is required.", nameof(request));
        client.RequireCapability("experiment/sourceChunk");
        await using FileStream archive = File.OpenRead(archivePath);
        if (bundle.Size is <= 0 or > ExperimentSourceBundle.MaximumArchiveBytes || archive.Length != bundle.Size ||
            Convert.ToHexString(await SHA256.HashDataAsync(archive, ct)) != bundle.Sha256)
        {
            throw new InvalidDataException("Local source archive does not match the approved plan.");
        }

        ExperimentSourceChunk chunk = new(request.ExperimentId, bundle.Sha256, bundle.Size, 0, ReadOnlyMemory<byte>.Empty);
        long offset = await WriteChunkAsync(chunk, ct);
        while (offset >= 0 && offset < bundle.Size)
        {
            archive.Position = offset;
            byte[] data = new byte[(int)Math.Min(ExperimentSourceBundle.ChunkBytes, bundle.Size - offset)];
            await archive.ReadExactlyAsync(data, ct);
            long next = await WriteChunkAsync(chunk with { Offset = offset, Data = data }, ct);
            if (next <= offset)
            {
                throw new InvalidDataException("Host source upload did not advance.");
            }
            offset = next;
        }
        return offset == bundle.Size
            ? await client.Workflows.StageExperimentAsync(request, ct)
            : throw new InvalidDataException("Host returned an invalid source upload offset.");
    }

    private Task<long> WriteChunkAsync(ExperimentSourceChunk chunk, CancellationToken ct) =>
        RpcRefusals.TranslateAsync(() => client.Proxy.WriteExperimentSourceChunkAsync(chunk, ct));
}
