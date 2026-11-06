namespace CMakeWorkflow.CMake.Build;

// Only valid when called by a CMake target with DEPENDS on the native target.
// It deliberately NEVER calls cmake --build, avoiding re-entrancy into Ninja.
public sealed class PreparedCMakeBuildContext : ICMakeBuildContext
{

    public async Task<BuiltArtifact> EnsureBuiltAsync(CMakeTargetRef target, CancellationToken cancellationToken)
    {
        target = CMakeConfiguration.Resolve(target);
        BuiltArtifact artifact = await CMakeFileApi.ResolveExecutableAsync(target, cancellationToken);
        return !File.Exists(artifact.ExecutablePath)
            ? throw new FileNotFoundException(
                "The prepared native target has no executable. Check the caller's CMake DEPENDS.",
                artifact.ExecutablePath)
            : artifact;
    }
}
