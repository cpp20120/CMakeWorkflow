using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed record RequestedBuildContext(string BuildDirectory, string? Configuration = null,
    string? ConfigurePreset = null, string? SourceDirectory = null,
    string ProvenancePolicy = "strict", string Mode = "legacy", string? Generator = null);

public sealed record NativeTargetObservation(string Name, string Configuration, string Type,
    string? Executable, string Evidence);

public sealed class ObservedBuildContext
{
    public string BuildDirectory { get; set; } = "";
    public string State { get; set; } = "Unknown";
    public string Freshness { get; set; } = "Unverified";
    public string PresetState { get; set; } = "Unknown";
    public string? SourceDirectory { get; set; }
    public string? Generator { get; set; }
    public bool? MultiConfig { get; set; }
    public List<string> Configurations { get; set; } = [];
    public string? IndexFile { get; set; }
    public string? IndexHash { get; set; }
    public string? CMakeExecutable { get; set; }
    public string? CTestExecutable { get; set; }
    public bool HasCodeModel { get; set; }
    public bool HasCacheReply { get; set; }
    public List<string> Toolchains { get; set; } = [];
    public List<NativeTargetObservation> Targets { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
    public ConfigureProvenance? ManagedConfigure { get; set; }
}

public sealed class ConfigureProvenance
{
    public string State { get; set; } = "Attempted";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string BuildDirectory { get; set; } = "";
    public string SourceDirectory { get; set; } = "";
    public string? Preset { get; set; }
    public string? IndexFile { get; set; }
    public string? IndexHash { get; set; }
    public int? ExitCode { get; set; }
    public string? Executable { get; set; }
    public string? WorkingDirectory { get; set; }
    public List<string> Arguments { get; set; } = [];
}

public static class CMakeInspection
{
    public static async Task<ObservedBuildContext> InspectAsync(string buildDirectory, CancellationToken ct)
    {
        var result = new ObservedBuildContext { BuildDirectory = buildDirectory };
        result.Diagnostics.Add("Configuration freshness and native output freshness are Unverified; inspection does not run CMake.");
        if (!Directory.Exists(buildDirectory))
        {
            result.Diagnostics.Add("Build directory does not exist. Explicit configure is required.");
            return result;
        }
        // Retry only a disappearing File API reply (CMake may replace its reply set).
        for (var attempt = 0; attempt < 3; ++attempt)
        {
            try { return await ReadAsync(buildDirectory, ct); }
            catch (FileNotFoundException) when (attempt < 2) { }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException
                                         or KeyNotFoundException or ArgumentException or UnauthorizedAccessException)
            {
                result.Diagnostics.Add($"Cannot trust the available CMake observation: {error.Message}");
                return result;
            }
        }
        result.Diagnostics.Add("File API replies changed during inspection; retry after configuration completes.");
        return result;
    }

