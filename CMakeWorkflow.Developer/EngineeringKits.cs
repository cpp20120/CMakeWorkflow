using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Contracts.Kits;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Developer;

public enum KitSelectionOrigin { Explicit, Active }

public sealed record SelectedEngineeringKit(string Id, KitSelectionOrigin Origin);

public sealed record EngineeringKitPlan(EngineeringKitDefinition Definition, string DefinitionSha256,
    EngineeringKitLock Environment, ProjectLockPlan Lock, bool Approved)
{
    public string? EnvironmentApprovalCommand { get; init; }
}

internal sealed record ProjectKitCatalog(int SchemaVersion, IReadOnlyList<EngineeringKitDefinition> Kits);
internal sealed record ActiveKitSelection(int SchemaVersion, string Id);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(ProjectKitCatalog))]
[JsonSerializable(typeof(ActiveKitSelection))]
internal sealed partial class ProjectKitJson : JsonSerializerContext;

public static partial class DeveloperLifecycle
{
    private const string ActiveKitPath = ".cmakeworkflow/active-kit.json";

    public static async Task<string?> FindKitProjectAsync(ProjectLocation location, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        try
        {
            return (await DiscoverProjectAsync(location, ct)).Directory;
        }
        catch (FileNotFoundException) when (location.ProjectDirectory is null)
        {
            return null;
        }
    }

    public static async Task<IReadOnlyList<EngineeringKitDefinition>> ListKitsAsync(string? projectDirectory = null,
        CancellationToken ct = default)
    {
        Dictionary<string, EngineeringKitDefinition> kits = new(StringComparer.Ordinal);
        foreach (EngineeringKitDefinition definition in new[]
        {
            BuiltIn("linux-clang", "Managed Linux Clang", KitProvider.ManagedClang, "Ninja"),
            BuiltIn("linux-clang-multi", "Managed Linux Clang Multi-Config", KitProvider.ManagedClang, "Ninja Multi-Config"),
            BuiltIn("linux-gcc", "Linux system GCC", KitProvider.AmbientGcc, "Ninja"),
            BuiltIn("linux-system-clang", "Linux system Clang", KitProvider.AmbientClang, "Ninja")
        })
        {
            kits.Add(definition.Id, definition);
        }

        if (projectDirectory is not null)
        {
            string path = BuildVariantConfiguration.CheckedPath(Path.GetFullPath(projectDirectory), "cmwf.kits.json");
            if (File.Exists(path))
            {
                ProjectKitCatalog? catalog = JsonSerializer.Deserialize(await BuildVariantConfiguration.ReadBoundedAsync(path, ct),
                    ProjectKitJson.Default.ProjectKitCatalog);
                if (catalog is not { SchemaVersion: 1, Kits.Count: <= 32 })
                {
                    throw new InvalidDataException("invalid-kit-catalog: expected schema 1 and at most 32 Kits.");
                }
                foreach (EngineeringKitDefinition definition in catalog.Kits)
                {
                    ValidateDefinition(definition);
                    if (!kits.TryAdd(definition.Id, definition))
                    {
                        throw new InvalidDataException("duplicate-kit: " + definition.Id);
                    }
                }
            }
        }

        return Array.AsReadOnly(kits.Values.OrderBy(kit => kit.Id, StringComparer.Ordinal).ToArray());
    }

    public static async Task<EngineeringKitDefinition> GetKitAsync(string id, string? projectDirectory = null,
        CancellationToken ct = default) =>
        (await ListKitsAsync(projectDirectory, ct)).SingleOrDefault(kit => kit.Id == id)
        ?? throw new InvalidDataException("unknown-kit: " + id);

    public static async Task<SelectedEngineeringKit?> GetSelectedKitAsync(string directory, string? explicitKit = null,
        CancellationToken ct = default)
    {
        if (explicitKit is not null)
        {
            return new(explicitKit, KitSelectionOrigin.Explicit);
        }

        string path = BuildVariantConfiguration.CheckedPath(Path.GetFullPath(directory), ActiveKitPath);
        byte[] bytes;
        try
        {
            bytes = await BuildVariantConfiguration.ReadBoundedAsync(path, ct);
        }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException)
        {
            return null;
        }

