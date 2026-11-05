using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.CMake.Build;

// Only valid when called by a CMake target with DEPENDS on the native target.
// It deliberately NEVER calls cmake --build, avoiding re-entrancy into Ninja.
public sealed class PreparedCMakeBuildContext : ICMakeBuildContext
{
    [SuppressMessage("Naming", "CA1725", Justification = "Preserve the existing public parameter name for named-argument callers.")]
    public async Task<BuiltArtifact> EnsureBuiltAsync(CMakeTargetRef target, CancellationToken ct)
    {
        target = CMakeConfiguration.Resolve(target);
        BuiltArtifact artifact = await CMakeFileApi.ResolveExecutableAsync(target, ct);
        return !File.Exists(artifact.ExecutablePath)
            ? throw new FileNotFoundException(
                "The prepared native target has no executable. Check the caller's CMake DEPENDS.",
                artifact.ExecutablePath)
            : artifact;
    }
}