    private static async Task<ObservedBuildContext> ReadAsync(string directory, CancellationToken ct)
    {
        var result = new ObservedBuildContext { BuildDirectory = directory };
        result.Diagnostics.Add("Configuration freshness and native output freshness are Unverified; inspection does not run CMake.");
        var cache = ReadObservedCache(directory);
        var reply = Path.Combine(directory, ".cmake", "api", "v1", "reply");
        var indexPath = Directory.Exists(reply) ? Directory.EnumerateFiles(reply, "*.json")
            .Where(p => Path.GetFileName(p).StartsWith("index-", StringComparison.Ordinal)
                     || Path.GetFileName(p).StartsWith("error-", StringComparison.Ordinal))
            .OrderByDescending(p => Path.GetFileName(p)[6..], StringComparer.Ordinal).FirstOrDefault() : null;
        if (indexPath is not null)
        {
            result.IndexFile = indexPath;
            var bytes = await File.ReadAllBytesAsync(indexPath, ct);
            result.IndexHash = Convert.ToHexString(SHA256.HashData(bytes));
            using var index = JsonDocument.Parse(bytes);
            if (Path.GetFileName(indexPath).StartsWith("error-", StringComparison.Ordinal))
            {
                result.State = "ConfigureFailed";
                result.Diagnostics.Add("The latest File API index records a failed CMake generation; old replies/manifests are not accepted.");
                return result;
            }
            var cmake = index.RootElement.GetProperty("cmake");
            var generator = cmake.GetProperty("generator");
            result.Generator = generator.GetProperty("name").GetString();
            result.MultiConfig = generator.GetProperty("multiConfig").GetBoolean();
            if (cmake.TryGetProperty("paths", out var tools))
            {
                result.CMakeExecutable = tools.GetProperty("cmake").GetString();
                result.CTestExecutable = tools.GetProperty("ctest").GetString();
            }
            var objects = index.RootElement.GetProperty("objects");
            using var model = await ReadObjectAsync(reply, objects, "codemodel", 2, ct);
            if (model is not null)
            {
                result.HasCodeModel = true;
                var paths = model.RootElement.GetProperty("paths");
                result.SourceDirectory = paths.GetProperty("source").GetString();
                var modelBuild = paths.GetProperty("build").GetString()!;
                if (!WorkflowPlanner.PathComparer.Equals(BuildTreeOwnership.CanonicalPath(modelBuild), directory))
                    throw new InvalidOperationException("File API build path does not match this build tree.");
                foreach (var configuration in model.RootElement.GetProperty("configurations").EnumerateArray())
                {
                    var name = configuration.GetProperty("name").GetString()!;
                    result.Configurations.Add(name);
                    foreach (var entry in configuration.GetProperty("targets").EnumerateArray())
                    {
                        using var target = await ReadJsonAsync(ReplyPath(reply, entry.GetProperty("jsonFile").GetString()), ct);
                        var data = target.RootElement;
                        var type = data.GetProperty("type").GetString()!;
                        var targetName = data.GetProperty("name").GetString()!;
                        if (targetName != entry.GetProperty("name").GetString())
                            throw new InvalidOperationException("File API target identity mismatch.");
                        string? executable = null;
                        if (type == "EXECUTABLE" && data.TryGetProperty("artifacts", out var artifacts))
                        {
                            var diskName = data.TryGetProperty("nameOnDisk", out var disk) ? disk.GetString() : null;
                            var candidates = artifacts.EnumerateArray().Select(a => a.GetProperty("path").GetString()!)
                                .Where(p => diskName is not null && Path.GetFileName(p) == diskName).ToArray();
                            if (candidates.Length == 1)
                                executable = Path.GetFullPath(candidates[0], directory);
                        }
                        result.Targets.Add(new(targetName, name, type, executable, "FileApi"));
                    }
                }
            }
            using var cacheReply = await ReadObjectAsync(reply, objects, "cache", 2, ct);
            if (cacheReply is not null)
            {
                result.HasCacheReply = true;
                // Expose only observed identity values, never reinterpret flags or presets.
                var apiCache = cacheReply.RootElement.GetProperty("entries").EnumerateArray()
                    .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("value").GetString()!);
                if (apiCache.TryGetValue("CMAKE_HOME_DIRECTORY", out var home)) result.SourceDirectory ??= home;
            }
            using var toolchains = await ReadObjectAsync(reply, objects, "toolchains", 1, ct);
            if (toolchains is not null)
                foreach (var toolchain in toolchains.RootElement.GetProperty("toolchains").EnumerateArray())
                {
                    var language = toolchain.GetProperty("language").GetString();
                    var compiler = toolchain.TryGetProperty("compiler", out var c) && c.TryGetProperty("path", out var p)
                        ? p.GetString() : "Unknown";
                    result.Toolchains.Add($"{language}: {compiler}");
                }
            if (result.HasCodeModel)
            {
                if (cache.TryGetValue("CMAKE_GENERATOR", out var cachedGenerator) && cachedGenerator != result.Generator)
                    throw new InvalidOperationException("Cache and File API generator observations disagree.");
                if (result.MultiConfig == false && cache.TryGetValue("CMAKE_BUILD_TYPE", out var buildType)
                    && !result.Configurations.SequenceEqual([buildType]))
                    throw new InvalidOperationException("Cache and File API configuration observations disagree.");
                if (result.MultiConfig == true && cache.TryGetValue("CMAKE_CONFIGURATION_TYPES", out var types)
                    && !result.Configurations.ToHashSet(StringComparer.Ordinal)
                        .SetEquals(types.Split(';', StringSplitOptions.RemoveEmptyEntries)))
                    throw new InvalidOperationException("Cache and File API multi-config observations disagree.");
                if (cache.TryGetValue("CMAKE_HOME_DIRECTORY", out var source)
                    && result.SourceDirectory is not null
                    && !WorkflowPlanner.PathComparer.Equals(BuildTreeOwnership.CanonicalPath(source),
                        BuildTreeOwnership.CanonicalPath(result.SourceDirectory)))
                    throw new InvalidOperationException("Cache and File API source observations disagree.");
            }
        }

