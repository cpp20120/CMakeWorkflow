using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.CMake.Planning;

namespace CMakeWorkflow.CMake.Build;

public static class CMakeInspection
{
    public static async Task<ObservedBuildContext> InspectAsync(string buildDirectory, CancellationToken ct,
        Action<string>? readGuard = null)
    {
        readGuard?.Invoke(buildDirectory);
        ObservedBuildContext result = new() { BuildDirectory = buildDirectory };
        result.Diagnostics.Add(
            "Configuration freshness and native output freshness are Unverified; inspection does not run CMake.");
        if (!Directory.Exists(buildDirectory))
        {
            result.Diagnostics.Add("Build directory does not exist. Explicit configure is required.");
            return result;
        }

        // Retry only a disappearing File API reply (CMake may replace its reply set).
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            try
            {
                return await ReadAsync(buildDirectory, readGuard, ct: ct);
            }
            catch (FileNotFoundException) when (attempt < 2)
            {
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException
                                              or KeyNotFoundException or ArgumentException
                                              or UnauthorizedAccessException)
            {
                result.Diagnostics.Add($"Cannot trust the available CMake observation: {error.Message}");
                return result;
            }
        }

        result.Diagnostics.Add("File API replies changed during inspection; retry after configuration completes.");
        return result;
    }

    private static async Task<ObservedBuildContext> ReadAsync(string directory, Action<string>? readGuard, CancellationToken ct)
    {
        ObservedBuildContext result = new() { BuildDirectory = directory };
        result.Diagnostics.Add(
            "Configuration freshness and native output freshness are Unverified; inspection does not run CMake.");
        Dictionary<string, string> cache = ReadObservedCache(directory, readGuard);
        string reply = Path.Combine(directory, ".cmake", "api", "v1", "reply");
        readGuard?.Invoke(reply);
        string? indexPath = Directory.Exists(reply)
            ? Directory.EnumerateFiles(reply, "*.json")
                .Where(p => Path.GetFileName(p).StartsWith("index-", StringComparison.Ordinal)
                            || Path.GetFileName(p).StartsWith("error-", StringComparison.Ordinal))
                .OrderByDescending(p => Path.GetFileName(p)[6..], StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (indexPath is not null)
        {
            result.IndexFile = indexPath;
            readGuard?.Invoke(indexPath);
            byte[] bytes = await File.ReadAllBytesAsync(indexPath, ct);
            result.IndexHash = Convert.ToHexString(SHA256.HashData(bytes));
            using JsonDocument index = JsonDocument.Parse(bytes);
            if (Path.GetFileName(indexPath).StartsWith("error-", StringComparison.Ordinal))
            {
                result.State = "ConfigureFailed";
                result.Diagnostics.Add(
                    "The latest File API index records a failed CMake generation; old replies/manifests are not accepted.");
                return result;
            }

            JsonElement cmake = index.RootElement.GetProperty("cmake");
            JsonElement generator = cmake.GetProperty("generator");
            result.Generator = generator.GetProperty("name").GetString();
            result.MultiConfig = generator.GetProperty("multiConfig").GetBoolean();
            if (cmake.TryGetProperty("paths", out JsonElement tools))
            {
                result.CMakeExecutable = tools.GetProperty("cmake").GetString();
                result.CTestExecutable = tools.GetProperty("ctest").GetString();
            }

            JsonElement objects = index.RootElement.GetProperty("objects");
            using JsonDocument? model = await ReadObjectAsync(reply, objects, "codemodel", 2, readGuard, ct: ct);
            if (model is not null)
            {
                result.HasCodeModel = true;
                JsonElement paths = model.RootElement.GetProperty("paths");
                result.SourceDirectory = paths.GetProperty("source").GetString();
                string modelBuild = paths.GetProperty("build").GetString()!;
                if (!WorkflowPlanner.PathComparer.Equals(BuildTreeOwnership.CanonicalPath(modelBuild), directory))
                {
                    throw new InvalidOperationException("File API build path does not match this build tree.");
                }

                foreach (JsonElement configuration in model.RootElement.GetProperty("configurations").EnumerateArray())
                {
                    string name = configuration.GetProperty("name").GetString()!;
                    result.Configurations.Add(name);
                    foreach (JsonElement entry in configuration.GetProperty("targets").EnumerateArray())
                    {
                        using JsonDocument target =
                            await ReadJsonAsync(ReplyPath(reply, entry.GetProperty("jsonFile").GetString()), readGuard, ct: ct);
                        JsonElement data = target.RootElement;
                        string type = data.GetProperty("type").GetString()!;
                        string targetName = data.GetProperty("name").GetString()!;
                        if (targetName != entry.GetProperty("name").GetString())
                        {
                            throw new InvalidOperationException("File API target identity mismatch.");
                        }

                        string? executable = null;
                        if (type == "EXECUTABLE" && data.TryGetProperty("artifacts", out JsonElement artifacts))
                        {
                            string? diskName = data.TryGetProperty("nameOnDisk", out JsonElement disk) ? disk.GetString() : null;
                            string[] candidates = [.. artifacts.EnumerateArray().Select(a => a.GetProperty("path").GetString()!).Where(p => diskName is not null && Path.GetFileName(p) == diskName)];
                            if (candidates.Length == 1)
                            {
                                executable = Path.GetFullPath(candidates[0], directory);
                            }
                        }

                        result.Targets.Add(new(targetName, name, type, executable, "FileApi"));
                    }
                }
            }

            using JsonDocument? cacheReply = await ReadObjectAsync(reply, objects, "cache", 2, readGuard, ct: ct);
            if (cacheReply is not null)
            {
                result.HasCacheReply = true;
                // Expose only observed identity values, never reinterpret flags or presets.
                Dictionary<string, string> apiCache = cacheReply.RootElement.GetProperty("entries").EnumerateArray()
                    .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("value").GetString()!);
                if (apiCache.TryGetValue("CMAKE_HOME_DIRECTORY", out string? home))
                {
                    result.SourceDirectory ??= home;
                }
            }

            using JsonDocument? toolchains = await ReadObjectAsync(reply, objects, "toolchains", 1, readGuard, ct: ct);
            if (toolchains is not null)
            {
                foreach (JsonElement toolchain in toolchains.RootElement.GetProperty("toolchains").EnumerateArray())
                {
                    string? language = toolchain.GetProperty("language").GetString();
                    string? compiler = toolchain.TryGetProperty("compiler", out JsonElement c) &&
                                   c.TryGetProperty("path", out JsonElement p)
                        ? p.GetString()
                        : "Unknown";
                    result.Toolchains.Add($"{language}: {compiler}");
                }
            }

            if (result.HasCodeModel)
            {
                if (cache.TryGetValue("CMAKE_GENERATOR", out string? cachedGenerator) &&
                    cachedGenerator != result.Generator)
                {
                    throw new InvalidOperationException("Cache and File API generator observations disagree.");
                }

                if (result.MultiConfig == false && cache.TryGetValue("CMAKE_BUILD_TYPE", out string? buildType)
                                                && !result.Configurations.SequenceEqual([buildType]))
                {
                    throw new InvalidOperationException("Cache and File API configuration observations disagree.");
                }

                if (result.MultiConfig == true && cache.TryGetValue("CMAKE_CONFIGURATION_TYPES", out string? types)
                                               && !result.Configurations.ToHashSet(StringComparer.Ordinal)
                                                   .SetEquals(types.Split(';', StringSplitOptions.RemoveEmptyEntries)))
                {
                    throw new InvalidOperationException("Cache and File API multi-config observations disagree.");
                }

                if (cache.TryGetValue("CMAKE_HOME_DIRECTORY", out string? source)
                    && result.SourceDirectory is not null
                    && !WorkflowPlanner.PathComparer.Equals(BuildTreeOwnership.CanonicalPath(source),
                        BuildTreeOwnership.CanonicalPath(result.SourceDirectory)))
                {
                    throw new InvalidOperationException("Cache and File API source observations disagree.");
                }
            }
        }

        if (!result.HasCodeModel)
        {
            result.Generator ??= cache.GetValueOrDefault("CMAKE_GENERATOR");
            result.SourceDirectory ??= cache.GetValueOrDefault("CMAKE_HOME_DIRECTORY");
            result.CMakeExecutable ??= cache.GetValueOrDefault("CMAKE_COMMAND");
            if (cache.TryGetValue("CMAKE_CONFIGURATION_TYPES", out string? configurations) && configurations.Length > 0)
            {
                result.MultiConfig = true;
                result.Configurations = [.. configurations.Split(';', StringSplitOptions.RemoveEmptyEntries)];
            }
            else if (cache.TryGetValue("CMAKE_BUILD_TYPE", out string? buildType) && result.Generator is not null)
            {
                result.MultiConfig ??= false;
                result.Configurations = [buildType];
            }

            result.Diagnostics.Add(
                "No codemodel: limited cache/bridge observations only; absent targets have Unknown type.");
            string manifests = Path.Combine(directory, "cmake-workflow", "targets");
            readGuard?.Invoke(manifests);
            if (Directory.Exists(manifests))
            {
                foreach (string path in BridgeFiles(manifests, readGuard, ct))
                {
                    using JsonDocument json = await ReadJsonAsync(path, readGuard, ct: ct);
                    JsonElement data = json.RootElement;
                    string target = data.GetProperty("target").GetString()!;
                    string configuration = data.GetProperty("configuration").GetString()!;
                    if (data.GetProperty("schemaVersion").GetInt32() != 1
                        || Path.GetFileNameWithoutExtension(path) != target
                        || (Path.GetFileName(Path.GetDirectoryName(path)) != configuration && configuration.Length > 0)
                        || !result.Configurations.Contains(configuration))
                    {
                        throw new InvalidOperationException("Bridge manifest identity/configuration mismatch.");
                    }

                    string executable = data.GetProperty("executable").GetString()!;
                    if (!Path.IsPathFullyQualified(executable))
                    {
                        throw new InvalidOperationException("Bridge executable path is not absolute.");
                    }

                    result.Targets.Add(new(target, configuration, "EXECUTABLE", executable, "BridgeManifest"));
                }
            }
        }

        if (result.MultiConfig is not null && result.Configurations.Count > 0)
        {
            result.State = result.HasCodeModel ? "Observed" : "ObservedLimited";
        }

        string provenance = ProvenancePath(directory);
        readGuard?.Invoke(provenance);
        if (File.Exists(provenance))
        {
            byte[] json = await File.ReadAllBytesAsync(provenance, ct);
            result.ManagedConfigure = JsonSerializer.Deserialize(json, Serialization.WorkflowJson.Default.ConfigureProvenance);
            if (result.ManagedConfigure is { } managed)
            {
                if (managed.State != "Succeeded")
                {
                    result.State = "ConfigureFailed";
                    result.Diagnostics.Add(
                        "The last managed configure did not complete successfully; explicit configure is required.");
                }
                else if (managed.IndexHash is not null && managed.IndexHash == result.IndexHash
                                                       && managed.IndexFile is not null && result.IndexFile is not null
                                                       && WorkflowPlanner.PathComparer.Equals(managed.IndexFile,
                                                           result.IndexFile))
                {
                    result.PresetState = "RecordedInvocation";
                }
                else
                {
                    // A new File API generation is not evidence of a *different*
                    // configuration. Nor does an equivalent codemodel prove that
                    // the original preset/environment is still current.
                    result.PresetState = "GenerationChanged";
                    result.Diagnostics.Add(
                        "CMake generation differs from the last managed configure; preset provenance is Unverified, not a configuration mismatch.");
                }
            }
        }

        result.Diagnostics.Add(
            "Requested preset identity is never inferred from cache values; configure provenance records only an invocation.");
        return result;
    }

    public static void ValidateIdentity(RequestedBuildContext request, ObservedBuildContext observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(request);
        if (observed.State is not ("Observed" or "ObservedLimited"))
        {
            throw new InvalidOperationException(
                $"Build context is {observed.State}/Unverified: {string.Join(" ", observed.Diagnostics)}");
        }

        if (request.SourceDirectory is not null && (observed.SourceDirectory is null
                                                    || !WorkflowPlanner.PathComparer.Equals(
                                                        BuildTreeOwnership.CanonicalPath(request.SourceDirectory),
                                                        BuildTreeOwnership.CanonicalPath(observed.SourceDirectory))))
        {
            throw new InvalidOperationException(
                "Requested source directory disagrees with the observed build context.");
        }

        if (request.Generator is not null && request.Generator != observed.Generator)
        {
            throw new InvalidOperationException(
                "Requested generator disagrees with the observed build context (or is Unknown).");
        }

        if (request.ConfigurePreset is not null)
        {
            bool invocationMatches = observed.PresetState == "RecordedInvocation"
                                    && observed.ManagedConfigure is { } managed
                                    && managed.Preset == request.ConfigurePreset
                                    && WorkflowPlanner.PathComparer.Equals(managed.BuildDirectory,
                                        request.BuildDirectory);
            if (observed.PresetState == "RecordedInvocation" && !invocationMatches)
            {
                throw new InvalidOperationException(
                    $"Requested preset '{request.ConfigurePreset}' conflicts with the last recorded configure invocation.");
            }

            if (!invocationMatches && request.ProvenancePolicy != "allowUnverified")
            {
                throw new InvalidOperationException(
                    $"Requested preset '{request.ConfigurePreset}' is Unverified. Add an explicit configure node or explicitly adopt it with provenancePolicy: allowUnverified.");
            }
        }
    }

    public static CMakeTargetRef Resolve(RequestedBuildContext request, ObservedBuildContext observed,
        string target = "")
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request, observed);
        string? configuration = request.Configuration;
        if (configuration is null)
        {
            if (observed.MultiConfig == true)
            {
                throw new InvalidOperationException(
                    "Multi-config build: pass --config or context.configuration explicitly.");
            }

            if (observed.Configurations.Count != 1)
            {
                throw new InvalidOperationException("Configuration selection is Unknown/Unverified.");
            }

            configuration = observed.Configurations[0];
        }

        return !observed.Configurations.Contains(configuration, StringComparer.Ordinal)
            ? throw new InvalidOperationException(
                $"Requested configuration '{configuration}' disagrees with observed configurations [{string.Join(", ", observed.Configurations)}].")
            : new CMakeTargetRef(request.BuildDirectory, target, configuration);
    }

    public static NativeTargetObservation RequireTarget(ObservedBuildContext observed, CMakeTargetRef target,
        bool executableOnly = false)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(target);
        NativeTargetObservation[] matches = [.. observed.Targets.Where(t => t.Name == target.Target && t.Configuration == target.Configuration)];
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Target '{target.Target}' is {(observed.HasCodeModel ? "absent or ambiguous" : "Unknown")}. Explicit configure with File API metadata is required before Build/Run; use invoke only for an intentional target execution.");
        }

        NativeTargetObservation item = matches[0];
        bool allowed = executableOnly
            ? item.Type == "EXECUTABLE"
            : item.Type is "EXECUTABLE" or "STATIC_LIBRARY" or "SHARED_LIBRARY" or "MODULE_LIBRARY" or "OBJECT_LIBRARY";
        return !allowed
            ? throw new InvalidOperationException(
                $"Target '{target.Target}' has type {item.Type}; Build/Run will not invoke it. Migrate intentional utility/campaign execution to kind: invoke (CLI: target invoke).")
            : item;
    }

    internal static string ProvenancePath(string tree) => Path.Combine(tree, "cmake-workflow", "configure.json");

    /// <summary>Replaces configure.json invocation evidence through a fixed temporary sibling.</summary>
    /// <remarks>
    /// Caller serializes writes under build-tree ownership. Creates the metadata directory, writes serialized
    /// bytes and overwrites the destination by move. There is no cancellation token, fsync/directory-sync durability
    /// confirmation or temporary-file cleanup on failure; this is not the durable runner dispatch checkpoint.
    /// </remarks>
    internal static async Task WriteProvenanceAsync(string tree, ConfigureProvenance provenance)
    {
        string path = ProvenancePath(tree);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary,
            JsonSerializer.SerializeToUtf8Bytes(provenance, Serialization.WorkflowJson.Default.ConfigureProvenance));
        File.Move(temporary, path, true);
    }

    private static Dictionary<string, string> ReadObservedCache(string directory, Action<string>? readGuard)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        string path = Path.Combine(directory, "CMakeCache.txt");
        readGuard?.Invoke(path);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (string line in File.ReadLines(path))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (colon <= 0 || equals < colon)
            {
                continue;
            }

            string key = line[..colon];
            if (key is "CMAKE_BUILD_TYPE" or "CMAKE_CONFIGURATION_TYPES" or "CMAKE_GENERATOR"
                or "CMAKE_HOME_DIRECTORY" or "CMAKE_COMMAND")
            {
                values[key] = line[(equals + 1)..];
            }
        }

        return values;
    }

    private static async Task<JsonDocument?> ReadObjectAsync(string directory, JsonElement objects, string kind,
        int version, Action<string>? readGuard, CancellationToken ct)
    {
        foreach (JsonElement item in objects.EnumerateArray())
        {
            if (item.GetProperty("kind").GetString() == kind &&
                item.GetProperty("version").GetProperty("major").GetInt32() == version)
            {
                return await ReadJsonAsync(ReplyPath(directory, item.GetProperty("jsonFile").GetString()), readGuard, ct: ct);
            }
        }

        return null;
    }

    private static string ReplyPath(string directory, string? file)
    {
        return string.IsNullOrEmpty(file) || Path.GetFileName(file) != file || file is "." or ".."
            ? throw new InvalidOperationException("Invalid File API reply filename.")
            : Path.Combine(directory, file);
    }

    private static IEnumerable<string> BridgeFiles(string directory, Action<string>? readGuard, CancellationToken ct)
    {
        Stack<string> pending = new();
        HashSet<string> visited = new(WorkflowPlanner.PathComparer);
        pending.Push(directory);
        while (pending.TryPop(out string? current))
        {
            ct.ThrowIfCancellationRequested();
            readGuard?.Invoke(current);
            if (!visited.Add(BuildTreeOwnership.CanonicalPath(current)))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(current, "*.json"))
            {
                yield return file;
            }

            foreach (string child in Directory.EnumerateDirectories(current))
            {
                pending.Push(child);
            }
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(string path, Action<string>? readGuard, CancellationToken ct)
    {
        readGuard?.Invoke(path);
        return JsonDocument.Parse(await File.ReadAllBytesAsync(path, ct));
    }
}