using CMakeWorkflow.Developer;
using CMakeWorkflow.Tooling;

namespace CMakeWorkflow.Commands;

/// <summary>Materializes a versioned, standalone CMake project; execution stays in the existing workflow engine.</summary>
internal static class ProjectSdkCommand
{
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
        CreatedProject project = await DeveloperLifecycle.CreateProjectAsync(
            new(sdkName, name, directory) { SdkVersion = version, EnvironmentLockSha256 = environmentLock, Variants = variants, InitializeDefaultLocks = false }, ct);
        await Console.Out.WriteLineAsync(project.Directory);
        return 0;
    }

    private static bool IsDigest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

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

}
