using System.Text.Json;
using System.Text.Json.Serialization;

#if EXPERIMENT_SDK
namespace CMakeWorkflow.Client.Experiments;
#else
namespace CMakeWorkflow.Tooling.Tools;
#endif

internal sealed record VcpkgProjectPlan(int SchemaVersion, string Directory, string ManifestSha256,
    string? ConfigurationSha256, string EnvironmentSha256, ManagedToolRelease Executable, ManagedToolRelease Checkout)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Commit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceSnapshotId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetTriplet { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HostTriplet { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Registries { get; init; }
}

internal static class VcpkgProjectPlanning
{
    internal const string Triplet = "x64-linux-cmwf";

    internal static byte[] Serialize(VcpkgProjectPlan plan) =>
        JsonSerializer.SerializeToUtf8Bytes(plan, ManagedPlanJson.Default.VcpkgProjectPlan);

    internal static async Task<VcpkgProjectPlan> PlanAsync(string project, CancellationToken ct)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        return await ReadProjectAsync(root, null, null, ct);
    }

    internal static Task<VcpkgProjectPlan> PlanSnapshotAsync(string directory, string commit, string snapshotId,
        CancellationToken ct) => ReadProjectAsync(directory, commit, snapshotId, ct);

    internal static async Task<VcpkgProjectPlan> ReadProjectAsync(string root, string? commit, string? snapshotId,
        CancellationToken ct)
    {
        bool pinned = snapshotId is not null;
        if (pinned && (snapshotId is not { Length: 64 } || !snapshotId.All(char.IsAsciiHexDigitUpper) ||
            commit is not { Length: 40 } || !commit.All(char.IsAsciiHexDigitLower)))
        {
            throw new InvalidDataException("Expected an immutable Git commit and source snapshot.");
        }
        byte[] manifest = await ReadManifestAsync(Path.Combine(root, "vcpkg.json"), ct);
        using JsonDocument document = JsonDocument.Parse(manifest);
        JsonElement properties = document.RootElement;
        if (properties.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Expected a vcpkg manifest object.");
        }

        if (properties.TryGetProperty("builtin-baseline", out JsonElement baseline) &&
            baseline.GetString() != ManagedReleases.VcpkgCommit)
        {
            throw new InvalidDataException($"The pinned checkout supports builtin-baseline {ManagedReleases.VcpkgCommit}. " +
                                           "Use an explicit pinned Git registry for another baseline.");
        }

        bool hasBuiltin = properties.TryGetProperty("builtin-baseline", out _);
        bool hasEmbedded = properties.TryGetProperty("vcpkg-configuration", out JsonElement embedded);
        if (pinned && (properties.TryGetProperty("overlay-ports", out _) ||
                       properties.TryGetProperty("overlay-triplets", out _)))
        {
            throw new InvalidDataException("Experiment vcpkg overlays are unsupported.");
        }
        string path = Path.Combine(root, "vcpkg-configuration.json");
        RejectManifestLink(new FileInfo(path));
        byte[]? config = File.Exists(path) ? await ReadManifestAsync(path, ct) : null;
        using JsonDocument? configured = config is null ? null : JsonDocument.Parse(config);
        if (pinned && hasEmbedded && configured is not null)
        {
            throw new InvalidDataException("Use either embedded vcpkg configuration or vcpkg-configuration.json.");
        }
        if (hasEmbedded)
        {
            ValidateConfiguration(embedded, pinned, hasBuiltin);
        }
        if (configured is not null)
        {
            ValidateConfiguration(configured.RootElement, pinned, hasBuiltin);
        }
        return pinned && !hasBuiltin && !hasEmbedded && configured is null
            ? throw new InvalidDataException("Experiments require an explicit builtin-baseline or pinned default registry.")
            : new(pinned ? 2 : 1, pinned ? "" : root, ManagedEnvironmentIdentity.Hash(manifest),
                config is null ? null : ManagedEnvironmentIdentity.Hash(config),
                ManagedEnvironmentIdentity.Hash(ManagedEnvironmentIdentity.SerializeEnvironment()), ManagedReleases.VcpkgRelease, ManagedReleases.VcpkgRegistry)
            {
                Commit = commit,
                SourceSnapshotId = snapshotId,
                TargetTriplet = pinned ? Triplet : null,
                HostTriplet = pinned ? Triplet : null,
                Registries = pinned ? DescribeRegistries(configured?.RootElement ?? embedded) : null
            };
    }

    private static string DescribeRegistries(JsonElement config)
    {
        List<string> identities = [];
        if (config.ValueKind == JsonValueKind.Object)
        {
            if (config.TryGetProperty("default-registry", out JsonElement primary))
            {
                identities.Add(primary.ValueKind == JsonValueKind.Null ? "default disabled" : Describe(primary));
            }
            if (config.TryGetProperty("registries", out JsonElement registries))
            {
                identities.AddRange(registries.EnumerateArray().Select(Describe));
            }
        }
        return identities.Count == 0 ? "builtin " + ManagedReleases.VcpkgCommit : string.Join("\n", identities);

        static string Describe(JsonElement registry) => registry.GetProperty("kind").GetString() == "git"
            ? $"git {registry.GetProperty("repository").GetString()} {registry.GetProperty("baseline").GetString()}"
            : "builtin " + ManagedReleases.VcpkgCommit;
    }

    private static void ValidateConfiguration(JsonElement config, bool pinned, bool hasBuiltin)
    {
        if (config.ValueKind != JsonValueKind.Object || config.TryGetProperty("overlay-ports", out _) ||
            config.TryGetProperty("overlay-triplets", out _))
        {
            throw new InvalidDataException("Managed vcpkg projects need versioned registries and the pinned Linux triplet. Overlay paths are unsupported.");
        }

        if (config.TryGetProperty("default-registry", out JsonElement registry))
        {
            if (registry.ValueKind != JsonValueKind.Null)
            {
                ValidateRegistry(registry, pinned);
            }
            else if (pinned && hasBuiltin)
            {
                throw new InvalidDataException("A disabled default registry conflicts with builtin-baseline.");
            }
        }
        else if (pinned && !hasBuiltin)
        {
            throw new InvalidDataException("Declare an explicit default registry or builtin-baseline.");
        }

        if (config.TryGetProperty("registries", out JsonElement registries))
        {
            foreach (JsonElement entry in registries.EnumerateArray())
            {
                ValidateRegistry(entry, pinned);
            }
        }
    }

    private static void ValidateRegistry(JsonElement registry, bool pinned)
    {
        if (registry.ValueKind != JsonValueKind.Object || !registry.TryGetProperty("kind", out JsonElement kind))
        {
            throw new InvalidDataException("A vcpkg registry must declare its kind.");
        }

        string? source = kind.GetString();
        string? baseline = registry.TryGetProperty("baseline", out JsonElement identity) ? identity.GetString() : null;
        if (baseline is not { Length: 40 } || !baseline.All(char.IsAsciiHexDigitLower) ||
            source is not ("git" or "builtin") || (source == "builtin" && baseline != ManagedReleases.VcpkgCommit))
        {
            throw new InvalidDataException("Use a Git registry with an exact commit baseline or the managed builtin checkout.");
        }
        if (pinned && source == "git")
        {
            if (!registry.TryGetProperty("repository", out JsonElement repository) ||
                repository.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(repository.GetString(), UriKind.Absolute, out Uri? uri) ||
                uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !registry.TryGetProperty("reference", out JsonElement reference) ||
                reference.ValueKind != JsonValueKind.String || reference.GetString() != baseline)
            {
                throw new InvalidDataException("Experiment Git registries require a credential-free HTTPS repository and reference equal to the baseline commit.");
            }
        }
    }

    internal static async Task<byte[]> ReadManifestAsync(string path, CancellationToken ct)
    {
        FileInfo file = new(path);
        RejectManifestLink(file);
        return file.Exists && file.Length <= 1024 * 1024
            ? await File.ReadAllBytesAsync(path, ct)
            : throw new InvalidDataException($"Expected a manifest of at most 1 MiB at {path}.");
    }

    private static void RejectManifestLink(FileInfo file)
    {
        if (file.LinkTarget is not null || (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidDataException($"Managed tool path contains a symbolic link: {file.FullName}");
        }
    }
}
