using System.Globalization;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.CMake.Execution;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Reporting;

namespace CMakeWorkflow.CMake.Build;

// The registry belongs to the execution host. Manifests select a name only.
internal sealed record CompilerCacheProfile(string Name, string Provider, string Executable,
    string ConfigurationFile, string? ServerSocket, int? ServerPort, bool ExternalTelemetry, string Fingerprint)
{
    internal static bool IsValidName(string value) => value.Length is >= 1 and <= 64
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    internal static async Task<Dictionary<string, CompilerCacheProfile>> ResolveAsync(WorkflowPlan plan,
        CancellationToken ct)
    {
        Dictionary<string, CompilerCacheProfile> profiles = new(StringComparer.Ordinal);
        string[] names = [.. plan.Nodes.Where(n => n.Definition.Kind is "configure" or "build" or "invoke")
            .Select(n => n.CompilerCacheProfile).OfType<string>().Distinct(StringComparer.Ordinal)];
        if (names.Length == 0)
        {
            return profiles;
        }

        string? registry = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_COMPILER_CACHE_CONFIG");
        if (registry is null || !Path.IsPathFullyQualified(registry))
        {
            throw new InvalidOperationException("Set CMAKEWORKFLOW_COMPILER_CACHE_CONFIG to the absolute host registry path.");
        }

        using JsonDocument document = JsonDocument.Parse(await ReadConfigurationAsync(registry, ct));
        if (!document.RootElement.TryGetProperty("profiles", out JsonElement entries)
            || entries.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Compiler cache registry requires a profiles object.");
        }

        foreach (string name in names)
        {
            if (!entries.TryGetProperty(name, out JsonElement entry) || entry.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Compiler cache profile '{name}' is not configured on this host.");
            }

            string? provider = Text(entry, "provider");
            if (provider is not ("ccache" or "sccache"))
            {
                throw new InvalidDataException($"Compiler cache profile '{name}' requires ccache or sccache.");
            }

            string executable = FilePath(entry, "executable", name);
            string config = FilePath(entry, "configurationFile", name);
            bool externalTelemetry = entry.TryGetProperty("externalTelemetry", out JsonElement telemetry)
                && telemetry.GetBoolean();
            string? socket = Text(entry, "serverSocket");
            int? port = entry.TryGetProperty("serverPort", out JsonElement portValue)
                && portValue.ValueKind == JsonValueKind.Number && portValue.TryGetInt32(out int number) ? number : null;
            if ((entry.TryGetProperty("serverPort", out _) && port is not (>= 1 and <= 65535))
                || (socket is not null && !Path.IsPathFullyQualified(socket))
                || (socket is not null && (port is not null || OperatingSystem.IsWindows()))
                || (provider != "sccache" && (socket is not null || port is not null)))
            {
                throw new InvalidDataException($"Compiler cache profile '{name}' has invalid sccache server settings.");
            }

            string binaryHash = await CMakeWorkflowNodeExecutor.HashExecutableAsync(executable, ct);
            string configurationHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                await ReadConfigurationAsync(config, ct)));
            string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\n', name, provider, executable, config, socket, port?.ToString(CultureInfo.InvariantCulture),
                    binaryHash, configurationHash, externalTelemetry ? "1" : "0", Text(entry, "revision")))));
            profiles.Add(name, new(name, provider, executable, config, socket, port, externalTelemetry, fingerprint));
        }

        return profiles;
    }

    internal static void RequireSameIdentity(WorkflowPlan plan, WorkflowReport saved,
        IReadOnlyDictionary<string, CompilerCacheProfile> profiles)
    {
        if (profiles.Count == 0 && saved.Nodes.All(n => n.CompilerCacheFingerprint is null))
        {
            return;
        }

        Dictionary<string, NodeResult> previous = saved.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (PlannedNode node in plan.Nodes)
        {
            string? expected = node.CompilerCacheProfile is { } name ? profiles.GetValueOrDefault(name)?.Fingerprint : null;
            if (!previous.TryGetValue(node.Definition.Id, out NodeResult? recorded)
                || !string.Equals(recorded.CompilerCacheFingerprint, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Compiler cache profile changed since the execution checkpoint.");
            }
        }
    }

    internal Dictionary<string, string> EnvironmentFor(IReadOnlyDictionary<string, string> inherited)
    {
        Dictionary<string, string> result = new(inherited, StringComparer.OrdinalIgnoreCase)
        {
            [Provider == "ccache" ? "CCACHE_CONFIGPATH" : "SCCACHE_CONF"] = ConfigurationFile
        };
        if (ServerSocket is not null)
        {
            result["SCCACHE_SERVER_UDS"] = ServerSocket;
        }
        if (ServerPort is int port)
        {
            result["SCCACHE_SERVER_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        }

        return result;
    }

    internal void RequireConfigured(ObservedBuildContext observed)
    {
        if (observed.Generator is not ("Ninja" or "Ninja Multi-Config")
            && observed.Generator?.EndsWith("Makefiles", StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException($"Compiler cache profile '{Name}' requires a CMake Ninja or Makefiles generator.");
        }

        Dictionary<string, string> cache = CMakeInspection.ReadObservedCache(observed.BuildDirectory, null);
        bool found = false;
        foreach (string language in new[] { "C", "CXX" })
        {
            if (!cache.ContainsKey($"CMAKE_{language}_COMPILER"))
            {
                continue;
            }

            found = true;
            string? launcher = cache.GetValueOrDefault($"CMAKE_{language}_COMPILER_LAUNCHER");
            if (launcher is null || !Path.IsPathFullyQualified(launcher)
                || !WorkflowPlanner.PathComparer.Equals(BuildTreeOwnership.CanonicalPath(launcher), Executable))
            {
                throw new InvalidOperationException(
                    $"Compiler cache profile '{Name}' requires its absolute {language} launcher in the configured CMake cache.");
            }
        }

        if (!found)
        {
            throw new InvalidOperationException($"Compiler cache profile '{Name}' requires a configured C or C++ compiler.");
        }
    }

    private static string FilePath(JsonElement entry, string property, string profile)
    {
        string? value = Text(entry, property);
        return value is null || !Path.IsPathFullyQualified(value) || !File.Exists(value)
               || value.IndexOfAny([';', '\n', '\r']) >= 0
            ? throw new InvalidDataException($"Compiler cache profile '{profile}' requires an absolute, existing {property}.")
            : BuildTreeOwnership.CanonicalPath(value);
    }

    private static async Task<byte[]> ReadConfigurationAsync(string path, CancellationToken ct)
    {
        return new FileInfo(path).Length > 1024 * 1024
            ? throw new InvalidDataException("Compiler cache configuration exceeds 1 MiB.")
            : await File.ReadAllBytesAsync(path, ct);
    }

    private static string? Text(JsonElement entry, string key) =>
        entry.TryGetProperty(key, out JsonElement property) ? property.GetString() : null;
}