        if (!result.HasCodeModel)
        {
            result.Generator ??= cache.GetValueOrDefault("CMAKE_GENERATOR");
            result.SourceDirectory ??= cache.GetValueOrDefault("CMAKE_HOME_DIRECTORY");
            result.CMakeExecutable ??= cache.GetValueOrDefault("CMAKE_COMMAND");
            if (cache.TryGetValue("CMAKE_CONFIGURATION_TYPES", out var configurations) && configurations.Length > 0)
            {
                result.MultiConfig = true;
                result.Configurations = configurations.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            else if (cache.TryGetValue("CMAKE_BUILD_TYPE", out var buildType) && result.Generator is not null)
            {
                result.MultiConfig ??= false;
                result.Configurations = [buildType];
            }
            result.Diagnostics.Add("No codemodel: limited cache/bridge observations only; absent targets have Unknown type.");
            var manifests = Path.Combine(directory, "cmake-workflow", "targets");
            if (Directory.Exists(manifests))
                foreach (var path in Directory.EnumerateFiles(manifests, "*.json", SearchOption.AllDirectories))
                {
                    using var json = await ReadJsonAsync(path, ct);
                    var data = json.RootElement;
                    var target = data.GetProperty("target").GetString()!;
                    var configuration = data.GetProperty("configuration").GetString()!;
                    if (data.GetProperty("schemaVersion").GetInt32() != 1
                        || Path.GetFileNameWithoutExtension(path) != target
                        || Path.GetFileName(Path.GetDirectoryName(path)) != configuration && configuration.Length > 0
                        || !result.Configurations.Contains(configuration))
                        throw new InvalidOperationException("Bridge manifest identity/configuration mismatch.");
                    var executable = data.GetProperty("executable").GetString()!;
                    if (!Path.IsPathFullyQualified(executable)) throw new InvalidOperationException("Bridge executable path is not absolute.");
                    result.Targets.Add(new(target, configuration, "EXECUTABLE", executable, "BridgeManifest"));
                }
        }
        if (result.MultiConfig is not null && result.Configurations.Count > 0)
            result.State = result.HasCodeModel ? "Observed" : "ObservedLimited";

        var provenance = ProvenancePath(directory);
        if (File.Exists(provenance))
        {
            var json = await File.ReadAllBytesAsync(provenance, ct);
            result.ManagedConfigure = JsonSerializer.Deserialize(json, WorkflowJson.Default.ConfigureProvenance);
            if (result.ManagedConfigure is { } managed)
            {
                if (managed.State != "Succeeded")
                {
                    result.State = "ConfigureFailed";
                    result.Diagnostics.Add("The last managed configure did not complete successfully; explicit configure is required.");
                }
                else if (managed.IndexHash is not null && managed.IndexHash == result.IndexHash
                    && managed.IndexFile is not null && result.IndexFile is not null
                    && WorkflowPlanner.PathComparer.Equals(managed.IndexFile, result.IndexFile))
                    result.PresetState = "RecordedInvocation";
                else
                {
                    // A new File API generation is not evidence of a *different*
                    // configuration. Nor does an equivalent codemodel prove that
                    // the original preset/environment is still current.
                    result.PresetState = "GenerationChanged";
                    result.Diagnostics.Add("CMake generation differs from the last managed configure; preset provenance is Unverified, not a configuration mismatch.");
                }
            }
        }
        result.Diagnostics.Add("Requested preset identity is never inferred from cache values; configure provenance records only an invocation.");
        return result;
    }

    public static void ValidateIdentity(RequestedBuildContext request, ObservedBuildContext observed)
    {
        if (observed.State is not ("Observed" or "ObservedLimited"))
            throw new InvalidOperationException($"Build context is {observed.State}/Unverified: {string.Join(" ", observed.Diagnostics)}");
        if (request.SourceDirectory is not null && (observed.SourceDirectory is null
                || !WorkflowPlanner.PathComparer.Equals(BuildTreeOwnership.CanonicalPath(request.SourceDirectory),
                    BuildTreeOwnership.CanonicalPath(observed.SourceDirectory))))
            throw new InvalidOperationException("Requested source directory disagrees with the observed build context.");
        if (request.Generator is not null && request.Generator != observed.Generator)
            throw new InvalidOperationException("Requested generator disagrees with the observed build context (or is Unknown).");
        if (request.ConfigurePreset is not null)
        {
            var invocationMatches = observed.PresetState == "RecordedInvocation"
                && observed.ManagedConfigure is { } managed
                && managed.Preset == request.ConfigurePreset
                && WorkflowPlanner.PathComparer.Equals(managed.BuildDirectory, request.BuildDirectory);
            if (observed.PresetState == "RecordedInvocation" && !invocationMatches)
                throw new InvalidOperationException($"Requested preset '{request.ConfigurePreset}' conflicts with the last recorded configure invocation.");
            if (!invocationMatches && request.ProvenancePolicy != "allowUnverified")
                throw new InvalidOperationException($"Requested preset '{request.ConfigurePreset}' is Unverified. Add an explicit configure node or explicitly adopt it with provenancePolicy: allowUnverified.");
        }
    }

