using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CMakeWorkflow;

// Shared invocation for executable and utility targets. Resolving an executable
// is a separate operation: a check/campaign/aggregate target has no binary.
public sealed record CMakeBuildCommand(IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public static CMakeBuildCommand Create(CMakeTargetRef target)
    {
        if (string.IsNullOrWhiteSpace(target.Target) || target.Target.StartsWith('-') || target.Target.Contains('\0'))
            throw new ArgumentException(
                "A literal native target name is required; build options are not target names.");
        target = CMakeConfiguration.Resolve(target);
        var args = new List<string> { "--build", target.BuildDirectory, "--target", target.Target };
        if (!string.IsNullOrWhiteSpace(target.Configuration))
            args.AddRange(["--config", target.Configuration]);
        return new CMakeBuildCommand(args, target.BuildDirectory);
    }
}
