using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CMakeWorkflow;

// The CMake build graph, not this program, decides HOW the target is built.
public interface ICMakeBuildContext
{
    Task<BuiltArtifact> EnsureBuiltAsync(CMakeTargetRef target, CancellationToken cancellationToken);
}
