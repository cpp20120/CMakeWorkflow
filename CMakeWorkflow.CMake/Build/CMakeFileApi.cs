using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CMakeWorkflow;

public static class CMakeFileApi
{
    public static async Task<BuiltArtifact> ResolveExecutableAsync(CMakeTargetRef target, CancellationToken ct,
        Action<string>? readGuard = null)
    {
        var observed = await CMakeInspection.InspectAsync(target.BuildDirectory, ct, readGuard);
        target = CMakeInspection.Resolve(new RequestedBuildContext(target.BuildDirectory, target.Configuration),
            observed, target.Target);
        var item = CMakeInspection.RequireTarget(observed, target, executableOnly: true);
        if (item.Executable is null)
            throw new InvalidOperationException(
                $"Executable artifact for '{target.Target}' is Unknown/ambiguous; no filename is guessed.");
        return new BuiltArtifact(target, item.Executable);
    }
}