        ActiveKitSelection? state = JsonSerializer.Deserialize(bytes, ProjectKitJson.Default.ActiveKitSelection);
        return state is { SchemaVersion: 1, Id: { } id } && ValidKitId(id)
            ? new(id, KitSelectionOrigin.Active) : throw new InvalidDataException("invalid-active-kit");
    }

    public static async Task SelectKitAsync(string directory, string id, CancellationToken ct = default)
    {
        string root = Path.GetFullPath(directory);
        _ = await DiscoverProjectAsync(new(root) { ProjectDirectory = root }, ct);
        _ = await GetKitAsync(id, root, ct);
        string path = BuildVariantConfiguration.CheckedPath(root, ActiveKitPath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string staging = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ActiveKitSelection(1, id), ProjectKitJson.Default.ActiveKitSelection);
            await File.WriteAllBytesAsync(staging, bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    public static async Task ClearKitSelectionAsync(string directory, CancellationToken ct = default)
    {
        string root = Path.GetFullPath(directory);
        _ = await DiscoverProjectAsync(new(root) { ProjectDirectory = root }, ct);
        string path = BuildVariantConfiguration.CheckedPath(root, ActiveKitPath);
        if (Directory.Exists(Path.GetDirectoryName(path)))
        {
            File.Delete(path);
        }
    }

    public static async Task<EngineeringKitPlan> PlanKitAsync(string directory, string id, CancellationToken ct = default)
    {
        string root = Path.GetFullPath(directory);
        using JsonDocument sdk = JsonDocument.Parse(await ProjectSdkResolution.ReadVerifiedAsync(root, ct));
        return await PlanKitCoreAsync(root, id, sdk.RootElement, ct);
    }

    public static async Task<EngineeringKitPlan> LockKitAsync(string directory, string id, string approvalSha256,
        CancellationToken ct = default)
    {
        string root = Path.GetFullPath(directory);
        EngineeringKitPlan plan = await PlanKitAsync(root, id, ct);
        if (plan.Lock.Sha256 != approvalSha256)
        {
            throw new InvalidDataException("stale-kit-approval: review the current Kit plan first.");
        }
        await ProjectConfiguration.PublishEvidenceAsync(root, plan.Lock.Path, plan.Lock.Json.ToArray(), ct);
        _ = await EngineeringKitEvidence.ReadAsync(root, plan.Lock.Sha256, ct);
        return plan with { Approved = true };
    }

    public static async Task<EngineeringKitPlan> VerifyKitAsync(string directory, string id, CancellationToken ct = default) =>
        RequireKitApproval(await PlanKitAsync(directory, id, ct));

    private static EngineeringKitPlan RequireKitApproval(EngineeringKitPlan plan) =>
        plan.Approved ? plan :
            throw new InvalidDataException($"kit-lock-unavailable: run kit lock {plan.Definition.Id} --plan-sha {plan.Lock.Sha256}.");

    internal static async Task<EngineeringKitPlan> PlanKitCoreAsync(string root, string id, JsonElement sdk, CancellationToken ct)
    {
        EngineeringKitDefinition definition = await GetKitAsync(id, root, ct);
        VariantEnvironment pin = BuildVariantConfiguration.SdkEnvironment(sdk.GetProperty("effective"));
        (string? environment, string? command) = ResolveKitEnvironment(definition, pin);
        EngineeringKitLock kit = new(1, definition.Provider, definition.ExecutionHost, definition.Target,
            definition.Backend, definition.CMake, environment);
        _ = EngineeringKitEvidence.Compose(kit, pin, ProjectSdkResolution.Descriptor(sdk));
        byte[] bytes = EngineeringKitEvidence.Serialize(kit);
        string digest = BuildVariantConfiguration.Hash(bytes);
        ProjectLockPlan evidence = new(digest, EngineeringKitEvidence.PathFor(digest), bytes);
        byte[] definitionBytes = JsonSerializer.SerializeToUtf8Bytes(definition, KitEvidenceJson.Default.EngineeringKitDefinition);
        return new(definition, BuildVariantConfiguration.Hash(definitionBytes), kit, evidence, await HasLockAsync(root, evidence, ct))
        {
            EnvironmentApprovalCommand = command
        };
    }

    private static (string? Digest, string? Command) ResolveKitEnvironment(EngineeringKitDefinition definition, VariantEnvironment pin)
    {
        if (definition.Provider != KitProvider.ManagedClang)
        {
            return (null, null);
        }

        if (pin.EnvironmentLockSha256 is { } digest && definition.Components.Count == 0)
        {
            return (digest, null);
        }

        EnvironmentRequirements requirements = ToolchainEnvironment.Native(definition.CMake.Generator,
            [.. definition.Components.Select(component => new ComponentRequirement(component.Id, component.Version))]);
        (ToolchainEnvironmentLock? environment, ToolchainDiagnostic? error) = ToolchainEnvironment.Resolve(requirements,
            ManagedComponentCatalog.Components);
        if (environment is null)
        {
            throw new InvalidDataException(error?.Code + ": " + error?.Message);
        }

        string hash = BuildVariantConfiguration.Hash(ToolchainEnvironment.Serialize(environment));
        string versions = string.Concat(definition.Components.Select(component => " --version " + component.Id + "=" + component.Version));
        return (hash, $"tooling lock --profile linux-clang --generator \"{definition.CMake.Generator}\"{versions} --approve-sha {hash}");
    }

    private static EngineeringKitDefinition BuiltIn(string id, string name, KitProvider provider, string generator) =>
        new(1, id, name, "cmake", provider, new("linux", "x64"), new("linux", "x64", "gnu"), new(generator));

    private static bool ValidKitId(string value) => value.Length is > 0 and <= 48 && char.IsAsciiLetterLower(value[0]) &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private static void ValidateDefinition(EngineeringKitDefinition definition)
    {
        if (definition is not { SchemaVersion: 1, Id: { } id, DisplayName.Length: > 0 and <= 100, Components.Count: <= 4 } ||
            !ValidKitId(id) || (definition.Provider != KitProvider.ManagedClang && definition.Components.Count != 0) ||
            definition.Components.Any(component => component is not { Id.Length: > 0 and <= 32, Version.Length: > 0 and <= 80 } ||
                !component.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
        {
            throw new InvalidDataException("invalid-kit-definition");
        }
        EngineeringKitEvidence.Validate(new(1, definition.Provider, definition.ExecutionHost, definition.Target,
            definition.Backend, definition.CMake, definition.Provider == KitProvider.ManagedClang ? new string('0', 64) : null));
    }
}
