#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

// The CMake build graph, not this program, decides HOW the target is built.
public interface ICMakeBuildContext
{
    Task<BuiltArtifact> EnsureBuiltAsync(CMakeTargetRef target, CancellationToken cancellationToken);
}
