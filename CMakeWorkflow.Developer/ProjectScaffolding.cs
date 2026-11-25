using System.Reflection;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.CMake.Build;

namespace CMakeWorkflow.Developer;

/// <summary>Materializes a versioned, standalone CMake project; execution stays in the existing workflow engine.</summary>
internal static class ProjectScaffolding
{
    private const string ResourcePrefix = "CMakeWorkflow.ProjectSdk.v1.";
    internal static async Task<string> CreateAsync(CreateProjectRequest request, CancellationToken ct)
    {
        string name = request.Name;
        string? environmentLock = request.EnvironmentLockSha256;
        bool variants = request.Variants;
        if (environmentLock is not null && !IsDigest(environmentLock))
        {
            throw new ArgumentException("Expected a lowercase SHA-256 environment lock identity.", nameof(request));
        }
        SdkDescriptor sdk = ProjectSdkCatalog.Resolve(request.Sdk, request.SdkVersion);
        ProjectSdkCatalog.RequireAvailable(sdk);
        if (environmentLock is not null && !sdk.Capabilities.ExecutionModes.Contains("linux-managed-clang", StringComparer.Ordinal))
        {
            throw new InvalidDataException("unsupported-sdk-environment: " + sdk.Id + " requires ambient dependencies.");
        }
        variants |= !sdk.Legacy;
        ValidateName(name);
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Directory));
        string parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Choose a new project directory.");
        if (!Directory.Exists(parent) || Path.Exists(destination))
        {
            throw new IOException("Project directory must not exist and its parent must exist.");
        }

        string staging = Path.Combine(parent, ".cmwf-project-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(staging);
        try
        {
            string buildTarget = sdk.BuildTarget.Replace("@NAME@", name, StringComparison.Ordinal);
            foreach (SdkFile file in sdk.Scaffold)
            {
                string path = file.Path.Replace("@NAME@", name, StringComparison.Ordinal);
                byte[] bytes = sdk.Legacy
                    ? variants && path == "CMakeLists.txt" && sdk.VariantTemplate is { } template
                        ? await ProjectConfiguration.ResourceAsync(template, ct)
                        : await ReadLegacyAsync(file.Resource, ct)
                    : ProjectSdkCatalog.ReadResource(file.Resource);
                if (request.InitializeDefaultLocks && variants && sdk.Capabilities.Packaging && path == "CMakeLists.txt")
                {
                    bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("include(CPack)",
                        "set(CPACK_GENERATOR ZIP)\n    set(CPACK_PACKAGE_FILE_NAME \"cmwf-package\")\n    include(CPack)", StringComparison.Ordinal));
                }
                await WriteAsync(staging, path, bytes, name, buildTarget, environmentLock, ct);
            }

            byte[] presets = sdk.Capabilities.Packaging ? await ReadLegacyAsync("CMakePresets.json", ct) :
                ProjectSdkCatalog.ReadResource("NoPackagePresets.json");
            await WriteAsync(staging, "CMakePresets.json", presets, name, buildTarget, environmentLock, ct);
            byte[] workflow = await ReadLegacyAsync("workflow.json", ct);
            await WriteAsync(staging, "workflow.json", WorkflowTemplate(sdk, workflow), name, buildTarget, environmentLock, ct);
            if (environmentLock is not null)
            {
                byte[] managed = await ReadLegacyAsync("workflow.managed.json", ct);
                await WriteAsync(staging, "workflow.managed.json", WorkflowTemplate(sdk, managed), name, buildTarget, environmentLock, ct);
            }

            await ProjectSdkResolution.InitializeAsync(staging, sdk, environmentLock, ct);
            if (variants)
            {
                await ProjectConfiguration.InitializeAsync(staging, ct);
                if (request.InitializeDefaultLocks)
                {
                    await File.WriteAllBytesAsync(Path.Combine(staging, "cmwf.developer.json"),
                        JsonSerializer.SerializeToUtf8Bytes(new ScaffoldIdentity(1, name), DeveloperJson.Default.ScaffoldIdentity), ct);
                    await PublishDefaultsAsync(staging, ct);
                }
            }
            string generatedManifestIgnore = request.InitializeDefaultLocks && variants ? "/.cmwf-developer-*.json\n/.cmakeworkflow/\n" : "";
            await File.WriteAllTextAsync(Path.Combine(staging, ".gitignore"), "/out/\n/CMakeUserPresets.json\n" + generatedManifestIgnore, ct);
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        return destination;
    }

    private static async Task PublishDefaultsAsync(string root, CancellationToken ct)
    {
        foreach (string profile in new[] { "debug", "release" })
        {
            ConfigurationSelection selection = new() { Profile = profile };
            ConfigurationPlan plan = await DeveloperLifecycle.PlanConfigurationAsync(root, selection, ct);
            await ProjectConfiguration.PublishAsync(root, plan.Lock.Sha256, plan.Lock.Json.ToArray(), ct);
        }
    }

    private static bool IsDigest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 64 || !char.IsAsciiLetter(name[0]) ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') ||
            (name.ToUpperInvariant() is "ALL" or "CLEAN" or "HELP" or "INSTALL" or "TEST" or "PACKAGE" or "PACKAGE_SOURCE" or "EDIT_CACHE" or "REBUILD_CACHE" or "CON" or "PRN" or "AUX" or "NUL") ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                  name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsAsciiDigit(name[3])))
        {
            throw new ArgumentException("Use a non-reserved ASCII project name (letters, digits, underscore; first character must be a letter).", nameof(name));
        }
    }

    private static byte[] WorkflowTemplate(SdkDescriptor sdk, byte[] original) =>
        sdk.Legacy ? original : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original)
            .Replace("\"kind\": \"build\"", "\"kind\": \"invoke\"", StringComparison.Ordinal)
            .Replace("@BUILD_TARGET@", "all", StringComparison.Ordinal));

    private static async Task<byte[]> ReadLegacyAsync(string resource, CancellationToken ct)
    {
        using Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + resource)
            ?? throw new InvalidOperationException("Missing Project SDK resource: " + resource);
        using StreamReader reader = new(input);
        return Encoding.UTF8.GetBytes((await reader.ReadToEndAsync(ct)).ReplaceLineEndings("\n"));
    }

    private static async Task WriteAsync(string root, string path, byte[] bytes, string name, string target,
        string? environmentLock, CancellationToken ct)
    {
        string content = Encoding.UTF8.GetString(bytes)
            .Replace("@NAME@", name, StringComparison.Ordinal)
            .Replace("@BUILD_TARGET@", target, StringComparison.Ordinal)
            .Replace("@ENVIRONMENT_LOCK@", environmentLock ?? "", StringComparison.Ordinal);
        string destination = BuildVariantConfiguration.CheckedPath(root, path);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using FileStream file = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
    }
}
