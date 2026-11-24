using System.Reflection;
using CMakeWorkflow.Tooling;

namespace CMakeWorkflow.Commands;

/// <summary>Materializes a versioned, standalone CMake project; execution stays in the existing workflow engine.</summary>
internal static class ProjectSdkCommand
{
    private const string ResourcePrefix = "CMakeWorkflow.ProjectSdk.v1.";
    private const string Usage = "project new <library|application> NAME DIRECTORY [--variants] [--environment-lock SHA256] | " +
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

        if (args is not ["new", "library" or "application", string name, string directory, .. string[] options])
        {
            throw new ArgumentException(Usage);
        }

        bool variants = options.Contains("--variants", StringComparer.Ordinal);
        string? environmentLock = options switch
        {
            [] => null,
            ["--variants"] => null,
            ["--environment-lock", string digest] when IsDigest(digest) => digest,
            ["--variants", "--environment-lock", string digest] when IsDigest(digest) => digest,
            ["--environment-lock", string digest, "--variants"] when IsDigest(digest) => digest,
            _ => throw new ArgumentException(Usage)
        };
        ValidateName(name);
        bool library = args[1] == "library";
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
            string buildTarget = library ? name + "_smoke" : name;
            string template = library ? "Library.CMakeLists.txt" : "Application.CMakeLists.txt";
            if (variants)
            {
                byte[] input = await ProjectConfigurationCommand.ResourceAsync(template, ct);
                string content = System.Text.Encoding.UTF8.GetString(input).Replace("@NAME@", name, StringComparison.Ordinal);
                await File.WriteAllTextAsync(Path.Combine(staging, "CMakeLists.txt"), content, ct);
            }
            else
            {
                await WriteAsync(staging, "CMakeLists.txt", template, name, buildTarget, environmentLock, ct);
            }
            await WriteAsync(staging, "CMakePresets.json", "CMakePresets.json", name, buildTarget, environmentLock, ct);
            await WriteAsync(staging, "workflow.json", "workflow.json", name, buildTarget, environmentLock, ct);
            if (environmentLock is not null)
            {
                await WriteAsync(staging, "workflow.managed.json", "workflow.managed.json", name, buildTarget, environmentLock, ct);
            }

            if (library)
            {
                await WriteAsync(staging, "include/" + name + "/api.hpp", "api.hpp", name, buildTarget, environmentLock, ct);
                await WriteAsync(staging, "src/library.cpp", "library.cpp", name, buildTarget, environmentLock, ct);
                await WriteAsync(staging, "tests/smoke.cpp", "smoke.cpp", name, buildTarget, environmentLock, ct);
            }
            else
            {
                await WriteAsync(staging, "src/main.cpp", "main.cpp", name, buildTarget, environmentLock, ct);
            }

            await ProjectSdkResolution.InitializeAsync(staging, library, environmentLock, ct);
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

    private static async Task WriteAsync(string root, string path, string resource, string name, string target,
        string? environmentLock, CancellationToken ct)
    {
        using Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + resource)
            ?? throw new InvalidOperationException("Missing Project SDK resource: " + resource);
        using StreamReader reader = new(input);
        string content = (await reader.ReadToEndAsync(ct))
            .ReplaceLineEndings("\n")
            .Replace("@NAME@", name, StringComparison.Ordinal)
            .Replace("@BUILD_TARGET@", target, StringComparison.Ordinal)
            .Replace("@ENVIRONMENT_LOCK@", environmentLock ?? "", StringComparison.Ordinal);
        string destination = Path.Combine(root, path);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, content, ct);
    }
}
