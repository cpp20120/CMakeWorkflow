using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;

namespace CMakeWorkflow.Developer;

internal static class CompilationDatabase
{
    internal static async Task<TranslationUnit[]> SelectAsync(string root, string build, string destination,
        ClangToolRequest request, CMakeSourceFiles? target, CancellationToken ct)
    {
        string path = Path.Combine(build, "compile_commands.json");
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Missing {path}. Configure with CMAKE_EXPORT_COMPILE_COMMANDS=ON using Ninja or Makefiles.");
        }
        Dictionary<string, string> cache = await CMakeInspection.ReadObservedCacheAsync(build, null, ct);
        if (cache.TryGetValue("CMAKE_HOME_DIRECTORY", out string? source) &&
            !WorkflowPlanner.PathComparer.Equals(root, BuildTreeOwnership.CanonicalPath(source)))
        {
            throw new InvalidDataException("The compilation database belongs to a different source directory.");
        }
        string? config = target?.Configuration ?? request.Configuration;
        bool multi = target?.MultiConfig == true || !string.IsNullOrWhiteSpace(cache.GetValueOrDefault("CMAKE_CONFIGURATION_TYPES"));
        if (multi && (string.IsNullOrWhiteSpace(config) || !cache.GetValueOrDefault("CMAKE_CONFIGURATION_TYPES", "")
            .Split(';').Contains(config, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Select a known multi-config configuration with --configuration.");
        }
        if (!multi && config is not null && cache.TryGetValue("CMAKE_BUILD_TYPE", out string? active) && active != config)
        {
            throw new InvalidDataException("The selected configuration differs from the compilation database.");
        }

        await using FileStream input = File.OpenRead(path);
        using JsonDocument document = await JsonDocument.ParseAsync(input, cancellationToken: ct);
        HashSet<string>? targetFiles = target?.TranslationUnits.ToHashSet(WorkflowPlanner.PathComparer);
        Dictionary<string, List<Entry>> units = new(WorkflowPlanner.PathComparer);
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            string directory = entry.GetProperty("directory").GetString()!;
            string file = Path.GetFullPath(entry.GetProperty("file").GetString()!, directory);
            if ((targetFiles is not null && !targetFiles.Contains(file)) || !ClangTools.EligibleSource(root, file))
            {
                continue;
            }

            if (!units.TryGetValue(file, out List<Entry>? list))
            {
                list = [];
                units.Add(file, list);
            }
            string? output = entry.TryGetProperty("output", out JsonElement outputField) ? outputField.GetString() : null;
            list.Add(new(entry, output?.Replace('\\', '/')));
        }

        List<(string File, Entry[] Commands)> selected = [];
        foreach ((string file, List<Entry> entries) in units.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if ((multi || (request.Target is not null && entries.Count > 1)) && entries.Any(entry => entry.Output is null))
            {
                throw new InvalidDataException($"Cannot disambiguate compilation entries for '{file}'. CMake output paths are required.");
            }
            Entry[] matches = [.. entries.Where(entry => Matches(entry.Output, request.Target, multi ? config : null))];
            if (matches.Length > 0)
            {
                selected.Add((file, matches));
            }
            else if (targetFiles is not null)
            {
                throw new InvalidDataException($"No target-specific compile command found for '{file}'.");
            }
        }

        // LibTooling opens a database in each process. Keep its input bounded
        // per process so analyzing N files does not reparse the full DB N times.
        int shardSize = request.Tool == ClangTool.IncludeWhatYouUse ? int.MaxValue : 64;
        return await WriteShardsAsync(destination, selected, shardSize, ct);
    }

    private static async Task<TranslationUnit[]> WriteShardsAsync(string root,
        List<(string File, Entry[] Commands)> selected, int shardSize, CancellationToken ct)
    {
        List<TranslationUnit> sources = new(selected.Count);
        for (int index = 0; index < selected.Count;)
        {
            string directory = shardSize == int.MaxValue ? root : Path.Combine(root,
                index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _ = Directory.CreateDirectory(directory);
            string database = Path.Combine(directory, "compile_commands.json");
            await using FileStream output = new(database, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 65536, FileOptions.Asynchronous);
            using Utf8JsonWriter writer = new(output);
            writer.WriteStartArray();
            int end = index + Math.Min(shardSize, selected.Count - index);
            for (; index < end; index++)
            {
                ct.ThrowIfCancellationRequested();
                (string file, Entry[] commands) = selected[index];
                foreach (Entry entry in commands)
                {
                    entry.Json.WriteTo(writer);
                }
                sources.Add(new(file, directory));
            }
            writer.WriteEndArray();
            await writer.FlushAsync(ct);
        }
        return [.. sources];
    }

    private static bool Matches(string? output, string? target, string? configuration)
    {
        if (output is null || (target is null && configuration is null))
        {
            return true;
        }
        string[] components = output.Split('/');
        for (int index = 1; index < components.Length - 1; index++)
        {
            if (components[index - 1] == "CMakeFiles" && components[index].EndsWith(".dir", StringComparison.Ordinal) &&
                (target is null || components[index] == target + ".dir") &&
                (configuration is null || components[index + 1] == configuration))
            {
                return true;
            }
        }
        return false;
    }

    internal sealed record TranslationUnit(string File, string DatabaseDirectory);

    private sealed record Entry(JsonElement Json, string? Output);
}
