using CMakeWorkflow.CMake.Build;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMakeWorkflow.Commands;

/// <summary>Pure project policy evaluation and explicit publication of a portable SDK lock.</summary>
internal static class ProjectSdkResolution
{
    private const string Manifest = "cmwf.project.json";
    private const string Lock = "cmwf.sdk.lock.json";
    private const string Prefix = "cmake/cmwf-sdk/v1/";
    private const string Resources = "CMakeWorkflow.ProjectSdk.v1.";
    private static readonly string[] Modules = ["Native.cmake", "PackageConfig.cmake.in", "Policy.cmake", "Catalog.json"];

    private sealed record ToolchainPolicy(string Generator, string EnvironmentLockSha256);
    private sealed record Policy(int CxxStandard = 23, bool CxxExtensions = false, bool WarningsAsErrors = false,
        ToolchainPolicy? Toolchain = null);

    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args is ["list", "--json"])
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(ProjectSdkCatalog.Document,
                ProjectSdkCatalogJson.Default.SdkCatalogDocument));
            return 0;
        }
        if (args is ["list"])
        {
            foreach (SdkDescriptor item in ProjectSdkCatalog.Document.Sdks.Where(item => item.Status != "abstract"))
            {
                await Console.Out.WriteLineAsync(item.Id + " " + item.Version + (item.Status == "available" ? "" : " " + item.Status));
            }
            return 0;
        }
        if (args is ["show", ..] or ["requirements", _, ..])
        {
            SdkDescriptor descriptor = args switch
            {
                ["show", string id] => ProjectSdkCatalog.Resolve(id, null),
                ["show", string id, "--json"] => ProjectSdkCatalog.Resolve(id, null),
                ["show", string id, "--version", string version] => ProjectSdkCatalog.Resolve(id, version),
                ["show", string id, "--version", string version, "--json"] => ProjectSdkCatalog.Resolve(id, version),
                ["requirements", string directory] => await ReadDescriptorAsync(directory, ct),
                ["requirements", string directory, "--json"] => await ReadDescriptorAsync(directory, ct),
                _ => throw new ArgumentException("Usage: project sdk show SDK [--version VERSION] [--json] | requirements DIRECTORY")
            };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(descriptor, ProjectSdkCatalogJson.Default.SdkDescriptor));
            return 0;
        }

        if (args is not (["plan" or "verify" or "workflow", _] or ["lock", _, "--plan-sha", _]))
        {
            throw new ArgumentException("Usage: project sdk list | plan DIRECTORY | verify DIRECTORY | workflow DIRECTORY | lock DIRECTORY --plan-sha SHA256");
        }

        string root = Path.GetFullPath(args[1]);
        byte[] plan = await EvaluateAsync(root, ct);
        string digest = Digest(plan);
        if (args[0] == "plan")
        {
            await Console.Out.WriteLineAsync(Encoding.UTF8.GetString(plan));
            await Console.Error.WriteLineAsync("SDK plan SHA-256: " + digest);
            return 0;
        }

        if (args[0] == "lock")
        {
            if (!string.Equals(args[3], digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException("SDK plan changed after review. Review a new plan.");
            }

            await PublishAsync(root, plan, ct);
        }

        byte[] actual = await File.ReadAllBytesAsync(CheckedPath(root, Lock), ct);
        byte[] current = await EvaluateAsync(root, ct);
        if (!actual.AsSpan().SequenceEqual(current))
        {
            throw new InvalidDataException("SDK lock is stale or modified. Review project sdk plan and lock again.");
        }

        if (args[0] == "workflow")
        {
            await WriteManagedWorkflowAsync(root, actual, ct);
        }
        else
        {
            await Console.Out.WriteLineAsync("SDK lock SHA-256: " + Digest(actual));
        }
        return 0;
    }

    internal static async Task InitializeAsync(string root, SdkDescriptor sdk, string? environmentLock, CancellationToken ct)
    {
        string settings = environmentLock is null ? "{}" :
            "{\"toolchain\":{\"profile\":\"linux-clang\",\"policy\":\"ManagedOnly\",\"generator\":\"Ninja Multi-Config\"," +
            "\"target\":{\"os\":\"linux\",\"architecture\":\"x64\"},\"environmentLockSha256\":\"" + environmentLock + "\"}}";
        string manifest = "{\n  \"schemaVersion\": 1,\n  \"sdk\": \"" + sdk.Id + "\",\n  \"sdkVersion\": \"" + sdk.Version +
                          "\",\n  \"policyFiles\": [],\n  \"settings\": " + settings + "\n}\n";
        await File.WriteAllTextAsync(Path.Combine(root, Manifest), manifest, ct);
        foreach (SdkFile module in ModuleFiles(sdk))
        {
            string path = CheckedPath(root, module.Path);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, await ModuleAsync(sdk, module, ct), ct);
        }

        await File.WriteAllTextAsync(Path.Combine(root, ".gitattributes"),
            sdk.Legacy ? "*.json text eol=lf\ncmake/cmwf-sdk/v1/* text eol=lf\n" :
                "*.json text eol=lf\ncmake/cmwf-sdk/ecosystem-v1/* text eol=lf\n", ct);
        await PublishAsync(root, await EvaluateAsync(root, ct), ct);
    }

    private static async Task<byte[]> EvaluateAsync(string root, CancellationToken ct)
    {
        byte[] manifest = await File.ReadAllBytesAsync(CheckedPath(root, Manifest), ct);
        using JsonDocument document = JsonDocument.Parse(manifest);
        JsonElement project = document.RootElement;
        CheckObject(project, "schemaVersion", "sdk", "sdkVersion", "policyFiles", "settings");
        if (project.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new InvalidDataException("Unsupported project manifest schema.");
        }

        SdkDescriptor sdk = Descriptor(project);
        ProjectSdkCatalog.RequireAvailable(sdk);
        SdkDescriptor[] lineage = [.. ProjectSdkCatalog.Lineage(sdk)];
        Policy policy = new();
        foreach (SdkDescriptor ancestor in lineage)
        {
            policy = policy with
            {
                CxxStandard = ancestor.Defaults.CxxStandard ?? policy.CxxStandard,
                CxxExtensions = ancestor.Defaults.CxxExtensions ?? policy.CxxExtensions,
                WarningsAsErrors = ancestor.Defaults.WarningsAsErrors ?? policy.WarningsAsErrors
            };
        }
        Dictionary<string, string> inputs = new(StringComparer.OrdinalIgnoreCase) { [Manifest] = Digest(manifest) };
        string modulePrefix = sdk.Legacy ? Prefix : "cmake/cmwf-sdk/ecosystem-v1/";
        foreach (JsonElement entry in project.GetProperty("policyFiles").EnumerateArray())
        {
            string path = entry.GetString() ?? "";
            if (!path.EndsWith(".json", StringComparison.Ordinal) ||
                string.Equals(path, Lock, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, Manifest, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(modulePrefix, StringComparison.OrdinalIgnoreCase) || inputs.Count >= 65 || inputs.ContainsKey(path))
            {
                throw new InvalidDataException("Invalid or repeated SDK policy file: " + path);
            }

            byte[] bytes = await File.ReadAllBytesAsync(CheckedPath(root, path), ct);
            inputs.Add(path, Digest(bytes));
            using JsonDocument layer = JsonDocument.Parse(bytes);
            policy = Overlay(policy, layer.RootElement);
        }

        policy = Overlay(policy, project.GetProperty("settings"));
        if (policy.Toolchain is not null && !sdk.Capabilities.ExecutionModes.Contains("linux-managed-clang", StringComparer.Ordinal))
        {
            throw new InvalidDataException("unsupported-sdk-environment: " + sdk.Id + " requires ambient dependencies.");
        }
        Dictionary<string, string> modules = new(StringComparer.Ordinal);
        foreach (SdkFile module in ModuleFiles(sdk))
        {
            string path = module.Path;
            byte[] expected = await ModuleAsync(sdk, module, ct);
            byte[] actual = await File.ReadAllBytesAsync(CheckedPath(root, path), ct);
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                throw new InvalidDataException("Vendored SDK module differs from the installed SDK version: " + path);
            }

            modules.Add(path, Digest(expected));
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("sdk", sdk.Id);
            writer.WriteString("sdkVersion", sdk.Version);
            if (!sdk.Legacy)
            {
                writer.WriteString("descriptorSha256", ProjectSdkCatalog.Digest(sdk));
            }
            writer.WriteStartArray("sdkLineage");
            foreach (SdkDescriptor ancestor in lineage)
            {
                writer.WriteStringValue(ancestor.Id + "/" + ancestor.Version);
            }

            writer.WriteEndArray();
            WriteDigests(writer, "inputs", inputs);
            WriteDigests(writer, "modules", modules);
            writer.WriteStartObject("effective");
            writer.WriteNumber("cxxStandard", policy.CxxStandard);
            writer.WriteBoolean("cxxExtensions", policy.CxxExtensions);
            writer.WriteBoolean("warningsAsErrors", policy.WarningsAsErrors);
            if (policy.Toolchain is { } toolchain)
            {
                writer.WriteStartObject("toolchain");
                writer.WriteString("profile", "linux-clang");
                writer.WriteString("policy", "ManagedOnly");
                writer.WriteString("generator", toolchain.Generator);
                writer.WriteStartObject("target");
                writer.WriteString("os", "linux");
                writer.WriteString("architecture", "x64");
                writer.WriteEndObject();
                writer.WriteString("environmentLockSha256", toolchain.EnvironmentLockSha256);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static SdkDescriptor Descriptor(JsonElement project)
    {
        string id = project.GetProperty("sdk").GetString() ?? "";
        SdkDescriptor sdk = ProjectSdkCatalog.Resolve(id, project.GetProperty("sdkVersion").GetString() ?? "");
        return sdk.Id == id ? sdk : throw new InvalidDataException("Project manifests require the exact SDK ID.");
    }

    private static async Task<SdkDescriptor> ReadDescriptorAsync(string directory, CancellationToken ct)
    {
        using JsonDocument sdk = JsonDocument.Parse(await ReadVerifiedAsync(Path.GetFullPath(directory), ct));
        return Descriptor(sdk.RootElement);
    }

    private static SdkFile[] ModuleFiles(SdkDescriptor sdk) => sdk.Legacy
        ? [.. Modules.Select(module => new SdkFile(Prefix + module, module))]
        : sdk.Modules;

    private static Task<byte[]> ModuleAsync(SdkDescriptor sdk, SdkFile module, CancellationToken ct) =>
        sdk.Legacy ? ResourceAsync(module.Resource, ct) : Task.FromResult(ProjectSdkCatalog.ReadResource(module.Resource));

    private static Policy Overlay(Policy inherited, JsonElement settings)
    {
        CheckObject(settings, "cxxStandard", "cxxExtensions", "warningsAsErrors", "toolchain");
        int standard = settings.TryGetProperty("cxxStandard", out JsonElement s) ? s.GetInt32() : inherited.CxxStandard;
        return standard is not (17 or 20 or 23)
            ? throw new InvalidDataException("cxxStandard must be 17, 20 or 23.")
            : new(standard,
            settings.TryGetProperty("cxxExtensions", out JsonElement e) ? e.GetBoolean() : inherited.CxxExtensions,
            settings.TryGetProperty("warningsAsErrors", out JsonElement w) ? w.GetBoolean() : inherited.WarningsAsErrors,
            settings.TryGetProperty("toolchain", out JsonElement toolchain) ? ReadToolchain(toolchain) : inherited.Toolchain);
    }

    private static ToolchainPolicy ReadToolchain(JsonElement element)
    {
        CheckObject(element, "profile", "policy", "generator", "target", "environmentLockSha256");
        JsonElement target = element.GetProperty("target");
        CheckObject(target, "os", "architecture");
        string? generator = element.GetProperty("generator").GetString();
        string? digest = element.GetProperty("environmentLockSha256").GetString();
        return element.GetProperty("profile").GetString() == "linux-clang" &&
               element.GetProperty("policy").GetString() == "ManagedOnly" &&
               target.GetProperty("os").GetString() == "linux" && target.GetProperty("architecture").GetString() == "x64" &&
               generator is "Ninja" or "Ninja Multi-Config" && digest is { Length: 64 } && digest.All(char.IsAsciiHexDigitLower)
            ? new(generator, digest)
            : throw new InvalidDataException("Project toolchain requires linux-clang, ManagedOnly, Linux x64, Ninja and an approved environment lock.");
    }

    private static async Task WriteManagedWorkflowAsync(string root, byte[] sdkLock, CancellationToken ct)
    {
        using JsonDocument document = JsonDocument.Parse(sdkLock);
        if (!document.RootElement.GetProperty("effective").TryGetProperty("toolchain", out JsonElement selected))
        {
            throw new InvalidDataException("The project SDK lock does not declare a toolchain.");
        }
        ToolchainPolicy toolchain = ReadToolchain(selected);
        JsonObject workflow = JsonNode.Parse(await File.ReadAllBytesAsync(CheckedPath(root, "workflow.json"), ct))!.AsObject();
        JsonObject context = workflow["contexts"]!["project"]!.AsObject();
        _ = context.Remove("configurePreset");
        context["buildDirectory"] = "out/build/managed-linux";
        context["configuration"] = "Release";
        context["generator"] = toolchain.Generator;
        context["toolchain"] = new JsonObject
        {
            ["environmentProfileId"] = "linux-managed-clang",
            ["generator"] = toolchain.Generator,
            ["hostArchitecture"] = "x64",
            ["targetArchitecture"] = "x64",
            ["environmentLockSha256"] = toolchain.EnvironmentLockSha256
        };
        await Console.Out.WriteLineAsync(workflow.ToJsonString());
    }

    internal static void CheckObject(JsonElement element, params string[] allowed)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
            {
                throw new InvalidDataException("Unknown or duplicate SDK property: " + property.Name);
            }
        }
    }

    private static void WriteDigests(Utf8JsonWriter writer, string name, Dictionary<string, string> digests)
    {
        writer.WriteStartObject(name);
        foreach ((string path, string digest) in digests.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            writer.WriteString(path, digest);
        }

        writer.WriteEndObject();
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static async Task<byte[]> ReadVerifiedAsync(string root, CancellationToken ct)
    {
        byte[] actual = await File.ReadAllBytesAsync(CheckedPath(root, Lock), ct);
        byte[] expected = await EvaluateAsync(root, ct);
        return actual.AsSpan().SequenceEqual(expected) ? actual :
            throw new InvalidDataException("SDK lock is stale or modified. Review project sdk plan and lock again.");
    }

    private static string CheckedPath(string root, string relative)
    {
        string path = root;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("SDK project root must not be a link.");
        }

        foreach (string segment in relative.Split('/'))
        {
            if (segment is "" or "." or ".." || segment.EndsWith('.') ||
                segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) || IsDeviceName(segment))
            {
                throw new InvalidDataException("Unsafe SDK path: " + relative);
            }

            path = Path.Combine(path, segment);
            FileInfo entry = new(path);
            if (entry.LinkTarget is not null || (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException("SDK paths must not contain links: " + relative);
            }
        }

        return path;
    }

    private static bool IsDeviceName(string segment)
    {
        string stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
               (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3]));
    }

    private static async Task<byte[]> ResourceAsync(string name, CancellationToken ct)
    {
        using Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resources + name)
            ?? throw new InvalidOperationException("Missing SDK resource: " + name);
        using StreamReader reader = new(input);
        return Encoding.UTF8.GetBytes((await reader.ReadToEndAsync(ct)).ReplaceLineEndings("\n"));
    }

    private static async Task PublishAsync(string root, byte[] plan, CancellationToken ct)
    {
        string destination = CheckedPath(root, Lock);
        string temporary = Path.Combine(root, ".cmwf-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(plan, ct);
                await output.FlushAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
