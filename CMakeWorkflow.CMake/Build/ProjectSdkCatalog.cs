using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.CMake.Build;

public sealed record SdkReference(string Id, string Version)
{
    public override string ToString() => Id + "/" + Version;
}

public sealed record SdkFile(string Path, string Resource);
public sealed record SdkDefaults(int? CxxStandard, bool? CxxExtensions, bool? WarningsAsErrors);
public sealed record SdkCapabilities(IReadOnlyList<string> Linkage, bool Testing, bool Sanitizers, bool Packaging,
    bool Benchmarking, bool Profiling, IReadOnlyList<string> ExecutionModes);
public sealed record SdkRequirement(string Kind, string Name, string? MinimumVersion, string Admission);
public sealed record SdkDescriptor(string Id, string Version, string? Alias, SdkReference? Extends,
    string Status, string ProjectKind, string Payload, IReadOnlyList<string> Languages, SdkDefaults Defaults, SdkCapabilities Capabilities,
    IReadOnlyList<SdkRequirement> Requirements, IReadOnlyList<SdkFile> Modules, IReadOnlyList<SdkFile> Scaffold, string? VariantTemplate, string BuildTarget,
    IReadOnlyList<string> Recipes)
{
    internal bool Legacy => Payload == "legacy-v1";
}

public sealed record SdkCatalogDocument(int SchemaVersion, IReadOnlyList<SdkDescriptor> Sdks);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SdkCatalogDocument))]
[JsonSerializable(typeof(SdkDescriptor))]
public sealed partial class ProjectSdkCatalogJson : JsonSerializerContext;

internal static class ProjectSdkCatalog
{
    private const string ResourcePrefix = "CMakeWorkflow.ProjectSdk.ecosystem-v1.";
    internal static SdkCatalogDocument Document { get; } = LoadBuiltIn();

    internal static SdkDescriptor Resolve(string key, string? version) => Resolve(Document, key, version);

    internal static SdkDescriptor Resolve(SdkCatalogDocument catalog, string key, string? version)
    {
        SdkDescriptor[] matches = [.. catalog.Sdks.Where(d => (d.Id == key || d.Alias == key) &&
            (version is null || d.Version == version))];
        return matches.Length == 0
            ? throw new InvalidDataException("Unknown SDK or unsupported exact SDK version. Run project sdk list.")
            : matches.OrderByDescending(item => Version.Parse(item.Version)).First();
    }

    internal static void RequireAvailable(SdkDescriptor sdk)
    {
        if (sdk.Status != "available")
        {
            throw new InvalidDataException(sdk.Status == "abstract" ? "Unknown or abstract SDK." :
                "unsupported-sdk-provider: " + sdk.Id + " requires a cross-target execution provider.");
        }
    }

    internal static IEnumerable<SdkDescriptor> Lineage(SdkDescriptor sdk)
    {
        if (sdk.Extends is { } parent)
        {
            foreach (SdkDescriptor ancestor in Lineage(Resolve(parent.Id, parent.Version)))
            {
                yield return ancestor;
            }
        }

        yield return sdk;
    }

    internal static string Digest(SdkDescriptor sdk) => BuildVariantConfiguration.Hash(
        JsonSerializer.SerializeToUtf8Bytes(sdk, ProjectSdkCatalogJson.Default.SdkDescriptor));

    internal static byte[] ReadResource(string name)
    {
        using Stream input = typeof(ProjectSdkCatalog).Assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidDataException("Missing embedded SDK resource: " + name);
        using StreamReader reader = new(input);
        return Encoding.UTF8.GetBytes(reader.ReadToEnd().ReplaceLineEndings("\n"));
    }

    private static SdkCatalogDocument LoadBuiltIn()
    {
        SdkCatalogDocument catalog = Load(ReadResource("Catalog.json"));
        HashSet<string> resources = new(typeof(ProjectSdkCatalog).Assembly.GetManifestResourceNames(), StringComparer.Ordinal);
        foreach (SdkFile file in catalog.Sdks.Where(sdk => !sdk.Legacy).SelectMany(sdk => sdk.Modules.Concat(sdk.Scaffold)))
        {
            if (!resources.Contains(ResourcePrefix + file.Resource))
            {
                throw new InvalidDataException("Missing embedded SDK resource: " + file.Resource);
            }
        }
        return catalog;
    }

