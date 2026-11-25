using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Developer;

public static partial class DeveloperLifecycle
{
    public static async Task<CreatedProject> CreateProjectAsync(CreateProjectRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(await ProjectScaffolding.CreateAsync(request, ct));
    }

    public static SdkCatalogDocument ListSdks() => JsonSerializer.Deserialize(
        JsonSerializer.SerializeToUtf8Bytes(ProjectSdkCatalog.Document, ProjectSdkCatalogJson.Default.SdkCatalogDocument),
        ProjectSdkCatalogJson.Default.SdkCatalogDocument)!;

    public static SdkDescriptor GetSdk(string id, string? version = null) => CopySdk(ProjectSdkCatalog.Resolve(id, version));

    public static async Task<SdkDescriptor> GetProjectSdkAsync(string directory, CancellationToken ct = default) =>
        CopySdk(await ProjectSdkResolution.ReadDescriptorAsync(directory, ct));

    private static SdkDescriptor CopySdk(SdkDescriptor sdk) => JsonSerializer.Deserialize(
        JsonSerializer.SerializeToUtf8Bytes(sdk, ProjectSdkCatalogJson.Default.SdkDescriptor),
        ProjectSdkCatalogJson.Default.SdkDescriptor)!;

    public static async Task<ProjectLockPlan> PlanSdkAsync(string directory, CancellationToken ct = default) =>
        SdkLock(await ProjectSdkResolution.EvaluateAsync(Path.GetFullPath(directory), ct));

    public static async Task<ProjectLockPlan> LockSdkAsync(string directory, string approvalSha256, CancellationToken ct = default)
    {
        string root = Path.GetFullPath(directory);
        byte[] bytes = await ProjectSdkResolution.EvaluateAsync(root, ct);
        if (approvalSha256 != ProjectSdkResolution.Digest(bytes))
        {
            throw new InvalidDataException("SDK plan changed after review. Review a new plan.");
        }
        await ProjectSdkResolution.PublishAsync(root, bytes, ct);
        return await VerifySdkAsync(root, ct);
    }

    public static async Task<ProjectLockPlan> VerifySdkAsync(string directory, CancellationToken ct = default) =>
        SdkLock(await ProjectSdkResolution.ReadVerifiedAsync(Path.GetFullPath(directory), ct));

    public static async Task WriteManagedSdkWorkflowAsync(string directory, CancellationToken ct = default)
    {
        string root = Path.GetFullPath(directory);
        await ProjectSdkResolution.WriteManagedWorkflowAsync(root, await ProjectSdkResolution.ReadVerifiedAsync(root, ct), ct);
    }

    public static Task<JsonElement> ListConfigurationProfilesAsync(string directory, CancellationToken ct = default) =>
        ProjectConfiguration.ReadProfilesAsync(Path.GetFullPath(directory), ct);

    public static async Task<ConfigurationPlan> PlanConfigurationAsync(string directory, ConfigurationSelection selection,
        CancellationToken ct = default) => (await EvaluateConfigurationAsync(Path.GetFullPath(directory), selection, ct)).Plan;

    public static async Task<ConfigurationPlan> LockConfigurationAsync(string directory, ConfigurationSelection selection,
        string approvalSha256, CancellationToken ct = default)
    {
        string root = Path.GetFullPath(directory);
        (_, ConfigurationPlan plan) = await EvaluateConfigurationAsync(root, selection, ct);
        if (approvalSha256 != plan.Lock.Sha256)
        {
            throw new InvalidDataException("stale-configuration-approval: review the current plan before publishing.");
        }
        await ProjectConfiguration.PublishAsync(root, plan.Lock.Sha256, plan.Lock.Json.ToArray(), ct);
        return await VerifyConfigurationAsync(root, selection, ct);
    }

    public static async Task<ConfigurationPlan> VerifyConfigurationAsync(string directory, ConfigurationSelection selection,
        CancellationToken ct = default) => (await VerifyConfigurationEvidenceAsync(Path.GetFullPath(directory), selection, ct)).Plan;

    public static async Task<WorkflowDocument> CreateConfigurationWorkflowAsync(string directory, ConfigurationSelection selection,
        string? variant = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        (VariantLock evidence, ConfigurationPlan plan) = await VerifyConfigurationEvidenceAsync(Path.GetFullPath(directory), selection, ct);
        return ProjectConfiguration.CreateWorkflow(evidence, plan.Lock.Sha256, variant ?? selection.Variant);
    }

    private static ProjectLockPlan SdkLock(byte[] bytes) =>
        new(ProjectSdkResolution.Digest(bytes), BuildVariantConfiguration.SdkLock, bytes);

    private static async Task<(VariantLock Evidence, ConfigurationPlan Plan)> EvaluateConfigurationAsync(
        string root, ConfigurationSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Matrix && selection.Profile is not null)
        {
            throw new ArgumentException("Choose either a profile or a matrix.", nameof(selection));
        }
        VariantLock evidence = await ProjectConfiguration.EvaluateAsync(root, selection, ct);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, VariantJson.Default.VariantLock);
        string digest = BuildVariantConfiguration.Hash(bytes);
        ProjectVariant[] variants = [.. evidence.Variants.Select(variant =>
            new ProjectVariant(variant.Name, variant.Id, BuildVariantConfiguration.BuildDirectory(variant),
                variant.Effective.Configuration.ToString(), variant.Environment.Generator, variant.Environment.EnvironmentLockSha256,
                variant.Effective.Testing))];
        return (evidence, new(new(digest, BuildVariantConfiguration.LockPath(digest), bytes), Array.AsReadOnly(variants)));
    }

    private static async Task<(VariantLock Evidence, ConfigurationPlan Plan)> VerifyConfigurationEvidenceAsync(
        string root, ConfigurationSelection selection, CancellationToken ct)
    {
        (VariantLock evidence, ConfigurationPlan plan) = await EvaluateConfigurationAsync(root, selection, ct);
        return await HasLockAsync(root, plan.Lock, ct)
            ? (evidence, plan)
            : throw new InvalidDataException("configuration-lock-unavailable: run restore --plan, then approve the configuration plan.");
    }

    private static async Task<bool> HasLockAsync(string root, ProjectLockPlan plan, CancellationToken ct)
    {
        string path = BuildVariantConfiguration.CheckedPath(root, plan.Path);
        return File.Exists(path) && (await BuildVariantConfiguration.ReadBoundedAsync(path, ct)).AsSpan().SequenceEqual(plan.Json.Span);
    }
}
