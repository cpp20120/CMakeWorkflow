using System.Text.Json;
using CMakeWorkflow.CMake.Planning;

namespace CMakeWorkflow.CMake.Build;

public sealed record CMakeSourceFiles(string SourceDirectory, string Configuration, bool MultiConfig,
    IReadOnlyList<string> Files, IReadOnlyList<string> TranslationUnits);

public static partial class CMakeInspection
{
    /// <summary>Reads non-generated source and header paths for a target from the latest CMake File API codemodel.</summary>
    public static async Task<CMakeSourceFiles> ReadTargetSourcesAsync(string buildDirectory, string target,
        string? configuration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        string directory = BuildTreeOwnership.CanonicalPath(buildDirectory);
        string reply = Path.Combine(directory, ".cmake", "api", "v1", "reply");
        string? indexPath = LatestIndex(reply);
        if (indexPath is null || Path.GetFileName(indexPath).StartsWith("error-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("A successful CMake File API codemodel is required. Configure with --preset or request codemodel-v2 before configuring.");
        }

        using JsonDocument index = await ReadJsonAsync(indexPath, null, ct);
        using JsonDocument model = await ReadObjectAsync(reply, index.RootElement.GetProperty("objects"),
            "codemodel", 2, null, ct) ?? throw new InvalidDataException("Configure this tree with a CMake codemodel-v2 query.");
        JsonElement paths = model.RootElement.GetProperty("paths");
        if (!WorkflowPlanner.PathComparer.Equals(directory, BuildTreeOwnership.CanonicalPath(paths.GetProperty("build").GetString()!)))
        {
            throw new InvalidDataException("The codemodel belongs to a different build tree.");
        }

        JsonElement[] configurations = [.. model.RootElement.GetProperty("configurations").EnumerateArray()
            .Where(item => configuration is null || item.GetProperty("name").GetString() == configuration)];
        if (configurations.Length != 1)
        {
            throw new InvalidDataException("Select exactly one codemodel configuration with --configuration.");
        }

        JsonElement selected = configurations[0];
        JsonElement[] targets = [.. selected.GetProperty("targets").EnumerateArray()
            .Where(item => item.GetProperty("name").GetString() == target)];
        if (targets.Length != 1)
        {
            throw new InvalidDataException($"Target '{target}' is missing or ambiguous in the selected configuration.");
        }

        using JsonDocument targetDocument = await ReadJsonAsync(
            ReplyPath(reply, targets[0].GetProperty("jsonFile").GetString()), null, ct);
        JsonElement data = targetDocument.RootElement;
        if (data.GetProperty("name").GetString() != target)
        {
            throw new InvalidDataException("File API target identity mismatch.");
        }
        string source = BuildTreeOwnership.CanonicalPath(paths.GetProperty("source").GetString()!);
        JsonElement[] sources = [.. data.GetProperty("sources").EnumerateArray()
            .Where(item => !item.TryGetProperty("isGenerated", out JsonElement generated) || !generated.GetBoolean())];
        IReadOnlyList<string> files = Paths(sources);
        IReadOnlyList<string> units = Paths(sources.Where(item => item.TryGetProperty("compileGroupIndex", out _)));
        bool multi = index.RootElement.GetProperty("cmake").GetProperty("generator").GetProperty("multiConfig").GetBoolean();
        return new(source, selected.GetProperty("name").GetString()!, multi, files, units);

        IReadOnlyList<string> Paths(IEnumerable<JsonElement> items) => Array.AsReadOnly(items
            .Select(item => Path.GetFullPath(item.GetProperty("path").GetString()!, source))
            .Distinct(WorkflowPlanner.PathComparer).Order(StringComparer.Ordinal).ToArray());
    }
}
