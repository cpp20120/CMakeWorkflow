using System.Reflection;
using System.Text;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Tooling;

namespace CMakeWorkflow.Commands;

/// <summary>Materializes a versioned, standalone CMake project; execution stays in the existing workflow engine.</summary>
internal static class ProjectSdkCommand
{
    private const string ResourcePrefix = "CMakeWorkflow.ProjectSdk.v1.";
    private const string Usage = "project new SDK NAME DIRECTORY [--sdk-version VERSION] [--variants] [--environment-lock SHA256] | " +
                                 "project tooling <plan|doctor|setup> [--json] [--yes --plan-sha SHA256] | project sdk <list|plan|lock|verify|workflow>";

    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args is ["config", .. string[] configuration])
        {
            return await ProjectConfigurationCommand.ExecuteAsync(configuration, ct);
        }
        if (args is ["sdk", .. string[] sdkArgs])
        {
            return await ProjectSdkResolution.ExecuteAsync(sdkArgs, ct);
        }

        if (args is ["tooling", "plan" or "doctor" or "setup", .. string[] tail])
        {
            // The fixed SDK profile includes the existing managed Clang/SDK plus CPack.
            // Do not accept profile, policy or installer overrides through this facade.
            for (int i = 0; i < tail.Length; i++)
            {
                if (tail[i] == "--plan-sha" && i + 1 < tail.Length && IsDigest(tail[i + 1]))
                {
                    i++;
                }
                else if (tail[i] is not ("--json" or "--yes"))
                {
                    throw new ArgumentException(Usage);
                }
            }

            return await ToolingCommand.ExecuteAsync(
                [args[1], "--profile", "linux-clang", "--profile", "packaging", "--tool-policy", "ManagedOnly", .. tail], ct);
        }

        if (args is not ["new", string sdkName, string name, string directory, .. string[] options])
        {
            throw new ArgumentException(Usage);
        }

        (string? version, string? environmentLock, bool variants) = ParseOptions(options);
        SdkDescriptor sdk = ProjectSdkCatalog.Resolve(sdkName, version);
        ProjectSdkCatalog.RequireAvailable(sdk);
        if (environmentLock is not null && !sdk.Capabilities.ExecutionModes.Contains("linux-managed-clang", StringComparer.Ordinal))
        {
            throw new InvalidDataException("unsupported-sdk-environment: " + sdk.Id + " requires ambient dependencies.");
        }
        variants |= !sdk.Legacy;
        ValidateName(name);
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
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
                        ? await ProjectConfigurationCommand.ResourceAsync(template, ct)
                        : await ReadLegacyAsync(file.Resource, ct)
                    : ProjectSdkCatalog.ReadResource(file.Resource);
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
                await ProjectConfigurationCommand.InitializeAsync(staging, ct);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, ".gitignore"), "/out/\n/CMakeUserPresets.json\n", ct);
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

        await Console.Out.WriteLineAsync(destination);
        return 0;
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

    private static (string? Version, string? EnvironmentLock, bool Variants) ParseOptions(string[] options)
    {
        string? version = null;
        string? environmentLock = null;
        bool variants = false;
        for (int i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--variants" when !variants: variants = true; break;
                case "--sdk-version" when version is null && i + 1 < options.Length:
                    version = options[++i]; break;
                case "--environment-lock" when environmentLock is null && i + 1 < options.Length && IsDigest(options[i + 1]):
                    environmentLock = options[++i]; break;
                default: throw new ArgumentException(Usage);
            }
        }
        return (version, environmentLock, variants);
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
