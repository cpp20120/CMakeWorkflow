using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CMakeWorkflow;

// Only valid when called by a CMake target with DEPENDS on the native target.
// It deliberately NEVER calls cmake --build, avoiding re-entrancy into Ninja.
public sealed class PreparedCMakeBuildContext : ICMakeBuildContext
{
    public async Task<BuiltArtifact> EnsureBuiltAsync(CMakeTargetRef target, CancellationToken ct)
    {
        target = CMakeConfiguration.Resolve(target);
        var artifact = await CMakeFileApi.ResolveExecutableAsync(target, ct);
        if (!File.Exists(artifact.ExecutablePath))
            throw new FileNotFoundException(
                "The prepared native target has no executable. Check the caller's CMake DEPENDS.",
                artifact.ExecutablePath);
        return artifact;
    }
}
