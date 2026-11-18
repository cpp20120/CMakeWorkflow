namespace CMakeWorkflow.CMake.Build;

// Shared invocation for executable and utility targets. Resolving an executable
// is a separate operation: a check/campaign/aggregate target has no binary.
public sealed record CMakeBuildCommand(IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public static async Task<CMakeBuildCommand> CreateAsync(CMakeTargetRef target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(target.Target) || target.Target.StartsWith('-') || target.Target.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A literal native target name is required; build options are not target names.");
        }

        target = await CMakeConfiguration.ResolveAsync(target, ct);
        List<string> args = ["--build", target.BuildDirectory, "--target", target.Target];
        if (!string.IsNullOrWhiteSpace(target.Configuration))
        {
            args.AddRange(["--config", target.Configuration]);
        }

        return new CMakeBuildCommand(args, target.BuildDirectory);
    }
}