    public static CMakeTargetRef Resolve(RequestedBuildContext request, ObservedBuildContext observed, string target = "")
    {
        ValidateIdentity(request, observed);
        var configuration = request.Configuration;
        if (configuration is null)
        {
            if (observed.MultiConfig == true)
                throw new InvalidOperationException("Multi-config build: pass --config or context.configuration explicitly.");
            if (observed.Configurations.Count != 1)
                throw new InvalidOperationException("Configuration selection is Unknown/Unverified.");
            configuration = observed.Configurations[0];
        }
        if (!observed.Configurations.Contains(configuration, StringComparer.Ordinal))
            throw new InvalidOperationException($"Requested configuration '{configuration}' disagrees with observed configurations [{string.Join(", ", observed.Configurations)}].");
        return new CMakeTargetRef(request.BuildDirectory, target, configuration);
    }

    public static NativeTargetObservation RequireTarget(ObservedBuildContext observed, CMakeTargetRef target, bool executableOnly = false)
    {
        var matches = observed.Targets.Where(t => t.Name == target.Target && t.Configuration == target.Configuration).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Target '{target.Target}' is {(observed.HasCodeModel ? "absent or ambiguous" : "Unknown")}. Explicit configure with File API metadata is required before Build/Run; use invoke only for an intentional target execution.");
        var item = matches[0];
        var allowed = executableOnly ? item.Type == "EXECUTABLE"
            : item.Type is "EXECUTABLE" or "STATIC_LIBRARY" or "SHARED_LIBRARY" or "MODULE_LIBRARY" or "OBJECT_LIBRARY";
        if (!allowed)
            throw new InvalidOperationException($"Target '{target.Target}' has type {item.Type}; Build/Run will not invoke it. Migrate intentional utility/campaign execution to kind: invoke (CLI: target invoke).");
        return item;
    }

    internal static string ProvenancePath(string tree) => Path.Combine(tree, "cmake-workflow", "configure.json");

    internal static async Task WriteProvenanceAsync(string tree, ConfigureProvenance provenance)
    {
        var path = ProvenancePath(tree);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(provenance, WorkflowJson.Default.ConfigureProvenance));
        File.Move(temporary, path, true);
    }

    private static Dictionary<string, string> ReadObservedCache(string directory)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(directory, "CMakeCache.txt");
        if (!File.Exists(path)) return values;
        foreach (var line in File.ReadLines(path))
        {
            var colon = line.IndexOf(':');
            var equals = line.IndexOf('=');
            if (colon <= 0 || equals < colon) continue;
            var key = line[..colon];
            if (key is "CMAKE_BUILD_TYPE" or "CMAKE_CONFIGURATION_TYPES" or "CMAKE_GENERATOR"
                or "CMAKE_HOME_DIRECTORY" or "CMAKE_COMMAND") values[key] = line[(equals + 1)..];
        }
        return values;
    }

    private static async Task<JsonDocument?> ReadObjectAsync(string directory, JsonElement objects, string kind, int version, CancellationToken ct)
    {
        foreach (var item in objects.EnumerateArray())
            if (item.GetProperty("kind").GetString() == kind && item.GetProperty("version").GetProperty("major").GetInt32() == version)
                return await ReadJsonAsync(ReplyPath(directory, item.GetProperty("jsonFile").GetString()), ct);
        return null;
    }

    private static string ReplyPath(string directory, string? file)
    {
        if (string.IsNullOrEmpty(file) || Path.GetFileName(file) != file || file is "." or "..")
            throw new InvalidOperationException("Invalid File API reply filename.");
        return Path.Combine(directory, file);
    }

    private static async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken ct) =>
        JsonDocument.Parse(await File.ReadAllBytesAsync(path, ct));
}