    internal static SdkCatalogDocument Load(byte[] bytes)
    {
        using JsonDocument syntax = JsonDocument.Parse(bytes);
        RejectDuplicateProperties(syntax.RootElement);
        SdkCatalogDocument? catalog = JsonSerializer.Deserialize(bytes, ProjectSdkCatalogJson.Default.SdkCatalogDocument);
        if (catalog is not { SchemaVersion: 1, Sdks.Count: > 0 })
        {
            throw new InvalidDataException("Unsupported SDK catalog.");
        }

        Dictionary<SdkReference, SdkDescriptor> index = [];
        foreach (SdkDescriptor? sdk in catalog.Sdks)
        {
            if (sdk is null)
            {
                throw new InvalidDataException("Null SDK descriptor.");
            }
            ValidateDescriptor(sdk);
            if (!index.TryAdd(new(sdk.Id, sdk.Version), sdk))
            {
                throw new InvalidDataException("Duplicate SDK descriptor: " + sdk.Id);
            }
        }

        foreach (SdkDescriptor sdk in catalog.Sdks)
        {
            SdkDescriptor current = sdk;
            HashSet<SdkReference> visited = [new(sdk.Id, sdk.Version)];
            while (current.Extends is { } parent)
            {
                if (!visited.Add(parent) || !index.TryGetValue(parent, out SdkDescriptor? next))
                {
                    throw new InvalidDataException("Missing or cyclic SDK inheritance: " + parent);
                }
                current = next;
            }
        }
        return catalog.Sdks.Where(sdk => sdk.Alias is not null).GroupBy(sdk => sdk.Alias, StringComparer.Ordinal)
            .Any(group => group.Select(sdk => sdk.Id).Distinct(StringComparer.Ordinal).Count() > 1)
            ? throw new InvalidDataException("Conflicting SDK aliases.") : catalog;
    }

    private static void ValidateDescriptor(SdkDescriptor sdk)
    {
        if (sdk.Id is null || !sdk.Id.StartsWith("Native.", StringComparison.Ordinal) ||
            !Version.TryParse(sdk.Version, out Version? version) || version.Build < 0 || version.Revision >= 0 ||
            sdk.Status is not ("abstract" or "available" or "unsupported") ||
            sdk.Payload is not ("legacy-v1" or "ecosystem-v1") ||
            sdk.ProjectKind is not ("base" or "library" or "application" or "embedded") ||
            sdk.Capabilities is null || sdk.Defaults is null || sdk.BuildTarget is null ||
            sdk.Modules is null || sdk.Scaffold is null || sdk.Languages is null || sdk.Requirements is null ||
            sdk.Recipes is null || sdk.Capabilities.ExecutionModes is null || sdk.Capabilities.Linkage is null ||
            sdk.Capabilities.ExecutionModes.Any(mode => mode is not ("ambient" or "linux-managed-clang")) ||
            sdk.Capabilities.Linkage.Any(linkage => linkage is not ("static" or "shared")) ||
            (sdk.Status == "available" && (sdk.Scaffold.Count == 0 || sdk.Capabilities.ExecutionModes.Count == 0)))
        {
            throw new InvalidDataException("Invalid SDK descriptor or capability: " + sdk.Id);
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (SdkFile? file in sdk.Modules.Concat(sdk.Scaffold))
        {
            if (file?.Path is null || file.Resource is null || !paths.Add(file.Path) ||
                file.Path.Split('/').Any(segment => segment.Length == 0 || segment.EndsWith('.') ||
                segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_' or '@'))) ||
                file.Resource.Length == 0 || file.Resource.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            {
                throw new InvalidDataException("Unsafe or duplicate SDK resource: " + file?.Path);
            }
        }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Duplicate SDK catalog property: " + property.Name);
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }
}
