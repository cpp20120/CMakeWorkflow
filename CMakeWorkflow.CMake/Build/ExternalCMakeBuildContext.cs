using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed class ExternalCMakeBuildContext : ICMakeBuildContext
{
    public async Task<BuiltArtifact> EnsureBuiltAsync(CMakeTargetRef target, CancellationToken ct)
    {
        var observed = await CMakeInspection.InspectAsync(target.BuildDirectory, ct);
        target = CMakeInspection.Resolve(new RequestedBuildContext(target.BuildDirectory, target.Configuration),
            observed, target.Target);
        CMakeInspection.RequireTarget(observed, target, executableOnly: true);
        var command = CMakeBuildCommand.Create(target);
        await ProcessRunner.RunAsync(observed.CMakeExecutable ?? "cmake", command.Arguments, command.WorkingDirectory,
            ct);
        return await CMakeFileApi.ResolveExecutableAsync(target, ct);
    }
}
