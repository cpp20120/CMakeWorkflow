namespace CMakeWorkflow.CMake.Build;

// The CMake build graph, not this program, decides HOW the target is built.
public interface ICMakeBuildContext
{
    Task<BuiltArtifact> EnsureBuiltAsync(CMakeTargetRef target, CancellationToken cancellationToken);
}
