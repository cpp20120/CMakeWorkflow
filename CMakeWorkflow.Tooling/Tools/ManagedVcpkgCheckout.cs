namespace CMakeWorkflow.Tooling.Tools;

internal static class ManagedVcpkgCheckout
{
    private const string Git = "/usr/bin/git";
    private const string Remote = "https://github.com/microsoft/vcpkg.git";

    internal static async Task PrepareAsync(string root, string commit, CancellationToken ct)
    {
        _ = await GitAsync(root, ["init", "--quiet", "--template="], ct);
        _ = await GitAsync(root, ["fetch", "--depth=1", "--no-tags", Remote, commit], ct);
        _ = await GitAsync(root, ["reset", "--mixed", commit], ct);
        await VerifyAsync(root, commit, ct);
    }

    internal static async Task VerifyAsync(string root, string commit, CancellationToken ct)
    {
        DirectoryInfo git = new(Path.Combine(root, ".git"));
        if (!git.Exists || git.LinkTarget is not null)
        {
            throw new InvalidDataException("Pinned vcpkg checkout lacks its Git metadata.");
        }

        string head = await GitAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], ct);
        if (head.Trim() != commit)
        {
            throw new InvalidDataException("Pinned vcpkg checkout no longer points to its approved commit.");
        }
    }

    private static async Task<string> GitAsync(string root, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        if (!File.Exists(Git))
        {
            throw new InvalidDataException("Managed vcpkg requires /usr/bin/git for pinned registry versions.");
        }

        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin:/bin",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0"
        };
        foreach (string key in new[] { "https_proxy", "HTTPS_PROXY", "no_proxy", "NO_PROXY", "SSL_CERT_FILE" })
        {
            if (System.Environment.GetEnvironmentVariable(key) is { } value)
            {
                environment.Add(key, value);
            }
        }

        SystemToolingRuntime runtime = new(root);
        ToolCommandResult result = await runtime.RunAsync(Git, arguments, TimeSpan.FromMinutes(3), environment, ct);
        return result.ExitCode == 0
            ? result.Output
            : throw new InvalidDataException("Pinned vcpkg Git checkout failed: " + result.Error.Trim());
    }
}
